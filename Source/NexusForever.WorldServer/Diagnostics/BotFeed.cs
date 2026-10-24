using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace NexusForever.WorldServer.Diagnostics
{
    /// <summary>
    /// One line per bot event (quest accepted/completed/stalled, died, level up, instance changes) in /cache/bot-feed.log, read by scripts/bot-feed.sh.
    /// Callers only enqueue a string; a background thread writes, so nothing touches the disk on the tick path. Rotates at 20 MB.
    /// </summary>
    public static class BotFeed
    {
        private const long MaxBytes = 20L * 1024 * 1024;
        private const int MaxQueued = 20000;

        private static readonly ConcurrentQueue<string> queue = new();
        public static int Queued => queue.Count;
        private static readonly AutoResetEvent signal = new(false);
        private static int started;

        public static string Path_ => System.IO.Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "bot-feed.log");

        /// <summary>Format: time, bot name, level, zone, event (tab separated).</summary>
        public static void Add(string bot, uint level, string zone, string text)
        {
            if (queue.Count > MaxQueued)
                return;

            queue.Enqueue($"{DateTime.Now:HH:mm:ss}\t{bot}\tL{level}\t{zone}\t{text}");
            if (Interlocked.Exchange(ref started, 1) == 0)
                new Thread(Run) { IsBackground = true, Name = "BotFeed" }.Start();

            signal.Set();
        }

        private static void Run()
        {
            while (true)
            {
                signal.WaitOne(2000);
                try
                {
                    if (queue.IsEmpty)
                        continue;

                    string path = Path_;
                    if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    {
                        File.Delete(path + ".1");
                        File.Move(path, path + ".1");
                    }

                    using var writer = new StreamWriter(path, true);
                    while (queue.TryDequeue(out string line))
                        writer.WriteLine(line);
                }
                catch (Exception)
                {
                    // diagnostics must never take the server down
                }
            }
        }
    }
}
