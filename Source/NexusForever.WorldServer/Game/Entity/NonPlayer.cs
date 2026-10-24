using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Database.World.Model;
using NexusForever.WorldServer.Game.CharacterCache;
using NexusForever.WorldServer.Game.Combat;
using NexusForever.WorldServer.Game.Entity.Movement.Generator;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Reputation.Static;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Network;
using NexusForever.WorldServer.Game.Entity.Network.Model;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    [DatabaseEntity(EntityType.NonPlayer)]
    public class NonPlayer : UnitEntity
    {
        public VendorInfo VendorInfo { get; private set; }

        /// <summary>The public event run that spawned this creature, if any.</summary>
        public object EventTag { get; set; }

        public NonPlayer()
            : base(EntityType.NonPlayer)
        {
        }

        /// <summary>Creature units spawned with another entity type (AiTurret, Destructible): same unit behaviour, their own network model.</summary>
        protected NonPlayer(EntityType type)
            : base(type)
        {
        }

        public override void Initialise(EntityModel model)
        {
            base.Initialise(model);

            if (model.EntityVendor == null)
            {
                Creature2Entry vendorEntry = GameTableManager.Instance.Creature2.GetEntry(model.Creature);
                if (VendorFallback.IsVendor(vendorEntry))
                    model = VendorFallback.Build(model, vendorEntry);
            }

            if (model.EntityVendor != null)
            {
                CreateFlags |= EntityCreateFlag.Vendor;
                VendorInfo = new VendorInfo(model);
            }

            // spawns without stat rows (researched spawns, summons) take their level from the creature table
            if (GetStatInteger(Stat.Level) == null)
            {
                Creature2Entry levelEntry = GameTableManager.Instance.Creature2.GetEntry(CreatureId);
                if (levelEntry != null && levelEntry.MinLevel > 0u)
                    Level = levelEntry.MaxLevel > levelEntry.MinLevel ? levelEntry.MinLevel + (uint)levelRandom.Next((int)(levelEntry.MaxLevel - levelEntry.MinLevel + 1u)) : levelEntry.MinLevel;
            }

            CalculateProperties();

            spawnModel = model;
            if (CombatConfig.Enabled)
            {
                // creatures without a usable health value get one from their level, otherwise they would die on the first hit
                combatStats = UnitStats.Creature(CreatureId, Level);
                uint tableHealth = (uint)UnitStats.Get(combatStats, Property.BaseHealth);
                uint baseHealth = (uint)(GetPropertyValue(Property.BaseHealth) ?? 0f);
                combatMaxHealth = baseHealth > 0u ? baseHealth : tableHealth > 0u ? tableHealth : Health > 0u ? Health : CombatConfig.CreatureHealth(Level);
                SetStat(Stat.Health, combatMaxHealth);
            }
        }

        private static readonly System.Random levelRandom = new();
        private EntityModel spawnModel;

        /// <summary>Unit property values from the tables (level row times archetype, difficulty and tier), set when combat is on.</summary>
        public float[] CombatStats => combatStats;
        private float[] combatStats = System.Array.Empty<float>();
        private uint combatMaxHealth;
        private bool veteranScaled;
        private Vector3 spawnPosition;
        private WorldEntity threat;
        private double aggroTimer;

        /// <summary>
        /// The entity this creature is currently fighting, if any.
        /// </summary>
        public WorldEntity Threat => threat;
        private double attackTimer;
        private double chaseTimer;
        private double corpseTimer;
        private bool removed;

        public override bool CanSleep => !IsDead && threat == null && SummonLifetime == null && EventTag == null && Encounter == null && attackers.Count == 0
            && MovementManager.IsIdle && !HasTimedState;

        public override uint MaxHealth => combatMaxHealth > 0u ? combatMaxHealth : base.MaxHealth;

        public override void OnAddToMap(BaseMap map, uint guid, Vector3 vector)
        {
            base.OnAddToMap(map, guid, vector);
            spawnPosition = vector;

            // worlds with veteran creature rows spawn those (VeteranVariants, real level and tier); the flat scale is only for worlds without them
            if (CombatConfig.Enabled && map is DungeonMapInstance { Difficulty: InstanceDifficulty.Veteran } && !veteranScaled && combatMaxHealth > 0u
                && !VeteranVariants.HasVeteran((ushort)map.Entry.Id))
            {
                veteranScaled = true;
                combatMaxHealth = (uint)(combatMaxHealth * DungeonMapInstance.VeteranHealthScale);
                SetStat(Stat.Health, combatMaxHealth);
                SetDamageScale(DungeonMapInstance.VeteranDamageScale);
                Diagnostics.FailureLedger.Record("veteran-scaled", $"world {map.Entry.Id}");
            }
        }

        /// <summary>
        /// Drops the creature's current target if it is the given unit (ThreatModification mode 2, e.g. Stalker "Tactical Retreat").
        /// The next damage dealt to it, or the next attacker still on its list, becomes the target again.
        /// </summary>
        public void DropThreat(WorldEntity from)
        {
            if (threat != from)
                return;

            attackers.Remove(from);
            threat = attackers.Keys.FirstOrDefault(a => a.Map == Map && a is not UnitEntity { IsDead: true });
        }

        protected override void OnDamaged(WorldEntity source, uint amount)
        {
            WakeUp();
            if (!CombatConfig.Enabled || source == null)
                return;

            if (threat == null)
                threat = source;

            // everyone who hurt the creature stays a candidate when the first target dies or leaves (group fights, bosses)
            if (source is UnitEntity && source != this)
                attackers[source] = fightClock;
        }

        // attacker -> time it was last seen on this map (a player that teleports inside the map is away for a moment)
        private readonly Dictionary<WorldEntity, double> attackers = new();
        private double fightClock;

        /// <summary>Everyone who damaged this creature during the current fight and is still around.</summary>
        public IReadOnlyCollection<WorldEntity> Attackers => attackers.Keys.Where(a => a.Map == Map).ToList();

        /// <summary>Boss fight logic, null for creatures without an encounter definition.</summary>
        public Encounters.EncounterController Encounter { get; private set; }
        private bool encounterChecked;
        private float baseAssault = -1f;

        /// <summary>
        /// Scale the creature's assault rating (enrage). 1 restores the table value.
        /// </summary>
        public void SetDamageScale(float scale)
        {
            if (combatStats.Length == 0)
                return;

            if (baseAssault < 0f)
                baseAssault = UnitStats.Get(combatStats, Property.AssaultRating);

            // the table row is shared between creatures of the same kind, so work on a copy
            combatStats = (float[])UnitStats.Creature(CreatureId, Level).Clone();
            if ((uint)Property.AssaultRating < combatStats.Length)
                combatStats[(uint)Property.AssaultRating] = baseAssault * scale;
            // creature attacks also scale with the primary attributes (spell parameter types 1-4)
            for (int i = (int)Property.Strength; i <= (int)Property.Magic && i < combatStats.Length; i++)
                combatStats[i] *= scale;
        }

        protected override void OnDeath(WorldEntity killer)
        {
            WakeUp();
            threat      = null;
            attackers.Clear();
            Encounter?.OnBossDeath();
            corpseTimer = CombatConfig.CorpseTime;

            Diagnostics.FailureLedger.Record("combat-stats", $"creature killed by {(killer is Player { Session: Bots.BotSession } ? "bot" : killer is Player ? "human" : "other")}");

            if (killer is Player player)
                CreditKill(player);

            MovementManager.StopSpline();
        }

        /// <summary>
        /// Give XP and quest progress to the killer and to grouped players close enough to have helped.
        /// </summary>
        private void CreditKill(Player killer)
        {
            var credited = new List<Player> { killer };

            PlayerGroup group = GroupManager.Instance.GetGroup(killer.CharacterId);
            if (group != null)
            {
                foreach (GroupMember member in group.Members)
                {
                    Player other = CharacterManager.Instance.GetPlayer(member.CharacterId);
                    if (other != null && other != killer && !other.IsDead && other.Map == Map
                        && Vector3.Distance(other.Position, Position) <= 80f)
                        credited.Add(other);
                }
            }

            // personal loot: everyone who gets the kill rolls on the drop table (assumption, docs/ASSUMPTIONS.md)
            // in dungeons and raids the group shares one drop table roll, the group loot rule decides who gets each item (GroupLoot)
            IEnumerable<Player> looters = Map is DungeonMapInstance && credited.Count > 1 ? new[] { killer } : credited;
            foreach (Player looter in looters)
                Research.CreatureLoot.Roll(this, looter);
            Events.PublicEventEngine.Instance.OnCreatureKilled(this, credited);
            if (Map is MapInstance killInstance)
                Bots.DungeonTeams.Instance.OnKill(killInstance);

            foreach (Player player in credited)
            {
                player.XpManager.GrantXp(CombatConfig.KillXp(Level), ExpReason.KillCreature);
                player.QuestManager.OnCreatureKilled(CreatureId, Guid);
                player.PathMissionManager.OnCreatureKilled(CreatureId, Guid);
            }
        }

        /// <summary>
        /// Seconds left for a creature summoned by a spell, null for normal spawns.
        /// </summary>
        public double? SummonLifetime { get; set; }

        /// <summary>
        /// Created by a spell summon effect (SummonCreature, SummonTrap, SummonPet). Such a unit does not cast researched abilities that summon
        /// its own creature id: "Grimwort - Proxy" 52647 got the Grimwort page's Living Grounds (55076/57840, a summon of 52647 on every unit in
        /// a 3 m circle) by name, the summons hit each other and doubled every cast until world 1061 held 1.2M queued adds and ran out of memory
        /// (docs/PERFORMANCE.md "Out of memory in world 1061").
        /// </summary>
        public bool IsSpellSummon { get; set; }

        /// <summary>Off only for the 'gt spawnnear ... unguarded' reproduction; the summon caps stay on either way.</summary>
        public static bool SelfSummonFilter = true;

        /// <summary>
        /// Only creatures placed by the world database (an entity row id) come back after death or a despawn. Units made at runtime (spell summons,
        /// event and holdout units, encounter adds, probes) used to be rescheduled from their made-up model and returned as permanent creatures
        /// (killed Grimwort proxies came back as permanent casters).
        /// </summary>
        private bool RespawnsAtSpawnPoint => spawnModel != null && spawnModel.Id != 0u && !SummonLifetime.HasValue && !despawning;

        // a summon whose lifetime ran out waits in the map's action queue for its removal; it must not act (cast, fight) meanwhile
        private bool despawning;

        /// <summary>
        /// Remove the creature because a spell says so (DespawnUnit): summoned units are gone, spawn point creatures come back after the respawn time
        /// (instance creatures stay gone until the instance resets).
        /// </summary>
        public void Despawn()
        {
            if (Map == null || removed)
                return;

            removed = true;
            if (RespawnsAtSpawnPoint && Map is not DungeonMapInstance)
                RespawnManager.Instance.Schedule(Map, spawnModel, NexusForever.WorldServer.Game.Events.PublicEventEngine.Instance.WorldBossRespawnSeconds(CreatureId) ?? CombatConfig.RespawnTime);

            RemoveFromMap();
        }

        /// <summary>SummonTrap: the unit that placed the trap, the spell it casts on the first hostile unit in <see cref="TrapRadius"/>, and the arming delay.</summary>
        public uint TrapOwnerGuid { get; set; }
        public uint TrapSpell4Id { get; set; }
        public float TrapRadius { get; set; }
        public double TrapArmTime { get; set; }

        /// <summary>SummonPet: the owner; the pet follows it and fights the owner's hostile target with the creature fight loop.</summary>
        public uint PetOwnerGuid { get; set; }
        /// <summary>The stance the owner chose (ClientPetStanceChange: a flag 1, 2, 4, 8 or 16; 0 none chosen). Stored and sent back, it does not change the pet's behaviour (the meaning of each flag is not decoded).</summary>
        public byte PetStance { get; set; }
        private double petTimer;

        private void UpdateTrap(double lastTick)
        {
            TrapArmTime -= lastTick;
            if (TrapArmTime > 0d)
                return;
            TrapArmTime = 0.25d;

            UnitEntity owner = GetVisible<UnitEntity>(TrapOwnerGuid);
            if (owner == null)
                return;

            UnitEntity victim = visibleEntities.Values.OfType<UnitEntity>()
                .Where(u => u != owner && u != this && !u.IsDead && Vector3.Distance(Position, u.Position) <= TrapRadius
                    && owner.GetDispositionTo(u.Faction1) == Disposition.Hostile)
                .OrderBy(u => Vector3.Distance(Position, u.Position))
                .FirstOrDefault();
            if (victim == null)
                return;

            uint spell4Id = TrapSpell4Id;
            TrapSpell4Id = 0u;
            Diagnostics.FailureLedger.Record("summon-trap", "triggered", $"trap creature {CreatureId} casts {spell4Id}");
            owner.CastSpell(spell4Id, new Spell.SpellParameters
            {
                UserInitiatedSpellCast = false,
                PrimaryTargetId        = victim.Guid
            });
            Despawn();
        }

        /// <summary>True when the pet logic handled this tick (no fight running).</summary>
        private bool UpdatePet(double lastTick)
        {
            petTimer -= lastTick;
            if (petTimer > 0d)
                return threat == null;
            petTimer = 0.5d;

            UnitEntity owner = GetVisible<UnitEntity>(PetOwnerGuid);
            if (owner == null || owner.IsDead)
            {
                Despawn();
                return true;
            }

            // the fight loop leashes to the spawn point: for a pet that is wherever the owner is
            spawnPosition = owner.Position;
            if (threat == null && owner.TargetGuid != 0u)
            {
                UnitEntity target = GetVisible<UnitEntity>(owner.TargetGuid);
                if (target != null && !target.IsDead && owner.InCombat && owner.GetDispositionTo(target.Faction1) == Disposition.Hostile)
                    threat = target;
            }
            if (threat != null)
                return false;

            if (Vector3.Distance(Position, owner.Position) > 6f)
                MovementManager.Follow(owner, 3f);
            return true;
        }

        public override void Update(double lastTick)
        {
            if (despawning)
                return;

            base.Update(lastTick);

            if (SummonLifetime.HasValue && Map != null)
            {
                SummonLifetime -= lastTick;
                if (SummonLifetime <= 0d)
                {
                    despawning = true;
                    removed = true;
                    RemoveFromMap();
                    return;
                }
            }

            if (TrapSpell4Id != 0u && Map != null)
            {
                UpdateTrap(lastTick);
                return;
            }

            if (PetOwnerGuid != 0u && Map != null && !IsDead && UpdatePet(lastTick))
                return;

            if (!CombatConfig.Enabled || Map == null)
                return;

            if (IsDead)
            {
                corpseTimer -= lastTick;
                if (corpseTimer <= 0d && !removed && spawnModel != null)
                {
                    removed = true;
                    if (!RespawnsAtSpawnPoint)
                    {
                        Diagnostics.FailureLedger.Record("respawn-skipped", "unit made at runtime (summon, event or holdout unit, encounter add) is not respawned", $"creature {CreatureId}");
                        RemoveFromMap();
                        return;
                    }
                    // dungeon and raid creatures stay dead until the instance resets
                    if (Map is not DungeonMapInstance)
                        RespawnManager.Instance.Schedule(Map, spawnModel, NexusForever.WorldServer.Game.Events.PublicEventEngine.Instance.WorldBossRespawnSeconds(CreatureId) ?? CombatConfig.RespawnTime);
                    RemoveFromMap();
                }
                return;
            }

            // stunned, asleep and similar states stop it from attacking or chasing
            if (IsIncapacitated)
                return;

            long t0 = Diagnostics.Profiler.Start();
            if (threat != null)
            {
                Fight(lastTick);
                Diagnostics.Profiler.Add("npc.fight", t0);
            }
            else if (CombatConfig.Aggro)
            {
                LookForPrey(lastTick);
                Diagnostics.Profiler.Add("npc.lookforprey", t0);
            }
        }

        /// <summary>
        /// Once a second, attack the nearest living player (or bot) in range if this creature is hostile to them.
        /// Vendors and other friendly creatures never attack.
        /// </summary>
        private void LookForPrey(double lastTick)
        {
            aggroTimer -= lastTick;
            if (aggroTimer > 0d)
                return;

            aggroTimer = 1d;
            if (VendorInfo != null)
                return;

            Player prey = visibleEntities.Values
                .OfType<Player>()
                .Where(p => !p.IsDead && !p.IsLoading && Vector3.Distance(Position, p.Position) <= CombatConfig.AggroRange
                    && GetDispositionTo(p.Faction1) == Disposition.Hostile)
                .OrderBy(p => Vector3.Distance(Position, p.Position))
                .FirstOrDefault();

            if (prey != null)
                threat = prey;
        }

        /// <summary>
        /// A creature only reacts once it has been hurt: chase the attacker, hit it in melee every few seconds
        /// and give up when the attacker dies, leaves the map or drags the creature too far from its spawn.
        /// </summary>
        private void Fight(double lastTick)
        {
            if (!encounterChecked)
            {
                encounterChecked = true;
                var definition = Encounters.EncounterManager.Instance.Get(this);
                if (definition != null)
                    Encounter = new Encounters.EncounterController(this, definition);
            }

            // the first target may be dead or gone while others still fight the creature; someone who is away for less than 10 s
            // (a teleport inside the map) is still in the fight
            fightClock += lastTick;
            foreach (WorldEntity attacker in attackers.Keys.ToList())
            {
                if (attacker is UnitEntity { IsDead: true })
                    attackers.Remove(attacker);
                else if (attacker.Map == Map)
                    attackers[attacker] = fightClock;
                else if (fightClock - attackers[attacker] > 10d)
                    attackers.Remove(attacker);
            }

            if (threat != null && (threat.Map != Map || threat is UnitEntity { IsDead: true }))
            {
                threat = attackers.Keys.Where(a => a.Map == Map).OrderBy(a => Vector3.Distance(Position, a.Position)).FirstOrDefault();
                if (threat == null && attackers.Count > 0)
                    return; // everyone is away for a moment
            }

            float leash = Encounter != null && Encounter.Definition.Leash > 0f ? Encounter.Definition.Leash : Encounter != null ? 120f : 45f;
            if (threat == null || Vector3.Distance(Position, spawnPosition) > leash)
            {
                threat = null;
                attackers.Clear();
                Encounter?.Reset();
                MovementManager.LaunchGenerator(new DirectMovementGenerator
                {
                    Begin = Position,
                    Final = spawnPosition,
                    Map   = Map
                }, 8f);
                SetHealth(MaxHealth);
                return;
            }

            // a creature fighting a unit keeps both in combat (UnitEntity.InCombat)
            MarkInCombat();
            (threat as UnitEntity)?.MarkInCombat();

            Encounter?.Update(lastTick);

            float distance = Vector3.Distance(Position, threat.Position);
            if (Encounter == null)
                TryCastAbility(lastTick, distance);

            if (distance > 3.5f)
            {
                chaseTimer -= lastTick;
                if (chaseTimer <= 0d)
                {
                    MovementManager.Follow(threat, 2f);
                    chaseTimer = 1d;
                }
                return;
            }

            attackTimer -= lastTick;
            if (attackTimer > 0d)
                return;

            attackTimer = 2.5d;
            if (threat is UnitEntity unit)
            {
                // the creature's own auto-attack spell (researched abilities, "[AUTO-ATTACK]" / "Basic Attack" rows: 7261 of 8890 creature pages
                // have one) gives the amount and damage type through the spell formula; without one the most common auto-attack row
                // (0.8 x Strength + 0.8 x assault power, 187 of 647 rows) is used (guess, docs/ASSUMPTIONS.md); no table stats keep the placeholder
                LoadAbilities();
                float? formula = basicAttackEffect != null ? SpellFormula.Amount(basicAttackEffect, this, unit) : null;
                uint damageType = basicAttackEffect != null && basicAttackEffect.DamageType >= 1u && basicAttackEffect.DamageType <= 3u ? basicAttackEffect.DamageType : 1u;
                float rating = UnitStats.Get(combatStats, Property.AssaultRating);
                float amount = formula ?? (rating > 0f ? 0.8f * (UnitStats.Get(combatStats, Property.Strength) + rating * SpellFormula.AssaultPowerPerRating) : 0f);
                if (formula == null)
                    Diagnostics.FailureLedger.Record("combat-stats", basicAttackEffect == null ? "auto-attack without a researched spell (default row)" : "auto-attack spell row without decoded parameters");
                uint damage = amount > 0f ? (uint)System.Math.Max(1d, amount * CombatConfig.DamageRoll()) : CombatConfig.MeleeDamage(Level);
                // the hit goes through the client's rating curves (deflect, mitigation of its damage type, crit, glance; Combat.DamageMath)
                DamageMath.HitResult hit = DamageMath.RollHit(this, unit, damageType);
                if (hit.Deflected)
                    return;
                uint dealt = (uint)System.Math.Round(damage * unit.GetModifiedProperty(Property.DamageTakenMultiplierPhysical + (int)(damageType - 1u), 1f) * hit.Multiplier);
                unit.TakeDamage(this, dealt, out uint overkill);
                DamageMath.AfterHit(this, unit, dealt > overkill ? dealt - overkill : 0u);
            }
        }

        /// <summary>The creature does nothing for a while (NpcExecutionDelay).</summary>
        public void DelayActions(double seconds)
        {
            attackTimer = System.Math.Max(attackTimer, seconds);
            abilityTimer = System.Math.Max(abilityTimer, seconds);
        }

        /// <summary>
        /// ModifySpellCooldown on a creature: change the cooldown of its known abilities that match. Returns how many abilities changed.
        /// </summary>
        public int ModifyAbilityCooldown(System.Func<Spell4Entry, bool> match, double seconds, bool set)
        {
            if (abilities == null)
                return 0;

            int changed = 0;
            foreach (Spell4Entry ability in abilities.Where(match))
            {
                double current = abilityCooldowns.GetValueOrDefault(ability.Id);
                if (!set && current <= 0d)
                    continue;

                abilityCooldowns[ability.Id] = System.Math.Max(0d, set ? seconds : current + seconds);
                changed++;
            }

            return changed;
        }

        private List<Spell4Entry> abilities;
        private Spell4EffectsEntry basicAttackEffect;
        private readonly Dictionary<uint, double> abilityCooldowns = new();
        private double abilityTimer = 2d;
        private static readonly System.Random abilityRandom = new();

        /// <summary>
        /// Researched abilities (data/research, from Jabbithole): the first Damage row of the first auto-attack spell, and one spell per base id
        /// of the others.
        /// </summary>
        private void LoadAbilities()
        {
            if (abilities != null)
                return;

            abilities = new List<Spell4Entry>();
            var info = Research.CreatureResearch.Get(CreatureId, (int)Level);
            var seenBase = new HashSet<uint>();
            bool veteran = (Map as NexusForever.WorldServer.Game.Map.DungeonMapInstance)?.Difficulty == NexusForever.WorldServer.Game.Map.InstanceDifficulty.Veteran;
            if (info != null)
                foreach (uint spellId in info.Abilities)
                {
                    Spell4Entry entry = GameTableManager.Instance.Spell4.GetEntry(spellId);
                    if (entry == null)
                    {
                        Diagnostics.FailureLedger.Record("creature-ability-missing", $"spell {spellId}");
                        continue;
                    }

                    string description = entry.Description ?? "";
                    // Jabbithole lists both difficulties' spells: "[KV] e444 - Grond - Bloodburst - Veteran" summons level 50 veteran fleas
                    if (Encounters.DesignerTag.Parse(description) is { } tag && !tag.FitsMode(veteran))
                    {
                        Diagnostics.FailureLedger.Record("creature-ability-other-difficulty", $"spell {spellId}", description);
                        continue;
                    }

                    if (SelfSummonFilter && IsSpellSummon && SummonsCreature(entry.Id, CreatureId))
                    {
                        Diagnostics.FailureLedger.Record("creature-ability-self-summon", $"summoned creature {CreatureId} does not cast {spellId}", description);
                        continue;
                    }

                    bool basic = description.Contains("Auto-Attack", System.StringComparison.OrdinalIgnoreCase) || description.Contains("Basic Attack", System.StringComparison.OrdinalIgnoreCase);
                    if (basic && basicAttackEffect == null)
                        basicAttackEffect = Spell.GlobalSpellManager.Instance.GetSpell4EffectEntries(entry.Id)
                        .FirstOrDefault(e => (Spell.Static.SpellEffectType)e.EffectType == Spell.Static.SpellEffectType.Damage);
                    if (!basic && seenBase.Add(entry.Spell4BaseIdBaseSpell))
                        abilities.Add(entry);
                }
        }

        /// <summary>True when one of the spell's effects summons (SummonCreature, SummonTrap, SummonPet) the given creature.</summary>
        public static bool SummonsCreature(uint spell4Id, uint creatureId)
        {
            return Spell.GlobalSpellManager.Instance.GetSpell4EffectEntries(spell4Id).Any(e => e.DataBits00 == creatureId
                && (Spell.Static.SpellEffectType)e.EffectType is Spell.Static.SpellEffectType.SummonCreature or Spell.Static.SpellEffectType.SummonTrap or Spell.Static.SpellEffectType.SummonPet);
        }

        /// <summary>
        /// Researched NPC abilities (data/research, from Jabbithole). Auto-attacks are the melee swing (Update); every other ability is cast at the
        /// threat when in range, off its own cooldown (table value, else 10 s) with at least 3 s between casts (assumption).
        /// </summary>
        private void TryCastAbility(double lastTick, float distance)
        {
            abilityTimer -= lastTick;
            foreach (uint id in abilityCooldowns.Keys.ToList())
                abilityCooldowns[id] -= lastTick;

            if (abilityTimer > 0d || threat == null)
                return;

            LoadAbilities();
            var ready = abilities.Where(a => abilityCooldowns.GetValueOrDefault(a.Id) <= 0d && distance <= (a.TargetMaxRange > 0f ? a.TargetMaxRange : 8f) + 2f).ToList();
            if (ready.Count == 0)
                return;

            Spell4Entry chosen = ready[abilityRandom.Next(ready.Count)];
            abilityCooldowns[chosen.Id] = chosen.SpellCoolDown > 0u ? chosen.SpellCoolDown / 1000d : 10d;
            abilityTimer = 3d + chosen.CastTime / 1000d;
            try
            {
                CastSpell(chosen.Id, new Spell.SpellParameters { PrimaryTargetId = threat.Guid });
                Diagnostics.FailureLedger.Record("creature-spell", $"cast {chosen.Id} {chosen.Description}");
            }
            catch (System.Exception e)
            {
                Diagnostics.FailureLedger.Record("creature-spell-exception", $"{chosen.Id} {e.GetType().Name}", string.Join(" | ", (e.StackTrace ?? "").Split('\n').Take(3).Select(f => f.Trim())));
            }
        }

        protected override IEntityModel BuildEntityModel()
        {
            if (Type == EntityType.AiTurret)
                return new AiTurretEntityModel { CreatureId = CreatureId, QuestChecklistIdx = 0 };
            if (Type == EntityType.Destructible)
                return new DestructibleEntityModel { CreatureId = CreatureId };

            return new NonPlayerEntityModel
            {
                CreatureId = CreatureId,
                QuestChecklistIdx = 0
            };
        }

        private void CalculateProperties()
        {
            Creature2Entry creatureEntry = GameTableManager.Instance.Creature2.GetEntry(CreatureId);

            // TODO: research this some more
            /*float[] values = new float[200];

            CreatureLevelEntry levelEntry = GameTableManager.Instance.CreatureLevel.GetEntry(6);
            for (uint i = 0u; i < levelEntry.UnitPropertyValue.Length; i++)
                values[i] = levelEntry.UnitPropertyValue[i];

            Creature2ArcheTypeEntry archeTypeEntry = GameTableManager.Instance.Creature2ArcheType.GetEntry(creatureEntry.Creature2ArcheTypeId);
            for (uint i = 0u; i < archeTypeEntry.UnitPropertyMultiplier.Length; i++)
                values[i] *= archeTypeEntry.UnitPropertyMultiplier[i];

            Creature2DifficultyEntry difficultyEntry = GameTableManager.Instance.Creature2Difficulty.GetEntry(creatureEntry.Creature2DifficultyId);
            for (uint i = 0u; i < difficultyEntry.UnitPropertyMultiplier.Length; i++)
                values[i] *= archeTypeEntry.UnitPropertyMultiplier[i];

            Creature2TierEntry tierEntry = GameTableManager.Instance.Creature2Tier.GetEntry(creatureEntry.Creature2TierId);
            for (uint i = 0u; i < tierEntry.UnitPropertyMultiplier.Length; i++)
                values[i] *= archeTypeEntry.UnitPropertyMultiplier[i];

            for (uint i = 0u; i < levelEntry.UnitPropertyValue.Length; i++)
                SetProperty((Property)i, values[i]);*/
        }
    }
}
