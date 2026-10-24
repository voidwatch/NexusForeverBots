using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Forced quest sweep (bot sweep start): every open doable quest that does not wait for an open prerequisite quest is handed to a free
    /// quest bot through the forced path (Bot.ApplyForcedQuest), one quest per bot, bounded in time. After a completion the same bot takes the
    /// follow-ups of that quest, so chains are walked with real prerequisites. One line per quest goes to /cache/sweep-results.tsv:
    /// quest, outcome (completed, stalled, not-takeable, timeout), via (root, chain, prereq-bypass), bot, level, seconds, the normal picker's
    /// verdict for the same bot at the test level, objective progress, the bot's last trace or stall detail.
    /// </summary>
    public static class ForceSweep
    {
        private sealed class Running
        {
            public ushort Quest;
            public string Via;
            public DateTime Since;
            public bool Applied;
            public string Verdict = "";
        }

        private static readonly object gate = new();
        private static readonly LinkedList<(ushort Quest, string Via)> queue = new();
        private static readonly Dictionary<Bot, Running> running = new();
        private static readonly Dictionary<Bot, Queue<ushort>> chained = new();
        private static readonly HashSet<ushort> seen = new();
        private static readonly HashSet<ushort> open = new();
        private static readonly Dictionary<string, int> outcomes = new();
        private static TimeSpan limit = TimeSpan.FromMinutes(8);
        private static int skippedNoBot;
        private static readonly Dictionary<(ulong Character, ushort Quest), List<string>> credits = new();

        /// <summary>A quest objective was credited without its real driver (synthetic credit or a public event given up).</summary>
        public static void NoteCredit(Player me, ushort quest, string what)
        {
            if (!Active)
                return;
            lock (gate)
            {
                if (!credits.TryGetValue((me.CharacterId, quest), out List<string> list))
                    credits[(me.CharacterId, quest)] = list = new List<string>();
                if (!list.Contains(what))
                    list.Add(what);
            }
        }

        public static bool Active { get; private set; }

        private static string Path_ => Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "sweep-results.tsv");

        /// <summary>
        /// Start a sweep. Quest list: /cache/sweep-queue.txt (one id per line) when it exists, otherwise every open doable quest whose prerequisite
        /// quests are completed by somebody (chain-blocked quests are reached through their roots).
        /// </summary>
        public static string Start(int minutes)
        {
            lock (gate)
            {
                queue.Clear(); running.Clear(); chained.Clear(); seen.Clear(); open.Clear(); outcomes.Clear(); credits.Clear();
                skippedNoBot = 0;
                limit = TimeSpan.FromMinutes(Math.Clamp(minutes, 2, 30));
                List<QuestInfo> openDoable = BotQuests.OpenDoable();
                foreach (QuestInfo info in openDoable)
                    open.Add((ushort)info.Entry.Id);

                string file = Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "sweep-queue.txt");
                // a queue file line "+" (or a file of only "+" ids) also queues the default list after the listed ids
                string[] listed = File.Exists(file) ? File.ReadAllLines(file) : new[] { "+" };
                IEnumerable<ushort> ids = listed.Select(l => ushort.TryParse(l.Trim(), out ushort id) ? id : (ushort)0).Where(id => id != 0);
                if (listed.Any(l => l.Trim() == "+" || l.Trim() == "*"))
                    ids = ids.Concat(openDoable.Where(q => !BotQuests.WaitsForOpenQuests(q)).Select(q => (ushort)q.Entry.Id));
                foreach (ushort id in ids.Distinct())
                    if (GlobalQuestManager.Instance.GetQuestInfo(id) != null)
                        queue.AddLast((id, "root"));
                // "*": also the chain-blocked quests, last, with their prerequisite quests skipped (tests the quest itself)
                if (listed.Any(l => l.Trim() == "*"))
                    foreach (QuestInfo info in openDoable.Where(BotQuests.WaitsForOpenQuests))
                        queue.AddLast(((ushort)info.Entry.Id, "prereq-bypass"));

                try
                {
                    File.WriteAllText(Path_, "quest\toutcome\tvia\tbot\tlevel\tseconds\tnormal_verdict\tobjectives\tdetail\tcredit\theuristic\n");
                }
                catch (Exception)
                {
                }

                Active = queue.Count > 0;
                return $"Sweep started: {queue.Count} quests queued ({open.Count} open doable), {limit.TotalMinutes} min per quest, results in {Path_}.";
            }
        }

        public static string Stop()
        {
            lock (gate)
            {
                Active = false;
                running.Clear();
                return "Sweep stopped. " + Status();
            }
        }

        public static string Status()
        {
            lock (gate)
                return $"sweep {(Active ? "active" : "idle")}: queued {queue.Count}, running {running.Count}, chained waiting {chained.Values.Sum(q => q.Count)}, "
                    + $"results {string.Join(", ", outcomes.OrderByDescending(o => o.Value).Select(o => $"{o.Key} {o.Value}"))}"
                    + (skippedNoBot > 0 ? $", waits for a fitting bot {skippedNoBot}" : "");
        }

        /// <summary>Next quest for a free quest bot, 0 when none fits it.</summary>
        public static ushort Next(Bot bot, Player me)
        {
            lock (gate)
            {
                if (!Active)
                    return 0;

                if (chained.TryGetValue(bot, out Queue<ushort> follow))
                    while (follow.Count > 0)
                    {
                        ushort id = follow.Dequeue();
                        if (seen.Contains(id))
                            continue;
                        QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(id);
                        bool all = info.Entry.PrerequisiteQuests.Where(p => p != 0u).All(p => me.QuestManager.GetQuestState((ushort)p) == QuestState.Completed);
                        bool any = info.Entry.PrerequisiteQuests.Where(p => p != 0u).Any(p => me.QuestManager.GetQuestState((ushort)p) == QuestState.Completed);
                        bool met = (info.Entry.PrerequisiteFlags & 1u) != 0u ? any : all;
                        return Take(bot, id, met ? "chain" : "prereq-bypass");
                    }

                for (LinkedListNode<(ushort Quest, string Via)> node = queue.First; node != null; node = node.Next)
                {
                    if (seen.Contains(node.Value.Quest))
                    {
                        queue.Remove(node);
                        return Next(bot, me);
                    }

                    QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(node.Value.Quest);
                    if (!BotQuests.FitsIdentity(info, me))
                        continue;

                    queue.Remove(node);
                    return Take(bot, node.Value.Quest, node.Value.Via);
                }

                // nothing fits this bot: when every bot is free and the queue is not empty, the rest has no fitting bot online
                if (queue.Count > 0 && running.Count == 0)
                {
                    foreach ((ushort quest, string via) in queue.ToList())
                    {
                        QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(quest);
                        Write(quest, "not-takeable", via, "-", 0, 0d, "", "", $"no online quest bot fits faction {info.Entry.QuestPlayerFactionEnum} race {info.Entry.PrerequisiteRace} class {info.Entry.PrerequisiteClass}");
                    }
                    queue.Clear();
                }

                if (queue.Count == 0 && running.Count == 0 && chained.Values.All(q => q.Count == 0))
                    Active = false;
                return 0;
            }
        }

        private static ushort Take(Bot bot, ushort quest, string via)
        {
            seen.Add(quest);
            running[bot] = new Running { Quest = quest, Via = via, Since = DateTime.UtcNow };
            return quest;
        }

        /// <summary>The forced path took the quest; verdict is what the normal picker says to it for this bot at the test level.</summary>
        public static void Applied(Bot bot, ushort quest, string verdict)
        {
            lock (gate)
                if (running.TryGetValue(bot, out Running run) && run.Quest == quest)
                {
                    run.Applied = true;
                    run.Verdict = verdict;
                }
        }

        public static bool IsSweeping(Bot bot)
        {
            lock (gate)
                return running.ContainsKey(bot);
        }

        /// <summary>True when the bot's sweep quest ran out of time (or was never applied within a minute).</summary>
        public static bool Expired(Bot bot)
        {
            lock (gate)
                return running.TryGetValue(bot, out Running run)
                    && DateTime.UtcNow - run.Since > (run.Applied ? limit : TimeSpan.FromSeconds(60));
        }

        /// <summary>Record the end of the bot's sweep quest. Completions queue the quest's open follow-ups for the same bot.</summary>
        public static void Finished(Bot bot, Player me, string outcome, string objectives, string detail)
        {
            lock (gate)
            {
                if (!running.Remove(bot, out Running run))
                    return;

                if (!run.Applied && outcome == "timeout")
                    outcome = "not-takeable";
                string credit = me != null && credits.Remove((me.CharacterId, run.Quest), out List<string> list) ? string.Join(",", list) : "";
                Write(run.Quest, outcome, run.Via, bot.Name, me?.Level ?? 0u, (DateTime.UtcNow - run.Since).TotalSeconds, run.Verdict, objectives, detail, credit);
                if (outcome != "completed" || me == null)
                    return;

                if (!chained.TryGetValue(bot, out Queue<ushort> follow))
                    chained[bot] = follow = new Queue<ushort>();
                foreach (ushort id in BotQuests.FollowUps(run.Quest))
                    if (open.Contains(id) && !seen.Contains(id) && BotQuests.FitsIdentity(GlobalQuestManager.Instance.GetQuestInfo(id), me))
                        follow.Enqueue(id);
            }
        }

        private static void Write(ushort quest, string outcome, string via, string bot, uint level, double seconds, string verdict, string objectives, string detail, string credit = "")
        {
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(quest);
            string heuristic = info != null ? QuestCoverage.HeuristicKind(info) : "";
            string line = string.Join("\t", quest, outcome, via, bot, level, $"{seconds:0}", Clean(verdict), Clean(objectives), Clean(detail), credit, heuristic) + "\n";
            try
            {
                File.AppendAllText(Path_, line);
            }
            catch (Exception)
            {
                // diagnostics only
            }
        }

        private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ');

        /// <summary>Objective progress of a quest: index:type:data:progress/max, the open ones marked with a star.</summary>
        public static string Objectives(Quest quest)
        {
            if (quest == null)
                return "";
            return string.Join(" ", quest.Select(o => $"{o.Index}:{(uint)o.ObjectiveInfo.Entry.Type}:{o.ObjectiveInfo.Entry.Data}:{o.Progress}/{o.MaxProgress}{(o.IsComplete() ? "" : "*")}"));
        }
    }
}
