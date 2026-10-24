using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.Game.Map;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;

namespace NexusForever.WorldServer.Game.Map
{
    /// <summary>
    /// MapZoneHexGroup / MapZoneHexGroupEntry indexed once (46k rows): hex groups per map zone, hexes per group, groups at a hex.
    /// The per-move discovery check used to scan the whole entry table once per hex group of the zone.
    /// </summary>
    public static class ZoneMapIndex
    {
        private const float ZoneMapBoundary = MapDefines.WorldGridOrigin * MapDefines.GridSize;

        private static readonly object buildLock = new();
        private static Dictionary<uint, List<uint>> groupsByZone;
        private static Dictionary<uint, List<(ushort X, ushort Y)>> hexesByGroup;
        private static Dictionary<(uint Zone, ushort X, ushort Y), List<uint>> groupsAtHex;
        private static Dictionary<(uint World, ushort X, ushort Y), List<uint>> mapZonesAtHex;

        private static void Ensure()
        {
            if (groupsAtHex != null)
                return;
            lock (buildLock)
            {
                if (groupsAtHex != null)
                    return;

                var zones = GameTableManager.Instance.MapZoneHexGroup.Entries.GroupBy(g => g.MapZoneId).ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToList());
                var zoneOf = GameTableManager.Instance.MapZoneHexGroup.Entries.ToDictionary(g => g.Id, g => g.MapZoneId);
                var hexes = new Dictionary<uint, List<(ushort, ushort)>>();
                var at = new Dictionary<(uint, ushort, ushort), List<uint>>();
                foreach (MapZoneHexGroupEntryEntry e in GameTableManager.Instance.MapZoneHexGroupEntry.Entries)
                {
                    if (!hexes.TryGetValue(e.MapZoneHexGroupId, out var list))
                        hexes[e.MapZoneHexGroupId] = list = new List<(ushort, ushort)>();
                    list.Add(((ushort)e.HexX, (ushort)e.HexY));
                    if (!zoneOf.TryGetValue(e.MapZoneHexGroupId, out uint zone))
                        continue;
                    var key = (zone, (ushort)e.HexX, (ushort)e.HexY);
                    if (!at.TryGetValue(key, out var groups))
                        at[key] = groups = new List<uint>();
                    if (!groups.Contains(e.MapZoneHexGroupId))
                        groups.Add(e.MapZoneHexGroupId);
                }
                // map zones of each world (MapZoneWorldJoin) by hex, for discovery across overlapping maps
                var worldAt = new Dictionary<(uint, ushort, ushort), List<uint>>();
                foreach (MapZoneWorldJoinEntry join in GameTableManager.Instance.MapZoneWorldJoin.Entries)
                {
                    if (!zones.TryGetValue(join.MapZoneId, out var groupIds))
                        continue;
                    foreach (uint g in groupIds)
                        if (hexes.TryGetValue(g, out var cells))
                            foreach ((ushort x, ushort y) in cells)
                            {
                                var key = (join.WorldId, x, y);
                                if (!worldAt.TryGetValue(key, out var list))
                                    worldAt[key] = list = new List<uint>();
                                if (!list.Contains(join.MapZoneId))
                                    list.Add(join.MapZoneId);
                            }
                }

                // groups without a hex (3 rows: Whitevale 286, ...) can never be discovered: they do not count
                groupsByZone = zones.ToDictionary(kv => kv.Key, kv => kv.Value.Where(g => hexes.ContainsKey(g)).ToList());
                hexesByGroup = hexes;
                mapZonesAtHex = worldAt;
                groupsAtHex = at;
            }
        }

        public static IReadOnlyList<uint> Groups(uint mapZoneId)
        {
            Ensure();
            return groupsByZone.TryGetValue(mapZoneId, out var list) ? list : new List<uint>();
        }

        public static IReadOnlyList<(ushort X, ushort Y)> Hexes(uint hexGroupId)
        {
            Ensure();
            return hexesByGroup.TryGetValue(hexGroupId, out var list) ? list : new List<(ushort, ushort)>();
        }

        /// <summary>Hex groups of the map zone with a hex within one hex of (x, y) (the +/-1 proximity of <see cref="ZoneMapManager.OnRelocate"/>).</summary>
        public static IEnumerable<uint> GroupsNear(uint mapZoneId, ushort x, ushort y)
        {
            Ensure();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (groupsAtHex.TryGetValue((mapZoneId, (ushort)(x + dx), (ushort)(y + dy)), out var groups))
                        foreach (uint g in groups)
                            yield return g;
        }

        /// <summary>Map zones of the world with a hex group within one hex of (x, y).</summary>
        public static IEnumerable<uint> MapZonesNear(uint worldId, ushort x, ushort y)
        {
            Ensure();
            var found = new HashSet<uint>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (mapZonesAtHex.TryGetValue((worldId, (ushort)(x + dx), (ushort)(y + dy)), out var list))
                        found.UnionWith(list);
            return found;
        }

        /// <summary>
        /// Centre of a hex in world X/Z, the inverse of <see cref="ZoneMapManager"/>'s position to hex conversion
        /// (that conversion reduces to X = floor((B + x) / 48 + 1/3), Y = floor(((B + z) / 27.712812 + 1) / 2)).
        /// </summary>
        public static (float X, float Z) HexCenter(ushort x, ushort y)
        {
            return ((x + 0.5f - 1f / 3f) * 48f - ZoneMapBoundary, 2f * y * 27.712812f - ZoneMapBoundary);
        }
    }
}
