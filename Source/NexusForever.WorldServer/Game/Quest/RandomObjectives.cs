using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Quest.Static;

namespace NexusForever.WorldServer.Game.Quest
{
    /// <summary>
    /// Quest objective type 31 is a slot the server fills from a pool: the client replaces the quest's type 31 objective with the
    /// QuestObjective row named by RandomResultId in ServerQuestInit / ServerQuestStateChange (certain, client quest handler). The pools
    /// (the type 31 objective's data) are not in the tables; data/research/workorder-pools.tsv (scripts/research/workorder_pools.py) maps
    /// 229 of the 257 pools to the unreferenced ObtainSchematic rows they draw from (likely: text and table layout, docs/ASSUMPTIONS.md).
    /// Which row of a pool a character gets is a hash of character and quest (assumption: retail picked at random; a fixed pick needs no
    /// saved state and stays the same after a relog).
    /// </summary>
    public static class RandomObjectives
    {
        private static Dictionary<uint, uint[]> pools;
        private static readonly object gate = new();

        private static Dictionary<uint, uint[]> Pools
        {
            get
            {
                lock (gate)
                {
                    if (pools != null)
                        return pools;

                    pools = new Dictionary<uint, uint[]>();
                    string file = Path.Combine(Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research", "workorder-pools.tsv");
                    try
                    {
                        foreach (string line in File.ReadLines(file))
                        {
                            if (line.StartsWith("#"))
                                continue;
                            string[] f = line.Split('\t');
                            if (f.Length < 3 || !uint.TryParse(f[0], out uint pool))
                                continue;
                            uint[] rows = f[2].Split(',').Select(s => uint.TryParse(s, out uint v) ? v : 0u)
                                .Where(v => GameTableManager.Instance.QuestObjective.GetEntry(v) != null).ToArray();
                            if (rows.Length > 0)
                                pools[pool] = rows;
                        }
                        FailureLedger.Record("quest-random-pools-loaded", $"{pools.Count} pools");
                    }
                    catch (Exception ex)
                    {
                        FailureLedger.Record("quest-random-pools-load-failed", ex.GetType().Name, ex.Message);
                    }
                    return pools;
                }
            }
        }

        public static bool HasPool(QuestObjectiveEntry entry) => entry != null && entry.Type == (uint)QuestObjectiveType.Unknown31 && Pools.ContainsKey(entry.Data);

        /// <summary>The pool row a character gets for a quest's type 31 objective, null when the pool is unknown.</summary>
        public static QuestObjectiveEntry Pick(ulong characterId, ushort questId, QuestObjectiveEntry entry)
        {
            if (entry == null || entry.Type != (uint)QuestObjectiveType.Unknown31)
                return null;
            if (!Pools.TryGetValue(entry.Data, out uint[] rows))
            {
                FailureLedger.Record("quest-random-pool-unknown", $"pool {entry.Data}", $"quest {questId}");
                return null;
            }

            ulong hash = characterId * 0x9E3779B97F4A7C15ul ^ ((ulong)questId * 0xC2B2AE3D27D4EB4Ful);
            hash ^= hash >> 29;
            return GameTableManager.Instance.QuestObjective.GetEntry(rows[(int)(hash % (ulong)rows.Length)]);
        }

        /// <summary>Every row a pool can give (for tools and bots).</summary>
        public static IEnumerable<QuestObjectiveEntry> Rows(QuestObjectiveEntry entry)
        {
            if (entry == null || !Pools.TryGetValue(entry.Data, out uint[] rows))
                return Enumerable.Empty<QuestObjectiveEntry>();
            return rows.Select(r => GameTableManager.Instance.QuestObjective.GetEntry(r));
        }
    }
}
