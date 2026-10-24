using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;

namespace NexusForever.WorldServer.Diagnostics
{
    /// <summary>
    /// What happened to each quest bots took, kept across restarts in quest-outcomes.txt (tab separated: quest, accepted, completed, stalled, last stall).
    /// Bots prefer quests that nobody has completed yet, so repeated runs work through the whole quest list and the file shows which quests never complete.
    /// </summary>
    public static class QuestOutcomes
    {
        private sealed class Outcome
        {
            public int Accepted, Completed, Stalled, Forced;
            public string LastStall = "";
        }

        private static readonly ConcurrentDictionary<ushort, Outcome> outcomes = new();
        private static readonly object gate = new();
        private static Timer writer;
        private static volatile bool dirty;

        private static string Path_ => Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "quest-outcomes.txt");

        static QuestOutcomes()
        {
            try
            {
                if (File.Exists(Path_))
                    foreach (string line in File.ReadAllLines(Path_))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 4 && ushort.TryParse(parts[0], out ushort id))
                            outcomes[id] = new Outcome { Accepted = int.Parse(parts[1]), Completed = int.Parse(parts[2]), Stalled = int.Parse(parts[3]), LastStall = parts.Length > 4 ? parts[4] : "", Forced = parts.Length > 5 && int.TryParse(parts[5], out int forced) ? forced : 0 };
                    }
            }
            catch (Exception)
            {
                // a damaged file only costs history
            }
        }

        // quests any character completed according to the database (loaded once in the background), so coverage and bot choices survive restarts
        private static volatile HashSet<ushort> completedInDatabase = new();
        private static int loadingDatabase;

        public static void LoadDatabaseHistory()
        {
            if (System.Threading.Interlocked.Exchange(ref loadingDatabase, 1) == 1)
                return;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var ids = await NexusForever.Shared.Database.DatabaseManager.Instance.CharacterDatabase.GetCompletedQuestIds();
                    completedInDatabase = new HashSet<ushort>(ids);
                }
                catch (Exception)
                {
                    // history only
                }
            });
        }

        // clean mode: "completed" means completed by a clean bot, so clean bots prefer what no clean bot has done and the coverage line is theirs
        public static bool IsCompleted(ushort questId) => Bots.BotManager.CleanMode ? QuestTruth.CleanCompleted(questId) : (outcomes.TryGetValue(questId, out Outcome outcome) && outcome.Completed > 0) || completedInDatabase.Contains(questId);

        public static int CompletedCount => outcomes.Where(o => o.Value.Completed > 0).Select(o => o.Key).Union(completedInDatabase).Count();

        public static bool HasHistory(ushort questId, out int accepted, out int stalled, out string lastStall)
        {
            accepted = stalled = 0;
            lastStall = "";
            if (!outcomes.TryGetValue(questId, out Outcome outcome))
                return false;

            accepted = outcome.Accepted;
            stalled = outcome.Stalled;
            lastStall = outcome.LastStall;
            return true;
        }

        public static void Accepted(ushort questId) => Update(questId, o => o.Accepted++);

        /// <summary>forced = taken through the forced sweep path, otherwise the normal picker (column 6 counts the forced completions, they are part of column 3).</summary>
        public static void Completed(ushort questId, bool forced = false) => Update(questId, o => { o.Completed++; if (forced) o.Forced++; });

        public static void Stalled(ushort questId, string reason) => Update(questId, o =>
        {
            o.Stalled++;
            o.LastStall = (reason ?? "").Replace('\t', ' ').Replace('\n', ' ');
        });

        private static void Update(ushort questId, Action<Outcome> change)
        {
            Outcome outcome = outcomes.GetOrAdd(questId, _ => new Outcome());
            lock (outcome)
                change(outcome);

            dirty = true;
            lock (gate)
                writer ??= new Timer(_ => Write(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        private static void Write()
        {
            if (!dirty)
                return;

            dirty = false;
            try
            {
                File.WriteAllLines(Path_, outcomes.OrderBy(o => o.Key).Select(o => $"{o.Key}\t{o.Value.Accepted}\t{o.Value.Completed}\t{o.Value.Stalled}\t{o.Value.LastStall}\t{o.Value.Forced}"));
            }
            catch (Exception)
            {
                // diagnostics must never take the server down
            }
        }
    }
}
