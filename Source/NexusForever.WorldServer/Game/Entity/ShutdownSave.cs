using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NexusForever.Shared.Network;
using NexusForever.WorldServer.Network;
using NLog;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Saves every online character when the process is asked to stop (SIGTERM from docker stop/restart, Ctrl-C), so a restart loses no progress
    /// made since the last 60 s save. The saves are started on the world thread, which keeps ticking (it completes the save tasks) until all are
    /// written or NF_SHUTDOWN_SAVE_SECONDS (default 15) has passed. The compose stop grace period must be longer than that.
    /// The outcome is appended to /cache/shutdown-save.log.
    /// </summary>
    public static class ShutdownSave
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();
        private static readonly ConcurrentQueue<Action> worldActions = new();
        private static int started;

        public static void Initialise()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Run("SIGTERM");
            Console.CancelKeyPress += (_, _) => Run("SIGINT");
        }

        /// <summary>
        /// Called by the world thread every tick.
        /// </summary>
        public static void Update()
        {
            while (worldActions.TryDequeue(out Action action))
                action();
        }

        private static void Run(string reason)
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                return;

            int seconds = int.TryParse(Environment.GetEnvironmentVariable("NF_SHUTDOWN_SAVE_SECONDS"), out int configured) && configured >= 0 ? configured : 15;
            if (seconds == 0)
            {
                Write($"{reason}: shutdown save off (NF_SHUTDOWN_SAVE_SECONDS=0)");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            int total = 0, saved = 0, failed = 0;
            using var startedEvent = new ManualResetEventSlim();
            using var doneEvent = new ManualResetEventSlim();

            worldActions.Enqueue(() =>
            {
                List<Player> players = NetworkManager<WorldSession>.Instance.GetSessions()
                    .Select(s => s.Player)
                    .Where(p => p != null)
                    .Distinct()
                    .ToList();
                total = players.Count;
                int remaining = total;
                foreach (Player player in players)
                {
                    try
                    {
                        player.Save(() =>
                        {
                            Interlocked.Increment(ref saved);
                            if (Interlocked.Decrement(ref remaining) == 0)
                                doneEvent.Set();
                        });
                    }
                    catch (Exception exception)
                    {
                        Interlocked.Increment(ref failed);
                        Diagnostics.FailureLedger.Record("shutdown-save-failed", exception.GetType().Name, $"character {player.CharacterId}: {exception.Message}");
                        if (Interlocked.Decrement(ref remaining) == 0)
                            doneEvent.Set();
                    }
                }
                if (total == 0)
                    doneEvent.Set();
                startedEvent.Set();
            });

            bool done = doneEvent.Wait(TimeSpan.FromSeconds(seconds));
            Diagnostics.QuestTruth.Flush();
            string line = $"{reason}: {total} characters online, {saved} saved, {failed} failed to start, {(done ? "all done" : "timed out")} in {stopwatch.ElapsedMilliseconds} ms"
                + (startedEvent.IsSet ? "" : " (world thread did not pick the saves up)");
            log.Info($"Shutdown save {line}");
            Write(line);
        }

        private static void Write(string line)
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "shutdown-save.log");
                System.IO.File.AppendAllText(path, $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // the log is a diagnostic aid only
            }
        }
    }
}
