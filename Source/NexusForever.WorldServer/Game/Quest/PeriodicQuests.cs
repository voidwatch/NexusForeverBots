using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups;

namespace NexusForever.WorldServer.Game.Quest
{
    /// <summary>
    /// Rotating daily quests and Contract Board offers, drawn exactly as the retail client draws them (docs/PROTOCOL.md "Periodic quests and contracts"):
    /// the client builds a tree from the tables (category -> set -> group -> quest, insert order = table row order, keys = running weight sums)
    /// and draws it with a Mersenne Twister seeded per pick from node id + picks left + the seed the server sends in ServerQuestInit / ServerQuestPeriodicReset.
    /// The server uses the same draw and seed, so the quests it accepts are the ones the client shows.
    /// Code: tree build, quest insert, redraw, node draws, MT19937.
    /// </summary>
    public static class PeriodicQuests
    {
        private sealed class Node
        {
            public uint Id;
            public uint Offered;
            public ulong Total;
            public uint Weight;
            public readonly List<(ulong Key, Node Child)> Children = new();
        }

        private static readonly object indexLock = new();
        private static List<Node> categories;
        private static Dictionary<uint, Node> groups;
        private static HashSet<uint> pooled;
        private static readonly Dictionary<(uint Type, uint Quality), Node> contractGroups = new();

        private static readonly ConcurrentDictionary<ulong, HashSet<uint>> offers = new();

        private static void EnsureIndex()
        {
            if (categories != null)
                return;

            lock (indexLock)
            {
                if (categories != null)
                    return;

                var tables = GameTableManager.Instance;
                var categoryNodes = new SortedDictionary<uint, Node>();
                var setNodes = new Dictionary<uint, Node>();
                var groupNodes = new Dictionary<uint, Node>();

                // tree build: groups in row order; a set joins its category when the first group of the set is read
                foreach (PeriodicQuestGroupEntry group in tables.PeriodicQuestGroup.Entries)
                {
                    PeriodicQuestSetEntry set = tables.PeriodicQuestSet.GetEntry(group.PeriodicQuestSetId);
                    if (set == null)
                        continue;

                    if (!setNodes.TryGetValue(set.Id, out Node setNode))
                    {
                        if (!categoryNodes.TryGetValue(set.PeriodicQuestSetCategoryId, out Node categoryNode))
                        {
                            PeriodicQuestSetCategoryEntry category = tables.PeriodicQuestSetCategory.GetEntry(set.PeriodicQuestSetCategoryId);
                            if (category == null)
                                continue;

                            categoryNode = new Node { Id = category.Id, Offered = category.PeriodicSetsOffered };
                            categoryNodes.Add(category.Id, categoryNode);
                        }

                        setNode = new Node { Id = set.Id, Offered = set.PeriodicGroupsOffered, Weight = set.Weight };
                        setNodes.Add(set.Id, setNode);
                        categoryNode.Total += set.Weight;
                        categoryNode.Children.Add((categoryNode.Total, setNode));
                    }

                    var groupNode = new Node { Id = group.Id, Offered = group.PeriodicQuestsOffered, Weight = group.Weight };
                    groupNodes[group.Id] = groupNode;
                    setNode.Total += group.Weight;
                    setNode.Children.Add((setNode.Total, groupNode));
                    contractGroups[(group.ContractTypeEnum, group.ContractQualityEnum)] = groupNode;
                }

                // quest insert: a quest with a repeat period and a weight joins its group (group 1 when its group does not exist), in row order
                var inPool = new HashSet<uint>();
                foreach (Quest2Entry quest in tables.Quest2.Entries)
                {
                    if (quest.QuestRepeatPeriodEnum == 0u || quest.PeriodicQuestWeight == 0u)
                        continue;

                    if (!groupNodes.TryGetValue(quest.PeriodicQuestGroupId, out Node groupNode) && !groupNodes.TryGetValue(1u, out groupNode))
                        continue;

                    groupNode.Total += quest.PeriodicQuestWeight;
                    groupNode.Children.Add((groupNode.Total, new Node { Id = quest.Id, Weight = quest.PeriodicQuestWeight }));
                    inPool.Add(quest.Id);
                }

                groups = groupNodes;
                pooled = inPool;
                categories = categoryNodes.Values.ToList();
                Diagnostics.FailureLedger.Record("periodic-pool", "client draw tree built",
                    $"{categories.Count} categories, {setNodes.Count} sets, {groupNodes.Count} groups, {inPool.Count} quests");
            }
        }

        public static DateTime DayOf(DateTime now) => InstanceLockouts.NextDailyReset(now).AddDays(-1d);

        /// <summary>
        /// The seed sent to clients for the day that contains <paramref name="now"/>. The value itself is free (the client draws with whatever it gets):
        /// one seed per realm and daily reset, the number of days since 0001-01-01 (ASSUMPTIONS "Periodic quests").
        /// </summary>
        public static ulong Seed(DateTime now) => (ulong)(DayOf(now).Ticks / TimeSpan.TicksPerDay);

        /// <summary>Whether the quest takes part in the client's draw; other quests are always offered.</summary>
        public static bool IsPooled(uint questId)
        {
            EnsureIndex();
            return pooled.Contains(questId);
        }

        /// <summary>
        /// Quest ids offered on the day that contains <paramref name="now"/>. The offer is realm wide (one seed), the character id is not used.
        /// </summary>
        public static HashSet<uint> Offered(ulong characterId, DateTime now) => OfferedForSeed(Seed(now));

        public static HashSet<uint> OfferedForSeed(ulong seed)
        {
            EnsureIndex();
            return offers.GetOrAdd(seed, s =>
            {
                foreach (ulong old in offers.Keys.Where(k => k + 3 < s).ToList())
                    offers.TryRemove(old, out _);

                var offered = new HashSet<uint>();
                uint seed32 = (uint)s;
                // redraw: categories by id, then the drawn sets in draw order, their drawn groups, their drawn quests
                foreach (Node category in categories)
                    foreach (Node set in Draw(category, seed32))
                        foreach (Node group in Draw(set, seed32))
                            foreach (Node quest in Draw(group, seed32))
                                offered.Add(quest.Id);

                Diagnostics.FailureLedger.Record("periodic-offer", "offers drawn for a seed", $"{offered.Count} quests");
                return offered;
            });
        }

        /// <summary>
        /// The quests of the Contract Board group for a contract type and quality that are offered today (ContractsLib.GetPeriodicContracts).
        /// </summary>
        public static IEnumerable<uint> OfferedContracts(uint type, uint quality, DateTime now)
        {
            EnsureIndex();
            if (!contractGroups.TryGetValue((type, quality), out Node group))
                return Enumerable.Empty<uint>();

            return Draw(group, (uint)Seed(now)).Select(n => n.Id);
        }

        /// <summary>
        /// One node draw: all children when the node offers at least as many as it has, else Offered picks without replacement.
        /// Each pick seeds MT19937 with node id + picks left + seed, rolls floor(NextDouble * total) + 1 and takes the first child whose running sum
        /// reaches it; the child's weight is then taken out of the sums. A roll past the last child aborts the node (the client returns an error and offers nothing).
        /// </summary>
        private static List<Node> Draw(Node node, uint seed32)
        {
            if (node.Offered >= node.Children.Count)
                return node.Children.Select(c => c.Child).ToList();

            var left = node.Children.ToList();
            var picked = new List<Node>();
            ulong total = node.Total;
            for (uint remaining = node.Offered; remaining > 0u; remaining--)
            {
                ulong roll = 0ul;
                if (total > 1ul)
                {
                    var twister = new MersenneTwister(unchecked(node.Id + remaining + seed32));
                    roll = (ulong)(long)(twister.NextDouble() * total);
                }

                int index = left.FindIndex(c => c.Key >= roll + 1ul);
                if (index < 0)
                {
                    Diagnostics.FailureLedger.Record("periodic-draw-empty", $"node {node.Id}", "roll past the last weight, the client offers nothing here");
                    return new List<Node>();
                }

                Node chosen = left[index].Child;
                picked.Add(chosen);
                if (remaining > 1u)
                {
                    left.RemoveAt(index);
                    for (int i = index; i < left.Count; i++)
                        left[i] = (left[i].Key - chosen.Weight, left[i].Child);
                    total -= chosen.Weight;
                }
            }

            return picked;
        }

        public static bool IsOffered(Player player, QuestInfo info)
        {
            if (info.Entry.PeriodicQuestGroupId == 0u || !IsPooled(info.Entry.Id))
                return true;

            DateTime now = DateTime.UtcNow;
            PeriodicQuestGroupEntry group = GameTableManager.Instance.PeriodicQuestGroup.GetEntry(info.Entry.PeriodicQuestGroupId);
            if (group != null && group.ContractTypeEnum != 0u)
                return BoardOffer(group.ContractTypeEnum, now).Contains(info.Entry.Id);

            return Offered(player.CharacterId, now).Contains(info.Entry.Id);
        }

        /// <summary>
        /// The Good quality contract of a type is not drawn by the client: the server names it in ServerQuestContractGoodQualityChanged (client handler).
        /// Here it is one pick from the type's quality 1 group with the client's draw (ASSUMPTIONS "Contract Board": the retail choice is unknown).
        /// </summary>
        public static uint GoodContract(uint type, DateTime now)
        {
            EnsureIndex();
            if (!contractGroups.TryGetValue((type, 1u), out Node group) || group.Children.Count == 0)
                return 0u;

            var single = new Node { Id = group.Id, Offered = 1u, Total = group.Total };
            single.Children.AddRange(group.Children);
            return Draw(single, (uint)Seed(now)).FirstOrDefault()?.Id ?? 0u;
        }

        /// <summary>What the Contract Board of a type shows: the Good contract, then the draws of the quality 2 and 3 groups.</summary>
        public static HashSet<uint> BoardOffer(uint type, DateTime now)
        {
            var offer = new HashSet<uint>();
            uint good = GoodContract(type, now);
            if (good != 0u)
                offer.Add(good);
            for (uint quality = 2u; quality < 4u; quality++)
                offer.UnionWith(OfferedContracts(type, quality, now));
            return offer;
        }

        /// <summary>
        /// Quests of the group the player has in the log (accepted or achieved) must stay below MaxPeriodicQuestsAllowed.
        /// </summary>
        public static bool GroupHasRoom(uint groupId, int inLog)
        {
            EnsureIndex();
            PeriodicQuestGroupEntry group = GameTableManager.Instance.PeriodicQuestGroup.GetEntry(groupId);
            return group == null || group.MaxPeriodicQuestsAllowed == 0u || inLog < group.MaxPeriodicQuestsAllowed;
        }

        /// <summary>MT19937 as the client has it: init_genrand, standard twist and tempering, NextDouble = u32 / 2^32.</summary>
        private sealed class MersenneTwister
        {
            private readonly uint[] mt = new uint[624];
            private int index;

            public MersenneTwister(uint seed)
            {
                mt[0] = seed;
                for (int i = 1; i < 624; i++)
                    mt[i] = unchecked(1812433253u * (mt[i - 1] ^ (mt[i - 1] >> 30)) + (uint)i);
                index = 624;
            }

            public uint Next()
            {
                if (index >= 624)
                {
                    for (int i = 0; i < 624; i++)
                    {
                        uint y = (mt[i] & 0x80000000u) | (mt[(i + 1) % 624] & 0x7fffffffu);
                        mt[i] = mt[(i + 397) % 624] ^ (y >> 1) ^ ((y & 1u) != 0u ? 0x9908b0dfu : 0u);
                    }
                    index = 0;
                }

                uint v = mt[index++];
                v ^= v >> 11;
                v ^= (v << 7) & 0x9d2c5680u;
                v ^= (v << 15) & 0xefc60000u;
                v ^= v >> 18;
                return v;
            }

            public double NextDouble() => Next() * (1.0 / 4294967296.0);
        }
    }
}
