using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>
    /// 0x0510 PlacePlug / SetPlugRotation (operation 1), RemovePlug (2), RepairPlug (4); client senders,
    /// client writer (docs/ASSUMPTIONS.md "Housing plugs").
    /// </summary>
    [Message(GameMessageOpcode.ClientHousingPlugUpdate)]
    public class ClientHousingPlugUpdate : IReadable
    {
        public const byte Place  = 1;
        public const byte Remove = 2;
        public const byte Repair = 4;

        public TargetPlayerIdentity TargetPlayerIdentity { get; } = new TargetPlayerIdentity();
        public uint PlotIndex { get; private set; }
        public uint PlugItemId { get; private set; }
        public uint PlugFacing { get; private set; }
        public uint Unknown { get; private set; }
        public byte Operation { get; private set; }

        public void Read(GamePacketReader reader)
        {
            TargetPlayerIdentity.Read(reader);

            PlotIndex  = reader.ReadUInt();
            PlugItemId = reader.ReadUInt();
            PlugFacing = reader.ReadUInt();
            Unknown    = reader.ReadUInt();
            Operation  = reader.ReadByte(3u);

            // 5 x 20 bytes, one per HousingContributionInfoId0x of the plug item (the client fills the first dword of each from the
            // contribution row); contributions are not modelled on the server
            reader.ReadBytes(5 * 20);
        }
    }
}
