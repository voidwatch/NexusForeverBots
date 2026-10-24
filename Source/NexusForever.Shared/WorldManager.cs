using System;
using System.Diagnostics;
using System.Threading;

namespace NexusForever.Shared
{
    public sealed class WorldManager : Singleton<WorldManager>
    {
        private volatile bool shutdownRequested;

        private WorldManager()
        {
        }

        public void Initialise(Action<double> updateAction)
        {
            var worldThread = new Thread(() =>
            {
                var stopwatch = new Stopwatch();
                double lastTick = 0d;

                // the stock loop only sleeps 1 ms between updates, which runs the world several hundred times a second and, with many
                // maps, spends most of the CPU on scheduling map updates. NF_TICK_MS sets the minimum time per update (default 20 = 50 Hz).
                int tickMs = int.TryParse(Environment.GetEnvironmentVariable("NF_TICK_MS"), out int configured) && configured >= 1 ? configured : 20;

                while (!shutdownRequested)
                {
                    stopwatch.Restart();

                    updateAction(lastTick);

                    Thread.Sleep(Math.Max(1, tickMs - (int)stopwatch.ElapsedMilliseconds));
                    lastTick = (double)stopwatch.ElapsedTicks / Stopwatch.Frequency;
                }
            });

            worldThread.Start();
        }

        public void Shutdown()
        {
            shutdownRequested = true;
        }
    }
}
