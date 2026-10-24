using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Database.World.Model;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Combat;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Game.Spell;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Game.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    public abstract class UnitEntity : WorldEntity
    {
        private readonly List<Spell.Spell> pendingSpells = new();

        /// <summary>Whether a cast of this unit is in progress.</summary>
        public bool IsCasting() => pendingSpells.Any(s => s.IsCasting);

        /// <summary>Spells held by the unit (casting, executing or waiting for their effects), for the memory line.</summary>
        public int PendingSpellCount => pendingSpells.Count;

        public float HitRadius { get; protected set; } = 1f;

        protected UnitEntity(EntityType type)
            : base(type)
        {
            InitialiseHitRadius();
        }

        public override void Initialise(EntityModel model)
        {
            base.Initialise(model);
        }

        /// <summary>
        /// True between <see cref="Kill"/> and <see cref="Revive"/>.
        /// </summary>
        public bool IsDead { get; private set; }

        /// <summary>Set by an encounter phase (adds phases): the unit takes no damage.</summary>
        public bool ScriptedInvulnerable { get; set; }

        /// <summary>
        /// Set health and tell everyone who can see the entity.
        /// </summary>
        public void SetHealth(uint value)
        {
            SetStat(Stat.Health, value);

            // health is not broadcast by SetStat, send it explicitly
            EnqueueToVisible(new ServerEntityStatUpdateInteger
            {
                UnitId = Guid,
                Stat   = new StatValue(Stat.Health, value)
            }, true);

            // the stat update alone does not redraw other units' health bars in the client; this message does
            EnqueueToVisible(new ServerEntityHealthUpdate
            {
                UnitId = Guid,
                Health = value
            }, true);
        }

        /// <summary>
        /// Remove health from the entity, killing it at zero. Returns true if this hit killed the entity.
        /// </summary>
        public bool TakeDamage(WorldEntity source, uint amount, out uint overkill)
        {
            overkill = 0u;
            if (IsDead || amount == 0u)
                return false;

            // personal modifiers: debuffs of the attacker's own spells on this unit (Analyze Weakness) and the attacker's debuffs on its target
            if (source != null)
            {
                float personal = PersonalFactor(source.Guid, 1u, false) * ((source as UnitEntity)?.PersonalFactor(Guid, 2u, false) ?? 1f);
                if (personal != 1f)
                    amount = (uint)System.Math.Max(1d, System.Math.Round(amount * personal));
            }

            if (source is UnitEntity attacker && attacker != this)
            {
                MarkInCombat();
                attacker.MarkInCombat();
            }

            if (Bots.BotManager.Immortal && this is Player { Session: Bots.BotSession })
                return false;

            if (IsDamageImmune || ScriptedInvulnerable)
            {
                Diagnostics.FailureLedger.Record("cc-state", "damage blocked by immunity", CreatureId.ToString());
                return false;
            }

            // CC states that break on damage (CCStates.defaultBreakProbability: Sleep 1.0, every other state 0)
            if (ccStates.Count > 0)
                foreach (uint state in ccStates.Keys.ToArray())
                    if (CCBreakChance(state) > 0f && breakRandom.NextDouble() < CCBreakChance(state))
                        ccStates.Remove(state);

            if (this is Player shielded)
            {
                amount = shielded.AbsorbWithShield(amount, source);
                if (amount == 0u)
                    return false;
            }

            uint health = Health;
            if (amount >= health)
            {
                overkill = amount - health;
                SetHealth(0u);
                Kill(source);
                return true;
            }

            SetHealth(health - amount);
            OnDamaged(source, amount);
            FireProcs(16u, source);
            return false;
        }

        /// <summary>
        /// Add health to the entity, capped at <see cref="WorldEntity.MaxHealth"/>.
        /// </summary>
        public void Heal(uint amount, WorldEntity source = null)
        {
            if (source != null)
                amount = (uint)System.Math.Max(1d, System.Math.Round(amount * PersonalFactor(source.Guid, 1u, true)));

            uint max = MaxHealth;
            if (IsDead || max == 0u || amount == 0u)
                return;

            SetHealth(System.Math.Min(max, Health + amount));
        }

        public void Kill(WorldEntity killer)
        {
            if (IsDead)
                return;

            // trigger 0 "on death" (self destruct, "if your target dies"), fired while the unit can still cast
            var preDeathSpells = pendingSpells.ToArray();
            FireProcs(0u, killer);

            IsDead = true;
            EnqueueToVisible(new ServerEntityDeathState
            {
                UnitId    = Guid,
                Dead      = true,
                Reason    = 0,
                RezHealth = 0u
            }, true);

            // a creature's casts in progress end with it
            if (this is not Player)
                foreach (Spell.Spell spell in preDeathSpells)
                {
                    if (spell.IsCasting)
                        spell.CancelCast(Spell.Static.CastResult.CasterCannotBeDead);
                    spell.AbortOnCasterDeath();
                }

            OnDeath(killer);
            (killer as UnitEntity)?.FireProcs(1u, this);
        }

        /// <summary>
        /// Bring a dead entity back with the supplied health.
        /// </summary>
        public void Revive(uint health)
        {
            if (!IsDead && Health > 0u)
                return;

            IsDead = false;
            SetHealth(health);
            EnqueueToVisible(new ServerEntityDeathState
            {
                UnitId    = Guid,
                Dead      = false,
                Reason    = 0,
                RezHealth = health
            }, true);

            OnRevive();
        }

        protected virtual void OnDamaged(WorldEntity source, uint amount)
        {
        }

        protected virtual void OnDeath(WorldEntity killer)
        {
        }

        protected virtual void OnRevive()
        {
        }

        private void InitialiseHitRadius()
        {
            if (CreatureId == 0u)
                return;

            Creature2Entry creatureEntry = GameTableManager.Instance.Creature2.GetEntry(CreatureId);
            if (creatureEntry == null)
                return;

            Creature2ModelInfoEntry modelInfoEntry = GameTableManager.Instance.Creature2ModelInfo.GetEntry(creatureEntry.Creature2ModelInfoId);
            if (modelInfoEntry != null)
                HitRadius = modelInfoEntry.HitRadius * creatureEntry.ModelScale;
        }

        /// <summary>
        /// A spell effect that lasts, remembered until it runs out or is removed.
        /// </summary>
        public sealed class ActiveAura
        {
            public uint Spell4Id;
            public uint EffectType;
            public double Remaining;
        }

        /// <summary>
        /// A property change from a spell, remembered with its raw data until the spell runs out. The meaning of <see cref="Type"/> is
        /// not settled yet (see gt propmod), so these are stored and reported but not applied to stats.
        /// </summary>
        public sealed class PropertyModifierEntry
        {
            public uint Spell4Id;
            public Property Property;
            public uint Type;
            public float ValueA;
            public float ValueB;
            public double Remaining;
            /// <summary>UnitPropertyConversion: <see cref="Property"/> gains <see cref="ValueA"/> x this property's value (DamageMath.Value); not a value x A + B row.</summary>
            public Property? ConvertFrom;
        }

        /// <summary>
        /// A proc registered by a spell: when <see cref="Trigger"/> happens the entity may cast <see cref="ProcSpell4Id"/>.
        /// Triggers decoded from 3498 proc rows, their spell names and tooltips (docs/ASSUMPTIONS.md, research/decode/proc-tooltips.txt):
        /// 0 on death, 1 on kill, 10 on cast, 12 and 30 on dealing damage ("chance on damage dealt", "chance on hit"), 16 when damaged,
        /// 20 on healing someone else, 21 on being healed, 114 shield takes damage, 115 shield depleted, 145 landing a critical hit,
        /// 146 suffering a critical hit, 147 landing a critical heal. Others (deflect, multi-hit, dash, block, resource thresholds) have no
        /// server event yet and are registered only.
        /// Mode (DataBits03) is a bit field: 4 = the proc spell is aimed at the other party (its effects target "primary target" in 554 of 700
        /// rows), 1 and 2 = aimed at the proc holder (target flag "caster" in 85%); 8/16/32 are not decoded (names say 10 "caster to target",
        /// 18 "target to caster").
        /// </summary>
        public sealed class ProcEntry
        {
            public uint Spell4Id;
            public uint Trigger;
            public uint ProcSpell4Id;
            public float Chance;
            public uint Mode;
            public uint CooldownMs;
            /// <summary>Unit that applied the proc (the caster of the spell that registered it).</summary>
            public uint ApplierGuid;
            public double Remaining;
            public double CooldownLeft;
        }

        private readonly List<PropertyModifierEntry> propertyModifiers = new();
        private readonly List<ProcEntry> procs = new();
        private readonly List<ActiveAura> auras = new();
        private readonly Dictionary<uint /*CCState*/, double /*seconds left*/> ccStates = new();
        private readonly Dictionary<uint /*immunity kind*/, double /*seconds left*/> immunities = new();

        // states in which a creature cannot act
        private static readonly uint[] incapacitating =
        {
            (uint)Spell.Static.CCState.Stun, (uint)Spell.Static.CCState.Sleep, (uint)Spell.Static.CCState.Polymorph, (uint)Spell.Static.CCState.Fear,
            (uint)Spell.Static.CCState.Hold, (uint)Spell.Static.CCState.Knockdown, (uint)Spell.Static.CCState.Disable, (uint)Spell.Static.CCState.Blind
        };

        public bool IsIncapacitated => incapacitating.Any(ccStates.ContainsKey);

        // kinds from SpellEffectImmunity data: 4 crowd control, 31 and 60 "immune to CC, force move, disguise and charm", 8 damage,
        // 32 invulnerable, 3 force move, 2 interrupt (spell counter), 29 healing
        public bool IsCrowdControlImmune => HasImmunity(4u) || HasImmunity(31u) || HasImmunity(60u);
        public bool IsDamageImmune => HasImmunity(8u) || HasImmunity(32u);
        public bool IsForceMoveImmune => HasImmunity(3u) || HasImmunity(31u) || HasImmunity(60u);

        public bool HasImmunity(uint kind) => immunities.ContainsKey(kind);

        public void AddImmunity(uint kind, double seconds)
        {
            WakeUp();
            immunities[kind] = Math.Max(seconds, immunities.TryGetValue(kind, out double left) ? left : 0d);
        }

        public bool HasCCState(uint state) => ccStates.ContainsKey(state);

        private static readonly Random breakRandom = new();
        private static float[] ccBreakChance;

        private static float CCBreakChance(uint state)
        {
            ccBreakChance ??= Enumerable.Range(0, 32)
                .Select(i => GameTableManager.Instance.CCStates?.GetEntry((ulong)i)?.DefaultBreakProbability ?? 0f)
                .ToArray();
            return state < ccBreakChance.Length ? ccBreakChance[state] : 0f;
        }

        public void SetCCState(uint state, double seconds)
        {
            WakeUp();
            ccStates[state] = Math.Max(seconds, ccStates.TryGetValue(state, out double left) ? left : 0d);
        }

        public int BreakCCStates(uint mask)
        {
            int cleared = 0;
            for (uint bit = 0u; bit < 32u; bit++)
                if ((mask & (1u << (int)bit)) != 0u && ccStates.Remove(bit))
                    cleared++;

            return cleared;
        }

        public void AddAura(uint spell4Id, uint effectType, double seconds)
        {
            WakeUp();
            ActiveAura existing = auras.FirstOrDefault(a => a.Spell4Id == spell4Id && a.EffectType == effectType);
            if (existing != null)
                existing.Remaining = Math.Max(existing.Remaining, seconds);
            else
                auras.Add(new ActiveAura { Spell4Id = spell4Id, EffectType = effectType, Remaining = seconds });
        }

        public int RemoveAuras(uint spell4Id)
        {
            return auras.RemoveAll(a => a.Spell4Id == spell4Id);
        }

        public bool HasAura(uint spell4Id)
        {
            return auras.Any(a => a.Spell4Id == spell4Id);
        }

        /// <summary>Any lasting effect (aura, property modifier, proc, personal modifier) of the spell is active.</summary>
        public bool HasEffectsOf(uint spell4Id)
        {
            return auras.Any(a => a.Spell4Id == spell4Id) || propertyModifiers.Any(m => m.Spell4Id == spell4Id)
                || procs.Any(p => p.Spell4Id == spell4Id) || personalMods.Any(m => m.Spell4Id == spell4Id) || spellMods.Any(m => m.Spell4Id == spell4Id);
        }

        /// <summary>Remove every lasting effect of the spell (used for passives that are switched off, e.g. AMPs); running casts of it stop too.</summary>
        public int RemoveSpellEffects(uint spell4Id)
        {
            foreach (Spell.Spell spell in pendingSpells)
                if (spell.Spell4Id == spell4Id && !spell.IsFinished)
                    spell.AbortOnCasterDeath();
            return auras.RemoveAll(a => a.Spell4Id == spell4Id) + propertyModifiers.RemoveAll(m => m.Spell4Id == spell4Id)
                + procs.RemoveAll(p => p.Spell4Id == spell4Id) + personalMods.RemoveAll(m => m.Spell4Id == spell4Id) + spellMods.RemoveAll(m => m.Spell4Id == spell4Id);
        }

        /// <summary>Spells with a lasting effect on the unit (auras, property modifiers, procs, personal and spell modifiers), oldest first.</summary>
        public List<uint> ActiveEffectSpellIds()
        {
            return auras.Select(a => a.Spell4Id).Concat(propertyModifiers.Select(m => m.Spell4Id)).Concat(procs.Select(p => p.Spell4Id))
                .Concat(personalMods.Select(m => m.Spell4Id)).Concat(spellMods.Select(m => m.Spell4Id)).Distinct().ToList();
        }

        /// <summary>An aura of the spell4 id, or of any tier of the spell base with that id.</summary>
        public bool HasAuraOrBase(uint id)
        {
            return auras.Any(a => a.Spell4Id == id || GameTableManager.Instance.Spell4.GetEntry(a.Spell4Id)?.Spell4BaseIdBaseSpell == id);
        }

        /// <summary>
        /// An active effect (aura, property modifier or proc) from a spell of the spell group (Spell4GroupList membership, see Spell.SpellGroups).
        /// </summary>
        public bool HasAuraInGroup(uint groupId)
        {
            static bool InGroup(uint spell4Id, uint group) => SpellGroups.InGroup(GameTableManager.Instance.Spell4.GetEntry(spell4Id), group);
            return auras.Any(a => InGroup(a.Spell4Id, groupId))
                || propertyModifiers.Any(m => InGroup(m.Spell4Id, groupId))
                || procs.Any(p => InGroup(p.Spell4Id, groupId));
        }

        public bool HasAuraOfEffect(uint effectType)
        {
            return auras.Any(a => a.EffectType == effectType);
        }

        /// <summary>
        /// Stealth is an aura of the Stealth effect (type 83), toggled by the Stealth effect itself.
        /// </summary>
        public bool IsStealthed => HasAuraOfEffect((uint)SpellEffectType.Stealth);

        public int RemoveAurasOfEffect(uint effectType)
        {
            return auras.RemoveAll(a => a.EffectType == effectType);
        }

        public void AddPropertyModifier(PropertyModifierEntry modifier)
        {
            WakeUp();
            propertyModifiers.RemoveAll(m => m.Spell4Id == modifier.Spell4Id && m.Property == modifier.Property && m.Type == modifier.Type && m.ConvertFrom == modifier.ConvertFrom);
            propertyModifiers.Add(modifier);
        }

        public IReadOnlyList<PropertyModifierEntry> PropertyModifiers => propertyModifiers;

        /// <summary>
        /// PersonalDmgHealMod: a factor on the damage (or healing) between this unit and the caster of the spell. Direction 1 = what this unit
        /// takes (or is healed for) from the caster, 2 = what this unit deals to the caster; kind 12 is healing, every other kind damage
        /// (the data has damage type numbers 3 to 8 and 12, assumption in docs/ASSUMPTIONS.md).
        /// </summary>
        public sealed class PersonalModEntry
        {
            public uint Spell4Id;
            public uint CasterGuid;
            public uint Kind;
            public uint Direction;
            public float Factor;
            public double Remaining;
        }

        private readonly List<PersonalModEntry> personalMods = new();

        /// <summary>ModifySpell (Spell.SpellModifications): rows that change the spells of a base (BaseId) or of a spell group (GroupId).</summary>
        public class SpellModEntry
        {
            public uint Spell4Id;
            public uint EffectId;
            public uint BaseId;
            public uint GroupId;
            public IReadOnlyList<Shared.GameTable.Model.Spell4ModificationEntry> Rows;
            public double Remaining;
        }

        private readonly List<SpellModEntry> spellMods = new();

        public void AddSpellMod(SpellModEntry entry)
        {
            WakeUp();
            spellMods.RemoveAll(m => m.Spell4Id == entry.Spell4Id && m.EffectId == entry.EffectId);
            spellMods.Add(entry);
        }

        /// <summary>A value of a spell (cast time, cooldown, innate cost...) after the active ModifySpell rows of this unit for that parameter.</summary>
        public float ModifySpellValue(Shared.GameTable.Model.Spell4Entry spell, uint parameter, float value)
        {
            if (spellMods.Count == 0 || spell == null)
                return value;
            List<Shared.GameTable.Model.Spell4ModificationEntry> rows = null;
            foreach (SpellModEntry mod in spellMods)
            {
                if (!Spell.SpellModifications.Matches(spell, mod.BaseId, mod.GroupId))
                    continue;
                foreach (Shared.GameTable.Model.Spell4ModificationEntry row in mod.Rows)
                    if (row.ModificationParameterEnum == parameter)
                        (rows ??= new()).Add(row);
            }
            if (rows == null)
                return value;
            value = Spell.SpellModifications.Evaluate(rows, parameter, value);
            return value < 0f ? 0f : value;
        }

        private (Reputation.Static.Faction Faction1, Reputation.Static.Faction Faction2)? factionBeforeSet;
        private double factionSetRemaining;

        /// <summary>
        /// FactionSet: the unit takes the faction for the effect's duration and turns back afterwards; the change is shown to the
        /// clients with ServerEntityFaction (client reader: u32 unit, Faction:14).
        /// </summary>
        public void SetFactionFor(Reputation.Static.Faction faction, double seconds)
        {
            WakeUp();
            factionBeforeSet ??= (Faction1, Faction2);
            Faction1 = faction;
            Faction2 = faction;
            factionSetRemaining = seconds;
            EnqueueToVisible(new NexusForever.WorldServer.Network.Message.Model.ServerEntityFaction { UnitId = Guid, Faction = (ushort)faction }, true);
        }

        private void RestoreFaction()
        {
            if (factionBeforeSet is not { } before)
                return;
            factionBeforeSet = null;
            factionSetRemaining = 0d;
            Faction1 = before.Faction1;
            Faction2 = before.Faction2;
            EnqueueToVisible(new NexusForever.WorldServer.Network.Message.Model.ServerEntityFaction { UnitId = Guid, Faction = (ushort)before.Faction1 }, true);
        }

        public void AddPersonalMod(PersonalModEntry entry)
        {
            WakeUp();
            personalMods.RemoveAll(m => m.Spell4Id == entry.Spell4Id && m.CasterGuid == entry.CasterGuid && m.Kind == entry.Kind && m.Direction == entry.Direction);
            personalMods.Add(entry);
        }

        /// <summary>Product of the personal modifiers of this unit that apply to an exchange with the other unit.</summary>
        public float PersonalFactor(uint otherGuid, uint direction, bool healing)
        {
            float factor = 1f;
            foreach (PersonalModEntry mod in personalMods)
                if (mod.CasterGuid == otherGuid && mod.Direction == direction && (mod.Kind == 12u) == healing)
                    factor *= mod.Factor;

            return factor;
        }

        /// <summary>
        /// A property value after the active spell modifiers. Every modifier row reads value x A + B (checked against the data: armor +20 is 1/20, a
        /// 25% snare is 0.75/0, sprint +50% speed is 1/0.5, "no falling damage" is 0/0). The type decides the order they apply in, 1 before 2 before 3 before 4.
        /// </summary>
        public float GetModifiedProperty(Property property, float baseValue)
        {
            float value = baseValue;
            foreach (PropertyModifierEntry modifier in propertyModifiers.Where(m => m.Property == property && m.ConvertFrom == null).OrderBy(m => m.Type))
                value = value * modifier.ValueA + modifier.ValueB;

            return value;
        }

        public void AddProc(ProcEntry proc)
        {
            WakeUp();
            procs.RemoveAll(p => p.Spell4Id == proc.Spell4Id && p.Trigger == proc.Trigger && p.ProcSpell4Id == proc.ProcSpell4Id);
            procs.Add(proc);
        }

        public IReadOnlyList<ProcEntry> Procs => procs;

        [ThreadStatic]
        private static bool firingProc;

        private static readonly Random procRng = new();

        /// <summary>
        /// Fire the procs registered for a trigger: chance roll, internal cooldown, then the proc spell is cast. Procs do not fire from inside a
        /// proc spell, so chains cannot loop. Mode (DataBits03) is a bit field (docs/ASSUMPTIONS.md "Proc mode"): the primary target is the other
        /// party with bit 4, else the unit that applied the proc with bit 1, else the holder with bit 2, else the other party; the caster is the
        /// chosen target with bit 32, the applier with bit 8, otherwise the holder (bit 16 and no bit alike).
        /// </summary>
        public void FireProcs(uint trigger, WorldEntity other)
        {
            if (procs.Count == 0 || firingProc)
                return;

            foreach (ProcEntry proc in procs.Where(p => p.Trigger == trigger && p.CooldownLeft <= 0d).ToList())
            {
                if (proc.Chance < 1f && procRng.NextDouble() >= proc.Chance)
                    continue;

                if (proc.CooldownMs != uint.MaxValue)
                    proc.CooldownLeft = proc.CooldownMs / 1000d;

                firingProc = true;
                try
                {
                    UnitEntity applier = proc.ApplierGuid == 0u || proc.ApplierGuid == Guid ? this : GetVisible<WorldEntity>(proc.ApplierGuid) as UnitEntity;
                    UnitEntity otherUnit = other as UnitEntity;

                    UnitEntity target;
                    if ((proc.Mode & 4u) != 0u)
                        target = otherUnit;
                    else if ((proc.Mode & 1u) != 0u)
                        target = applier;
                    else if ((proc.Mode & 2u) != 0u)
                        target = this;
                    else
                        target = otherUnit;

                    UnitEntity procCaster = (proc.Mode & 32u) != 0u ? target : (proc.Mode & 8u) != 0u ? applier : this;
                    if (procCaster == null || procCaster.IsDead && procCaster != this)
                    {
                        Diagnostics.FailureLedger.Record("proc-skipped", $"trigger {trigger} mode {proc.Mode}: {((proc.Mode & 32u) != 0u ? "target" : "applier")} not here or dead", $"spell {proc.Spell4Id} casts {proc.ProcSpell4Id}");
                        continue;
                    }

                    procCaster.CastSpell(proc.ProcSpell4Id, new Spell.SpellParameters
                    {
                        UserInitiatedSpellCast = false,
                        PrimaryTargetId        = target?.Guid ?? 0u
                    });
                    Diagnostics.FailureLedger.Record("proc-fired", $"trigger {trigger} mode {proc.Mode}", $"spell {proc.Spell4Id} casts {proc.ProcSpell4Id}");
                }
                catch (Exception exception)
                {
                    Diagnostics.FailureLedger.Record("proc-error", $"{exception.GetType().Name}: {exception.Message}", $"spell {proc.Spell4Id} casts {proc.ProcSpell4Id}");
                }
                finally
                {
                    firingProc = false;
                }
            }
        }

        public float GetShieldMax() => GetPropertyValue(Property.ShieldCapacityMax) ?? 0f;

        /// <summary>Current amount of class resource 0-6 (Stat Resource0-6).</summary>
        public float Focus => GetStatFloat(Stat.Focus) ?? 0f;

        public float GetResource(uint index) => index <= 6u ? GetStatFloat((Stat)((uint)Stat.Resource0 + index)) ?? 0f : 0f;

        /// <summary>
        /// Current vital as a percentage of its maximum (same vital ids as <see cref="AddVital"/>), null when not understood.
        /// </summary>
        public float? GetVitalPercent(uint vital)
        {
            if (vital == 1u)
                return MaxHealth == 0u ? null : Health * 100f / MaxHealth;

            if (this is not Player)
                return null;

            if (vital == 3u)
            {
                float shield = GetShieldMax();
                return shield <= 0f ? null : Shield * 100f / shield;
            }

            if (!ResolveVital(vital, out Stat stat, out Property max))
                return null;

            float ceiling = GetPropertyValue(max) ?? 0f;
            return ceiling <= 0f ? null : (GetStatFloat(stat) ?? 0f) * 100f / ceiling;
        }

        /// <summary>
        /// Stat and maximum property behind a Vital table id for a player. 5-11 are Endurance and Resource1-6, 15 Focus, 16 Dash; the class
        /// named vitals (4 Kinetic Energy, 12 Suit Power, 18 Actuator, 19 Spell Surge, 26 Volatility) are the resource slot whose
        /// Class.vitalEnumResourceNN names them (certain; before, VitalModifier rows on them were dropped, e.g.
        /// Medic Recharge's -4 Actuator).
        /// </summary>
        /// <summary>
        /// Current amount of a vital (same ids as <see cref="AddVital"/>: 1 Health, 3 shield, 5-11 Resource0-6, 15 Focus, 16 Dash, class named
        /// vitals by their resource slot), null when the id is not understood.
        /// </summary>
        public float? GetVitalValue(uint vital)
        {
            if (vital == 1u)
                return Health;
            if (vital == 3u)
                return Shield;
            return ResolveVital(vital, out Stat stat, out _) ? GetStatFloat(stat) ?? 0f : null;
        }

        private bool ResolveVital(uint vital, out Stat stat, out Property max)
        {
            stat = Stat.Health;
            max  = Property.BaseHealth;
            if (vital >= 5u && vital <= 11u)
            {
                stat = (Stat)((uint)Stat.Resource0 + (vital - 5u));
                max  = (Property)((uint)Property.ResourceMax0 + (vital - 5u));
                return true;
            }
            if (vital == 15u)
            {
                stat = Stat.Focus;
                max  = Property.BaseFocusPool;
                return true;
            }
            if (vital == 16u)
            {
                stat = Stat.Dash;
                max  = Property.ResourceMax7;
                return true;
            }
            if (this is Player player && vital != 0u)
            {
                uint? slot = Spell.InnateCost.ClassSlot(player, vital);
                if (slot != null && slot.Value <= 6u)
                {
                    stat = (Stat)((uint)Stat.Resource0 + slot.Value);
                    max  = (Property)((uint)Property.ResourceMax0 + slot.Value);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Maximum of a vital (same ids as <see cref="AddVital"/>), 0 when it has none.</summary>
        public float GetVitalMax(uint vital)
        {
            if (vital == 1u)
                return MaxHealth;
            if (vital == 3u)
                return GetShieldMax();
            if (this is not Player || !ResolveVital(vital, out _, out Property max))
                return 0f;
            return GetPropertyValue(max) ?? 0f;
        }

        /// <summary>
        /// Add to a vital. Ids come from the VitalModifier data: 1 Health, 5-11 Resource0-6, 15 Focus (inferred, see gt vital).
        /// Returns false when the vital is not one that is understood or the entity has no such stat.
        /// </summary>
        public bool AddVital(uint vital, float amount)
        {
            try
            {
                if (vital == 1u)
                {
                    if (amount >= 0f)
                        Heal((uint)amount);
                    else
                        TakeDamage(this, (uint)-amount, out _);

                    return true;
                }

                if (this is not Player)
                    return false;

                if (vital == 3u)
                {
                    float shield = GetShieldMax();
                    if (shield <= 0f)
                        return false;

                    SetStat(Stat.Shield, (uint)Math.Clamp(Shield + amount, 0f, shield));
                    return true;
                }

                if (!ResolveVital(vital, out Stat stat, out Property maxProperty))
                    return false;
                Property? max = maxProperty;

                float current = GetStatFloat(stat) ?? 0f;
                float ceiling = max.HasValue ? GetPropertyValue(max.Value) ?? float.MaxValue : float.MaxValue;
                SetStat(stat, Math.Clamp(current + amount, 0f, ceiling));
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Anything with a timer: effects, auras, modifiers, procs, crowd control, casts in progress.</summary>
        protected bool HasTimedState => combatTimer > 0d || factionBeforeSet != null || immunities.Count > 0 || auras.Count > 0 || propertyModifiers.Count > 0 || personalMods.Count > 0 || spellMods.Count > 0 || procs.Count > 0 || ccStates.Count > 0 || pendingSpells.Count > 0;

        /// <summary>
        /// Seconds a unit stays in combat after its last hostile action (guess, docs/ASSUMPTIONS.md "Combat resource economy"): the server used to have no
        /// combat state. The client keeps one per unit (the client reads it at unit + 0x2ac for Spell4Conditions bit 0x2) and the server
        /// tells it through ServerUnitEnteredCombat. Refreshed by hostile damage dealt or taken and by a creature fighting the unit.
        /// </summary>
        public const double CombatTimeout = 5d;
        private double combatTimer;

        public bool InCombat => combatTimer > 0d;

        public void MarkInCombat()
        {
            if (IsDead)
                return;
            bool entered = combatTimer <= 0d;
            combatTimer = CombatTimeout;
            if (entered)
                OnCombatChanged(true);
        }

        private void OnCombatChanged(bool inCombat)
        {
            EnqueueToVisible(new ServerUnitEnteredCombat { UnitId = Guid, InCombat = inCombat }, true);
            if (this is Player)
                Diagnostics.FailureLedger.Record("combat-state", inCombat ? "player entered combat" : "player left combat");
        }

        private void UpdateEffectStates(double lastTick)
        {
            if (combatTimer > 0d && ((combatTimer -= lastTick) <= 0d || IsDead))
            {
                combatTimer = 0d;
                OnCombatChanged(false);
            }

            if (factionBeforeSet != null && (factionSetRemaining -= lastTick) <= 0d)
                RestoreFaction();

            if (immunities.Count > 0)
                foreach (uint kind in immunities.Keys.ToArray())
                    if ((immunities[kind] -= lastTick) <= 0d)
                        immunities.Remove(kind);

            if (auras.Count > 0)
                auras.RemoveAll(a => (a.Remaining -= lastTick) <= 0d);

            if (propertyModifiers.Count > 0)
                propertyModifiers.RemoveAll(m => (m.Remaining -= lastTick) <= 0d);

            if (personalMods.Count > 0)
                personalMods.RemoveAll(m => (m.Remaining -= lastTick) <= 0d);

            if (spellMods.Count > 0)
                spellMods.RemoveAll(m => (m.Remaining -= lastTick) <= 0d);

            if (procs.Count > 0)
            {
                procs.RemoveAll(p => (p.Remaining -= lastTick) <= 0d);
                foreach (ProcEntry proc in procs)
                    if (proc.CooldownLeft > 0d)
                        proc.CooldownLeft -= lastTick;
            }

            if (ccStates.Count > 0)
                foreach (uint state in ccStates.Keys.ToArray())
                    if ((ccStates[state] -= lastTick) <= 0d)
                        ccStates.Remove(state);
        }

        public override void Update(double lastTick)
        {
            base.Update(lastTick);
            UpdateEffectStates(lastTick);

            foreach (Spell.Spell spell in pendingSpells.ToArray())
            {
                try
                {
                    spell.Update(lastTick);
                }
                catch (System.Exception exception)
                {
                    // one broken spell must not abort the update of the whole map, drop it and count it
                    Diagnostics.FailureLedger.Record("spell-update-exception", $"spell {spell.Spell4Id}: {exception.GetType().Name}: {exception.Message}", (exception.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
                    pendingSpells.Remove(spell);
                    continue;
                }

                if (spell.IsFinished)
                    pendingSpells.Remove(spell);
            }
        }

        /// <summary>
        /// Cast a <see cref="Spell"/> with the supplied spell id and <see cref="SpellParameters"/>.
        /// </summary>
        public void CastSpell(uint spell4Id, SpellParameters parameters)
        {
            if (parameters == null)
                throw new ArgumentNullException();

            Spell4Entry spell4Entry = GameTableManager.Instance.Spell4.GetEntry(spell4Id);
            if (spell4Entry == null)
                throw new ArgumentOutOfRangeException();

            CastSpell(spell4Entry.Spell4BaseIdBaseSpell, (byte)spell4Entry.TierIndex, parameters);
        }

        /// <summary>
        /// Cast a <see cref="Spell"/> with the supplied spell base id, tier and <see cref="SpellParameters"/>.
        /// </summary>
        public void CastSpell(uint spell4BaseId, byte tier, SpellParameters parameters)
        {
            if (parameters == null)
                throw new ArgumentNullException();

            SpellBaseInfo spellBaseInfo = GlobalSpellManager.Instance.GetSpellBaseInfo(spell4BaseId);
            if (spellBaseInfo == null)
                throw new ArgumentOutOfRangeException();

            SpellInfo spellInfo = spellBaseInfo.GetSpellInfo(tier);
            if (spellInfo == null)
                throw new ArgumentOutOfRangeException();

            parameters.SpellInfo = spellInfo;
            CastSpell(parameters);
        }

        /// <summary>
        /// Cast a <see cref="Spell"/> with the supplied <see cref="SpellParameters"/>.
        /// </summary>
        public void CastSpell(SpellParameters parameters)
        {
            WakeUp();
            if (parameters == null)
                throw new ArgumentNullException();

            if (DisableManager.Instance.IsDisabled(DisableType.BaseSpell, parameters.SpellInfo.BaseInfo.Entry.Id))
            {
                if (this is Player player)
                    player.SendSystemMessage($"Unable to cast base spell {parameters.SpellInfo.BaseInfo.Entry.Id} because it is disabled.");
                return;
            }

            if (DisableManager.Instance.IsDisabled(DisableType.Spell, parameters.SpellInfo.Entry.Id))
            {
                if (this is Player player)
                    player.SendSystemMessage($"Unable to cast spell {parameters.SpellInfo.Entry.Id} because it is disabled.");
                return;
            }

            if (parameters.UserInitiatedSpellCast)
            {
                if (this is Player player)
                    player.Dismount();
            }

            // a dead creature does not cast (a dead player still gets the normal cast result for the client)
            if (IsDead && this is not Player && Combat.CombatConfig.Enabled)
            {
                Diagnostics.FailureLedger.Record("creature-cast-while-dead", $"spell {parameters.SpellInfo.Entry.Id}", $"parent {parameters.ParentSpellInfo?.Entry.Id} root {parameters.RootSpellInfo?.Entry.Id} creature {CreatureId}");
                return;
            }

            // a runaway (a raid fight once held 62k live spells and ran the world out of memory): say which spells pile up and refuse more
            if (pendingSpells.Count >= MaxPendingSpells)
            {
                Diagnostics.FailureLedger.Record("spell-pending-overflow", $"spell {parameters.SpellInfo.Entry.Id} refused, {pendingSpells.Count} pending on {(this is Player ? "player" : $"creature {CreatureId}")}",
                    string.Join(", ", pendingSpells.GroupBy(s => s.Spell4Id).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()} (casting {g.Count(s => s.IsCasting)}, finished {g.Count(s => s.IsFinished)}, next event in {g.Select(s => s.NextEventDelay).Min()?.ToString("0.##") ?? "none"}-{g.Select(s => s.NextEventDelay).Max()?.ToString("0.##") ?? "none"} s)"))
                    + $"; caster dead {IsDead}, on map {Map?.Entry.Id}");
                return;
            }

            var spell = new Spell.Spell(this, parameters);
            spell.Cast();
            pendingSpells.Add(spell);
        }

        private const int MaxPendingSpells = 400;

        /// <summary>
        /// Cancel any <see cref="Spell"/>'s that are interrupted by movement.
        /// </summary>
        public void CancelSpellsOnMove()
        {
            foreach (Spell.Spell spell in pendingSpells)
                if (spell.IsMovingInterrupted() && spell.IsCasting)
                    spell.CancelCast(CastResult.CasterMovement);
        }

        /// <summary>
        /// Cancel a <see cref="Spell"/> based on its casting id
        /// </summary>
        /// <param name="castingId">Casting ID of the spell to cancel</param>
        public void CancelSpellCast(uint castingId)
        {
            Spell.Spell spell = pendingSpells.SingleOrDefault(s => s.CastingId == castingId);
            spell?.CancelCast(CastResult.SpellCancelled);
        }
    }
}
