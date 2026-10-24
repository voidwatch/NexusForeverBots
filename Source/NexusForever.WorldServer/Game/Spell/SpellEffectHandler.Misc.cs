using System;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Reputation.Static;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Effects seen in the soak runs that had no handler: random proxies, temporary reputation, unit states and action bar swaps.
    /// </summary>
    public partial class Spell
    {
        private static readonly Random ProxyRng = new();

        // DataBits00/01, 02/03, 04/05... = (spell to cast, weight) pairs, exactly one is cast (Skash "Death Gaze": 63467 at 50, 63468 at 50).
        [SpellEffectHandler(SpellEffectType.ProxyRandomExclusive)]
        private void HandleEffectProxyRandomExclusive(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            uint[] bits = { info.Entry.DataBits00, info.Entry.DataBits01, info.Entry.DataBits02, info.Entry.DataBits03, info.Entry.DataBits04, info.Entry.DataBits05 };
            ProxyWeighted(target, info, bits);
        }

        // ASSUMPTION (docs/ASSUMPTIONS.md): same layout as ProxyRandomExclusive, (spell, weight) pairs in DataBits00-09, one picked per tick of the channel.
        [SpellEffectHandler(SpellEffectType.ProxyChannelVariableTime)]
        private void HandleEffectProxyChannelVariableTime(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            uint[] bits =
            {
                info.Entry.DataBits00, info.Entry.DataBits01, info.Entry.DataBits02, info.Entry.DataBits03, info.Entry.DataBits04,
                info.Entry.DataBits05, info.Entry.DataBits06, info.Entry.DataBits07, info.Entry.DataBits08, info.Entry.DataBits09
            };
            ProxyWeighted(target, info, bits);
        }

        private void ProxyWeighted(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info, uint[] bits)
        {
            uint total = 0u;
            for (int i = 0; i + 1 < bits.Length; i += 2)
                if (bits[i] != 0u)
                    total += Math.Max(1u, bits[i + 1]);

            if (total == 0u)
                return;

            uint roll = (uint)ProxyRng.Next((int)total);
            for (int i = 0; i + 1 < bits.Length; i += 2)
            {
                if (bits[i] == 0u)
                    continue;

                uint weight = Math.Max(1u, bits[i + 1]);
                if (roll >= weight)
                {
                    roll -= weight;
                    continue;
                }

                // a dead creature neither ticks nor proxies effects
                if (target.IsDead && target is not Player)
                    return;

                if (GameTableManager.Instance.Spell4.GetEntry(bits[i]) == null)
                {
                    Diagnostics.FailureLedger.Record("proxy-spell-missing", $"spell4 {bits[i]} not in the tables", $"spell {info.Entry.SpellId}");
                    return;
                }

                target.CastSpell(bits[i], new SpellParameters
                {
                    ParentSpellInfo        = parameters.SpellInfo,
                    RootSpellInfo          = parameters.RootSpellInfo,
                    UserInitiatedSpellCast = false
                });
                return;
            }
        }

        // DataBits00 = faction, DataBits01 = float amount. ASSUMPTION: an effect with a duration is a temporary disposition change
        // (Grimvault "Bone Cage" hazard, 10 s, amount 50000) and not a permanent reputation grant, so only permanent ones are applied.
        [SpellEffectHandler(SpellEffectType.ReputationModify)]
        private void HandleEffectReputationModify(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            var faction = (Faction)info.Entry.DataBits00;
            float amount = Float(info.Entry.DataBits01);
            bool applied = false;

            if (target is Player player && info.Entry.DurationTime == 0u && NexusForever.WorldServer.Game.Reputation.FactionManager.Instance.GetFaction(faction) != null)
            {
                player.ReputationManager.UpdateReputation(faction, amount);
                applied = true;
            }

            Diagnostics.FailureLedger.Record("effect-stored", $"ReputationModify faction {(uint)faction} {(applied ? "applied" : "not applied (temporary or not a player)")}", $"spell {parameters.SpellInfo.Entry.Id}, amount {amount}");
        }

        // DataBits00 = unit state id (6 in the Canimid "Awful Auger" knock, 1 s). Meaning not settled, stored for the report.
        [SpellEffectHandler(SpellEffectType.UnitStateSet)]
        private void HandleEffectUnitStateSet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-stored", $"UnitStateSet state {info.Entry.DataBits00} not applied (state meaning undecoded)", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // DataBits00 = action bar set id (502/503 in the Grimvault socket spells): swaps the player's bar for the duration, a client UI state.
        [SpellEffectHandler(SpellEffectType.ActionBarSet)]
        private void HandleEffectActionBarSet(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            Diagnostics.FailureLedger.Record("effect-stored", $"ActionBarSet set {info.Entry.DataBits00} not applied (client action bar, packet unknown)", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // Explorer dig ("Searching", spells 27712-27714): buried scavenger hunt clues within their radius of the caster are found
        [SpellEffectHandler(SpellEffectType.PathActionExplorerDig)]
        private void HandleEffectPathActionExplorerDig(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (caster is Player player)
                player.PathMissionManager.OnDig(player.Position);
            Diagnostics.FailureLedger.Record("path-explorer-dig", caster is Player ? "player dig" : "non-player caster", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // Transference ("Drain Life", 402 of 559 rows have DataBits00 1). ASSUMPTION (docs/ASSUMPTIONS.md): DataBits00 = vital drained (1 health;
        // 3 reads "Drain Energy" in spell names, resource ids undecoded so it is recorded), DataBits02 = coefficient like Damage (x spell damage
        // for the caster's level), DataBits05 = share given to the caster (capped at 1; spell 2010 "Gives to user" has 100f).
        [SpellEffectHandler(SpellEffectType.Transference)]
        private void HandleEffectTransference(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (info.Entry.DataBits00 != 1u || !Combat.CombatConfig.Enabled)
            {
                Diagnostics.FailureLedger.Record("effect-stored", $"Transference vital {info.Entry.DataBits00} not applied (resource ids undecoded)", $"spell {parameters.SpellInfo.Entry.Id}");
                return;
            }

            if (target == caster || target.IsDead || caster.IsDead)
                return;
            if (caster is Player == target is Player)
                return;

            float coefficient = System.BitConverter.Int32BitsToSingle((int)info.Entry.DataBits02);
            float share = System.BitConverter.Int32BitsToSingle((int)info.Entry.DataBits05);
            if (!(coefficient > 0f) || float.IsInfinity(coefficient))
                coefficient = 1f;
            if (!(share > 0f) || float.IsNaN(share))
                share = 1f;
            share = System.Math.Min(share, 1f);

            float? formula = Combat.SpellFormula.Amount(info.Entry, caster, target);
            uint amount = (uint)System.Math.Max(1d, System.Math.Round(formula ?? coefficient * Combat.CombatConfig.SpellDamage(caster.Level)));
            target.TakeDamage(caster, amount, out uint overkill);
            uint gained = (uint)System.Math.Round((amount - overkill) * share);
            if (gained > 0u)
                caster.Heal(gained, caster);
        }

        // ASSUMPTION (docs/ASSUMPTIONS.md): DamageShields removes the formula amount (Damage-style parameters) from the target's shield; the rune
        // "Supercharged" specials pair it with a Damage effect and target the caster (TargetFlags 1), i.e. they spend shield for damage.
        [SpellEffectHandler(SpellEffectType.DamageShields)]
        private void HandleEffectDamageShields(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (!Combat.CombatConfig.Enabled || target.IsDead || target is not Player player)
                return;

            float? formula = Combat.SpellFormula.Amount(info.Entry, caster, target);
            player.DrainShield((uint)System.Math.Max(0d, System.Math.Round(formula ?? 0f)));
        }

        // DataBits00 = mode. ASSUMPTION (docs/ASSUMPTIONS.md): 2 = drop the caster's threat on the target (spell 34298 "Stalker - Drop Threat for
        // Tactical Retreat", Avatus "Fixate Move" uses 2 as well); 0 and 4 (taunt/fixate/add, amount in DataBits02) have no threat table to act on
        // because creatures keep a single target, so they are recorded.
        [SpellEffectHandler(SpellEffectType.ThreatModification)]
        private void HandleEffectThreatModification(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            if (info.Entry.DataBits00 == 2u && target is NonPlayer creature)
            {
                creature.DropThreat(caster);
                return;
            }

            Diagnostics.FailureLedger.Record("effect-stored", $"ThreatModification mode {info.Entry.DataBits00} not applied (no threat table)", $"spell {parameters.SpellInfo.Entry.Id}");
        }

        // Hazard meters (docs/ASSUMPTIONS.md "Hazards"): HazardEnable DataBits00 = Hazard id (20/20 rows), HazardModify DataBits04 = Hazard id
        // (49/49) with the amount in DataBits03 (float, e.g. "Heat Dissipation" -3 per tick), HazardSuspend DataBits00 = Hazard id (20/20); certain
        // for the ids. The meter itself (when the damage spell and the threshold procs fire) and the meter value message are not decoded, so the
        // effects are recorded with the hazard they name and nothing is applied.
        [SpellEffectHandler(SpellEffectType.HazardEnable)]
        private void HandleEffectHazardEnable(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteHazard("HazardEnable", info.Entry.DataBits00, info);

        [SpellEffectHandler(SpellEffectType.HazardModify)]
        private void HandleEffectHazardModify(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteHazard("HazardModify", info.Entry.DataBits04, info);

        [SpellEffectHandler(SpellEffectType.HazardSuspend)]
        private void HandleEffectHazardSuspend(UnitEntity target, SpellTargetInfo.SpellTargetEffectInfo info) => NoteHazard("HazardSuspend", info.Entry.DataBits00, info);

        private void NoteHazard(string name, uint hazardId, SpellTargetInfo.SpellTargetEffectInfo info)
        {
            HazardEntry hazard = GameTableManager.Instance.Hazard.GetEntry(hazardId);
            string label = hazard == null ? "unknown hazard" : $"type {hazard.HazardTypeEnum} {GameTableManager.Instance.TextEnglish.GetEntry(hazard.LocalizedTextIdName)}";
            Diagnostics.FailureLedger.Record("effect-stored", $"{name} recorded, hazard meter not simulated", $"spell {parameters.SpellInfo.Entry.Id} hazard {hazardId} ({label})");
        }
    }
}
