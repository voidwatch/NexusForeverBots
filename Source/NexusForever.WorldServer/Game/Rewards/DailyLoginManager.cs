using System;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Quest;

namespace NexusForever.WorldServer.Game.Rewards
{
    /// <summary>
    /// Daily login rewards (DailyLoginReward, 365 rows by LoginDay). One claim per daily reset day; claiming on the day after the last claim continues the streak, any
    /// longer gap restarts it at day 1. ASSUMPTIONS (docs/ASSUMPTIONS.md "Daily login"): RewardObjectValue is an Item id (type enum 0, the only value in the table), the
    /// streak wraps after day 365, there is no client claim message in the protocol, so only the server API and the bots call Claim.
    /// </summary>
    public class DailyLoginManager
    {
        public sealed class Saved
        {
            public string LastDay { get; set; } = "";
            public int Streak { get; set; }
        }

        private readonly Player player;
        private readonly CharacterJson<Saved> store;

        public DailyLoginManager(Player player)
        {
            this.player = player;
            store = new CharacterJson<Saved>("dailylogin", player.CharacterId);
        }

        public int Streak => store.Value.Streak;

        private static string Today(DateTime now) => PeriodicQuests.DayOf(now).ToString("yyyy-MM-dd");

        public bool CanClaim(DateTime now) => store.Value.LastDay != Today(now);

        /// <summary>
        /// Claim today's reward. Returns the login day claimed, or 0 when today was already claimed or the item could not be given.
        /// </summary>
        public uint Claim(DateTime now)
        {
            if (!CanClaim(now))
                return 0u;

            Saved saved = store.Value;
            string yesterday = PeriodicQuests.DayOf(now).AddDays(-1d).ToString("yyyy-MM-dd");
            int streak = saved.LastDay == yesterday ? saved.Streak + 1 : 1;
            uint day = (uint)((streak - 1) % 365) + 1u;

            var row = GameTableManager.Instance.DailyLoginReward.Entries.FirstOrDefault(r => r != null && r.LoginDay == day);
            if (row == null)
            {
                FailureLedger.Record("daily-login", "no reward row for the login day", $"day {day}");
                return 0u;
            }

            if (row.DailyLoginRewardTypeEnum != 0u || GameTableManager.Instance.Item.GetEntry(row.RewardObjectValue) == null)
            {
                FailureLedger.Record("daily-login", "reward is not an item that exists", $"day {day} type {row.DailyLoginRewardTypeEnum} object {row.RewardObjectValue}");
            }
            else if (player.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) == 0u)
            {
                FailureLedger.Record("daily-login", "bags full, not claimed", $"day {day}");
                return 0u;
            }
            else
            {
                player.Inventory.ItemCreate(InventoryLocation.Inventory, row.RewardObjectValue, 1u, ItemUpdateReason.Script);
                FailureLedger.Record("daily-login", "item given", $"day {day} item {row.RewardObjectValue}");
            }

            saved.LastDay = Today(now);
            saved.Streak = streak;
            store.Save();
            return day;
        }
    }
}
