using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>
    /// Health of a unit for everyone who can see it. Unlike the integer stat update this makes the client redraw the unit's health bar.
    /// </summary>
    [Message(GameMessageOpcode.ServerEntityHealthUpdate)]
    public class ServerEntityHealthUpdate : IWritable
    {
        public uint UnitId { get; set; }
        public uint Health { get; set; }
        public bool Unknown0 { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(UnitId);
            writer.Write(Health);
            writer.Write(Unknown0);
        }
    }
}
