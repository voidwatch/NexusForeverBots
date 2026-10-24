using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using NexusForever.Database.World.Model;
using NexusForever.Shared;
using NexusForever.Shared.Configuration;
using NexusForever.Shared.Game.Map;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.IO.Map;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Map.Static;
using NexusForever.WorldServer.Game.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Map
{
    public class BaseMap : IMap
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();

        public const float DefaultVisionRange = 128f;

        /// <summary>
        /// Distance between a <see cref="Player"/> and a <see cref="GridEntity"/> before the entity can be seen.
        /// </summary>
        public virtual float VisionRange { get; protected set; } = DefaultVisionRange;

        public WorldEntry Entry { get; private set; }
        public MapFile File { get; private set; }

        private readonly MapGrid[] grids = new MapGrid[MapDefines.WorldGridCount * MapDefines.WorldGridCount];
        private readonly HashSet<(uint GridX, uint GridZ)> activeGrids = new();

        protected readonly ConcurrentQueue<IGridAction> pendingActions = new();

        private readonly QueuedCounter entityCounter = new();
        protected readonly Dictionary<uint /*guid*/, GridEntity> entities = new();
        private EntityCache entityCache;

        /// <summary>
        /// Initialise <see cref="BaseMap"/> with <see cref="WorldEntry"/>.
        /// </summary>
        public virtual void Initialise(WorldEntry entry)
        {
            Entry       = entry;
            File        = BaseMapManager.Instance.GetBaseMap(Entry.AssetPath);
            entityCache = EntityCacheManager.Instance.GetEntityCache((ushort)Entry.Id);
        }

        /// <summary>
        /// Invoked each world tick with the delta since the previous tick occurred.
        /// </summary>
        public int ActiveGridCount => activeGrids.Count;
        public int EntityCount => entities.Count;
        public int PendingActionCount => pendingActions.Count;

        /// <summary>Spell-summoned units on the map (<see cref="NonPlayer.IsSpellSummon"/>), bounded by the summon handlers.</summary>
        public int SpellSummonCount => spellSummons;
        private int spellSummons;
        private DateTime lastBacklogReport;

        /// <summary>
        /// Grid actions processed per update: the configured threshold (100), more while a backlog is waiting. With a fixed 100 a burst that
        /// arrives faster than that never drains; the removals of expired summons then wait behind it and the summons keep acting.
        /// </summary>
        private static int ActionBudget(int pending, uint threshold)
            => pending <= threshold * 10 ? (int)threshold : (int)Math.Min(Math.Max(threshold, pending / 20), 2000);

        /// <summary>
        /// One ledger line per minute while a map has more than 20000 queued grid actions: what the queue holds (action types, the creatures most
        /// often waiting to be added). A runaway shows up here before the memory does.
        /// </summary>
        private void ReportBacklog(int pending)
        {
            if (pending < 20000 || (DateTime.UtcNow - lastBacklogReport).TotalSeconds < 60d)
                return;

            lastBacklogReport = DateTime.UtcNow;
            try
            {
                var types = new Dictionary<string, int>();
                var creatures = new Dictionary<uint, int>();
                foreach (IGridAction action in pendingActions)
                {
                    string type = action.GetType().Name;
                    types[type] = types.GetValueOrDefault(type) + 1;
                    if (action is GridActionAdd { Entity: WorldEntity added })
                        creatures[added.CreatureId] = creatures.GetValueOrDefault(added.CreatureId) + 1;
                }
                Diagnostics.FailureLedger.Record("grid-action-backlog", $"map {Entry.Id}",
                    $"{pending} queued: {string.Join(", ", types.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value}"))}; adds by creature "
                    + string.Join(", ", creatures.OrderByDescending(c => c.Value).Take(5).Select(c => $"{c.Key} x{c.Value}")) + $"; {spellSummons} spell summons on the map");
            }
            catch (Exception exception)
            {
                Diagnostics.FailureLedger.Record("grid-action-backlog", $"map {Entry.Id}", $"{pending} queued, breakdown failed: {exception.GetType().Name}");
            }
        }
        public int EntityCountSpawnModels => entityCache == null ? 0 : (int)entityCache.EntityCount;
        public System.Collections.Generic.List<GridEntity> AllEntities() { lock (entities) return entities.Values.ToList(); }

        public virtual void Update(double lastTick)
        {
            long t0 = Diagnostics.Profiler.Start();
            ProcessGridActions();
            Diagnostics.Profiler.Add("map.actions", t0);
            t0 = Diagnostics.Profiler.Start();
            UpdateGrids(lastTick);
            Diagnostics.Profiler.Add("map.grids", t0);
        }

        private void ProcessGridActions()
        {
            int pending = pendingActions.Count;
            if (pending == 0)
                return;

            ReportBacklog(pending);
            var newActions = new List<IGridAction>();
            foreach (IGridAction action in pendingActions.Dequeue((uint)ActionBudget(pending,
                ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.GridActionThreshold ?? 100u)))
            {
                try
                {
                switch (action)
                {
                    case GridActionAdd actionAdd:
                    {
                        if (CanAddEntity(actionAdd.Entity, actionAdd.Vector))
                            AddEntity(actionAdd.Entity, actionAdd.Vector);
                        else
                        {
                            // retry threshold to prevent any issues with stuck actions
                            actionAdd.RequeueCount++;
                            if (actionAdd.RequeueCount > (ConfigurationManager<WorldServerConfiguration>.Instance.Config.Map.GridActionMaxRetry ?? 5u))
                            {
                                log.Error($"Failed to add entity to map {Entry.Id} at position X: {actionAdd.Vector.X}, Y: {actionAdd.Vector.Y}, Z: {actionAdd.Vector.Z}!");
                            }
                            else
                                newActions.Add(action);
                        }

                        break;
                    }
                    case GridActionPending actionPending:
                    {
                        if (actionPending.Entity.Map == null)
                        {
                            newActions.Add(new GridActionAdd
                            {
                                Entity = actionPending.Entity,
                                Vector = actionPending.Vector
                            });
                        }
                        else
                            newActions.Add(actionPending);

                        break;
                    }
                    case GridActionRelocate actionRelocate:
                        // the entity may have left this map (teleport, logout) after the relocation was queued
                        if (actionRelocate.Entity.Map != this)
                            Diagnostics.FailureLedger.Record("grid-action-stale", "relocate for an entity that is not in the map", $"map {Entry.Id}");
                        else
                            RelocateEntity(actionRelocate.Entity, actionRelocate.Vector);
                        break;
                    case GridActionRemove actionRemove:
                        if (actionRemove.Entity.Map != this)
                            Diagnostics.FailureLedger.Record("grid-action-stale", "remove for an entity that is not in the map", $"map {Entry.Id}");
                        else
                            RemoveEntity(actionRemove.Entity);
                        break;
                }
                }
                catch (System.Exception exception)
                {
                    // one bad action must not lose the rest of the batch or stop the map, count it
                    Diagnostics.FailureLedger.Record("grid-action-exception", $"map {Entry.Id} {action.GetType().Name}: {exception.GetType().Name}: {exception.Message}", (exception.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
                }
            }

            // new actions are added to the queue after processing so they are processed starting next update
            foreach (IGridAction action in newActions)
                pendingActions.Enqueue(action);
        }

        private void UpdateGrids(double lastTick)
        {
            var unloadedGrids = new List<MapGrid>();
            foreach ((uint gridX, uint gridZ) in activeGrids)
            {
                MapGrid grid = GetGrid(gridX, gridZ);
                grid.Update(lastTick);

                if (grid.UnloadStatus == MapUnloadStatus.Complete)
                    unloadedGrids.Add(grid);
            }

            foreach (MapGrid grid in unloadedGrids)
            {
                grids[grid.Coord.Z * MapDefines.WorldGridCount + grid.Coord.X] = null;
                activeGrids.Remove(grid.Coord);

                log.Trace($"Deactivated grid at X:{grid.Coord.X}, Z:{grid.Coord.Z}.");
            }
        }

        /// <summary>
        /// Enqueue <see cref="GridEntity"/> to be added to <see cref="BaseMap"/>.
        /// </summary>
        /// <remarks>
        /// Characters should not be added directly through this method.
        /// Use <see cref="Player.TeleportTo(MapPosition, TeleportReason)"/> instead.
        /// </remarks>
        public void EnqueueAdd(GridEntity entity, MapPosition position)
        {
            entity.OnEnqueueAddToMap();

            if (entity.Map != null)
            {
                // entity is on an existing map, will need to be removed before add
                entity.RemoveFromMap();

                pendingActions.Enqueue(new GridActionPending
                {
                    Entity = entity,
                    Vector = position.Position
                });
            }
            else
            {
                pendingActions.Enqueue(new GridActionAdd
                {
                    Entity = entity,
                    Vector = position.Position
                });
            }
        }

        /// <summary>
        /// Returns if <see cref="GridEntity"/> can be added to <see cref="BaseMap"/>.
        /// </summary>
        public virtual bool CanEnter(GridEntity entity, MapPosition position)
        {
            if (!IsValidPosition(position.Position))
                return false;

            return true;
        }

        /// <summary>
        /// Returns if <see cref="Player"/> can be added to <see cref="BaseMap"/>.
        /// </summary>
        public virtual GenericError? CanEnter(Player player, MapPosition position)
        {
            if (!IsValidPosition(position.Position))
                return GenericError.InstanceInvalidDestination;

            return null;
        }

        private bool IsValidPosition(Vector3 position)
        {
            // make sure player position falls within a valid grid
            int radius = (MapDefines.GridSize * MapDefines.WorldGridCount) / 2;
            if (position.X < -radius || position.X > radius
                || position.Z < -radius || position.Z > radius)
                return false;

            // TODO: check if map file has a grid at position?

            return true;
        }

        /// <summary>
        /// Enqueue <see cref="GridEntity"/> to be removed from <see cref="BaseMap"/>.
        /// </summary>
        public void EnqueueRemove(GridEntity entity)
        {
            entity.OnEnqueueRemoveFromMap();
            pendingActions.Enqueue(new GridActionRemove
            {
                Entity = entity
            });
        }

        /// <summary>
        /// Enqueue <see cref="GridEntity"/> to be relocated in <see cref="BaseMap"/> to <see cref="Vector3"/>.
        /// </summary>
        public void EnqueueRelocate(GridEntity entity, Vector3 position)
        {
            pendingActions.Enqueue(new GridActionRelocate
            {
                Entity = entity,
                Vector = position
            });
        }

        /// <summary>
        /// Return all <see cref="GridEntity"/>'s from <see cref="Vector3"/> in range that satisfy <see cref="ISearchCheck"/>.
        /// </summary>
        public void Search(Vector3 vector, float radius, ISearchCheck check, out List<GridEntity> intersectedEntities)
        {
            // negative radius is unlimited distance
            if (radius < 0)
            {
                intersectedEntities = entities.Values.ToList();
                return;
            }

            intersectedEntities = new List<GridEntity>();
            // a large radius near the world edge would step outside the grid range (GetGridCoord throws), clamp to the world
            for (float z = System.Math.Max(vector.Z - radius, WorldMin); z < System.Math.Min(vector.Z + radius + MapDefines.GridCellSize, WorldMax); z += MapDefines.GridCellSize)
            {
                for (float x = System.Math.Max(vector.X - radius, WorldMin); x < System.Math.Min(vector.X + radius + MapDefines.GridCellSize, WorldMax); x += MapDefines.GridCellSize)
                {
                    var searchVector = new Vector3(x, 0f, z);
                    // don't activate new grids during search
                    GetGrid(searchVector)?.Search(searchVector, check, intersectedEntities);
                }
            }
        }

        /// <summary>
        /// Return all <see cref="MapGrid"/>'s from <see cref="Vector3"/> in range.
        /// </summary>
        public void GridSearch(Vector3 vector, float radius, out List<MapGrid> intersectedGrids)
        {
            // negative radius is unlimited distance
            if (radius < 0)
            {
                intersectedGrids = GetActiveGrids().ToList();
                return;
            }

            intersectedGrids = new List<MapGrid>();
            for (float z = System.Math.Max(vector.Z - radius, WorldMin); z < System.Math.Min(vector.Z + radius + MapDefines.GridSize, WorldMax); z += MapDefines.GridSize)
            {
                for (float x = System.Math.Max(vector.X - radius, WorldMin); x < System.Math.Min(vector.X + radius + MapDefines.GridSize, WorldMax); x += MapDefines.GridSize)
                {
                    MapGrid grid = GetGrid(new Vector3(x, 0f, z));
                    if (grid != null)
                        intersectedGrids.Add(grid);
                }
            }
        }

        /// <summary>
        /// Return <see cref="GridEntity"/> by guid.
        /// </summary>
        public T GetEntity<T>(uint guid) where T : GridEntity
        {
            if (!entities.TryGetValue(guid, out GridEntity entity))
                return null;
            return (T)entity;
        }

        /// <summary>
        /// Enqueue broadcast of <see cref="IWritable"/> to all <see cref="Player"/>'s on the map.
        /// </summary>
        public void EnqueueToAll(IWritable message)
        {
            foreach (GridEntity entity in entities.Values)
            {
                Player player = entity as Player;
                player?.Session.EnqueueMessageEncrypted(message);
            }
        }

        /// <summary>
        /// Notify <see cref="MapGrid"/> at coordinates of the addition of new <see cref="Player"/> that is in vision range.
        /// </summary>
        public void GridAddVisiblePlayer(uint gridX, uint gridZ)
        {
            MapGrid grid = GetGrid(gridX, gridZ);
            grid.AddVisiblePlayer();
        }

        /// <summary>
        /// Notify <see cref="MapGrid"/> at coordinates of the removal of an existing <see cref="Player"/> that is no longer in vision range.
        /// </summary>
        public void GridRemoveVisiblePlayer(uint gridX, uint gridZ)
        {
            MapGrid grid = GetGrid(gridX, gridZ);
            grid.RemoveVisiblePlayer();
        }

        /// <summary>
        /// Every creature spawn defined for this map, whether or not its grid is loaded.
        /// </summary>
        public IEnumerable<EntityModel> GetSpawnModels()
        {
            return entityCache?.All ?? Enumerable.Empty<EntityModel>();
        }

        /// <summary>
        /// Returns true if the <see cref="MapGrid"/> at the supplied position is currently loaded.
        /// </summary>
        public bool IsGridActive(Vector3 vector)
        {
            (uint gridX, uint gridZ) = MapGrid.GetGridCoord(vector);
            if (gridX >= MapDefines.WorldGridCount || gridZ >= MapDefines.WorldGridCount)
                return false;

            MapGrid grid = GetGrid(gridX, gridZ);
            return grid != null && !grid.UnloadStatus.HasValue;
        }

        private MapGrid GetGrid(uint gridX, uint gridZ)
        {
            return grids[gridZ * MapDefines.WorldGridCount + gridX];
        }

        /// <summary>World extent in metres along x and z (128 grids of 512 m around the origin).</summary>
        private const float WorldMin = -MapDefines.WorldGridOrigin * MapDefines.GridSize;
        private const float WorldMax = (MapDefines.WorldGridCount - MapDefines.WorldGridOrigin) * MapDefines.GridSize - 0.01f;

        private MapGrid GetGrid(Vector3 vector)
        {
            (uint gridX, uint gridZ) = MapGrid.GetGridCoord(vector);
            return GetGrid(gridX, gridZ);
        }

        protected IEnumerable<MapGrid> GetActiveGrids()
        {
            return activeGrids
                .Select(g => GetGrid(g.GridX, g.GridZ));
        }

        /// <summary>
        /// Activate one or more <see cref="MapGrid"/>'s at <see cref="Vector"/> depending on vision range of <see cref="GridEntity"/>.
        /// </summary>
        private void ActivateGrid(GridEntity entity, Vector3 vector)
        {
            float range = entity.ActivationRange;
            for (float z = vector.Z - range; z < vector.Z + range + MapDefines.GridSize; z += MapDefines.GridSize)
            {
                for (float x = vector.X - range; x < vector.X + range + MapDefines.GridSize; x += MapDefines.GridSize)
                {
                    (uint gridX, uint gridZ) = MapGrid.GetGridCoord(new Vector3(x, 0f, z));
                    MapGrid grid = GetGrid(gridX, gridZ);
                    if (grid == null)
                        ActivateGrid(gridX, gridZ);
                }
            }
        }

        /// <summary>
        /// Activate single <see cref="MapGrid"/> at position.
        /// </summary>
        private void ActivateGrid(uint gridX, uint gridZ)
        {
            // instance grids are not unloaded, the entire instance is unloaded instead during inactivity
            var grid = new MapGrid(gridX, gridZ, this is not MapInstance);
            grids[gridZ * MapDefines.WorldGridCount + gridX] = grid;
            activeGrids.Add(grid.Coord);

            log.Trace($"Activated grid at X:{gridX}, Z:{gridZ}.");

            SpawnGrid(gridX, gridZ);
        }

        protected virtual void SpawnGrid(uint gridX, uint gridZ)
        {
            foreach (EntityModel model in entityCache.GetEntities(gridX, gridZ))
            {
                // non issue once all entities types are handled
                // researched spawns (ids from 9000000, scripts/import-research-spawns.sh) have no height, the terrain gives it
                if (model.Id >= 9000000u && model.Y == 0f && File != null)
                    model.Y = GetTerrainHeight(model.X, model.Z, 0f);
                // in prop-geometry instances their height is the nearest world location's (an estimate): put them on the closest floor
                else if (model.Id >= 9000000u && File?.Floor != null && File.TryGetNearestFloorHeight(model.X, model.Z, model.Y, out float floorY))
                    model.Y = floorY;

                if (SkipSpawn(model))
                    continue;

                WorldEntity entity = EntityManager.Instance.NewEntity((EntityType)model.Type) ?? EntityManager.Instance.NewEntity(EntityType.Simple);
                entity.Initialise(SpawnModel(model));

                var position = new MapPosition
                {
                    Position = new Vector3(model.X, model.Y, model.Z)
                };

                if (CanEnter(entity, position))
                    EnqueueAdd(entity, position);
            }
        }

        /// <summary>The spawn as this map creates it (veteran instances swap creature rows, <see cref="VeteranVariants"/>).</summary>
        protected virtual EntityModel SpawnModel(EntityModel model) => model;

        /// <summary>True for a spawn this map does not create (dungeon creatures marked for the other difficulty).</summary>
        protected virtual bool SkipSpawn(EntityModel model) => false;

        private bool CanAddEntity(GridEntity entity, Vector3 vector)
        {
            ActivateGrid(entity, vector);

            // if the grid doesn't exist we can't add the new entity to it
            MapGrid grid = GetGrid(vector);
            if (grid == null)
                return false;

            // if the grid is unloading we can't add the new entity to it
            // we will need to wait for the grid to fully unload
            if (grid.UnloadStatus.HasValue)
                return false;

            return true;
        }

        protected virtual void AddEntity(GridEntity entity, Vector3 vector)
        {
            Debug.Assert(entity.Map == null);

            MapGrid grid = GetGrid(vector);
            grid.AddEntity(entity, vector);

            uint guid = entityCounter.Dequeue();
            entities.Add(guid, entity);
            if (entity is NonPlayer { IsSpellSummon: true })
                spellSummons++;

            entity.OnAddToMap(this, guid, vector);

            log.Trace($"Added entity {entity.Guid} to map {Entry.Id}.");
        }

        protected virtual void RemoveEntity(GridEntity entity)
        {
            Debug.Assert(entity.Map != null);

            GetGrid(entity.Position).RemoveEntity(entity);

            log.Trace($"Removed entity {entity.Guid} from map {Entry.Id}.");

            entityCounter.Enqueue(entity.Guid);
            entities.Remove(entity.Guid);
            if (entity is NonPlayer { IsSpellSummon: true })
                spellSummons--;

            entity.OnRemoveFromMap();
        }

        protected virtual void RelocateEntity(GridEntity entity, Vector3 vector)
        {
            long profileStart = Diagnostics.Profiler.Start();
            RelocateEntityInner(entity, vector);
            Diagnostics.Profiler.Add("map.relocate", profileStart);
        }

        private void RelocateEntityInner(GridEntity entity, Vector3 vector)
        {
            Debug.Assert(entity.Map != null);

            ActivateGrid(entity, vector);
            MapGrid newGrid = GetGrid(vector);
            MapGrid oldGrid = GetGrid(entity.Position);

            if (newGrid.Coord.X != oldGrid.Coord.X
                || newGrid.Coord.Z != oldGrid.Coord.Z)
            {
                oldGrid.RemoveEntity(entity);
                newGrid.AddEntity(entity, vector);
            }
            else
                oldGrid.RelocateEntity(entity, vector);

            entity.OnRelocate(vector);
        }

        /// <summary>
        /// Return terrain height at supplied position.
        /// </summary>
        public float GetTerrainHeight(float x, float z, float fallbackY)
        {
            // terrain grid plus prop floors (.nffloor) of instances; the floor nearest below fallbackY wins, without data the entity keeps
            // its height (see docs/ASSUMPTIONS.md)
            return File != null && File.TryGetFloorHeight(x, z, fallbackY, out float height) ? height : fallbackY;
        }

        public float GetTerrainHeight(float x, float z)
        {
            // TODO: handle cases for water and props
            return File.GetTerrainHeight(new Vector3(x, 0, z));
        }
    }
}
