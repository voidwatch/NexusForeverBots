using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace NexusForever.WorldServer.Diagnostics
{
    /// <summary>
    /// Cheap accumulating timers for finding out what makes the world slow (gt profile). Each scope adds its elapsed time and a call count
    /// to a named bucket; nothing is stored per call.
    /// </summary>
    public static class Profiler
    {
        private sealed class Bucket
        {
            public long Ticks;
            public long Calls;
        }

        private static readonly ConcurrentDictionary<string, Bucket> buckets = new();
        private static long since = Stopwatch.GetTimestamp();

        /// <summary>NF_PROFILE=1 turns the timers on: they cost more than the cheap things they measure (a clock read and a shared counter per call).</summary>
        public static bool Enabled { get; } = System.Environment.GetEnvironmentVariable("NF_PROFILE") == "1";

        public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        public static void Add(string name, long start)
        {
            if (!Enabled)
                return;

            Bucket bucket = buckets.GetOrAdd(name, _ => new Bucket());
            System.Threading.Interlocked.Add(ref bucket.Ticks, Stopwatch.GetTimestamp() - start);
            System.Threading.Interlocked.Increment(ref bucket.Calls);
        }

        public static void Reset()
        {
            buckets.Clear();
            since = Stopwatch.GetTimestamp();
        }

        /// <summary>Name, milliseconds of work per second of wall time (can exceed 1000 with several threads), calls per second, microseconds per call.</summary>
        public static IEnumerable<(string Name, double MsPerSecond, double CallsPerSecond, double MicrosPerCall)> Snapshot()
        {
            double seconds = (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;
            return buckets.Select(b => (b.Key,
                    b.Value.Ticks * 1000d / Stopwatch.Frequency / seconds,
                    b.Value.Calls / seconds,
                    b.Value.Calls == 0 ? 0d : b.Value.Ticks * 1000000d / Stopwatch.Frequency / b.Value.Calls))
                .OrderByDescending(b => b.Item2).ToList();
        }
    }
}
