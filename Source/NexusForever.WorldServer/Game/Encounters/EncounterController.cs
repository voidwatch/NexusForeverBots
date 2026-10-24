using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Static;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Research;
using NexusForever.WorldServer.Game.Spell;

namespace NexusForever.WorldServer.Game.Encounters
{
    /// <summary>
    /// Runs one boss fight from its <see cref="EncounterDefinition"/>: phase changes by health, ability timers, add waves, enrage.
    /// The creature keeps doing its melee through the normal fight code, the controller adds the scripted part.
    /// </summary>
    public sealed class EncounterController
    {
        private static readonly Random random = new();

        public EncounterDefinition Definition { get; }
        public int Phase { get; private set; } = -1;
        public double Elapsed { get; private set; }

        private readonly NonPlayer boss;
        private ushort worldId;
        private readonly Dictionary<AbilityDefinition, double> abilityTimers = new();
        private readonly Dictionary<AddDefinition, double> addTimers = new();
        private readonly Dictionary<string, uint> resolved = new();
        private readonly List<NonPlayer> adds = new();
        private double castGap;
        private int forcedPhase;
        private readonly List<NonPlayer> phaseAdds = new();
        private int enrageStacks;
        private double enrageTimer;
        private bool enraged;

        public EncounterController(NonPlayer boss, EncounterDefinition definition)
        {
            this.boss = boss;
            Definition = definition;
        }

        public void Update(double lastTick)
        {
            if (boss.Map == null || boss.IsDead)
                return;

            if (boss.Map is NexusForever.WorldServer.Game.Map.MapInstance current)
                worldId = (ushort)current.Entry.Id;

            if (Elapsed == 0d)
            {
                Diagnostics.BotFeed.Add("-", boss.Level, boss.Zone != null ? $"{boss.Zone.Id}" : "-", $"boss engaged: {Definition.Boss}");
                Diagnostics.FailureLedger.Record($"encounter-engaged-{EncounterClass.Of(Definition)}", Definition.Boss, $"names {Definition.Names ?? "-"}, timers {Definition.Confidence ?? "guess"}");
            }

            Elapsed += lastTick;
            castGap -= lastTick;

            double percent = boss.MaxHealth == 0u ? 100d : boss.Health * 100d / boss.MaxHealth;
            int wanted = Math.Max(Math.Max(0, Definition.Phases.Count(p => p.Health >= percent) - 1), forcedPhase);
            if (Definition.Phases.Count > 0 && wanted != Phase)
                EnterPhase(wanted);

            if (Phase >= 0 && Phase < Definition.Phases.Count)
            {
                PhaseDefinition phase = Definition.Phases[Phase];
                foreach (AbilityDefinition ability in phase.Abilities)
                {
                    abilityTimers[ability] -= lastTick;
                    if (abilityTimers[ability] > 0d || castGap > 0d)
                        continue;

                    // a repeat never comes before the cast (Spell4 CastTime) is over, plus the 1.5 s gap between scripted casts
                    abilityTimers[ability] = ability.Every > 0d ? Math.Max(ability.Every, CastSeconds(ability) + 2d) : double.MaxValue;
                    Cast(ability);
                }

                foreach (AddDefinition add in phase.Adds)
                {
                    addTimers[add] -= lastTick;
                    if (addTimers[add] > 0d)
                        continue;

                    addTimers[add] = add.Every > 0d ? add.Every : double.MaxValue;
                    SpawnAdds(add);
                }
            }

            if (Phase >= 0 && Phase < Definition.Phases.Count && Definition.Phases[Phase].NextWhenAddsDead && phaseAdds.Count > 0 && phaseAdds.All(a => a.IsDead || a.Map == null)
                && Phase + 1 < Definition.Phases.Count)
                forcedPhase = Phase + 1;

            if (Definition.Enrage > 0d && Elapsed >= Definition.Enrage)
            {
                if (!enraged)
                {
                    enraged = true;
                    Diagnostics.FailureLedger.Record("encounter-enrage", Definition.Boss, $"after {Elapsed:0} s");
                }

                enrageTimer -= lastTick;
                if (enrageTimer <= 0d && enrageStacks < 10)
                {
                    enrageTimer = 20d;
                    enrageStacks++;
                    boss.SetDamageScale(1f + 0.5f * enrageStacks);
                }
            }
        }

        private void EnterPhase(int index)
        {
            Phase = index;
            PhaseDefinition phase = Definition.Phases[index];
            phaseAdds.Clear();
            boss.ScriptedInvulnerable = phase.Invulnerable;
            abilityTimers.Clear();
            addTimers.Clear();
            foreach (AbilityDefinition ability in phase.Abilities)
                abilityTimers[ability] = ability.First;
            foreach (AddDefinition add in phase.Adds)
                addTimers[add] = add.First;

            Diagnostics.FailureLedger.Record("encounter-phase", $"{Definition.Boss}: {phase.Name ?? "phase " + (index + 1)}", $"source {Definition.Source ?? "hand written"}");
            if (!string.IsNullOrEmpty(phase.Say))
                foreach (Player player in Attackers().OfType<Player>())
                    player.SendSystemMessage($"{Definition.Boss}: {phase.Say}");
        }

        private IEnumerable<WorldEntity> Attackers()
        {
            return boss.Attackers.Where(a => a.Map == boss.Map && a is not UnitEntity { IsDead: true });
        }

        private void Cast(AbilityDefinition ability)
        {
            uint spellId = Resolve(ability.Spell);
            if (spellId == 0u)
                return;

            WorldEntity target = boss.Threat;
            if (ability.Target == "random")
            {
                var candidates = Attackers().Where(a => Vector3.Distance(a.Position, boss.Position) <= 60f).ToList();
                if (candidates.Count > 0)
                    target = candidates[random.Next(candidates.Count)];
            }
            else if (ability.Target == "self")
                target = null;

            castGap = 1.5d;
            try
            {
                boss.CastSpell(spellId, new SpellParameters { PrimaryTargetId = target?.Guid ?? 0u });
                Diagnostics.FailureLedger.Record("encounter-cast", $"{Definition.Boss}: spell {spellId}", $"phase {Phase + 1}");
            }
            catch (Exception e)
            {
                Diagnostics.FailureLedger.Record("encounter-cast-exception", $"{Definition.Boss}: spell {spellId}", $"{e.GetType().Name} {string.Join(" | ", (e.StackTrace ?? "").Split('\n').Take(3).Select(f => f.Trim()))}");
            }
        }

        private double CastSeconds(AbilityDefinition ability)
        {
            uint id = Resolve(ability.Spell);
            var entry = id == 0u ? null : GameTableManager.Instance.Spell4.GetEntry(id);
            return entry == null ? 0d : Math.Max(entry.CastTime, entry.ChannelMaxTime) / 1000d;
        }

        /// <summary>A spell id from the definition: a number, or a name matched against the researched abilities of the boss.</summary>
        private uint Resolve(string spell)
        {
            if (resolved.TryGetValue(spell, out uint cached))
                return cached;

            uint id = 0u;
            if (!uint.TryParse(spell, out id))
            {
                id = 0u;
                var info = CreatureResearch.Get(boss.CreatureId, (int)boss.Level);
                string wanted = CreatureResearch.Slug(spell);
                bool veteran = EncounterManager.IsVeteran(boss);
                int best = int.MaxValue;
                if (info != null)
                    foreach (uint candidate in info.Abilities)
                    {
                        var entry = GameTableManager.Instance.Spell4.GetEntry(candidate);
                        var baseEntry = entry == null ? null : GameTableManager.Instance.Spell4Base.GetEntry(entry.Spell4BaseIdBaseSpell);
                        string name = baseEntry == null ? null : GameTableManager.Instance.GetTextTable(Language.English)?.GetEntry(baseEntry.LocalizedTextIdName);
                        if (name == null || CreatureResearch.Slug(name) != wanted)
                            continue;

                        // a base spell of this difficulty over a proxy or a spell of the other difficulty (designer description)
                        DesignerTag tag = DesignerTag.Parse(entry.Description);
                        int rank = tag == null || (!tag.IsHelper && tag.FitsMode(veteran)) ? 0 : 1;
                        if (rank < best)
                        {
                            best = rank;
                            id = candidate;
                        }
                    }
            }

            if (id == 0u)
                id = FindSpellByName(spell);

            if (id == 0u)
                Diagnostics.FailureLedger.Record("encounter-spell-unresolved", $"{Definition.Boss}: {spell}");

            resolved[spell] = id;
            return id;
        }

        /// <summary>
        /// A spell the research did not list for the boss: a spell with that text name whose designer description names the boss
        /// ("[STL] e314 - Stormtalon - Static Wave") or carries the encounter id (e###) of a spell the boss was seen casting (Kuralak's spells
        /// say "Genetic Architect"). Base spells of the current difficulty first, then the lowest tier.
        /// </summary>
        private uint FindSpellByName(string name)
        {
            string wanted = CreatureResearch.Slug(name);
            // any word of the boss name ("Kraggar the Earth-Render" is "Gronyx - Kraggar - Ground Slam" in the descriptions)
            string[] bossWords = Definition.Boss.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(',', '\'')).Where(w => w.Length >= 4).ToArray();
            if (bossWords.Length == 0)
                bossWords = new[] { Definition.Boss };
            var encounters = new HashSet<uint>();
            var info = CreatureResearch.Get(boss.CreatureId, (int)boss.Level);
            if (info != null)
                foreach (uint id in info.Abilities)
                    if (DesignerTag.Parse(GameTableManager.Instance.Spell4.GetEntry(id)?.Description) is DesignerTag known)
                        encounters.Add(known.Encounter);

            bool veteran = EncounterManager.IsVeteran(boss);
            var text = GameTableManager.Instance.GetTextTable(Language.English);
            foreach (var baseEntry in GameTableManager.Instance.Spell4Base.Entries)
            {
                if (CreatureResearch.Slug(text?.GetEntry(baseEntry.LocalizedTextIdName)) != wanted)
                    continue;

                var spell = GameTableManager.Instance.Spell4.Entries
                    .Where(e => e.Spell4BaseIdBaseSpell == baseEntry.Id)
                    .Select(e => (entry: e, tag: DesignerTag.Parse(e.Description)))
                    .Where(e => bossWords.Any(w => (e.entry.Description ?? "").Contains(w, StringComparison.OrdinalIgnoreCase)) || (e.tag != null && encounters.Contains(e.tag.Encounter)))
                    .OrderBy(e => e.tag == null || (!e.tag.IsHelper && e.tag.FitsMode(veteran)) ? 0 : 1)
                    // warplot (PvP) copies, test rows and proxies of the same spell last
                    .ThenBy(e => (e.entry.Description ?? "").Contains("Warplot", StringComparison.OrdinalIgnoreCase) || (e.entry.Description ?? "").Contains("[TEST]", StringComparison.Ordinal)
                        || (e.entry.Description ?? "").Contains("Proxy", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(e => e.entry.TierIndex)
                    .Select(e => e.entry).FirstOrDefault();
                if (spell != null)
                    return spell.Id;
            }

            return 0u;
        }

        private void SpawnAdds(AddDefinition add)
        {
            uint creatureId = Resolve(add);
            if (creatureId == 0u)
                return;

            for (int i = 0; i < add.Count; i++)
            {
                double angle = random.NextDouble() * Math.PI * 2d;
                Vector3 position = boss.Position + new Vector3((float)Math.Cos(angle) * 8f, 0f, (float)Math.Sin(angle) * 8f);
                NonPlayer unit = CreatureSpawner.Spawn(boss.Map, creatureId, position, 1200d, this);
                if (unit != null)
                {
                    adds.Add(unit);
                    phaseAdds.Add(unit);
                }
            }

            Diagnostics.FailureLedger.Record("encounter-adds", $"{Definition.Boss}: {add.Count} x {add.Creature}");
        }

        private uint Resolve(AddDefinition add)
        {
            string key = "add:" + add.Creature;
            if (resolved.TryGetValue(key, out uint cached))
                return cached;

            uint id = uint.TryParse(add.Creature, out uint numeric) ? numeric : 0u;
            if (id == 0u)
            {
                string wanted = CreatureResearch.Slug(add.Creature);
                var text = GameTableManager.Instance.GetTextTable(Language.English);
                // prefer a creature with the level of the boss (the same name exists per level and difficulty)
                id = GameTableManager.Instance.Creature2.Entries
                    .Where(c => CreatureResearch.Slug(text?.GetEntry(c.LocalizedTextIdName)) == wanted)
                    .OrderBy(c => Math.Abs((int)c.MinLevel - (int)boss.Level))
                    .Select(c => c.Id).FirstOrDefault();
            }

            if (id == 0u)
                Diagnostics.FailureLedger.Record("encounter-add-unresolved", $"{Definition.Boss}: {add.Creature}");

            resolved[key] = id;
            return id;
        }

        /// <summary>The fight ended without a kill (everyone dead or gone): back to the start.</summary>
        public void Reset()
        {
            Despawn();
            Phase = -1;
            forcedPhase = 0;
            boss.ScriptedInvulnerable = false;
            Elapsed = 0d;
            enrageStacks = 0;
            enraged = false;
            boss.SetDamageScale(1f);
            Diagnostics.FailureLedger.Record("encounter-reset", Definition.Boss, "no attacker left");
        }

        public void OnBossDeath()
        {
            boss.ScriptedInvulnerable = false;
            Despawn();
            Diagnostics.BotFeed.Add("-", boss.Level, boss.Zone != null ? $"{boss.Zone.Id}" : "-", $"boss killed: {Definition.Boss} after {Elapsed:0} s");
            if (boss.Map is NexusForever.WorldServer.Game.Map.MapInstance instance)
            {
                Bots.DungeonTeams.Instance.OnBossKilled(instance);
                (instance as NexusForever.WorldServer.Game.Map.DungeonMapInstance)?.OnBossKilled(boss);
                var mapType = (NexusForever.WorldServer.Game.Map.Static.MapType)(GameTableManager.Instance.World.GetEntry(instance.Entry.Id)?.Type ?? 0u);
                foreach (var other in NexusForever.Shared.Network.NetworkManager<NexusForever.WorldServer.Network.WorldSession>.Instance.GetSessions())
                    if (other.Player != null && other.Player.Map == instance && !other.Player.IsDead)
                        Groups.InstanceLockouts.OnBossKilled(other.Player, mapType, (ushort)instance.Entry.Id, boss.CreatureId);
            }
            Diagnostics.FailureLedger.Record("encounter-boss-killed", $"world {worldId} {Definition.Boss} (entity {boss.Guid}, creature {boss.CreatureId})", $"after {Elapsed:0} s, phase {Phase + 1}, source {Definition.Source ?? "hand written"}");
            // one kind per boss: the category header counts the distinct bosses killed with sourced, partly sourced, guide or generated scripts
            Diagnostics.FailureLedger.Record($"encounter-killed-{EncounterClass.Of(Definition)}", Definition.Boss, $"names {Definition.Names ?? "-"}, timers {Definition.Confidence ?? "guess"}");
        }

        private void Despawn()
        {
            foreach (NonPlayer unit in adds)
                if (unit.Map != null && !unit.IsDead)
                    unit.RemoveFromMap();
            adds.Clear();
        }
    }
}
