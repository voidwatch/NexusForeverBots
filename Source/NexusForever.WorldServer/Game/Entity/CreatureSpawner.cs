using System.Linq;
using System.Numerics;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Creates creatures at runtime (event units, boss adds) from the creature table: display from the display group, faction from the creature.
    /// </summary>
    public static class CreatureSpawner
    {
        /// <summary>Spawn a creature next to a position on a map, null when the creature is unknown or the position is blocked.</summary>
        public static NonPlayer Spawn(BaseMap map, uint creatureId, Vector3 position, double lifetimeSeconds, object eventTag = null)
        {
            Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(creatureId);
            if (creature == null || map == null)
                return null;

            Creature2DisplayGroupEntryEntry displayGroup = GameTableManager.Instance.Creature2DisplayGroupEntry.Entries.FirstOrDefault(d => d.Creature2DisplayGroupId == creature.Creature2DisplayGroupId);
            var unit = new NonPlayer { EventTag = eventTag };
            unit.Initialise(new NexusForever.Database.World.Model.EntityModel
            {
                Type        = (byte)EntityType.NonPlayer,
                Creature    = creature.Id,
                World       = (ushort)map.Entry.Id,
                X           = position.X,
                Y           = position.Y,
                Z           = position.Z,
                DisplayInfo = displayGroup?.Creature2DisplayInfoId ?? 0u,
                Faction1    = (ushort)creature.FactionId,
                Faction2    = (ushort)creature.FactionId
            });

            if (lifetimeSeconds > 0d)
                unit.SummonLifetime = lifetimeSeconds;

            var mapPosition = new MapPosition { Position = position };
            if (!map.CanEnter(unit, mapPosition))
                return null;

            map.EnqueueAdd(unit, mapPosition);
            return unit;
        }
    }
}
