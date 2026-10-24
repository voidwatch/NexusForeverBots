using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map.Static;
using NexusForever.WorldServer.Game.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Map
{
    public abstract class InstancedMap<T> : IMap, IInstancedMap where T : MapInstance, new()
    {
        private class PendingAdd
        {
            public Player Player { get; init; }
            public MapPosition MapPosition { get; set; }
            // see comment further down
            public bool ExplictRemove { get; set; }
        }

        // ReSharper disable once StaticMemberInGenericType
        private static readonly Logger log = LogManager.GetCurrentClassLogger();

        public WorldEntry Entry { get; private set; }

        private readonly ConcurrentQueue<PendingAdd> pendingAdds = new();
        private readonly Dictionary</*instanceId*/ ulong, T> instances = new();

        /// <summary>
        /// Initialise <see cref="InstancedMap{T}"/> with <see cref="WorldEntry"/>.
        /// </summary>
        public void Initialise(WorldEntry entry)
        {
            Entry = entry;
        }

        /// <summary>
        /// Enqueue <see cref="GridEntity"/> to be added to <see cref="InstancedMap{T}"/>.
        /// </summary>
        /// <remarks>
        /// Characters should not be added directly through this method.
        /// Use <see cref="Player.TeleportTo(MapPosition, TeleportReason)"/> instead.
        /// </remarks>
        public void EnqueueAdd(GridEntity entity, MapPosition position)
        {
            // entities can't be added directly to an instance map
            // they need to be added to the map instance instead
            throw new InvalidOperationException();
        }

        /// <summary>
        /// Returns if <see cref="GridEntity"/> can be added to <see cref="InstancedMap{T}"/>.
        /// </summary>
        public virtual bool CanEnter(GridEntity entity, MapPosition position)
        {
            return false;
        }

        /// <summary>
        /// Enqueue <see cref="Player"/> to be added to <see cref="IMap"/>.
        /// </summary>
        /// <remarks>
        /// Characters should not be added directly through this method.
        /// Use <see cref="Player.TeleportTo(MapPosition, TeleportReason)"/> instead.
        /// </remarks>
        public void EnqueueAdd(Player player, MapPosition position)
        {
            pendingAdds.Enqueue(new PendingAdd
            {
                Player      = player,
                MapPosition = position
            });
        }

        /// <summary>
        /// Returns if <see cref="Player"/> can be added to <see cref="InstancedMap{T}"/>.
        /// </summary>
        public virtual GenericError? CanEnter(Player player, MapPosition position)
        {
            if (!MapManager.Instance.CanCreateInstance(player))
                return GenericError.InstanceLimitExceeded;

            return null;
        }

        /// <summary>
        /// Invoked each world tick with the delta since the previous tick occurred.
        /// </summary>
        public void Update(double lastTick)
        {
            ProcessPending();
            UpdateInstances(lastTick);
        }

        public System.Collections.Generic.IReadOnlyCollection<MapInstance> GetInstances()
        {
            lock (instances)
                return new List<MapInstance>(instances.Values);
        }

        private void ProcessPending()
        {
            var newActions = new List<PendingAdd>();
            while (pendingAdds.TryDequeue(out PendingAdd pending))
            {
                T instance = null;

                // maps that share instances (dungeons: one per group) name the instance for a player that did not ask for one
                MapInfo info = pending.MapPosition.Info;
                ulong? resolved = ResolveInstanceId(pending.Player, info);
                if (resolved != info.InstanceId)
                {
                    info = new MapInfo { Entry = info.Entry, InstanceId = resolved };
                    pending.MapPosition = new MapPosition { Info = info, Position = pending.MapPosition.Position };
                }

                if (info.InstanceId.HasValue)
                    instance = GetInstance(info.InstanceId.Value);

                // specified instance doesn't have an instance or no specific instance was specified
                if (instance == null)
                {
                    instance = CreateInstance(pending.Player, info);
                    lock (instances)
                        instances.Add(instance.InstanceId, instance);
                    InstanceStats.Created(Entry);

                    log.Trace($"Created new instance {instance.InstanceId} for map {instance.Entry.Id}.");

                    MapManager.Instance.IncreaseInstanceCount(pending.Player);
                }

                // existing instance is unloading
                if (instance.UnloadStatus.HasValue)
                {
                    // this is a special rare case when a player is removed from an instance during unload but the return position is the same instance
                    // in this case we must explicitly remove the player from the instance and wait for the instance to unload before recreating
                    // you can replicate this functionality by invoking the instance unload command while on your own residence
                    if (instance.UnloadStatus == MapUnloadStatus.UnloadingPlayers
                        && pending.Player.Map == instance
                        && !pending.ExplictRemove)
                    {
                        pending.Player.RemoveFromMap();
                        pending.ExplictRemove = true;
                    }

                    // wait for instance to completely unload before recreating and adding
                    newActions.Add(pending);
                    continue;
                }

                GenericError? result = instance.CanEnter(pending.Player, pending.MapPosition);
                if (result.HasValue)
                {
                    // one refused player must not drop the rest of the queue (and the players waiting for an unload, kept in newActions)
                    pending.Player.OnTeleportToFailed(result.Value);
                    continue;
                }

                instance.EnqueueAdd(pending.Player, pending.MapPosition);
            }

            // new actions are added to the queue after processing so they are processed starting next update
            foreach (PendingAdd action in newActions)
                pendingAdds.Enqueue(action);
        }

        private void UpdateInstances(double lastTick)
        {
            var unloadedInstances = new List<MapInstance>();
            foreach (T map in instances.Values)
            {
                map.Update(lastTick);

                if (map.UnloadStatus == MapUnloadStatus.Complete)
                    unloadedInstances.Add(map);
            }

            foreach (MapInstance instance in unloadedInstances)
            {
                lock (instances)
                    instances.Remove(instance.InstanceId);
                InstanceStats.Released(Entry, instance);
                log.Trace($"Unloaded existing instance {instance.InstanceId} for map {instance.Entry.Id}.");
            }
        }

        /// <summary>
        /// The instance id to use for a player, the requested one by default.
        /// </summary>
        protected virtual ulong? ResolveInstanceId(Player player, MapInfo info)
        {
            return info.InstanceId;
        }

        /// <summary>
        /// Create a new instance for <see cref="Player"/> with <see cref="MapInfo"/>.
        /// </summary>
        protected abstract T CreateInstance(Player player, MapInfo info);

        /// <summary>
        /// Get an existing instance with supplied id.
        /// </summary>
        protected virtual T GetInstance(ulong instanceId)
        {
            return instances.TryGetValue(instanceId, out T map) ? map : default;
        }
    }
}
