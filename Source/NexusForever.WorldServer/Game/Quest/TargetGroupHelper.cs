using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Quest.Static;

namespace NexusForever.WorldServer.Game.Quest
{
    /// <summary>
    /// Resolves target groups (used by kill, talk and activate objectives) to the creatures they contain.
    /// Leaf groups: 1 creature ids, 3 creature factions, 4 creature factions to exclude, 12 creature races. Group 10 holds the ids of other groups and
    /// needs every one of them (the data pairs a race list with "not faction X"), group 11 needs any one of them (race lists together with faction lists).
    /// </summary>
    public static class TargetGroupHelper
    {
        private static readonly Dictionary<uint, HashSet<uint>> cache = new();
        private static readonly Dictionary<uint, HashSet<uint>> resolvedCache = new();
        private static readonly object gate = new();

        /// <summary>
        /// Every creature id a group stands for, for bots that need something to kill or talk to.
        /// </summary>
        public static HashSet<uint> ResolveAll(uint groupId)
        {
            lock (gate)
            {
                if (resolvedCache.TryGetValue(groupId, out HashSet<uint> cached))
                    return cached;

                var result = new HashSet<uint>(GetCreatures(groupId).Where(id => Matches(groupId, id, GameTableManager.Instance.Creature2.GetEntry(id), new HashSet<uint>())));
                foreach (Creature2Entry creature in GameTableManager.Instance.Creature2.Entries)
                    if (Matches(groupId, creature.Id, creature, new HashSet<uint>()))
                        result.Add(creature.Id);

                // groups defined by race, faction and similar filters match thousands of creatures; keeping every one of them (about a thousand
                // such groups) cost over a gigabyte, so only small results are kept (gt commands, the audit)
                if (result.Count <= 2000)
                    resolvedCache.Add(groupId, result);
                return result;
            }
        }

        private static HashSet<uint> spawnedCreatures;
        private static readonly Dictionary<uint, HashSet<uint>> spawnedCache = new();

        /// <summary>
        /// The creatures of a group that have a spawn somewhere in the world database (the only ones bots and events can use). Small and cached.
        /// </summary>
        public static HashSet<uint> ResolveSpawned(uint groupId)
        {
            lock (gate)
            {
                if (spawnedCache.TryGetValue(groupId, out HashSet<uint> cached))
                    return cached;

                spawnedCreatures ??= new HashSet<uint>(NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Select(s => s.Creature));
                var result = new HashSet<uint>(GetCreatures(groupId).Where(id => spawnedCreatures.Contains(id) && Matches(groupId, id, GameTableManager.Instance.Creature2.GetEntry(id), new HashSet<uint>())));
                foreach (uint id in spawnedCreatures)
                {
                    Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(id);
                    if (creature != null && !result.Contains(id) && Matches(groupId, id, creature, new HashSet<uint>()))
                        result.Add(id);
                }

                spawnedCache.Add(groupId, result);
                return result;
            }
        }

        /// <summary>
        /// True when the creature has a spawn somewhere in the world database.
        /// </summary>
        public static bool IsSpawned(uint creatureId)
        {
            lock (gate)
            {
                spawnedCreatures ??= new HashSet<uint>(NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Select(s => s.Creature));
                return spawnedCreatures.Contains(creatureId);
            }
        }

        /// <summary>
        /// Structure of a group and the groups it contains, for gt objective (not limited to once like <see cref="Describe"/>).
        /// </summary>
        public static string Structure(uint groupId)
        {
            var lines = new List<string>();
            DescribeInto(groupId, lines, new HashSet<uint>(), 0);
            return string.Join(" | ", lines);
        }

        public static string DescribeCreature(uint creatureId)
        {
            Creature2Entry c = GameTableManager.Instance.Creature2.GetEntry(creatureId);
            return c == null ? $"creature {creatureId} missing" :
                $"creature {creatureId} race {c.UnitRaceId} family {c.Creature2FamilyId} affiliation {c.Creature2AffiliationId} archetype {c.Creature2ArcheTypeId} tier {c.Creature2TierId} difficulty {c.Creature2DifficultyId} displayGroup {c.Creature2DisplayGroupId} model {c.Creature2ModelInfoId} outfit {c.Creature2OutfitGroupId} tract {c.Creature2TractId} visual {c.UnitVisualTypeId} resist {c.Creature2ResistId} faction {c.FactionId}";
        }

        /// <summary>
        /// The creature ids listed explicitly in the group and the groups below it (ignores how the groups are combined).
        /// </summary>
        public static HashSet<uint> GetCreatures(uint groupId)
        {
            lock (gate)
            {
                if (cache.TryGetValue(groupId, out HashSet<uint> creatures))
                    return creatures;

                creatures = new HashSet<uint>();
                Collect(groupId, creatures, new HashSet<uint>());
                cache.Add(groupId, creatures);
                return creatures;
            }
        }

        private static readonly HashSet<uint> described = new();

        /// <summary>
        /// Dump the structure of a group for diagnostics, once per group.
        /// </summary>
        public static string Describe(uint groupId)
        {
            if (!described.Add(groupId))
                return null;

            var lines = new List<string>();
            DescribeInto(groupId, lines, new HashSet<uint>(), 0);
            return string.Join(" | ", lines);
        }

        private static void DescribeInto(uint groupId, List<string> lines, HashSet<uint> visited, int depth)
        {
            if (!visited.Add(groupId) || depth > 4)
                return;

            TargetGroupEntry entry = GameTableManager.Instance.TargetGroup.GetEntry(groupId);
            if (entry == null)
            {
                lines.Add($"group {groupId} missing");
                return;
            }

            lines.Add($"group {groupId} type {entry.Type} data [{string.Join(",", entry.DataEntries.Where(d => d != 0u))}]");
            if ((TargetGroupType)entry.Type == TargetGroupType.OtherTargetGroup || (TargetGroupType)entry.Type == TargetGroupType.Unknown11)
                foreach (uint child in entry.DataEntries.Where(d => d != 0u))
                    DescribeInto(child, lines, visited, depth + 1);
        }

        public static bool Contains(uint groupId, uint creatureId)
        {
            return Matches(groupId, creatureId, GameTableManager.Instance.Creature2.GetEntry(creatureId), new HashSet<uint>());
        }

        private static bool Matches(uint groupId, uint creatureId, Creature2Entry creature, HashSet<uint> path)
        {
            TargetGroupEntry entry = GameTableManager.Instance.TargetGroup.GetEntry(groupId);
            if (entry == null || !path.Add(groupId))
                return false;

            try
            {
                IEnumerable<uint> data = entry.DataEntries.Where(d => d != 0u);
                switch ((TargetGroupType)entry.Type)
                {
                    case TargetGroupType.CreatureIdGroup:
                    case TargetGroupType.CreatureIdGroup9:
                        return data.Contains(creatureId);
                    case TargetGroupType.FactionIdGroup:
                        return creature != null && data.Contains(creature.FactionId);
                    case TargetGroupType.NotFactionIdGroup:
                        return creature != null && !data.Contains(creature.FactionId);
                    case TargetGroupType.CreatureRaceIdGroup:
                        return creature != null && data.Contains(creature.UnitRaceId);
                    case TargetGroupType.OtherTargetGroup:
                    {
                        List<uint> children = data.ToList();
                        return children.Count > 0 && children.All(child => Matches(child, creatureId, creature, path));
                    }
                    case TargetGroupType.Unknown11:
                        return data.Any(child => Matches(child, creatureId, creature, path));
                    default:
                        return false;
                }
            }
            finally
            {
                path.Remove(groupId);
            }
        }

        private static void Collect(uint groupId, HashSet<uint> creatures, HashSet<uint> visited)
        {
            if (!visited.Add(groupId))
                return;

            TargetGroupEntry entry = GameTableManager.Instance.TargetGroup.GetEntry(groupId);
            if (entry == null)
                return;

            foreach (uint data in entry.DataEntries)
            {
                if (data == 0u)
                    continue;

                switch ((TargetGroupType)entry.Type)
                {
                    case TargetGroupType.CreatureIdGroup:
                    case TargetGroupType.CreatureIdGroup9:
                        creatures.Add(data);
                        break;
                    case TargetGroupType.OtherTargetGroup:
                    case TargetGroupType.Unknown11:
                        Collect(data, creatures, visited);
                        break;
                }
            }
        }
    }
}
