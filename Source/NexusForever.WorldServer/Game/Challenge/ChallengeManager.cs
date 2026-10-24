using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Quest;

namespace NexusForever.WorldServer.Game.Challenge
{
    /// <summary>
    /// Challenges (timed optional objectives). ChallengeTypeEnum values are the client's ChallengeType enum (certain, client registration code):
    /// 0 Combat (Target = target group, kills count), 1 Ability (Target = Spell4 id, successful casts count), 2 General (races: reaching the
    /// indicator location ends it), 3 UNUSED01, 4 Item (Target = Item2 id, items coming into the bags count), 7 ChecklistActivate (Target = target
    /// group, each unit used once counts). Type 5 is not in the client enum; its Target is a VirtualItem (names match: 70 of 73 rows) collected from
    /// the creatures its progress text names (data/research/challenge-sources.tsv), kills and uses of them count.
    /// The tier counts give bronze, silver and gold; tiers above 1000 are milliseconds of time left (races, "[0, 30000, 40000]").
    /// Assumptions (time limit, rewards, sources) are in docs/ASSUMPTIONS.md "Challenges".
    /// </summary>
    public class ChallengeManager
    {
        /// <summary>No table holds challenge durations; 180 s is the assumed limit (ASSUMPTIONS.md).</summary>
        public const double TimeLimitSeconds = 180d;

        public const uint CombatType = 0u, AbilityType = 1u, GeneralType = 2u, ItemType = 4u, VirtualItemType = 5u, ChecklistType = 7u;

        private static Dictionary<uint, HashSet<uint>> sources;

        /// <summary>Creatures that give the virtual item of a type 5 challenge or drop the item of a type 4 one (scripts/research/challenge_sources.py).</summary>
        public static HashSet<uint> Sources(uint challengeId)
        {
            if (sources == null)
            {
                var loaded = new Dictionary<uint, HashSet<uint>>();
                string file = System.IO.Path.Combine(Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research", "challenge-sources.tsv");
                if (System.IO.File.Exists(file))
                    foreach (string line in System.IO.File.ReadLines(file))
                    {
                        string[] f = line.Split('\t');
                        if (line.StartsWith('#') || f.Length < 3 || !uint.TryParse(f[0], out uint id))
                            continue;
                        loaded[id] = f[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => uint.TryParse(x, out uint c) ? c : 0u).Where(c => c != 0u).ToHashSet();
                    }
                sources = loaded;
            }
            return sources.TryGetValue(challengeId, out HashSet<uint> set) ? set : new HashSet<uint>();
        }

        /// <summary>What a bot (or a player) has to do for a challenge; null when the type or row cannot be played.</summary>
        public static string Playable(ChallengeEntry entry)
        {
            switch (entry.ChallengeTypeEnum)
            {
                case CombatType:
                    return entry.Target != 0u && GameTableManager.Instance.TargetGroup.GetEntry(entry.Target) != null ? "group" : null;
                case ChecklistType:
                    if (entry.Target == 0u || GameTableManager.Instance.TargetGroup.GetEntry(entry.Target) == null)
                        return null;
                    return TimedTiers(entry) ? "group-timed" : "group";
                case AbilityType:
                    return entry.Target != 0u && GameTableManager.Instance.Spell4.GetEntry(entry.Target) != null ? "spell" : null;
                case ItemType:
                    return entry.Target != 0u && GameTableManager.Instance.Item.GetEntry(entry.Target) != null ? "item" : null;
                case VirtualItemType:
                    return Sources(entry.Id).Count > 0 ? "sources" : null;
                case GeneralType:
                    if (entry.WorldLocation2IdIndicator != 0u && entry.WorldLocation2IdIndicator != entry.WorldLocation2IdStartLocation)
                        return "reach";
                    // scripted rows whose progress text names the creatures ("Smash $+ Gloomthorn Eggs"): kills and uses of them count
                    return !TimedTiers(entry) && Sources(entry.Id).Count > 0 ? "sources" : null;
            }
            return null;
        }

        /// <summary>
        /// Time-tiered challenges: the tiers are milliseconds left on the clock (races, timed checklists), not counts. The client's
        /// Challenge:IsTimeTiered reads bit 3 (8) of Challenge.challengeFlags (certain); 80 rows. The earlier rule (a tier above 1000) took
        /// three untimed rows with large counts (423, 1115: 30000/60000; 499) for timed and missed eleven timed rows with tier 0.
        /// </summary>
        public static bool TimedTiers(ChallengeEntry entry) => (entry.ChallengeFlags & 8u) != 0u;

        /// <summary>
        /// Units a timed checklist needs: the number in its progress text ("Disable the 4 Eldan Power Generators"), else the distinct
        /// creatures of its target group (guess, docs/ASSUMPTIONS.md "Challenges").
        /// </summary>
        private static uint ChecklistGoal(ChallengeEntry entry)
        {
            string progress = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdProgress) ?? "";
            var number = System.Text.RegularExpressions.Regex.Match(progress, @"\b([2-9]|1[0-9])\b");
            if (number.Success)
                return uint.Parse(number.Groups[1].Value);
            return (uint)Math.Max(1, TargetGroupHelper.ResolveAll(entry.Target).Count);
        }

        public sealed class Active
        {
            public ChallengeEntry Entry;
            public uint Count;
            public double Elapsed;
            public uint[] TierCounts;
            public int Medal;
            public HashSet<uint> Used = new();
            public uint Goal;
            public double Limit = TimeLimitSeconds;
        }

        private readonly Player player;
        private readonly Dictionary<uint, Active> active = new();
        private readonly Dictionary<uint, int> bestMedal = new();

        public ChallengeManager(Player player)
        {
            this.player = player;
        }

        public IReadOnlyCollection<Active> ActiveChallenges => active.Values;

        // challenges started at least once this session (the client marks a challenge unlocked when the server first triggers it)
        private readonly HashSet<uint> unlocked = new();

        public bool IsActive(uint challengeId) => active.ContainsKey(challengeId);

        /// <summary>Prerequisite 74 (client challenge record "unlocked" flag). The server unlocks a challenge when it first starts (likely).</summary>
        public bool IsUnlocked(uint challengeId) => unlocked.Contains(challengeId);

        /// <summary>Prerequisite 125 (client IsInCooldown). This server has no challenge cooldown, so no challenge is in one.</summary>
        public bool IsInCooldown(uint challengeId) => false;

        public int BestMedal(uint challengeId) => bestMedal.TryGetValue(challengeId, out int medal) ? medal : 0;

        public int CompletedCount => bestMedal.Count;

        public bool Start(uint challengeId)
        {
            ChallengeEntry entry = GameTableManager.Instance.Challenge.GetEntry(challengeId);
            if (entry == null || active.ContainsKey(challengeId))
                return false;

            if (Playable(entry) == null)
            {
                FailureLedger.Record("challenge-type-unsupported", $"type {entry.ChallengeTypeEnum}", $"challenge {challengeId}");
                return false;
            }

            uint[] tierIds = { entry.ChallengeTierId00, entry.ChallengeTierId01, entry.ChallengeTierId02 };
            uint[] counts = tierIds.Where(id => id != 0u).Select(id => GameTableManager.Instance.ChallengeTier.GetEntry(id)?.Count ?? 0u).OrderBy(c => c).ToArray();
            // a race tier of 0 is "reached at all"; count tiers of 0 mean nothing in the other types
            bool timed = TimedTiers(entry);
            if (!timed && Playable(entry) != "reach")
                counts = counts.Where(c => c > 0u).ToArray();
            if (counts.Length == 0)
            {
                FailureLedger.Record("challenge-no-tiers-or-target", $"challenge {challengeId}");
                return false;
            }

            active[challengeId] = new Active { Entry = entry, TierCounts = counts, Goal = timed && entry.ChallengeTypeEnum == ChecklistType ? ChecklistGoal(entry) : 0u,
                // a millisecond tier above the 180 s guess (957: 180000, 1078: 265000) must be reachable: the clock runs 30 s longer than the best tier (guess)
                Limit = timed ? Math.Max(TimeLimitSeconds, counts.Max() / 1000d + 30d) : TimeLimitSeconds };
            unlocked.Add(challengeId);
            FailureLedger.Record("challenge-started", $"challenge {challengeId}");
            return true;
        }

        /// <summary>AbandonChallenge (0x00C5 choice 2): the active challenge ends without a medal.</summary>
        public bool Abandon(uint challengeId)
        {
            if (!active.Remove(challengeId))
                return false;
            FailureLedger.Record("challenge-abandoned", $"challenge {challengeId}");
            return true;
        }

        public void OnCreatureKilled(uint creatureId) => OnCreatureKilled(creatureId, 0u);

        public void OnCreatureKilled(uint creatureId, uint guid)
        {
            foreach (Active challenge in active.Values.ToList())
                if ((challenge.Entry.ChallengeTypeEnum == CombatType && TargetGroupHelper.Contains(challenge.Entry.Target, creatureId))
                    || (IsSourced(challenge.Entry) && Sources(challenge.Entry.Id).Contains(creatureId)))
                    Add(challenge, 1u);
        }

        private static bool IsSourced(ChallengeEntry entry) => entry.ChallengeTypeEnum == VirtualItemType
            || (entry.ChallengeTypeEnum == GeneralType && (entry.WorldLocation2IdIndicator == 0u || entry.WorldLocation2IdIndicator == entry.WorldLocation2IdStartLocation));

        public void OnCreatureActivated(uint creatureId) => OnCreatureActivated(creatureId, 0u);

        public void OnCreatureActivated(uint creatureId, uint guid)
        {
            foreach (Active challenge in active.Values.ToList())
            {
                if (IsSourced(challenge.Entry) && Sources(challenge.Entry.Id).Contains(creatureId))
                    Add(challenge, 1u);
                // checklist: each unit once (the creature id when the caller has no unit)
                else if (challenge.Entry.ChallengeTypeEnum == ChecklistType && TargetGroupHelper.Contains(challenge.Entry.Target, creatureId)
                    && challenge.Used.Add(guid != 0u ? guid : creatureId))
                {
                    // timed checklist: done when every unit is used, the medal by the time left (like a race)
                    if (challenge.Goal > 0u)
                    {
                        if (challenge.Used.Count >= challenge.Goal)
                            FinishTimed(challenge);
                    }
                    else
                        Add(challenge, 1u);
                }
            }
        }

        /// <summary>A spell cast by the player succeeded: ability challenges count casts of their Spell4 (or of its base spell).</summary>
        public void OnSpellSuccess(uint spell4Id)
        {
            if (active.Count == 0)
                return;
            uint baseId = GameTableManager.Instance.Spell4.GetEntry(spell4Id)?.Spell4BaseIdBaseSpell ?? 0u;
            foreach (Active challenge in active.Values.ToList())
                if (challenge.Entry.ChallengeTypeEnum == AbilityType && (challenge.Entry.Target == spell4Id
                    || (baseId != 0u && GameTableManager.Instance.Spell4.GetEntry(challenge.Entry.Target)?.Spell4BaseIdBaseSpell == baseId)))
                    Add(challenge, 1u);
        }

        /// <summary>Items coming into the bags: item challenges count their Item2.</summary>
        public void OnItemCollected(uint itemId, uint count)
        {
            foreach (Active challenge in active.Values.ToList())
                if (challenge.Entry.ChallengeTypeEnum == ItemType && challenge.Entry.Target == itemId)
                    Add(challenge, count);
        }

        private void Add(Active challenge, uint amount)
        {
            challenge.Count += amount;
            while (challenge.Medal < challenge.TierCounts.Length && challenge.Count >= challenge.TierCounts[challenge.Medal])
                challenge.Medal++;

            if (challenge.Medal == challenge.TierCounts.Length)
                Finish(challenge);
        }

        /// <summary>
        /// A race (General with a goal location) ends at the goal: tiers up to 1 are "reached", larger tiers are milliseconds left on the clock.
        /// </summary>
        private void CheckReach(Active challenge)
        {
            var goal = GameTableManager.Instance.WorldLocation2.GetEntry(challenge.Entry.WorldLocation2IdIndicator);
            if (goal == null || player.Map == null || goal.WorldId != player.Map.Entry.Id)
                return;
            float range = Math.Max(goal.Radius, 8f);
            float dx = player.Position.X - goal.Position0, dz = player.Position.Z - goal.Position2;
            if (dx * dx + dz * dz > range * range || Math.Abs(player.Position.Y - goal.Position1) > Math.Max(goal.MaxVerticalDistance, 15f))
                return;

            FinishTimed(challenge);
        }

        private void FinishTimed(Active challenge)
        {
            uint left = (uint)Math.Max(1d, (challenge.Limit - challenge.Elapsed) * 1000d);
            challenge.Medal = challenge.TierCounts.Count(t => t <= 1u || t <= left);
            challenge.Count = left;
            FailureLedger.Record("challenge-goal-reached", $"challenge {challenge.Entry.Id}", $"{challenge.Elapsed:0} s, medal {challenge.Medal}");
            Finish(challenge);
        }

        public void Update(double lastTick)
        {
            if (active.Count == 0)
                return;

            foreach (Active challenge in active.Values.ToList())
            {
                challenge.Elapsed += lastTick;
                if (challenge.Entry.ChallengeTypeEnum == GeneralType && !IsSourced(challenge.Entry))
                    CheckReach(challenge);
                if (active.ContainsKey(challenge.Entry.Id) && challenge.Elapsed >= challenge.Limit)
                    Finish(challenge);
            }
        }

        /// <summary>
        /// Reward of one medal. ASSUMPTION (docs/ASSUMPTIONS.md "Challenge rewards"): RewardTrackRewards rows of the challenge's RewardTrackId whose RewardPointFlags
        /// has the bit of the medal (1 bronze, 2 silver, 4 gold, 8, 16, 32) are the alternatives of that medal (class rows filtered by their prerequisite); one is picked
        /// at random like a bot chooses, its item choice ids (Item table, type enum 0) are given with their counts, plus its currency.
        /// </summary>
        private void GrantReward(uint trackId, int medal)
        {
            if (trackId == 0u)
                return;

            uint flag = 1u << (medal - 1);
            var rows = GameTableManager.Instance.RewardTrackRewards.Entries
                .Where(r => r != null && r.RewardTrackId == trackId && (r.RewardPointFlags & flag) != 0u
                    && (r.PrerequisiteId == 0u || Prerequisite.PrerequisiteManager.Instance.Meets(player, r.PrerequisiteId)))
                .ToList();
            if (rows.Count == 0)
            {
                FailureLedger.Record("challenge-reward", "no reward row for the medal", $"track {trackId} medal {medal}");
                return;
            }

            RewardTrackRewardsEntry row = rows[rewardRandom.Next(rows.Count)];
            uint[] types   = { row.RewardTrackRewardTypeEnum00, row.RewardTrackRewardTypeEnum01, row.RewardTrackRewardTypeEnum02 };
            uint[] choices = { row.RewardChoiceId00, row.RewardChoiceId01, row.RewardChoiceId02 };
            uint[] counts  = { row.RewardChoiceCount00, row.RewardChoiceCount01, row.RewardChoiceCount02 };
            int given = 0;
            for (int i = 0; i < choices.Length; i++)
            {
                if (choices[i] == 0u || types[i] != 0u || GameTableManager.Instance.Item.GetEntry(choices[i]) == null)
                    continue;

                if (player.Inventory.GetInventorySlotsRemaining(Entity.Static.InventoryLocation.Inventory) == 0u)
                {
                    FailureLedger.Record("challenge-reward", "bags full, item not given", $"track {trackId} item {choices[i]}");
                    continue;
                }

                player.Inventory.ItemCreate(Entity.Static.InventoryLocation.Inventory, choices[i], Math.Max(1u, counts[i]), Entity.Static.ItemUpdateReason.Quest);
                given++;
            }

            if (row.CurrencyAmount > 0u && Enum.IsDefined(typeof(Entity.Static.CurrencyType), (byte)row.CurrencyTypeId))
            {
                player.CurrencyManager.CurrencyAddAmount((Entity.Static.CurrencyType)row.CurrencyTypeId, row.CurrencyAmount);
                given++;
            }

            FailureLedger.Record("challenge-reward", given > 0 ? "reward given" : "reward row had nothing to give", $"track {trackId} medal {medal} row {row.Id}");
        }

        private static readonly Random rewardRandom = new();

        private void Finish(Active challenge)
        {
            active.Remove(challenge.Entry.Id);
            if (challenge.Medal > 0)
            {
                bestMedal[challenge.Entry.Id] = Math.Max(BestMedal(challenge.Entry.Id), challenge.Medal);
                FailureLedger.Record("challenge-completed", $"medal {challenge.Medal}", $"challenge {challenge.Entry.Id}, reward track {challenge.Entry.RewardTrackId}");
                for (int medal = 1; medal <= challenge.Medal; medal++)
                    GrantReward(challenge.Entry.RewardTrackId, medal);
            }
            else
                FailureLedger.Record("challenge-timed-out", $"challenge {challenge.Entry.Id}");
        }
    }
}
