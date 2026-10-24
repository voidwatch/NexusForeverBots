using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>
    /// 0x015E: salvage an item (certain: the confirm button of type SalvageItem (4) in the client's inventory and salvage windows runs the
    /// function that sends 0x015E with the item's location and its guid; upstream called the opcode ClientItemUseLootBag).
    /// </summary>
    [Message(GameMessageOpcode.ClientItemSalvage)]
    public class ClientItemSalvage : IReadable
    {
        public ItemLocation Location { get; } = new ItemLocation();
        public ulong Guid { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Location.Read(reader);
            Guid = reader.ReadULong();
        }
    }
}
