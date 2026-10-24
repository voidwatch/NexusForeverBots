using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>Sent when a Scientist dismisses the scanbot (no payload).</summary>
    [Message(GameMessageOpcode.ClientPathScientistDismissScanbotPathAction)]
    public class ClientPathScientistDismissScanbotPathAction : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }
}
