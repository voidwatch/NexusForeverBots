using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map;
using NLog;

namespace NexusForever.WorldServer.Diagnostics
{
    /// <summary>
    /// The "memory" line of the failure report and the heap-growth detector. Both run on the report timer thread, not on the world
    /// thread, so they keep working while a map update hangs. Everything read here is a count; collections owned by the world thread are read
    /// without locks and a read that races a change is reported as '?' instead of throwing.
    /// </summary>
    public static class MemoryWatch
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        /// <summary>Managed heap growth that is reported once (ledger "heap-growth") when it happens within <see cref="GrowthWindow"/>.</summary>
        public const long GrowthBytes = 1L << 30;
        private static readonly TimeSpan GrowthWindow = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan GrowthReportGap = TimeSpan.FromMinutes(10);

        private static readonly Queue<(DateTime Time, long Heap)> samples = new();
        private static DateTime lastGrowthReport = DateTime.MinValue;
        private static readonly object gate = new();

        /// <summary>Largest per-map counts seen since start (pending grid actions, spell summons): 'gt memory' and the gate read them.</summary>
        public static int PeakPendingActions { get; private set; }
        public static int PeakSpellSummons { get; private set; }

        public static string Line()
        {
            long heap = GC.GetTotalMemory(false);
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            string line = $"heap {heap >> 20} MB, gc heap {info.HeapSizeBytes >> 20} MB, fragmented {info.FragmentedBytes >> 20} MB, committed {info.TotalCommittedBytes >> 20} MB, "
                + $"limit {info.TotalAvailableMemoryBytes >> 20} MB, gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}, working set {Environment.WorkingSet >> 20} MB; "
                + Collections();
            Check(heap, line);
            return line;
        }

        private static string Collections()
        {
            int maps = 0, entities = 0, pending = 0, maxPending = 0, summons = 0, spells = 0, maxSpells = 0;
            ushort maxPendingMap = 0;
            bool spellsKnown = true;
            try
            {
                foreach (IMap map in MapManager.Instance.LoadedMaps)
                {
                    var baseMaps = new List<BaseMap>();
                    if (map is BaseMap baseMap)
                        baseMaps.Add(baseMap);
                    if (map is IInstancedMap instanced)
                        baseMaps.AddRange(instanced.GetInstances().OfType<BaseMap>());

                    foreach (BaseMap m in baseMaps)
                    {
                        maps++;
                        entities += m.EntityCount;
                        summons += m.SpellSummonCount;
                        int p = m.PendingActionCount;
                        pending += p;
                        if (p > maxPending)
                        {
                            maxPending = p;
                            maxPendingMap = (ushort)m.Entry.Id;
                        }
                        PeakSpellSummons = Math.Max(PeakSpellSummons, m.SpellSummonCount);

                        try
                        {
                            foreach (UnitEntity unit in m.AllEntities().OfType<UnitEntity>())
                            {
                                int count = unit.PendingSpellCount;
                                spells += count;
                                maxSpells = Math.Max(maxSpells, count);
                            }
                        }
                        catch (Exception)
                        {
                            spellsKnown = false; // the world thread changed the entity list while it was read
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                return $"maps unreadable ({exception.GetType().Name})";
            }

            PeakPendingActions = Math.Max(PeakPendingActions, maxPending);
            return $"{maps} maps, {entities} entities, {pending} queued grid actions (most {maxPending} in {maxPendingMap}, peak {PeakPendingActions}), "
                + $"{summons} spell summons (peak per map {PeakSpellSummons}), {(spellsKnown ? spells.ToString() : "?")} pending spells (most {maxSpells} on one unit), "
                + $"{Game.Combat.RespawnManager.Instance.PendingCount} respawns queued, {FailureLedger.Count} ledger keys, {BotFeed.Queued} feed lines queued";
        }

        private static void Check(long heap, string line)
        {
            lock (gate)
            {
                DateTime now = DateTime.UtcNow;
                samples.Enqueue((now, heap));
                while (samples.Count > 0 && now - samples.Peek().Time > GrowthWindow)
                    samples.Dequeue();

                long lowest = samples.Min(s => s.Heap);
                if (heap - lowest < GrowthBytes || now - lastGrowthReport < GrowthReportGap)
                    return;

                lastGrowthReport = now;
                FailureLedger.Record("heap-growth", $"+{(heap - lowest) >> 20} MB within {GrowthWindow.TotalMinutes:0} min", line);
                log.Warn($"Managed heap grew {(heap - lowest) >> 20} MB within {GrowthWindow.TotalMinutes:0} min: {line}");
            }
        }
    }
}
