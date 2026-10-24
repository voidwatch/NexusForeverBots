using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Database.World.Model;
using NexusForever.Shared;
using NexusForever.Shared.Game;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Map.Search;
using NLog;

namespace NexusForever.WorldServer.Game.Combat
{
    /// <summary>
    /// Brings killed creatures back. A creature is respawned from the same database model it was first created from.
    /// </summary>
    public sealed class RespawnManager : Singleton<RespawnManager>, IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        private class Pending
        {
            public BaseMap Map;
            public EntityModel Model;
            public double Time;
        }

        private readonly List<Pending> pending = new();

        public int PendingCount => pending.Count;

        public void Schedule(BaseMap map, EntityModel model, double delay)
        {
            pending.Add(new Pending { Map = map, Model = model, Time = delay });
        }

        public void Update(double lastTick)
        {
            if (pending.Count == 0)
                return;

            foreach (Pending entry in pending.ToList())
            {
                entry.Time -= lastTick;
                if (entry.Time > 0d)
                    continue;

                pending.Remove(entry);
                Respawn(entry);
            }
        }

        private static void Respawn(Pending entry)
        {
            var position = new Vector3(entry.Model.X, entry.Model.Y, entry.Model.Z);

            // an unloaded grid spawns its creatures again when it loads, so there is nothing to do
            if (!entry.Map.IsGridActive(position))
                return;

            // the grid may have unloaded and loaded again while the creature was away, don't double spawn
            entry.Map.Search(position, 2f, new SearchCheckRange(position, 2f), out List<GridEntity> nearby);
            if (nearby.OfType<NonPlayer>().Any(e => e.CreatureId == entry.Model.Creature && !e.IsDead))
                return;

            WorldEntity entity = EntityManager.Instance.NewEntity((EntityType)entry.Model.Type) ?? EntityManager.Instance.NewEntity(EntityType.Simple);
            entity.Initialise(entry.Model);
            entry.Map.EnqueueAdd(entity, new MapPosition { Position = position });

            Diagnostics.FailureLedger.Record("combat-stats", "creature respawned");
            log.Trace($"Respawned creature {entry.Model.Creature}.");
        }
    }
}
