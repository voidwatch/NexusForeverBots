using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;
using NexusForever.Shared.Game.Map;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Achievement.Static;
using NexusForever.WorldServer.Game.Entity;

namespace NexusForever.WorldServer.Game.Map
{
    public class ZoneMapManager : ISaveCharacter
    {
        private const float ZoneMapBoundary = MapDefines.WorldGridOrigin * MapDefines.GridSize;
        private const byte RateLimit = 10;

        private ushort currentZoneMap;
        private ZoneMapCoordinate currentZoneMapCoordinate = new();
        private Vector3 lastPosition;

        private readonly Dictionary<ushort /*ZoneMapId*/, ZoneMap> zoneMaps = new();

        private readonly Player player;

        /// <summary>
        /// Create a new <see cref="ZoneMapManager"/> from existing <see cref="CharacterModel"/> database model.
        /// </summary>
        public ZoneMapManager(Player owner, CharacterModel characterModel)
        {
            player = owner;

            foreach (CharacterZonemapHexgroupModel hexGroupModel in characterModel.ZonemapHexgroup)
            {
                if (!zoneMaps.TryGetValue(hexGroupModel.ZoneMap, out ZoneMap zoneMap))
                {
                    MapZoneEntry entry = GameTableManager.Instance.MapZone.GetEntry(hexGroupModel.ZoneMap);
                    zoneMap = new ZoneMap(entry, player);
                    zoneMaps.Add(hexGroupModel.ZoneMap, zoneMap);
                }

                zoneMap.AddHexGroup(hexGroupModel.HexGroup, false);
            }
        }

        public void Save(CharacterContext context)
        {
            foreach (ZoneMap zoneMap in zoneMaps.Values)
                zoneMap.Save(context);
        }

        /// <summary>Map zone and hex of the last discovery check (diagnostics).</summary>
        public string Where => $"map zone {currentZoneMap} hex {currentZoneMapCoordinate?.X},{currentZoneMapCoordinate?.Y}";

        /// <summary>Hex groups of the map zone the player has discovered.</summary>
        public int DiscoveredGroups(ushort mapZoneId) => zoneMaps.TryGetValue(mapZoneId, out ZoneMap zoneMap) ? zoneMap.DiscoveredGroups : 0;

        /// <summary>The first hex group of the map zone the player has not discovered yet, 0 when all are (bot driver).</summary>
        public uint NextUndiscovered(ushort mapZoneId, ICollection<uint> skip = null)
        {
            zoneMaps.TryGetValue(mapZoneId, out ZoneMap zoneMap);
            return ZoneMapIndex.Groups(mapZoneId).FirstOrDefault(g => (zoneMap == null || !zoneMap.HasHexGroup((ushort)g)) && (skip == null || !skip.Contains(g)));
        }

        public void SendInitialPackets()
        {
            SendZoneMaps();
        }

        public void SendZoneMaps()
        {
            foreach (ZoneMap zoneMap in zoneMaps.Values)
                zoneMap.Send();
        }

        /// <summary>
        /// Invoked when <see cref="Player"/> moves to a new <see cref="Vector3"/>.
        /// </summary>
        public void OnRelocate(Vector3 vector)
        {
            if (player.Zone == null || currentZoneMap == 0 || (player.IsLoading && player.Session is not Bots.BotSession))
                return;

            // rate limit for e.g. micro movement when idle swimming in a current
            if (Vector3.Distance(lastPosition, vector) < RateLimit)
                return;

            lastPosition = vector;

            ZoneMapCoordinate newZoneMapCoordinate = Points2ZoneMapCoordinate(vector);
            if (currentZoneMapCoordinate?.X == newZoneMapCoordinate.X && currentZoneMapCoordinate?.Y == newZoneMapCoordinate.Y)
                return;

            currentZoneMapCoordinate = newZoneMapCoordinate;

            // the current zone's map, plus every other map zone of this world (MapZoneWorldJoin) with a hex group here: maps overlap
            // (Auroria's hex groups 412.. lie inside Illium, 9773 of 27812 hexes belong to several map zones), and a map whose groups lie
            // partly under another zone could never be completed otherwise (docs/ASSUMPTIONS.md "Path missions", ExploreZone)
            var touched = new HashSet<ushort>();
            if (ZoneMapIndex.GroupsNear(currentZoneMap, currentZoneMapCoordinate.X, currentZoneMapCoordinate.Y).Any())
                touched.Add(currentZoneMap);
            if (player.Map != null)
                foreach (uint mapZoneId in ZoneMapIndex.MapZonesNear(player.Map.Entry.Id, currentZoneMapCoordinate.X, currentZoneMapCoordinate.Y))
                    touched.Add((ushort)mapZoneId);

            foreach (ushort mapZoneId in touched)
            {
                if (!zoneMaps.TryGetValue(mapZoneId, out ZoneMap zoneMap))
                {
                    MapZoneEntry entry = GameTableManager.Instance.MapZone.GetEntry(mapZoneId);
                    if (entry == null)
                        continue;
                    zoneMaps.Add(mapZoneId, zoneMap = new ZoneMap(entry, player));
                }
                if (zoneMap.IsComplete)
                    continue;

                bool added = false;
                foreach (uint hexGroupId in ZoneMapIndex.GroupsNear(mapZoneId, currentZoneMapCoordinate.X, currentZoneMapCoordinate.Y).ToList())
                {
                    // +/-1 is for proximity (ZoneMapIndex.GroupsNear)
                    if (zoneMap.HasHexGroup((ushort)hexGroupId))
                        continue;
                    zoneMap.AddHexGroup((ushort)hexGroupId);
                    added = true;
                }
                if (!added)
                    continue;

                player.PathMissionManager.OnZoneMapExplored(mapZoneId, zoneMap.DiscoveredGroups);
                if (zoneMap.IsComplete)
                    player.AchievementManager.CheckAchievements(player, AchievementType.MapComplete, mapZoneId);
            }
        }

        private static ZoneMapCoordinate Points2ZoneMapCoordinate(Vector3 position)
        {
            float a = (ZoneMapBoundary + position.Z) / 27.712812f + 1f;
            float b = (ZoneMapBoundary + position.X) / 32f + 0.5f;
            float c = a * 0.5f + b;
            float d = c - a * 2f;
            float e = (float)(d * 0.33333334d + 0.0000099999997d);

            return new ZoneMapCoordinate
            {
                X = (ushort)(e * 2f + a),
                Y = (ushort)(a * 0.5f)
            };
        }

        /// <summary>
        /// Invoked when <see cref="Player"/> moves to a new zone.
        /// </summary>
        public void OnZoneUpdate()
        {
            // maybe there is a more efficient lookup method @sub_1406FB130 - this works for all zones though
            WorldZoneEntry worldZoneEntry = player.Zone;
            MapZoneEntry zoneMap = null;

            do
            {
                if (worldZoneEntry == null)
                    break;

                zoneMap = GameTableManager.Instance.MapZone.Entries.FirstOrDefault(m => m.WorldZoneId == worldZoneEntry.Id);
                if (zoneMap != null)
                    break;

                worldZoneEntry = GameTableManager.Instance.WorldZone.GetEntry(worldZoneEntry.ParentZoneId);
            }
            while (worldZoneEntry != null);

            if (zoneMap == null)
            {
                MapZoneWorldJoinEntry mapZoneWorldJoin = GameTableManager.Instance.MapZoneWorldJoin.Entries.FirstOrDefault(m => m.WorldId == player.Map.Entry.Id);
                if (mapZoneWorldJoin != null)
                    zoneMap = GameTableManager.Instance.MapZone.GetEntry(mapZoneWorldJoin.MapZoneId);
            }

            if (zoneMap == null)
            {
                currentZoneMap = 0;
                return;
            }

            if (zoneMap.Id == currentZoneMap)
                return;

            if (!zoneMaps.ContainsKey((ushort) zoneMap.Id))
                zoneMaps.Add((ushort)zoneMap.Id, new ZoneMap(zoneMap, player));

            currentZoneMap = (ushort)zoneMap.Id;
        }
    }
}
