using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Vehicle from the SummonVehicle spell effect (type 6; 12 effects: turrets, rocket bike, airships, war mechs, battle bots). Spell4Effects
    /// DataBits00 = Creature2 id, DataBits01 = UnitVehicle id (every row has one pilot seat and no passenger or gunner seats). The caster
    /// pilots it; the vehicle leaves the map when its last passenger leaves (Vehicle.PassengerRemove). Its abilities are the vehicle's spells cast
    /// through the normal cast message.
    /// </summary>
    public class SummonedVehicle : Vehicle
    {
        public uint OwnerGuid { get; }

        public SummonedVehicle(Player owner, uint spell4Id, uint creatureId, uint vehicleId)
            : base(EntityType.Vehicle, creatureId, vehicleId, spell4Id)
        {
            OwnerGuid = owner.Guid;
            Rotation  = owner.Rotation;
            Position  = owner.Position;

            Creature2DisplayGroupEntryEntry displayGroupEntry = GameTableManager.Instance.Creature2DisplayGroupEntry.Entries
                .FirstOrDefault(x => x != null && x.Creature2DisplayGroupId == CreatureEntry.Creature2DisplayGroupId);
            DisplayInfo = displayGroupEntry?.Creature2DisplayInfoId ?? 0u;
        }
    }
}
