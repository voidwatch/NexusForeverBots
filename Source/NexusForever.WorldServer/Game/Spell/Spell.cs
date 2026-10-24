using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Prerequisite;
using NexusForever.WorldServer.Game.Spell.Event;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NLog;

namespace NexusForever.WorldServer.Game.Spell
{
    public partial class Spell : IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public uint CastingId { get; }
        public bool IsCasting => status == SpellStatus.Casting;
        public bool IsFinished => status == SpellStatus.Finished;

        /// <summary>Seconds until the spell's next event (cast end, effect tick), null without one (diagnostics, spell-pending-overflow).</summary>
        public double? NextEventDelay => events.NextDelay;

        private readonly UnitEntity caster;
        private readonly SpellParameters parameters;

        /// <summary>
        /// Id of the spell being cast.
        /// </summary>
        public uint Spell4Id => parameters.SpellInfo.Entry.Id;
        private SpellStatus status;

        private readonly List<SpellTargetInfo> targets = new();
        /// <summary>
        /// Spell effect types that were executed without a handler, with the number of times and one example spell.
        /// </summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<SpellEffectType, (int Count, uint ExampleSpellId)> UnhandledEffects = new();

        private bool activationHandled;
        private readonly List<Telegraph> telegraphs = new();

        private readonly SpellEventManager events = new();

        public Spell(UnitEntity caster, SpellParameters parameters)
        {
            this.caster     = caster;
            this.parameters = parameters;
            CastingId       = GlobalSpellManager.Instance.NextCastingId;
            status          = SpellStatus.Initiating;

            if (parameters.RootSpellInfo == null)
                parameters.RootSpellInfo = parameters.SpellInfo;
        }

        public void Update(double lastTick)
        {
            events.Update(lastTick);

            if (status == SpellStatus.Executing && !events.HasPendingEvent)
            {
                // spell effects have finished executing
                status = SpellStatus.Finished;
                log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has finished.");

                // TODO: add a timer to count down on the Effect before sending the finish - sending the finish will e.g. wear off the buff
                //SendSpellFinish();
            }
        }

        /// <summary>
        /// Begin cast, checking prerequisites before initiating.
        /// </summary>
        public void Cast()
        {
            if (status != SpellStatus.Initiating)
                throw new InvalidOperationException();

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started initating.");

            CastResult result = CheckCast();
            if (result != CastResult.Ok)
            {
                SendSpellCastResult(result);
                // a refused cast is over; left in Initiating it stayed in the caster's pending list for good (a bot held 400 refused
                // Blade Dance fluff casts, 83139-83143 x80, and every later cast was refused with spell-pending-overflow)
                status = SpellStatus.Finished;
                return;
            }

            if (caster is Player player)
                if (parameters.SpellInfo.GlobalCooldown != null)
                    player.SpellManager.SetGlobalSpellCooldown(parameters.SpellInfo.GlobalCooldown.CooldownTime / 1000d);

            // It's assumed that non-player entities will be stood still to cast (most do). 
            // TODO: There are a handful of telegraphs that are attached to moving units (specifically rotating units) which this needs to be updated to account for.
            if (caster is not Player)
                InitialiseTelegraphs();

            SendSpellStart();

            // enqueue spell to be executed after cast time
            events.EnqueueEvent(new SpellEvent(caster.ModifySpellValue(parameters.SpellInfo.Entry, SpellModifications.CastTime, parameters.SpellInfo.Entry.CastTime) / 1000d, Execute));
            status = SpellStatus.Casting;

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started casting.");
        }

        private CastResult CheckCast()
        {
            CastResult preReqCheck = CheckPrerequisites();
            if (preReqCheck != CastResult.Ok)
                return preReqCheck;

            CastResult ccResult = CheckCCConditions();
            if (ccResult != CastResult.Ok)
                return ccResult;

            if (Combat.CombatConfig.Enabled && caster.IsDead)
                return CastResult.CasterCannotBeDead;

            CastResult conditionResult = CheckConditions();
            if (conditionResult != CastResult.Ok)
                return conditionResult;

            // spells cast by effects (proxies, procs) do not wait for the caster's cooldowns
            if (caster is Player player && parameters.UserInitiatedSpellCast)
            {
                if (player.SpellManager.GetSpellCooldown(parameters.SpellInfo.Entry.Id) > 0d)
                    return CastResult.SpellCooldown;

                // this isn't entirely correct, research GlobalCooldownEnum
                if (parameters.SpellInfo.Entry.GlobalCooldownEnum == 0
                    && player.SpellManager.GetGlobalSpellCooldown() > 0d)
                    return CastResult.SpellGlobalCooldown;

                if (parameters.CharacterSpell?.MaxAbilityCharges > 0 && parameters.CharacterSpell?.AbilityCharges == 0)
                    return CastResult.SpellNoCharges;

                CastResult costResult = InnateCost.Check(player, parameters.SpellInfo.Entry);
                if (costResult != CastResult.Ok)
                    return costResult;
            }

            return CastResult.Ok;
        }

        private CastResult CheckPrerequisites()
        {
            // TODO: Remove below line and evaluate PreReq's for Non-Player Entities
            if (!(caster is Player player))
                return CastResult.Ok;

            if (parameters.SpellInfo.CasterCastPrerequisite != null && !CheckRunnerOverride(player))
            {
                if (!PrerequisiteManager.Instance.Meets(player, parameters.SpellInfo.CasterCastPrerequisite.Id, true))
                    return CastResult.PrereqCasterCast;
            }

            // not sure if this should be for explicit and/or implicit targets
            if (parameters.SpellInfo.TargetCastPrerequisites != null)
            {
            }

            // this probably isn't the correct place, name implies this should be constantly checked
            if (parameters.SpellInfo.CasterPersistencePrerequisites != null)
            {
            }

            if (parameters.SpellInfo.TargetPersistencePrerequisites != null)
            {
            }

            return CastResult.Ok;
        }

        /// <summary>A Spell4Conditions id after the caster's ModifySpell rows for parameter 17 (caster) or 18 (target).</summary>
        private Spell4ConditionsEntry ModifiedConditions(uint id, uint parameter, Spell4ConditionsEntry entry)
        {
            uint modified = (uint)caster.ModifySpellValue(parameters.SpellInfo.Entry, parameter, id);
            return modified == id ? entry : GameTableManager.Instance.Spell4Conditions.GetEntry(modified);
        }

        /// <summary>
        /// Spell4Conditions of the caster and of the primary target (SpellConditions). Casts a unit chose (player casts, creature
        /// abilities) are refused like the client refuses them; casts started by effects (proxies, procs, passives) only record what would
        /// have failed, because the client evaluates conditions in its cast check, not for spells the server triggers (likely).
        /// </summary>
        private CastResult CheckConditions()
        {
            bool enforce = parameters.UserInitiatedSpellCast || caster is not Player;
            uint spellId = parameters.SpellInfo.Entry.Id;

            // ModifySpell parameters 17/18 replace the Spell4Conditions row (row 388: caster conditions at most row 1)
            Spell4ConditionsEntry casterConditions = ModifiedConditions(parameters.SpellInfo.Entry.Spell4ConditionsIdCaster, SpellModifications.CasterConditions, parameters.SpellInfo.CasterConditions);
            Spell4ConditionsEntry targetConditions = ModifiedConditions(parameters.SpellInfo.Entry.Spell4ConditionsIdTarget, SpellModifications.TargetConditions, parameters.SpellInfo.TargetConditions);
            CastResult result = SpellConditions.Check(caster, casterConditions, true, spellId);
            if (result == CastResult.Ok && targetConditions != null)
            {
                uint targetId = parameters.PrimaryTargetId != 0u ? parameters.PrimaryTargetId : caster.TargetGuid;
                if (targetId != 0u && targetId != caster.Guid && caster.GetVisible<WorldEntity>(targetId) is UnitEntity target)
                    result = SpellConditions.Check(target, targetConditions, false, spellId);
            }

            if (result == CastResult.Ok || enforce)
                return result;

            Diagnostics.FailureLedger.Record("spell-condition", $"triggered cast passed despite {result}", $"spell {spellId}");
            return CastResult.Ok;
        }

        private bool CheckRunnerOverride(Player player)
        {
            foreach (PrerequisiteEntry runnerPrereq in parameters.SpellInfo.PrerequisiteRunners)
                if (PrerequisiteManager.Instance.Meets(player, runnerPrereq.Id, true))
                    return true;

            return false;
        }

        /// <summary>
        /// Spell4CCConditions of the caster and the primary target (SpellCCConditions). Same scope as <see cref="CheckConditions"/>: casts a unit
        /// chose are refused, casts started by effects record what would have failed (the client skips the caster's "cannot" rules for them).
        /// </summary>
        private CastResult CheckCCConditions()
        {
            bool enforce = parameters.UserInitiatedSpellCast || caster is not Player;
            uint spellId = parameters.SpellInfo.Entry.Id;

            CastResult result = SpellCCConditions.Check(caster, parameters.SpellInfo.CasterCCConditions, true, spellId);
            if (result == CastResult.Ok && parameters.SpellInfo.TargetCCConditions != null)
            {
                uint targetId = parameters.PrimaryTargetId != 0u ? parameters.PrimaryTargetId : caster.TargetGuid;
                if (targetId != 0u && targetId != caster.Guid && caster.GetVisible<WorldEntity>(targetId) is UnitEntity target)
                    result = SpellCCConditions.Check(target, parameters.SpellInfo.TargetCCConditions, false, spellId);
            }

            if (result == CastResult.Ok || enforce)
                return result;

            Diagnostics.FailureLedger.Record("spell-cc-condition", $"triggered cast passed despite {result}", $"spell {spellId}");
            return CastResult.Ok;
        }

        private void InitialiseTelegraphs()
        {
            telegraphs.Clear();
            foreach (TelegraphDamageEntry telegraphDamageEntry in parameters.SpellInfo.Telegraphs)
            {
                // a spell can point at a telegraph that is missing from the game tables
                if (telegraphDamageEntry == null)
                {
                    Diagnostics.FailureLedger.Record("spell-telegraph-missing", $"spell {parameters.SpellInfo.Entry.Id}");
                    continue;
                }

                telegraphs.Add(new Telegraph(telegraphDamageEntry, caster, caster.Position, caster.Rotation));
            }
        }

        /// <summary>
        /// Cancel cast with supplied <see cref="CastResult"/>.
        /// </summary>
        public void CancelCast(CastResult result)
        {
            if (status != SpellStatus.Casting)
                throw new InvalidOperationException();

            if (caster is Player player && !player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new Server07F9
                {
                    ServerUniqueId = CastingId,
                    CastResult     = result,
                    CancelCast     = true
                });
            }

            events.CancelEvents();
            status = SpellStatus.Executing;

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} cast was cancelled.");
        }

        /// <summary>
        /// End a spell whose caster died: pending timed effects and child casts never fire.
        /// </summary>
        public void AbortOnCasterDeath()
        {
            // finished spells can still hold delayed effect and child cast events
            events.CancelEvents();
            status = SpellStatus.Finished;
        }

        private void Execute()
        {
            status = SpellStatus.Executing;
            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} has started executing.");

            if (caster is Player player)
                if (parameters.SpellInfo.Entry.SpellCoolDown != 0u)
                    player.SpellManager.SetSpellCooldown(parameters.SpellInfo.Entry.Id, player.ModifySpellValue(parameters.SpellInfo.Entry, SpellModifications.Cooldown, parameters.SpellInfo.Entry.SpellCoolDown) / 1000d);

            SelectTargets();
            ExecuteEffects();
            CostSpell();
            CastTooltipGenerators();

            // an activation spell without an Activate effect (CSI spells: cast method 5, e.g. 41000 "Drill for Water", only SetBusy) is the
            // activation itself; a finished cast credits it like the effect would (the CSI minigame is client side and always succeeds for bots)
            if (parameters.IsActivation && caster is Player activator && !parameters.SpellInfo.Effects.Any(e => (SpellEffectType)e.EffectType == SpellEffectType.Activate))
            {
                Diagnostics.FailureLedger.Record("activation-credit-cast-finish", $"cast method {parameters.SpellInfo.BaseInfo.Entry.CastMethod}", $"spell {parameters.SpellInfo.Entry.Id}");
                CreditActivation(activator);
            }

            // proc trigger 10 "on cast" ("On Action Cast", "if your target casts a spell"), only for casts a unit chose, not proxies or procs
            if (parameters.UserInitiatedSpellCast || caster is NonPlayer)
                caster.FireProcs(10u, parameters.PrimaryTargetId != 0u ? caster.GetVisible<WorldEntity>(parameters.PrimaryTargetId) : null);

            if (caster is Player questPlayer)
            {
                WorldEntity primaryTarget = parameters.PrimaryTargetId != 0u ? questPlayer.GetVisible<WorldEntity>(parameters.PrimaryTargetId) : null;
                questPlayer.QuestManager.OnSpellSuccess(parameters.SpellInfo.Entry, primaryTarget?.CreatureId ?? 0u, primaryTarget?.Guid ?? 0u);
                questPlayer.PathMissionManager.OnSpellSuccess(parameters.SpellInfo.Entry.Id, primaryTarget?.CreatureId ?? 0u, primaryTarget?.Guid ?? 0u);
                questPlayer.ChallengeManager.OnSpellSuccess(parameters.SpellInfo.Entry.Id);
            }

            SendSpellGo();
        }

        /// <summary>A completed player cast grants the resource generators its tooltip names but no table links (TooltipGenerators).</summary>
        private void CastTooltipGenerators()
        {
            if (caster is not Player player || !parameters.UserInitiatedSpellCast || player.IsDead)
                return;
            if (player.Session is Bots.BotSession)
                Diagnostics.FailureLedger.Record("bot-cast-executed", $"{player.Class} spell {parameters.SpellInfo.Entry.Id}");
            foreach (uint generator in TooltipGenerators.For(parameters.SpellInfo.Entry.Id))
            {
                player.CastSpell(generator, new SpellParameters
                {
                    ParentSpellInfo        = parameters.SpellInfo,
                    RootSpellInfo          = parameters.RootSpellInfo ?? parameters.SpellInfo,
                    UserInitiatedSpellCast = false,
                    PrimaryTargetId        = player.Guid
                });
                Diagnostics.FailureLedger.Record("tooltip-generator", "generator cast after the ability", $"spell {parameters.SpellInfo.Entry.Id} -> {generator}");
            }
        }

        private void CostSpell()
        {
            if (parameters.CharacterSpell?.MaxAbilityCharges > 0)
                parameters.CharacterSpell.UseCharge();

            if (caster is Player player && parameters.UserInitiatedSpellCast)
                InnateCost.Charge(player, parameters.SpellInfo.Entry);
        }

        private void SelectTargets()
        {
            targets.Add(new SpellTargetInfo(SpellEffectTargetFlags.Caster, caster));

            // player casts carry no explicit target, use the current one (assumes flag 0x02 means "primary target")
            if (Combat.CombatConfig.Enabled)
            {
                uint primaryId = parameters.PrimaryTargetId != 0u ? parameters.PrimaryTargetId : caster.TargetGuid;
                UnitEntity primary = primaryId != 0u && primaryId != caster.Guid ? caster.GetVisible<WorldEntity>(primaryId) as UnitEntity : null;
                // a dead primary target only for spells that resurrect it
                if (primary != null && (!primary.IsDead || parameters.SpellInfo.Effects.Any(e => e.EffectType == (uint)SpellEffectType.Resurrect)))
                    targets.Add(new SpellTargetInfo(SpellEffectTargetFlags.Unknown02, primary));
            }

            if (caster is Player)
                InitialiseTelegraphs();

            // Spell4AoeTargetConstraints.TargetCount caps how many units the telegraphs hit (certain for the field: the tooltip macro $(aoeTc),
            // Telekinetic Strike "Deal damage to $(aoeTc) foes" has 5; all 690 rows are 1-40). Before every unit in the shape was hit:
            // Esper Mending Banner (count 1) proxied its heals onto ~100 units per cast and filled the caster's 400 pending spells.
            // Which units are kept (TargetSelection 1-5) is not decoded: the nearest to the caster (guess, docs/ASSUMPTIONS.md).
            // (a unit inside two telegraphs of the spell is still hit once per telegraph, as before)
            var telegraphTargets = new List<UnitEntity>();
            foreach (Telegraph telegraph in telegraphs)
                telegraphTargets.AddRange(telegraph.GetTargets());

            uint aoeCap = parameters.SpellInfo.AoeTargetConstraints?.TargetCount ?? 0u;
            List<UnitEntity> distinct = telegraphTargets.Distinct().ToList();
            if (aoeCap > 0u && distinct.Count > aoeCap)
            {
                var kept = new HashSet<UnitEntity>(distinct.OrderBy(e => System.Numerics.Vector3.DistanceSquared(e.Position, caster.Position)).Take((int)aoeCap));
                telegraphTargets = telegraphTargets.Where(kept.Contains).ToList();
                Diagnostics.FailureLedger.Record("aoe-target-cap", $"telegraph targets capped at {aoeCap} (Spell4AoeTargetConstraints)", $"spell {parameters.SpellInfo.Entry.Id}");
            }

            foreach (UnitEntity entity in telegraphTargets)
                targets.Add(new SpellTargetInfo(SpellEffectTargetFlags.Telegraph, entity));
        }

        private void ExecuteEffects()
        {
            foreach (Spell4EffectsEntry spell4EffectsEntry in parameters.SpellInfo.Effects)
            {
                // dungeon spells carry a normal and a veteran copy of their rows, told apart by prerequisite type 143
                if (!DifficultyAllows(spell4EffectsEntry))
                    continue;

                // select targets for effect
                List<SpellTargetInfo> effectTargets = targets
                    .Where(t => (t.Flags & (SpellEffectTargetFlags)spell4EffectsEntry.TargetFlags) != 0)
                    .ToList();

                // an activation is about the caster and the activated object, which the handler looks up itself
                if (caster is Player
                    && effectTargets.Count == 0
                    && (SpellEffectType)spell4EffectsEntry.EffectType == SpellEffectType.Activate)
                {
                    effectTargets.Add(targets.First(t => t.Flags == SpellEffectTargetFlags.Caster));
                }

                // effect target flags are not fully understood, so a damage effect that matched nobody still hits the selected target
                if (Combat.CombatConfig.Enabled
                    && caster is Player
                    && effectTargets.Count == 0
                    && (SpellEffectType)spell4EffectsEntry.EffectType == SpellEffectType.Damage)
                {
                    SpellTargetInfo primaryTarget = targets.FirstOrDefault(t => t.Flags == SpellEffectTargetFlags.Unknown02);
                    if (primaryTarget != null)
                        effectTargets.Add(primaryTarget);
                }

                // a tab-targeted damage effect always reaches the selected target, telegraph shapes are unreliable here
                if (Combat.CombatConfig.Enabled
                    && caster is Player
                    && (SpellEffectType)spell4EffectsEntry.EffectType == SpellEffectType.Damage)
                {
                    SpellTargetInfo primaryTarget = targets.FirstOrDefault(t => t.Flags == SpellEffectTargetFlags.Unknown02);
                    if (primaryTarget != null && effectTargets.All(t => t.Entity != primaryTarget.Entity))
                        effectTargets.Add(primaryTarget);
                }

                // a Proxy row's DataBits04 caps how many of the matched targets it is applied to (likely: Telekinetic Strike 32893 "Deal damage
                // to $(aoeTc) foes and generate a Psi Point" proxies 30900 "+1 Psi Point" with DataBits04 1; 0xFFFFFFFF = every target)
                if ((SpellEffectType)spell4EffectsEntry.EffectType == SpellEffectType.Proxy
                    && spell4EffectsEntry.DataBits04 > 0u && spell4EffectsEntry.DataBits04 < 1000u
                    && effectTargets.Count > spell4EffectsEntry.DataBits04)
                {
                    effectTargets = effectTargets.Take((int)spell4EffectsEntry.DataBits04).ToList();
                    Diagnostics.FailureLedger.Record("proxy-target-cap", $"proxy applied to {spell4EffectsEntry.DataBits04} of the matched targets", $"spell {parameters.SpellInfo.Entry.Id}");
                }

                // proxies on telegraph targets (TargetFlags 4) of player casts (class builders: Telekinetic Strike -> 30900 "+1 Psi Point").
                // When the telegraph matched nobody, the proxy lands on the selected target, like the Damage rows above that always reach it
                // (same guess as COMBAT-MATH step 1: a hit on the tab target counts as a telegraph hit).
                if (Combat.CombatConfig.Enabled && caster is Player && (SpellEffectType)spell4EffectsEntry.EffectType == SpellEffectType.Proxy
                    && (spell4EffectsEntry.TargetFlags & (uint)SpellEffectTargetFlags.Telegraph) != 0u)
                {
                    if (effectTargets.Count > 0)
                        Diagnostics.FailureLedger.Record("proxy-telegraph", "player telegraph proxy landed on a telegraph target", $"spell {parameters.SpellInfo.Entry.Id}");
                    else
                    {
                        SpellTargetInfo primaryTarget = targets.FirstOrDefault(t => t.Flags == SpellEffectTargetFlags.Unknown02);
                        Diagnostics.FailureLedger.Record("proxy-telegraph", primaryTarget != null ? "telegraph matched nobody, proxy on the selected target" : $"telegraph matched nobody, no selected target ({telegraphs.Count} telegraphs)", $"spell {parameters.SpellInfo.Entry.Id}");
                        if (primaryTarget != null)
                            effectTargets.Add(primaryTarget);
                    }
                }

                if (Combat.CombatConfig.Enabled && caster is Player humanCaster && humanCaster.Session is not NexusForever.WorldServer.Bots.BotSession)
                    log.Info($"Combat: spell {parameters.SpellInfo.Entry.Id} effect {(SpellEffectType)spell4EffectsEntry.EffectType} flags {spell4EffectsEntry.TargetFlags} -> {effectTargets.Count} target(s) of {targets.Count} (selected {caster.TargetGuid})");

                SpellEffectDelegate handler = GlobalSpellManager.Instance.GetEffectHandler((SpellEffectType)spell4EffectsEntry.EffectType);
                if (handler == null)
                {
                    // count instead of warning on every cast, 'gt effects' prints the totals
                    var effectType = (SpellEffectType)spell4EffectsEntry.EffectType;
                    UnhandledEffects.AddOrUpdate(effectType, (1, parameters.SpellInfo.Entry.Id), (_, old) => (old.Count + 1, old.ExampleSpellId));
                    Diagnostics.FailureLedger.Record("unhandled-spell-effect", effectType.ToString(), $"spell {parameters.SpellInfo.Entry.Id}");
                    if (UnhandledEffects[effectType].Count == 1)
                        log.Warn($"Unhandled spell effect {effectType} (first seen on spell {parameters.SpellInfo.Entry.Id}), further occurrences are only counted.");
                }
                else
                {
                    uint effectId = GlobalSpellManager.Instance.NextEffectId;
                    foreach (SpellTargetInfo effectTarget in effectTargets)
                    {
                        var info = new SpellTargetInfo.SpellTargetEffectInfo(effectId, spell4EffectsEntry);
                        effectTarget.Effects.Add(info);

                        // TODO: if there is an unhandled exception in the handler, there will be an infinite loop on Execute()
                        handler.Invoke(this, effectTarget.Entity, info);
                        SchedulePeriodic(handler, effectTarget.Entity, spell4EffectsEntry);

                        // effects that last are remembered on the target, so they can be removed or asked about later
                        if (spell4EffectsEntry.DurationTime > 0u && effectTarget.Entity != null)
                            effectTarget.Entity.AddAura(parameters.SpellInfo.Entry.Id, spell4EffectsEntry.EffectType, spell4EffectsEntry.DurationTime / 1000d);
                    }
                }
            }
        }

        /// <summary>
        /// Damage and Heal rows with a tick time apply their amount once per tick for the duration: tooltips read "Deals $(eff.input0pct) damage
        /// every $(eff.tickTimeMS) seconds for $(eff.durationMS) seconds" (spell 33287, certain that the amount is per tick). 2143 Damage and
        /// 715 Heal rows tick. The cast applies the first tick, the others follow every tick time (duration / tick ticks in total; whether
        /// the first tick lands at the cast or after one interval is a guess). Ticks stop when the target dies or leaves the caster's map.
        /// </summary>
        /// <summary>
        /// Effect rows gated by prerequisite type 143 (instance veteran mode, rows 14409 = 0 / 14893 = 1) apply only in the matching mode
        /// of the caster's map. Effect prerequisites used to be ignored and both copies landed (normal x1 + veteran x~1.5).
        /// Only the type 143 terms are evaluated here; the other terms of effect prerequisites are still not checked (counted in the ledger).
        /// </summary>
        private bool DifficultyAllows(Spell4EffectsEntry entry)
        {
            foreach (uint prerequisiteId in new[] { entry.PrerequisiteIdCasterApply, entry.PrerequisiteIdTargetApply })
            {
                if (prerequisiteId == 0u)
                    continue;
                PrerequisiteEntry row = GameTableManager.Instance.Prerequisite.GetEntry(prerequisiteId);
                if (row == null)
                    continue;

                bool any143 = false, anyMet = false, allMet = true, others = false;
                for (int i = 0; i < row.PrerequisiteTypeId.Length; i++)
                {
                    if (row.PrerequisiteTypeId[i] == 0u)
                        continue;
                    if (row.PrerequisiteTypeId[i] != (uint)Prerequisite.Static.PrerequisiteType.InstanceVeteran)
                    {
                        others = true;
                        continue;
                    }
                    any143 = true;
                    bool met = PrerequisiteManager.CompareInstanceVeteran(caster.Map, (Prerequisite.Static.PrerequisiteComparison)row.PrerequisiteComparisonId[i], row.Value[i]);
                    anyMet |= met;
                    allMet &= met;
                }
                if (!any143)
                    continue;

                // OR rows with other terms could still pass through those terms, which are not evaluated here
                bool pass = (Prerequisite.Static.EvaluationMode)row.Flags == Prerequisite.Static.EvaluationMode.EvaluateOR ? anyMet || others : allMet;
                if (!pass)
                {
                    Diagnostics.FailureLedger.Record("effect-difficulty-gate", "row of the other difficulty skipped", $"prerequisite {prerequisiteId}");
                    return false;
                }
            }
            return true;
        }

        private void SchedulePeriodic(SpellEffectDelegate handler, UnitEntity target, Spell4EffectsEntry entry)
        {
            var type = (SpellEffectType)entry.EffectType;
            if (target == null || entry.TickTime == 0u || entry.DurationTime <= entry.TickTime || (type != SpellEffectType.Damage && type != SpellEffectType.Heal))
                return;

            uint ticks = Math.Min(entry.DurationTime / entry.TickTime, 60u);
            for (uint tick = 1u; tick < ticks; tick++)
                events.EnqueueEvent(new SpellEvent(tick * entry.TickTime / 1000d, () =>
                {
                    if (target.IsDead || target.Map == null || target.Map != caster.Map)
                        return;
                    try
                    {
                        handler.Invoke(this, target, new SpellTargetInfo.SpellTargetEffectInfo(GlobalSpellManager.Instance.NextEffectId, entry));
                        Diagnostics.FailureLedger.Record("combat-stats", $"periodic {type} tick");
                    }
                    catch (Exception exception)
                    {
                        Diagnostics.FailureLedger.Record("spell-tick-exception", $"spell {entry.SpellId} {type}: {exception.GetType().Name}", (exception.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
                    }
                }));
        }

        public bool IsMovingInterrupted()
        {
            // TODO: implement correctly
            return parameters.SpellInfo.Entry.CastTime > 0;
        }

        /// <summary>Result of the last refused cast on this thread (bot diagnostics: forced test bots name it in their trace).</summary>
        [ThreadStatic] public static CastResult? LastFailure;

        private void SendSpellCastResult(CastResult castResult)
        {
            if (castResult == CastResult.Ok)
                return;

            LastFailure = castResult;

            log.Trace($"Spell {parameters.SpellInfo.Entry.Id} failed to cast {castResult}.");
            Diagnostics.FailureLedger.Record("cast-failed", $"{castResult} spell {parameters.SpellInfo.Entry.Id}", caster is Player { Session: Bots.BotSession } ? (parameters.UserInitiatedSpellCast ? "bot" : "bot-child") : caster is Player ? "human" : "creature");

            if (caster is Player player && !player.IsLoading)
            {
                player.Session.EnqueueMessageEncrypted(new ServerSpellCastResult
                {
                    Spell4Id   = parameters.SpellInfo.Entry.Id,
                    CastResult = castResult
                });
            }
        }

        private void SendSpellStart()
        {
            var spellStart = new ServerSpellStart
            {
                CastingId              = CastingId,
                CasterId               = caster.Guid,
                PrimaryTargetId        = caster.Guid,
                Spell4Id               = parameters.SpellInfo.Entry.Id,
                RootSpell4Id           = parameters.RootSpellInfo?.Entry.Id ?? 0,
                ParentSpell4Id         = parameters.ParentSpellInfo?.Entry.Id ?? 0,
                FieldPosition          = new Position(caster.Position),
                Yaw                    = caster.Rotation.X,
                UserInitiatedSpellCast = parameters.UserInitiatedSpellCast,
                InitialPositionData    = new List<ServerSpellStart.InitialPosition>(),
                TelegraphPositionData  = new List<ServerSpellStart.TelegraphPosition>()
            };

            var unitsCasting = new List<UnitEntity>();
            if (parameters.PrimaryTargetId > 0)
                unitsCasting.Add(caster.GetVisible<UnitEntity>(parameters.PrimaryTargetId) ?? caster);
            else
                unitsCasting.Add(caster);

            foreach (UnitEntity unit in unitsCasting)
            {
                spellStart.InitialPositionData.Add(new ServerSpellStart.InitialPosition
                {
                    UnitId      = unit.Guid,
                    Position    = new Position(unit.Position),
                    TargetFlags = 3,
                    Yaw         = unit.Rotation.X
                });
            }

            foreach (UnitEntity unit in unitsCasting)
            {
                foreach (Telegraph telegraph in telegraphs)
                {
                    spellStart.TelegraphPositionData.Add(new ServerSpellStart.TelegraphPosition
                    {
                        TelegraphId    = (ushort)telegraph.TelegraphDamage.Id,
                        AttachedUnitId = unit.Guid,
                        TargetFlags    = 3,
                        Position       = new Position(telegraph.Position),
                        Yaw            = telegraph.Rotation.X
                    });
                }
            }

            caster.EnqueueToVisible(spellStart, true);
        }

        private void SendSpellFinish()
        {
            if (status != SpellStatus.Finished)
                return;

            caster.EnqueueToVisible(new ServerSpellFinish
            {
                ServerUniqueId = CastingId,
            }, true);
        }

        private void SendSpellGo()
        {
            var serverSpellGo = new ServerSpellGo
            {
                ServerUniqueId     = CastingId,
                PrimaryDestination = new Position(caster.Position),
                Phase              = -1
            };

            foreach (SpellTargetInfo targetInfo in targets
                .Where(t => t.Effects.Count > 0))
            {
                var networkTargetInfo = new TargetInfo
                {
                    UnitId        = targetInfo.Entity.Guid,
                    TargetFlags   = 1,
                    InstanceCount = 1,
                    CombatResult  = CombatResult.Hit
                };

                foreach (SpellTargetInfo.SpellTargetEffectInfo targetEffectInfo in targetInfo.Effects)
                {
                    var networkTargetEffectInfo = new TargetInfo.EffectInfo
                    {
                        Spell4EffectId = targetEffectInfo.Entry.Id,
                        EffectUniqueId = targetEffectInfo.EffectId,
                        TimeRemaining  = -1
                    };

                    if (targetEffectInfo.Damage != null)
                    {
                        networkTargetEffectInfo.InfoType = 1;
                        networkTargetEffectInfo.DamageDescriptionData = new TargetInfo.EffectInfo.DamageDescription
                        {
                            RawDamage          = targetEffectInfo.Damage.RawDamage,
                            RawScaledDamage    = targetEffectInfo.Damage.RawScaledDamage,
                            AbsorbedAmount     = targetEffectInfo.Damage.AbsorbedAmount,
                            ShieldAbsorbAmount = targetEffectInfo.Damage.ShieldAbsorbAmount,
                            AdjustedDamage     = targetEffectInfo.Damage.AdjustedDamage,
                            OverkillAmount     = targetEffectInfo.Damage.OverkillAmount,
                            KilledTarget       = targetEffectInfo.Damage.KilledTarget,
                            CombatResult       = CombatResult.Hit,
                            DamageType         = targetEffectInfo.Damage.DamageType
                        };
                    }

                    networkTargetInfo.EffectInfoData.Add(networkTargetEffectInfo);
                }

                serverSpellGo.TargetInfoData.Add(networkTargetInfo);
            }

            var unitsCasting = new List<UnitEntity>
            {
                caster
            };

            foreach (UnitEntity unit in unitsCasting)
            {
                serverSpellGo.InitialPositionData.Add(new InitialPosition
                {
                    UnitId      = unit.Guid,
                    Position    = new Position(unit.Position),
                    TargetFlags = 3,
                    Yaw         = unit.Rotation.X
                });
            }

            foreach (UnitEntity unit in unitsCasting)
            {
                foreach (Telegraph telegraph in telegraphs)
                {
                    serverSpellGo.TelegraphPositionData.Add(new TelegraphPosition
                    {
                        TelegraphId    = (ushort)telegraph.TelegraphDamage.Id,
                        AttachedUnitId = unit.Guid,
                        TargetFlags    = 3,
                        Position       = new Position(telegraph.Position),
                        Yaw            = telegraph.Rotation.X
                    });
                }
            }

            caster.EnqueueToVisible(serverSpellGo, true);
        }

        private void SendRemoveBuff(uint unitId)
        {
            if (!parameters.SpellInfo.BaseInfo.HasIcon)
                throw new InvalidOperationException();

            caster.EnqueueToVisible(new ServerSpellBuffRemove
            {
                CastingId = CastingId,
                CasterId  = unitId
            }, true);
        }
    }
}
