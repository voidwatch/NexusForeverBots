using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>
    /// The client asks to change the stance of one of its pets (37 bits: unit id 32, stance 5; the stance is one of the flags 1, 2, 4, 8, 16).
    /// </summary>
    [Message(GameMessageOpcode.ClientPetStanceChange)]
    public class ClientPetStanceChange : IReadable
    {
        public uint PetUnitId { get; private set; }
        public byte Stance { get; private set; }

        public void Read(GamePacketReader reader)
        {
            PetUnitId = reader.ReadUInt();
            Stance    = reader.ReadByte(5u);
        }
    }
}
