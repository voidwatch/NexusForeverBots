using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>Tells the owner which unit is its scanbot; unit id 0 means it was dismissed, with the cooldown before the next deploy.</summary>
    [Message(GameMessageOpcode.ServerPathScientistScanbotState)]
    public class ServerPathScientistScanbotState : IWritable
    {
        public uint ScanbotUnitId { get; set; }
        public uint ScanbotCooldownMS { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(ScanbotUnitId);
            writer.Write(ScanbotCooldownMS);
        }
    }
}
