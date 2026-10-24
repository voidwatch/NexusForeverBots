using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace NexusForever.WorldServer.Diagnostics
{
    /// <summary>
    /// Collects things the server could not handle (unhandled spell effects, prerequisite types, failed casts, unmatched quest kills, stuck quests)
    /// as deduplicated counters, so a long bot run can be summarised as a short list of causes instead of megabytes of log.
    /// </summary>
    public static class FailureLedger
    {
        public sealed class Entry
        {
            public string Category;
            public string Key;
            public int Count;
            public string Example;
            public DateTime FirstSeen;
            public DateTime LastSeen;
        }

        private static readonly ConcurrentDictionary<(string, string), Entry> entries = new();

        private static Timer writer;
        private static readonly object writerLock = new();

        public static string ReportPath => Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "failure-report.txt");

        /// <summary>
        /// The report as text, the same one 'gt report' prints.
        /// </summary>
        public static string Format()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Failure report at {DateTime.UtcNow:u}");
            try { sb.AppendLine(Bots.BotQuests.CoverageLine()); } catch (Exception) { }
            try { sb.AppendLine(Game.Map.InstanceStats.Line()); } catch (Exception) { }
            try { sb.AppendLine("== memory"); sb.AppendLine("  " + MemoryWatch.Line()); } catch (Exception) { }
            foreach (var group in Snapshot().GroupBy(e => e.Category))
            {
                sb.AppendLine($"== {group.Key} ({group.Count()} kinds, {group.Sum(e => e.Count)} total)");
                foreach (Entry entry in group.OrderByDescending(e => e.Count).Take(group.Key.StartsWith("system") ? 80 : group.Key.StartsWith("dungeon-") || group.Key == "encounter-boss-killed" ? 400 : 25))
                    sb.AppendLine($"  {entry.Count}x {entry.Key}{(entry.Example != null ? $" [{entry.Example}]" : "")}");
            }

            return sb.ToString();
        }

        private static void Write(object state)
        {
            try
            {
                File.WriteAllText(ReportPath, Format());
            }
            catch (Exception)
            {
                // diagnostics must never take the server down
            }
        }

        /// <summary>Write the report file now (crash path).</summary>
        public static void WriteNow() => Write(null);

        public static void Record(string category, string key, string example = null)
        {
            // rewrite the report file every 30 seconds once something has been recorded
            lock (writerLock)
                writer ??= new Timer(Write, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

            DateTime now = DateTime.UtcNow;
            entries.AddOrUpdate((category, key),
                _ => new Entry { Category = category, Key = key, Count = 1, Example = example, FirstSeen = now, LastSeen = now },
                (_, entry) =>
                {
                    entry.Count++;
                    entry.LastSeen = now;
                    return entry;
                });
        }

        public static int Count => entries.Count;

        public static IReadOnlyList<Entry> Snapshot()
        {
            return entries.Values.OrderBy(e => e.Category).ThenByDescending(e => e.Count).ToList();
        }

        public static void Reset()
        {
            entries.Clear();
            Write(null);
        }
    }
}
