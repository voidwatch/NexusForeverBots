using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;

namespace NexusForever.WorldServer.Game.Quest
{
    /// <summary>
    /// Item imbuements: Item2.ItemImbuementId points to an ItemImbuement row with up to 15 quests (Quest2Id00-14, a chain) and a reward per step
    /// (ItemImbuementRewardId00-14). Archived Jabbithole quest pages list these quests (category "Item Imbuements") as "Started by" the items and
    /// "Completed by" nothing, and Quest2.Flags 0x40000 marks 563 of the 597 chain entries. So: equipping the item offers the first unfinished quest
    /// of its chain, the quest completes without a receiver (docs/ASSUMPTIONS.md "Item imbuement quests", confidence likely).
    /// </summary>
    public static class ItemImbuements
    {
        private static readonly object gate = new();
        private static Dictionary<uint, (uint Quest, uint Reward)[]> chains;
        private static Dictionary<uint, (uint Imbuement, int Step)> stepByQuest;
        private static List<Item2Entry> imbuedItems;

        private static void EnsureIndex()
        {
            if (chains != null)
                return;

            lock (gate)
            {
                if (chains != null)
                    return;

                var c = new Dictionary<uint, (uint, uint)[]>();
                var s = new Dictionary<uint, (uint, int)>();
                foreach (ItemImbuementEntry e in GameTableManager.Instance.ItemImbuement.Entries)
                {
                    uint[] quests = { e.Quest2Id00, e.Quest2Id01, e.Quest2Id02, e.Quest2Id03, e.Quest2Id04, e.Quest2Id05, e.Quest2Id06, e.Quest2Id07,
                        e.Quest2Id08, e.Quest2Id09, e.Quest2Id10, e.Quest2Id11, e.Quest2Id12, e.Quest2Id13, e.Quest2Id14 };
                    uint[] rewards = { e.ItemImbuementRewardId00, e.ItemImbuementRewardId01, e.ItemImbuementRewardId02, e.ItemImbuementRewardId03,
                        e.ItemImbuementRewardId04, e.ItemImbuementRewardId05, e.ItemImbuementRewardId06, e.ItemImbuementRewardId07, e.ItemImbuementRewardId08,
                        e.ItemImbuementRewardId09, e.ItemImbuementRewardId10, e.ItemImbuementRewardId11, e.ItemImbuementRewardId12, e.ItemImbuementRewardId13,
                        e.ItemImbuementRewardId14 };
                    var chain = new List<(uint, uint)>();
                    for (int i = 0; i < quests.Length; i++)
                    {
                        if (quests[i] == 0u)
                            continue;
                        chain.Add((quests[i], rewards[i]));
                        s.TryAdd(quests[i], (e.Id, chain.Count - 1));
                    }
                    if (chain.Count > 0)
                        c[e.Id] = chain.ToArray();
                }

                stepByQuest = s;
                imbuedItems = GameTableManager.Instance.Item.Entries.Where(i => i.ItemImbuementId != 0u && c.ContainsKey(i.ItemImbuementId)).ToList();
                chains = c;
            }
        }

        /// <summary>
        /// Quests (with the reward of each step) of the imbuement, in order; empty when the imbuement has none.
        /// </summary>
        public static IReadOnlyList<(uint Quest, uint Reward)> Chain(uint itemImbuementId)
        {
            EnsureIndex();
            return chains.TryGetValue(itemImbuementId, out var chain) ? chain : System.Array.Empty<(uint, uint)>();
        }

        public static bool IsImbuementQuest(uint questId)
        {
            EnsureIndex();
            return stepByQuest.ContainsKey(questId);
        }

        /// <summary>
        /// ItemImbuementReward row of the first imbuement that has the quest as a step, or null (a quest can be a step of several imbuements
        /// with different rewards: the gear code uses <see cref="Earned"/> per item).
        /// </summary>
        public static ItemImbuementRewardEntry RewardFor(uint questId)
        {
            EnsureIndex();
            if (!stepByQuest.TryGetValue(questId, out var step))
                return null;
            uint reward = chains[step.Imbuement][step.Step].Reward;
            return reward == 0u ? null : GameTableManager.Instance.ItemImbuementReward?.GetEntry(reward);
        }

        public const uint RewardStat = 0u, RewardItemSpecial = 1u, RewardQuality = 4u, RewardItemLevel = 5u, RewardRuneSlot = 6u;

        /// <summary>
        /// Reward types the gear code applies (docs/ASSUMPTIONS.md "Imbuement rewards", certain from 31 archived Jabbithole tooltips):
        /// 0 = property RewardObjectId gets RewardValueFloat x the item's unrounded secondary rating sum, 1 = ItemSpecial RewardObjectId
        /// (its on-equip spell, the tooltip's "Special:" line).
        /// </summary>
        /// Types 4, 5 and 6 follow the client's item builder, which sums ItemImbuementReward.RewardValue of the finished steps by type:
        /// 4 = quality steps (quality + sum, clamped to 2-7, the ItemQuality rows with rune slots), 5 = item levels added to the level the stat
        /// budget is computed from, 6 = rune slots added to ItemQuality.DefaultRunes and MaxRunes (at most 8). docs/ASSUMPTIONS.md "Imbuement rewards".
        public static bool IsApplied(ItemImbuementRewardEntry reward) =>
            reward.ItemImbuementRewardTypeEnum is RewardStat or RewardItemSpecial or RewardQuality or RewardItemLevel or RewardRuneSlot;

        /// <summary>Sum of RewardValue over the item's finished imbuement steps of one reward type.</summary>
        public static uint Sum(Item2Entry item, uint type, System.Func<uint, bool> questCompleted)
        {
            uint sum = 0u;
            foreach (ItemImbuementRewardEntry reward in Earned(item, questCompleted))
                if (reward.ItemImbuementRewardTypeEnum == type)
                    sum += reward.RewardValue;
            return sum;
        }

        /// <summary>
        /// Rewards of the steps of the item's imbuement whose quest is completed.
        /// </summary>
        public static IEnumerable<ItemImbuementRewardEntry> Earned(Item2Entry item, System.Func<uint, bool> questCompleted)
        {
            if (item == null || item.ItemImbuementId == 0u)
                yield break;
            foreach ((uint quest, uint reward) in Chain(item.ItemImbuementId))
            {
                if (reward == 0u || !questCompleted(quest))
                    continue;
                ItemImbuementRewardEntry entry = GameTableManager.Instance.ItemImbuementReward?.GetEntry(reward);
                if (entry != null)
                    yield return entry;
            }
        }

        /// <summary>
        /// Items that carry an imbuement with at least one quest (1621 in build 16042), for bots and audits.
        /// </summary>
        public static IReadOnlyList<Item2Entry> ImbuedItems()
        {
            EnsureIndex();
            return imbuedItems;
        }
    }
}
