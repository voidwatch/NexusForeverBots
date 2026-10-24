using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using NexusForever.Shared.Database;
using NexusForever.Shared.GameTable;

namespace NexusForever.WorldServer.Game.Combat
{
    /// <summary>
    /// Spawn points of the bind point creatures (Creature2.BindPointId: 55 "Transmat Terminal" creatures, BindPoint rows name their
    /// settlements), indexed per world from the world database in the background. Used as the "Return to Holocrypt" destination: the
    /// tables have no holocrypt (graveyard) positions (docs/ASSUMPTIONS.md "Death and resurrection", guess).
    /// </summary>
    public static class BindPoints
    {
        private static volatile Dictionary<ushort, List<Vector3>> spawns;
        private static int loading;

        public static bool Ready => spawns != null;

        public static void Load()
        {
            if (Interlocked.Exchange(ref loading, 1) == 1)
                return;

            Task.Run(() =>
            {
                var index = new Dictionary<ushort, List<Vector3>>();
                try
                {
                    var ids = new HashSet<uint>(GameTableManager.Instance.Creature2.Entries
                        .Where(c => c != null && c.BindPointId != 0u)
                        .Select(c => c.Id));
                    foreach (var spawn in DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns())
                    {
                        if (!ids.Contains(spawn.Creature))
                            continue;
                        if (!index.TryGetValue(spawn.World, out List<Vector3> list))
                            index[spawn.World] = list = new List<Vector3>();
                        list.Add(new Vector3(spawn.X, spawn.Y, spawn.Z));
                    }
                    Diagnostics.FailureLedger.Record("system-info", "bind point spawns indexed", $"{index.Values.Sum(l => l.Count)} spawns in {index.Count} worlds");
                }
                catch (Exception ex)
                {
                    Diagnostics.FailureLedger.Record("system-fail", "bindpoint-index", ex.Message);
                }
                spawns = index;
            });
        }

        /// <summary>The bind point spawn nearest to the position in the world, or null (none in that world, or the index is not loaded).</summary>
        public static Vector3? Nearest(ushort worldId, Vector3 position)
        {
            Dictionary<ushort, List<Vector3>> index = spawns;
            if (index == null)
            {
                Load();
                return null;
            }

            if (!index.TryGetValue(worldId, out List<Vector3> list) || list.Count == 0)
                return null;

            return list.OrderBy(p => Vector3.DistanceSquared(p, position)).First();
        }
    }
}
