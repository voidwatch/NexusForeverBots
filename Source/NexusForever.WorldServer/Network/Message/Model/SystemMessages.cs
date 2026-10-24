using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Client messages reachable from the Lua API (research/clientre/lua-sends2.tsv), layouts from
    // the retail client's writers/readers (client analysis). Field meanings: docs/ASSUMPTIONS.md "Remaining partial systems".

    /// <summary>
    /// 0x014C RepairItemVendor / RepairAllItemsVendor, client writer: three u64. RepairItemVendor sends {item guid, 0, 0};
    /// RepairAllItemsVendor (client code) sends {0, a qword of the vendor unit, a client-side value}: item guid 0 = repair all.
    /// </summary>
    [Message(GameMessageOpcode.ClientVendorRepair)]
    public class ClientVendorRepair : IReadable
    {
        public ulong ItemGuid { get; private set; }
        public ulong Unknown1 { get; private set; }
        public ulong Unknown2 { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ItemGuid = reader.ReadULong();
            Unknown1 = reader.ReadULong();
            Unknown2 = reader.ReadULong();
        }
    }

    /// <summary>
    /// 0x01A7 to the client, client reader / client handler: item guid (u64), durability (float, fraction 0..1). The client stores the
    /// float in the item and fires the Lua event "ItemDurabilityUpdate".
    /// </summary>
    [Message(GameMessageOpcode.ServerItemDurabilityUpdate)]
    public class ServerItemDurabilityUpdate : IWritable
    {
        public ulong ItemGuid { get; set; }
        public float Durability { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(ItemGuid);
            writer.Write(Durability);
        }
    }

    /// <summary>
    /// 0x00FF PurchaseFlightPath / TakeShuttle (taxi vendor unit), client writer: count (32) then count u32 taxi node ids. PurchaseFlightPath
    /// sends the path the client found to the chosen node (TaxiMap.lua tPath.tRoute is a list of idNode), TakeShuttle one node.
    /// </summary>
    [Message(GameMessageOpcode.ClientTaxiPurchase)]
    public class ClientTaxiPurchase : IReadable
    {
        public List<uint> Nodes { get; } = new();

        public void Read(GamePacketReader reader)
        {
            uint count = reader.ReadUInt();
            for (uint i = 0u; i < count && i < 64u; i++)
                Nodes.Add(reader.ReadUInt());
        }
    }

    /// <summary>
    /// 0x05EF MatchMakingLib.Queue, 0x05F3 QueueAsGroup (Lua client function), client writer: match type (5), count (32), count u32
    /// MatchingGameMap ids, a 14 bit field, option flags (32: 0x20 bAsMercenary, 0x80 bFindOthers, 0x100 prime level set, masked per match type),
    /// roles (32, bit 1 &lt;&lt; role from arRoles), prime level (32, nPrimeLevel capped). Options table parsed by client function.
    /// </summary>
    public abstract class ClientMatchingQueueBase : IReadable
    {
        public byte MatchType { get; private set; }
        public List<uint> Maps { get; } = new();
        public ushort Unknown { get; private set; }
        public uint Flags { get; private set; }
        public uint Roles { get; private set; }
        public uint PrimeLevel { get; private set; }

        public void Read(GamePacketReader reader)
        {
            MatchType = reader.ReadByte(5u);
            uint count = reader.ReadUInt();
            for (uint i = 0u; i < count && i < 256u; i++)
                Maps.Add(reader.ReadUInt());
            Unknown    = reader.ReadUShort(14u);
            Flags      = reader.ReadUInt();
            Roles      = reader.ReadUInt();
            PrimeLevel = reader.ReadUInt();
        }
    }

    [Message(GameMessageOpcode.ClientMatchingQueue)]
    public class ClientMatchingQueue : ClientMatchingQueueBase
    {
    }

    /// <summary>0x05F3 QueueAsGroup: same writer as 0x05EF.</summary>
    [Message(GameMessageOpcode.ClientMatchingQueueParty)]
    public class ClientMatchingQueueParty : ClientMatchingQueueBase
    {
    }

    /// <summary>0x05F8 random queue (Lua client function, random match), client writer: match type (5), roles (32), flags (32).</summary>
    [Message(GameMessageOpcode.ClientMatchingQueueRandom)]
    public class ClientMatchingQueueRandom : IReadable
    {
        public byte MatchType { get; private set; }
        public uint Value1 { get; private set; }
        public uint Value2 { get; private set; }

        public void Read(GamePacketReader reader)
        {
            MatchType = reader.ReadByte(5u);
            Value1    = reader.ReadUInt();
            Value2    = reader.ReadUInt();
        }
    }

    /// <summary>
    /// 0x05B2 ConfirmRole / DeclineRoleCheck, client writer: match type (5), roles (32, 1 &lt;&lt; role), accept (1). DeclineRoleCheck sends
    /// roles 0, accept 0.
    /// </summary>
    [Message(GameMessageOpcode.ClientMatchingRoleCheckResponse)]
    public class ClientMatchingRoleCheckResponse : IReadable
    {
        public byte MatchType { get; private set; }
        public uint Roles { get; private set; }
        public bool Accept { get; private set; }

        public void Read(GamePacketReader reader)
        {
            MatchType = reader.ReadByte(5u);
            Roles     = reader.ReadUInt();
            Accept    = reader.ReadBit();
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>0x057C LeavePendingRemovalInstance (Lua client function, second send client function): no body.</summary>
    [Message(GameMessageOpcode.ClientLeavePendingRemoval)]
    public class ClientLeavePendingRemoval : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }

    /// <summary>
    /// 0x0408 group member flags, client writer, built by client function: group id (u64), member identity (realm 14, character u64),
    /// the member's new flags (32) and the bits that changed (32). Flag bits per Lua setter: SetInvitePermission 0x2, SetKickPermission 0x4,
    /// SetRoleTank 0x20, SetRoleHealer 0x40, SetRoleDPS 0x80, SetMainTank 0x100, SetMainAssist 0x200, SetRaidAssistant 0x400, SetReady 0x800
    /// (also sets 0x4000 HasSetReady), SetRoleLocked 0x1000, SetCanMark 0x2000: the server's GroupMemberInfoFlags.
    /// </summary>
    [Message(GameMessageOpcode.ClientGroupMemberFlagsUpdate)]
    public class ClientGroupMemberFlagsUpdate : IReadable
    {
        public ulong GroupId { get; private set; }
        public Shared.Identity Target { get; } = new();
        public uint Flags { get; private set; }
        public uint Changed { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId = reader.ReadULong();
            Target.Read(reader);
            Flags   = reader.ReadUInt();
            Changed = reader.ReadUInt();
        }
    }

    /// <summary>0x0406 group flags, client writer: group id (u64), new group flags (32, GroupFlags: Raid 0x2, join request / referral bits).</summary>
    [Message(GameMessageOpcode.ClientGroupFlagsUpdate)]
    public class ClientGroupFlagsUpdate : IReadable
    {
        public ulong GroupId { get; private set; }
        public uint Flags { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId = reader.ReadULong();
            Flags   = reader.ReadUInt();
        }
    }

    /// <summary>0x044A ReadyCheck, client writer: group id (u64), message (wide string).</summary>
    [Message(GameMessageOpcode.ClientGroupReadyCheck)]
    public class ClientGroupReadyCheck : IReadable
    {
        public ulong GroupId { get; private set; }
        public string Message { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId = reader.ReadULong();
            Message = reader.ReadWideString();
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>0x03B3 FriendshipLib.AddByUnit / AddByGroupIndex / AddBySuggested / SetType, client writer: identity, type (4), note.</summary>
    [Message(GameMessageOpcode.ClientFriendshipAdd)]
    public class ClientFriendshipAdd : IReadable
    {
        public Shared.TargetPlayerIdentity Target { get; } = new();
        public byte Type { get; private set; }
        public string Note { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Target.Read(reader);
            Type = reader.ReadByte(4u);
            Note = reader.ReadWideString();
        }
    }

    /// <summary>0x03C1 FriendshipLib.Remove, client writer: identity, type (4).</summary>
    [Message(GameMessageOpcode.ClientFriendshipRemove)]
    public class ClientFriendshipRemove : IReadable
    {
        public Shared.TargetPlayerIdentity Target { get; } = new();
        public byte Type { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Target.Read(reader);
            Type = reader.ReadByte(4u);
        }
    }

    /// <summary>0x03C7 FriendshipLib.SetNote, client writer (shared with neighbour invite/evict): identity, note.</summary>
    [Message(GameMessageOpcode.ClientFriendshipSetNote)]
    public class ClientFriendshipSetNote : IReadable
    {
        public Shared.TargetPlayerIdentity Target { get; } = new();
        public string Note { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Target.Read(reader);
            Note = reader.ReadWideString();
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>0x0512 NeighborInvite / NeighborInviteByName, client writer: identity (zero when by name), name.</summary>
    [Message(GameMessageOpcode.ClientHousingNeighborInvite)]
    public class ClientHousingNeighborInvite : IReadable
    {
        public Shared.TargetPlayerIdentity Target { get; } = new();
        public string Name { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Target.Read(reader);
            Name = reader.ReadWideString();
        }
    }

    /// <summary>0x0513 NeighborInviteAccept / NeighborInviteDecline, client writer: accept (1 bit).</summary>
    [Message(GameMessageOpcode.ClientHousingNeighborResponse)]
    public class ClientHousingNeighborResponse : IReadable
    {
        public bool Accept { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Accept = reader.ReadBit();
        }
    }

    /// <summary>0x0515 NeighborEvict, client writer: identity, name.</summary>
    [Message(GameMessageOpcode.ClientHousingNeighborEvict)]
    public class ClientHousingNeighborEvict : IReadable
    {
        public Shared.TargetPlayerIdentity Target { get; } = new();
        public string Name { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Target.Read(reader);
            Name = reader.ReadWideString();
        }
    }

    /// <summary>0x0518 NeighborSetPermission, client writer: identity, name, permission (32; bit 0 roommate).</summary>
    [Message(GameMessageOpcode.ClientHousingNeighborPermission)]
    public class ClientHousingNeighborPermission : IReadable
    {
        public Shared.TargetPlayerIdentity Target { get; } = new();
        public string Name { get; private set; }
        public uint Permission { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Target.Read(reader);
            Name       = reader.ReadWideString();
            Permission = reader.ReadUInt();
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>0x0432 SetTargetMarker / ClearTargetMarker, client writer (built by client function): marker (32), unit id (32).</summary>
    [Message(GameMessageOpcode.ClientGroupMarkUnit)]
    public class ClientGroupMarkUnit : IReadable
    {
        public uint Marker { get; private set; }
        public uint UnitId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Marker = reader.ReadUInt();
            UnitId = reader.ReadUInt();
        }
    }

    /// <summary>0x0431 to the client, client reader: group id (u64), count (32), count x {marker (32), unit id (32)} (same pair order the client keeps).</summary>
    [Message(GameMessageOpcode.ServerGroupMarkers)]
    public class ServerGroupMarkers : IWritable
    {
        public ulong GroupId { get; set; }
        public List<(uint Marker, uint UnitId)> Markers { get; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write((uint)Markers.Count);
            foreach ((uint marker, uint unitId) in Markers)
            {
                writer.Write(marker);
                writer.Write(unitId);
            }
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>0x0411 GroupLib.GotoGroupInstance (Lua client function, send client function), client writer: group id (u64).</summary>
    [Message(GameMessageOpcode.ClientGroupGotoInstance)]
    public class ClientGroupGotoInstance : IReadable
    {
        public ulong GroupId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId = reader.ReadULong();
        }
    }

    /// <summary>0x0412 GroupLib.SetInstanceDifficulty (Lua client function), client writer: group id (u64), difficulty (2 bits).</summary>
    [Message(GameMessageOpcode.ClientGroupInstanceDifficulty)]
    public class ClientGroupInstanceDifficulty : IReadable
    {
        public ulong GroupId { get; private set; }
        public byte Difficulty { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId    = reader.ReadULong();
            Difficulty = reader.ReadByte(2u);
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Model
{
    /// <summary>
    /// 0x05C8 answer to a pending matched game: one bit, 1 = accept. The client sends it from the pending-game popup and, on a decline,
    /// raises its MatchingCancelPendingGame event (certain: client sender and writer).
    /// </summary>
    [Message(GameMessageOpcode.ClientMatchingMatchReadyResponse)]
    public class ClientMatchingMatchReadyResponse : IReadable
    {
        public bool Accept { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Accept = reader.ReadBit();
        }
    }

    /// <summary>0x0606 MatchingGame.TransferIntoMatch: no body (certain: the client writer writes nothing).</summary>
    [Message(GameMessageOpcode.ClientMatchingTransferIntoMatch)]
    public class ClientMatchingTransferIntoMatch : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }

    /// <summary>0x05DA MatchingGame.LeaveGame: no body (certain: the client writer writes nothing).</summary>
    [Message(GameMessageOpcode.ClientMatchingMatchLeave)]
    public class ClientMatchingMatchLeave : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }
}
