using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>
    /// The stance of a pet; the client stores it per pet unit and fires PetStanceChanged.
    /// </summary>
    [Message(GameMessageOpcode.ServerChangePetStance)]
    public class ServerChangePetStance : IWritable
    {
        public uint PetUnitId { get; set; }
        public byte Stance { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(PetUnitId);
            writer.Write(Stance, 5u);
        }
    }
}
