using System;
using System.Collections.Concurrent;
using System.Linq;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map.Static;

namespace NexusForever.WorldServer.Game.Groups
{
    /// <summary>
    /// Raid lockouts and daily dungeon rewards. Source: WildStar raids have a weekly lockout, killing any boss locks every member of the
    /// instance until the weekly reset (Wednesday 02:00 PST = 10:00 UTC, wildstar.fandom.com/wiki/Raid). Dungeons have no lockout;
    /// the daily reward reset at the same hour is an assumption (docs/ASSUMPTIONS.md "Resets"). State is in memory only (todo: persistence).
    /// </summary>
    public static class InstanceLockouts
    {
        private static readonly ConcurrentDictionary<(ulong Character, ushort World, uint Boss), DateTime> raidLockouts = new();
        private static readonly ConcurrentDictionary<ulong, DateTime> dailyClaimed = new();

        /// <summary>Clock used for resets, replaced in tests.</summary>
        public static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        public static DateTime NextDailyReset(DateTime now)
        {
            DateTime reset = now.Date.AddHours(10d);
            return reset > now ? reset : reset.AddDays(1d);
        }

        public static DateTime NextWeeklyReset(DateTime now)
        {
            DateTime reset = NextDailyReset(now);
            while (reset.DayOfWeek != DayOfWeek.Wednesday)
                reset = reset.AddDays(1d);

            // NextDailyReset already points at today's 10:00 when it is a Wednesday before the reset
            return reset;
        }

        /// <summary>
        /// Raid boss loot gate: the first kill of a boss in a lockout period is allowed and locks the player, later kills are denied.
        /// </summary>
        public static bool TryLockRaidBoss(ulong character, ushort world, uint boss)
        {
            DateTime now = Now();
            DateTime reset = NextWeeklyReset(now);
            bool allowed = true;
            raidLockouts.AddOrUpdate((character, world, boss), reset, (_, existing) =>
            {
                if (existing > now)
                {
                    allowed = false;
                    return existing;
                }

                return reset;
            });

            FailureLedger.Record(allowed ? "lockout-raid-set" : "lockout-loot-denied", $"world {world}", $"boss {boss}");
            return allowed;
        }

        /// <summary>
        /// Raid progress kept for the lockout period: spawn ids of the bosses an owner's raid instance killed, until the weekly reset. A raid
        /// instance created for that owner again (leader of the group, or the lone character) does not spawn them (likely: "killing any boss
        /// locks every member", wildstar.fandom.com/wiki/Raid; the owner rule is a default, docs/ASSUMPTIONS.md). NF_RAID_SAVED_INSTANCES=0 turns it off.
        /// </summary>
        private static readonly ConcurrentDictionary<(ulong Owner, ushort World), ConcurrentDictionary<uint, DateTime>> savedBosses = new();

        public static bool SavedInstancesEnabled { get; } = Environment.GetEnvironmentVariable("NF_RAID_SAVED_INSTANCES") != "0";

        public static void SaveRaidBoss(ulong owner, ushort world, uint spawnId)
        {
            if (!SavedInstancesEnabled || spawnId == 0u)
                return;
            savedBosses.GetOrAdd((owner, world), _ => new ConcurrentDictionary<uint, DateTime>())[spawnId] = NextWeeklyReset(Now());
            FailureLedger.Record("raid-progress-saved", $"world {world}", $"spawn {spawnId} until the weekly reset");
        }

        public static bool IsRaidBossSaved(ulong owner, ushort world, uint spawnId)
        {
            return SavedInstancesEnabled && savedBosses.TryGetValue((owner, world), out var bosses) && bosses.TryGetValue(spawnId, out DateTime until) && until > Now();
        }

        /// <summary>Saved raid bosses of an owner in a world (still in the lockout period).</summary>
        public static int SavedCount(ulong owner, ushort world)
        {
            DateTime now = Now();
            return savedBosses.TryGetValue((owner, world), out var bosses) ? bosses.Count(b => b.Value > now) : 0;
        }

        /// <summary>Called when a boss dies: a dungeon boss grants the daily reward once per day.</summary>
        public static void OnBossKilled(Player player, MapType mapType, ushort world, uint boss)
        {
            DateTime now = Now();
            if (mapType == MapType.Dungeon && (!dailyClaimed.TryGetValue(player.CharacterId, out DateTime until) || until <= now))
            {
                dailyClaimed[player.CharacterId] = NextDailyReset(now);
                player.CurrencyManager.CurrencyAddAmount(Entity.Static.CurrencyType.Credits, 5000ul, true);
                FailureLedger.Record("daily-dungeon-reward", $"world {world}", "5000 credits (assumed amount)");
            }
        }
    }
}
