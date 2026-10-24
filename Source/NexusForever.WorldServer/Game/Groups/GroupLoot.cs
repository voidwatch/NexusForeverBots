using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.Network;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Network;

namespace NexusForever.WorldServer.Game.Groups
{
    /// <summary>
    /// Decides who gets a dropped item in a group, using the group's loot rules (set by the leader with ClientGroupLootRulesChange,
    /// default NeedBeforeGreed for both rules with threshold Good, the values ServerGroupJoin tells the client). Items below the threshold
    /// quality use the normal rule, the rest the threshold rule (field names of GroupLib.SetLootRules in the client UI, docs/ASSUMPTIONS.md "Group loot").
    /// </summary>
    public static class GroupLoot
    {
        private static readonly Random random = new();
        private static readonly ConcurrentDictionary<ulong, int> roundRobin = new();

        public static Player Choose(Player killer, ItemInfo item)
        {
            PlayerGroup group = GroupManager.Instance.GetGroup(killer.CharacterId);
            if (group == null || group.Members.Count < 2)
                return killer;

            LootRule rule = item.Entry.ItemQualityId < (uint)group.ThresholdQuality ? group.NormalRule : group.ThresholdRule;

            List<Player> eligible = group.Members
                .Select(m => NetworkManager<WorldSession>.Instance.GetSession(s => s.Player != null && s.Player.CharacterId == m.CharacterId)?.Player)
                .Where(p => p != null && p.Map == killer.Map && p.Inventory.HasFreeSlot())
                .ToList();
            if (eligible.Count == 0)
                return killer;

            Player winner;
            switch (rule)
            {
                case LootRule.RoundRobin:
                    winner = eligible[(roundRobin.AddOrUpdate(group.Id, 0, (_, v) => v + 1) & int.MaxValue) % eligible.Count];
                    break;
                case LootRule.Master:
                    winner = eligible.FirstOrDefault(p => p.CharacterId == group.LeaderId) ?? killer;
                    break;
                case LootRule.NeedBeforeGreed:
                {
                    // members whose class can use the item roll need, everyone else rolls greed; need beats greed
                    bool usable(Player p) => item.Entry.ClassRequired == 0u || item.Entry.ClassRequired == (uint)p.Class;
                    var rolls = eligible.Select(p => (Player: p, Need: usable(p), Roll: random.Next(1, 101))).ToList();
                    winner = rolls.OrderByDescending(r => r.Need).ThenByDescending(r => r.Roll).First().Player;
                    FailureLedger.Record("group-loot-rolled", $"{eligible.Count} players", $"item {item.Id} won by {winner.Name}");
                    break;
                }
                default:
                    winner = killer;
                    break;
            }

            if (winner != killer)
                FailureLedger.Record("group-loot-redirected", rule.ToString());

            return winner;
        }
    }
}
