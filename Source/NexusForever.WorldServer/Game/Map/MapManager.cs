using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using NexusForever.Shared;
using NexusForever.Shared.Configuration;
using NexusForever.Shared.Game;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map.Static;
using NexusForever.WorldServer.Game.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Map
{
    public sealed class MapManager : Singleton<MapManager>, IUpdate
    {
        private class PendingAdd
        {
            public Player Player { get; init; }
            public MapPosition MapPosition { get; init; }
        }

        private static readonly Logger log = LogManager.GetCurrentClassLogger();

        private readonly ConcurrentQueue<PendingAdd> pendingAdds = new();
        private readonly Dictionary</*worldId*/ ushort, IMap> maps = new();

        public sealed class MapProfile
        {
            public long Ticks;
            public double TotalMs;
            public double MaxMs;
            public int ActiveGrids;
        }

        private readonly ConcurrentDictionary<ushort, MapProfile> profiles = new();
        private readonly ConcurrentDictionary<string, bool> loggedMapExceptions = new();
        private DateTime lastSlowWarning;
        private int suppressedSlowWarnings;

        /// <summary>
        /// Time spent updating each map since start, for finding out what makes the world update slow.
        /// </summary>
        public IReadOnlyDictionary<ushort, MapProfile> Profiles => profiles;

        private void TimedUpdate(IMap map, double lastTick)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                map.Update(lastTick);
            }
            catch (Exception exception)
            {
                // an exception here aborts the whole map update for this tick, so entities stop moving, respawning and fighting
                string frames = string.Join(" | ", (exception.StackTrace ?? "").Split('\n').Take(5).Select(f => f.Trim()));
                NexusForever.WorldServer.Diagnostics.FailureLedger.Record("map-update-exception", $"world {(map as BaseMap)?.Entry.Id}: {exception.GetType().Name}: {exception.Message}", frames);
                if (loggedMapExceptions.TryAdd(exception.GetType().Name + exception.Message, true))
                    log.Error(exception, $"Map {(map as BaseMap)?.Entry.Id} update threw, further occurrences are only counted.");
                return;
            }

            double ms = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;

            if (map is not BaseMap baseMap)
                return;

            MapProfile profile = profiles.GetOrAdd((ushort)baseMap.Entry.Id, _ => new MapProfile());
            profile.Ticks++;
            profile.TotalMs += ms;
            if (ms > profile.MaxMs)
                profile.MaxMs = ms;
            // a slow tick with the minute it happened in, to tell start-up work (map and index loads) from stalls during the run
            if (ms > 200d)
                NexusForever.WorldServer.Diagnostics.FailureLedger.Record("map-slow-tick", $"world {baseMap.Entry.Id}",
                    $"minute {(int)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMinutes}, {ms:0} ms");
            profile.ActiveGrids = baseMap.ActiveGridCount;
        }

        private readonly ConcurrentDictionary<ulong, uint> instanceCounts = new();
        // reset instance limit counts every hour
        private readonly UpdateTimer instanceCountReset = new(TimeSpan.FromMinutes(60));

        private MapManager()
        {
        }

        /// <summary>
        /// Invoked each world tick with the delta since the previous tick occurred.
        /// </summary>
        public void Update(double lastTick)
        {
            instanceCountReset.Update(lastTick);
            if (instanceCountReset.HasElapsed)
            {
                instanceCounts.Clear();
                instanceCountReset.Reset();
            }

            ProcessPending();
            UpdateMaps(lastTick);
            UnloadIdleMaps(lastTick);
        }

        private readonly Dictionary<ushort, double> idleMaps = new();
        private double idleCheck;

        /// <summary>
        /// Seconds an open world map with no active grid, entity or pending action stays loaded (NF_MAP_IDLE_UNLOAD, default 420). Its file is
        /// 200 to 500 MB in memory; every world a bot visited stayed forever, which showed up as a steady memory climb over a soak run.
        /// </summary>
        private static readonly double IdleMapUnloadSeconds = double.TryParse(Environment.GetEnvironmentVariable("NF_MAP_IDLE_UNLOAD"), out double idle) ? idle : 420d;

        private void UnloadIdleMaps(double lastTick)
        {
            idleCheck += lastTick;
            if (idleCheck < 10d)
                return;

            double elapsed = idleCheck;
            idleCheck = 0d;
            List<ushort> precached = ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.PrecacheBaseMaps ?? new List<ushort>();
            foreach (KeyValuePair<ushort, IMap> pair in maps.ToList())
            {
                // only plain open world maps: instanced maps manage their own lifetime
                if (pair.Value.GetType() != typeof(BaseMap) || precached.Contains(pair.Key))
                    continue;

                var baseMap = (BaseMap)pair.Value;
                if (baseMap.ActiveGridCount > 0 || baseMap.PendingActionCount > 0 || baseMap.EntityCount > 0)
                {
                    idleMaps.Remove(pair.Key);
                    continue;
                }

                idleMaps[pair.Key] = idleMaps.GetValueOrDefault(pair.Key) + elapsed;
                if (idleMaps[pair.Key] < IdleMapUnloadSeconds)
                    continue;

                maps.Remove(pair.Key);
                idleMaps.Remove(pair.Key);
                string asset = baseMap.Entry.AssetPath;
                // worlds can share a map file (several worlds use Map\\Eastern)
                if (!maps.Values.Any(m => m is BaseMap other && other.Entry.AssetPath == asset))
                    BaseMapManager.Instance.UnloadBaseMap(asset);

                NexusForever.WorldServer.Diagnostics.FailureLedger.Record("map-unloaded", $"world {pair.Key} idle for {IdleMapUnloadSeconds:0} s", asset);
                log.Info($"Unloaded idle map for world {pair.Key}.");
            }
        }

        private void ProcessPending()
        {
            if (pendingAdds.Count == 0)
                return;

            var sw = Stopwatch.StartNew();

            while (pendingAdds.TryDequeue(out PendingAdd pending))
            {
                IMap map;
                try
                {
                    map = CreateMap(pending.MapPosition.Info.Entry);
                }
                catch (Exception exception)
                {
                    // a world whose map file is missing must not take the whole server down, the player stays where they are
                    NexusForever.WorldServer.Diagnostics.FailureLedger.Record("map-create-failed", $"world {pending.MapPosition.Info.Entry.Id} ({pending.MapPosition.Info.Entry.AssetPath}): {exception.GetType().Name}: {exception.Message}");
                    pending.Player.OnTeleportToFailed(GenericError.InstanceNotFound);
                    continue;
                }

                GenericError? result = map.CanEnter(pending.Player, pending.MapPosition);
                if (result.HasValue)
                {
                    pending.Player.OnTeleportToFailed(result.Value);
                    continue;
                }

                if (map is IInstancedMap instancedMap)
                    instancedMap.EnqueueAdd(pending.Player, pending.MapPosition);
                else
                    map.EnqueueAdd(pending.Player, pending.MapPosition);
            }

            sw.Stop();
            if (sw.ElapsedMilliseconds > 10)
                log.Warn($"{pendingAdds.Count} pending add(s) took {sw.ElapsedMilliseconds}ms to process!");
        }

        private void UpdateMaps(double lastTick)
        {
            if (maps.Count == 0)
                return;

            var sw = Stopwatch.StartNew();

            try
            {
                if (ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.SynchronousUpdate)
                {
                    foreach (IMap map in maps.Values)
                        TimedUpdate(map, lastTick);
                }
                else
                {
                    var tasks = new List<Task>();
                    foreach (IMap map in maps.Values)
                        tasks.Add(Task.Run(() => { TimedUpdate(map, lastTick); }));

                    Task.WaitAll(tasks.ToArray());
                }
            }
            catch
            {
                // ignored.
            }

            sw.Stop();
            if (sw.ElapsedMilliseconds > 10)
            {
                // once every ten seconds, otherwise a busy world drowns the log
                suppressedSlowWarnings++;
                if (sw.ElapsedMilliseconds > 250 || DateTime.UtcNow - lastSlowWarning > TimeSpan.FromSeconds(10))
                {
                    log.Warn($"{maps.Count} map(s) took {sw.ElapsedMilliseconds}ms to update! ({suppressedSlowWarnings} slow update(s) since the last message)");
                    lastSlowWarning = DateTime.UtcNow;
                    suppressedSlowWarnings = 0;
                }
            }
        }

        /// <summary>
        /// Enqueue <see cref="Player"/> to be added to a map. 
        /// </summary>
        public System.Collections.Generic.IReadOnlyCollection<IMap> LoadedMaps => new List<IMap>(maps.Values);

        public void AddToMap(Player player, MapPosition mapPosition)
        {
            pendingAdds.Enqueue(new PendingAdd
            {
                Player      = player,
                MapPosition = mapPosition
            });
        }

        /// <summary>
        /// Create base <see cref="IMap"/> for <see cref="WorldEntry"/>.
        /// </summary>
        private IMap CreateMap(WorldEntry entry)
        {
            if (maps.TryGetValue((ushort)entry.Id, out IMap map))
                return map;

            switch ((MapType)entry.Type)
            {
                case MapType.Residence:
                case MapType.Community:
                    map = new ResidenceInstancedMap();
                    break;
                case MapType.Expedition:
                case MapType.Adventure:
                case MapType.Raid:
                case MapType.Dungeon:
                    map = new DungeonInstancedMap();
                    break;
                default:
                    map = new BaseMap();
                    break;
            }

            long heapBefore = GC.GetTotalMemory(false);
            map.Initialise(entry);
            maps.Add((ushort)entry.Id, map);
            Diagnostics.FailureLedger.Record("map-created", $"world {entry.Id} type {entry.Type}", $"managed heap {heapBefore / 1048576} MB before, {GC.GetTotalMemory(false) / 1048576} MB after, {(map as BaseMap)?.EntityCountSpawnModels ?? 0} spawn models");

            log.Trace($"Created new base map for world {entry.Id}.");

            return map;
        }

        /// <summary>
        /// Returns if <see cref="Player"/> can create a new instance.
        /// </summary>
        public bool CanCreateInstance(Player player)
        {
            if (instanceCounts.TryGetValue(player.CharacterId, out uint instanceCount)
                && instanceCount > (ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.MaxInstances ?? 10u))
                return false;

            return true;
        }

        /// <summary>
        /// Increase instance count for <see cref="Player"/>.
        /// </summary>
        public void IncreaseInstanceCount(Player player)
        {
            // players with bypass RBAC have no limit to instances in an hour
            if (player.Session.AccountRbacManager.HasPermission(RBAC.Static.Permission.BypassInstanceLimits))
                return;

            instanceCounts.AddOrUpdate(player.CharacterId, 1, (k, v) => v + 1);
        }
    }
}
