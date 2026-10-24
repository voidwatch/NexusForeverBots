using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Combat;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Layouts follow the WildStar client, taken from the NexusForever death and resurrection implementation.

    [Message(GameMessageOpcode.ServerEntityDeathState)]
    public class ServerEntityDeathState : IWritable
    {
        public uint UnitId { get; set; }
        public bool Dead { get; set; }
        public byte Reason { get; set; }
        public uint RezHealth { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(UnitId);
            writer.Write(Dead);
            writer.Write(Reason, 5u);
            writer.Write(RezHealth);
        }
    }

    [Message(GameMessageOpcode.ServerResurrectionShow)]
    public class ServerResurrectionShow : IWritable
    {
        public uint GhostId { get; set; }
        public uint RezCost { get; set; }
        public uint TimeUntilRezMs { get; set; }
        public bool Dead { get; set; }
        public ResurrectionType ShowRezFlags { get; set; }
        public bool HasCasterRezRequest { get; set; }
        public uint TimeUntilWakeHereMs { get; set; }
        public uint TimeUntilForceRezMs { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GhostId);
            writer.Write(RezCost);
            writer.Write(TimeUntilRezMs);
            writer.Write(Dead);
            writer.Write(ShowRezFlags, 8u);
            writer.Write(HasCasterRezRequest);
            writer.Write(TimeUntilWakeHereMs);
            writer.Write(TimeUntilForceRezMs);
        }
    }

    [Message(GameMessageOpcode.ClientResurrectRequest)]
    public class ClientResurrectRequest : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }

    [Message(GameMessageOpcode.ClientResurrectAccept)]
    public class ClientResurrectAccept : IReadable
    {
        public uint UnitId { get; private set; }
        public ResurrectionType Type { get; private set; }
        public uint Value { get; private set; }

        // client writer (Lua Unit:Resurrect): u32 unit, RezType:7, u32 (the caster unit for SpellCasterLocation, 0 from
        // the stock death screen)
        public void Read(GamePacketReader reader)
        {
            UnitId = reader.ReadUInt();
            Type   = reader.ReadEnum<ResurrectionType>(7u);
            Value  = reader.ReadUInt();
        }
    }
}
