using System;
using System.Collections.Concurrent;
using System.Linq;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Map.Static;

namespace NexusForever.WorldServer.Game.Map
{
    /// <summary>
    /// Live instance count per world type: created when the first player asks for an instance, released when its unload completes.
    /// The failure report prints the line from <see cref="Line"/>; a finished run must return to the count it started with (no instance
    /// kept alive after its players left).
    /// </summary>
    public static class InstanceStats
    {
        private static readonly ConcurrentDictionary<MapType, int> live = new();
        private static int created;
        private static int released;
        private static double lifetimeSeconds;

        private static MapType TypeOf(WorldEntry entry) => (MapType)entry.Type;

        public static void Created(WorldEntry entry)
        {
            live.AddOrUpdate(TypeOf(entry), 1, (_, n) => n + 1);
            System.Threading.Interlocked.Increment(ref created);
        }

        public static void Released(WorldEntry entry, MapInstance instance)
        {
            live.AddOrUpdate(TypeOf(entry), 0, (_, n) => Math.Max(0, n - 1));
            System.Threading.Interlocked.Increment(ref released);
            double seconds = (DateTime.UtcNow - instance.CreatedAt).TotalSeconds;
            lifetimeSeconds += seconds;
            Diagnostics.FailureLedger.Record("instance-released", $"world {entry.Id} {TypeOf(entry)}", $"instance {instance.InstanceId}, alive {seconds:0} s");
        }

        public static int Live => live.Values.Sum();

        public static string Line()
        {
            string types = string.Join(", ", live.Where(p => p.Value > 0).OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"));
            return $"instances: {Live} live ({(types.Length == 0 ? "none" : types)}), {created} created, {released} released"
                + (released > 0 ? $", mean lifetime {lifetimeSeconds / released:0} s" : "");
        }
    }
}
