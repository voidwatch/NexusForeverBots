using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared;
using NexusForever.Shared.Game.Map;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map.Search;
using NLog;

namespace NexusForever.WorldServer.Game.Map
{
    public class MapCell : IUpdate
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Coordinates of cell within the grid.
        /// </summary>
        public (uint X, uint Z) Coord { get; }

        private readonly HashSet<GridEntity> entities = new();

        /// <summary>
        /// Return <see cref="MapCell"/> at supplied <see cref="Vector3"/>.
        /// </summary>
        public static (uint cellX, uint cellZ) GetCellCoord(Vector3 vector)
        {
            int x = (int)Math.Floor(MapDefines.GridCellCount * (MapDefines.WorldGridOrigin + vector.X / MapDefines.GridSize));
            if (x < 0 || x > MapDefines.WorldGridCount * MapDefines.GridCellCount)
                throw new ArgumentOutOfRangeException($"Position X: {vector.X} is invalid!");

            int z = (int)Math.Floor(MapDefines.GridCellCount * (MapDefines.WorldGridOrigin + vector.Z / MapDefines.GridSize));
            if (z < 0 || z > MapDefines.WorldGridCount * MapDefines.GridCellCount)
                throw new ArgumentOutOfRangeException($"Position Z: {vector.X} is invalid!");

            return ((uint)x & MapDefines.GridCellCount - 1, (uint)z & MapDefines.GridCellCount - 1);
        }

        /// <summary>
        /// Initialise new <see cref="MapCell"/> at the supplied position.
        /// </summary>
        public MapCell(uint cellX, uint cellZ)
        {
            Coord = (cellX, cellZ);
        }

        /// <summary>An idle entity is updated every this many ticks (25 ticks of 20 ms: reaction to being hit within one tick, everything else 500 ms).</summary>
        private const int SleepEvery = 25;

        // Only awake entities are visited each tick. Sleeping ones sit in a slot of the wheel and are looked at when their slot comes up.
        private readonly HashSet<GridEntity> awake = new();
        private readonly List<(GridEntity Entity, int Epoch)>[] wheel = new List<(GridEntity, int)>[SleepEvery];
        private readonly List<GridEntity> wakeQueue = new();
        private readonly List<GridEntity> toSleep = new();
        private int slot;
        private double clock;

        /// <summary>Move a sleeping entity to the awake set (applied at the start of the next update).</summary>
        public void Wake(GridEntity entity)
        {
            if (!entity.Sleeping || entity.Cell != this)
                return;

            entity.Sleeping = false;
            entity.SleepEpoch++;
            entity.SleepAccumulated = clock - entity.SleepClock;
            wakeQueue.Add(entity);
        }

        public void Update(double lastTick)
        {
            clock += lastTick;
            slot = (slot + 1) % SleepEvery;

            List<(GridEntity Entity, int Epoch)> due = wheel[slot];
            if (due != null && due.Count > 0)
            {
                foreach ((GridEntity entity, int epoch) in due)
                {
                    if (!entity.Sleeping || entity.SleepEpoch != epoch || entity.Cell != this)
                        continue;

                    entity.Sleeping = false;
                    entity.SleepEpoch++;
                    entity.SleepAccumulated = clock - entity.SleepClock - lastTick;
                    awake.Add(entity);
                }
                due.Clear();
            }

            if (wakeQueue.Count > 0)
            {
                foreach (GridEntity entity in wakeQueue)
                    if (entity.Cell == this)
                        awake.Add(entity);
                wakeQueue.Clear();
            }

            foreach (GridEntity entity in awake)
            {
                double elapsed = lastTick + entity.SleepAccumulated;
                entity.SleepAccumulated = 0d;

                long start = Diagnostics.Profiler.Start();
                entity.Update(elapsed);
                if (Diagnostics.Profiler.Enabled)
                    Diagnostics.Profiler.Add("entity." + entity.GetType().Name, start);

                // an entity with nothing to do goes to sleep, the first time at a slot that depends on its guid so they do not all wake together
                if (entity.CanSleep && entity.Cell == this)
                    toSleep.Add(entity);
            }

            if (toSleep.Count > 0)
            {
                foreach (GridEntity entity in toSleep)
                {
                    awake.Remove(entity);
                    int offset = entity.SleepStaggered ? SleepEvery : 1 + (int)(entity.Guid % SleepEvery);
                    entity.SleepStaggered = true;
                    entity.Sleeping = true;
                    entity.SleepClock = clock;
                    (wheel[(slot + offset) % SleepEvery] ??= new List<(GridEntity, int)>()).Add((entity, entity.SleepEpoch));
                }
                toSleep.Clear();
            }
        }

        /// <summary>
        /// Unload <see cref="GridEntity"/>'s from the <see cref="MapCell"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="GridEntity"/>'s are delay unloaded.
        /// </remarks>
        public void Unload()
        {
            foreach (GridEntity entity in entities)
                entity.RemoveFromMap();
        }

        /// <summary>
        /// Add <see cref="GridEntity"/> to the <see cref="MapCell"/>.
        /// </summary>
        public void AddEntity(GridEntity entity)
        {
            entities.Add(entity);
            entity.Cell = this;
            entity.Sleeping = false;
            entity.SleepEpoch++;
            entity.SleepAccumulated = 0d;
            awake.Add(entity);
            log.Trace($"Added entity {entity.Guid} to cell at X:{Coord.X}, Z:{Coord.Z}.");
        }

        /// <summary>
        /// Remove <see cref="GridEntity"/> from the <see cref="MapCell"/>.
        /// </summary>
        public void RemoveEntity(GridEntity entity)
        {
            entities.Remove(entity);
            awake.Remove(entity);
            entity.Sleeping = false;
            entity.SleepEpoch++;
            if (entity.Cell == this)
                entity.Cell = null;
            log.Trace($"Removed entity {entity.Guid} from cell at X:{Coord.X}, Z:{Coord.Z}.");
        }

        /// <summary>
        /// Return all <see cref="GridEntity"/>'s in the <see cref="MapCell"/> that satisfy <see cref="ISearchCheck"/>.
        /// </summary>
        public void Search(ISearchCheck check, List<GridEntity> intersectedEntities)
        {
            intersectedEntities.AddRange(entities.Where(check.CheckEntity));
        }
    }
}
