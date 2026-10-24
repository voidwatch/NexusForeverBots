using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>Sent when a Scientist deploys the scanbot (path action key or Lua) and none is out.</summary>
    [Message(GameMessageOpcode.ClientPathScientistRequestScanbot)]
    public class ClientPathScientistRequestScanbot : IReadable
    {
        public uint ScanbotProfile { get; private set; }
        public bool IsGameCommand { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ScanbotProfile = reader.ReadUInt();
            IsGameCommand  = reader.ReadBit();
        }
    }
}
