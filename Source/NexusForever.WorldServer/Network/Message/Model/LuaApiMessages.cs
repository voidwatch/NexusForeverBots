using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Client messages reachable from the Lua API (docs/OPCODE-COVERAGE.md), layouts recovered from the retail client's writers and
    // readers (docs/PROTOCOL.md "Lua-reachable client messages"). Field meanings: docs/ASSUMPTIONS.md.

    /// <summary>
    /// 0x00C5, client writer: challenge id (14), choice (5), value (32). Choice constants from the Lua functions:
    /// ActivateChallenge 1, AbandonChallenge 2, AcceptSharedChallenge 14, RejectSharedChallenge 15.
    /// </summary>
    [Message(GameMessageOpcode.ClientChallengeChoice)]
    public class ClientChallengeChoice : IReadable
    {
        public const byte Activate     = 1;
        public const byte Abandon      = 2;
        public const byte AcceptShared = 14;
        public const byte RejectShared = 15;

        public uint ChallengeId { get; private set; }
        public byte Choice { get; private set; }
        public uint Value { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ChallengeId = reader.ReadUInt(14u);
            Choice      = reader.ReadByte(5u);
            Value       = reader.ReadUInt();
        }
    }

    /// <summary>0x0167 SellJunkToVendor: no body.</summary>
    [Message(GameMessageOpcode.ClientSellJunkToVendor)]
    public class ClientSellJunkToVendor : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }

    /// <summary>0x0150 ResetAttributePoints: no body.</summary>
    [Message(GameMessageOpcode.ClientResetAttributePoints)]
    public class ClientResetAttributePoints : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }

    /// <summary>0x0720 Cinematics_Play, client writer: state (8), flag (1).</summary>
    [Message(GameMessageOpcode.ClientCinematicState)]
    public class ClientCinematicState : IReadable
    {
        public byte State { get; private set; }
        public bool Flag { get; private set; }

        public void Read(GamePacketReader reader)
        {
            State = reader.ReadByte();
            Flag  = reader.ReadBit();
        }
    }

    /// <summary>0x05B4 LeaveAllQueues: no body.</summary>
    [Message(GameMessageOpcode.ClientMatchingQueueLeaveAll)]
    public class ClientMatchingQueueLeaveAll : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }

    /// <summary>0x05B5 LeaveQueue, client writer: match type (5).</summary>
    [Message(GameMessageOpcode.ClientMatchingQueueLeave)]
    public class ClientMatchingQueueLeave : IReadable
    {
        public byte MatchType { get; private set; }

        public void Read(GamePacketReader reader)
        {
            MatchType = reader.ReadByte(5u);
        }
    }

    /// <summary>0x05B6 LeaveQueueAsGroup: same writer as 0x05B5.</summary>
    [Message(GameMessageOpcode.ClientMatchingQueueLeaveAsGroup)]
    public class ClientMatchingQueueLeaveAsGroup : IReadable
    {
        public byte MatchType { get; private set; }

        public void Read(GamePacketReader reader)
        {
            MatchType = reader.ReadByte(5u);
        }
    }

    /// <summary>0x0422 GroupLib.Join, client writer: player name, realm name (wide strings).</summary>
    [Message(GameMessageOpcode.ClientGroupRequestJoin)]
    public class ClientGroupRequestJoin : IReadable
    {
        public string Name { get; private set; }
        public string Realm { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Name  = reader.ReadWideString();
            Realm = reader.ReadWideString();
        }
    }

    /// <summary>
    /// 0x0453 GroupLib.AcceptRequest/DenyRequest, client writer: the pending request the client stored from 0x0424 (group id,
    /// requester name, a flag) with the leader's answer (1 bit, 1 = accept) after the group id.
    /// </summary>
    [Message(GameMessageOpcode.ClientGroupRequestJoinResponse)]
    public class ClientGroupRequestJoinResponse : IReadable
    {
        public ulong GroupId { get; private set; }
        public bool Accept { get; private set; }
        public string Name { get; private set; }
        public bool Flag { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId = reader.ReadULong();
            Accept  = reader.ReadBit();
            Name    = reader.ReadWideString();
            Flag    = reader.ReadBit();
        }
    }

    /// <summary>0x0424 to the group leader ("Group_JoinRequest" event), client reader: group id, the requester as group member info.</summary>
    [Message(GameMessageOpcode.ServerGroupRequestJoinResponse)]
    public class ServerGroupRequestJoinResponse : IWritable
    {
        public ulong GroupId { get; set; }
        public GroupMemberInfo Requester { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            Requester.Write(writer);
        }
    }

    /// <summary>0x0456 to the requester, client reader: group id, name, result (5, GroupLib.Result), flag (1).</summary>
    [Message(GameMessageOpcode.ServerGroupRequestJoinResult)]
    public class ServerGroupRequestJoinResult : IWritable
    {
        public ulong GroupId { get; set; }
        public string Name { get; set; }
        public GroupInviteResult Result { get; set; }
        public bool Flag { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.WriteStringWide(Name);
            writer.Write(Result, 5u);
            writer.Write(Flag);
        }
    }

    /// <summary>
    /// 0x03B4 FriendshipLib.AddByName, client writer: name, realm, friendship type (4 bits), note. Types from the client's enum
    /// registration: CharacterFriendshipType_Friend 0, _Ignore 1, _Rival 3, _Account 8.
    /// </summary>
    [Message(GameMessageOpcode.ClientFriendshipAddByName)]
    public class ClientFriendshipAddByName : IReadable
    {
        public const byte Friend = 0;
        public const byte Ignore = 1;
        public const byte Rival  = 3;

        public string Name { get; private set; }
        public string Realm { get; private set; }
        public byte Type { get; private set; }
        public string Note { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Name  = reader.ReadWideString();
            Realm = reader.ReadWideString();
            Type  = reader.ReadByte(4u);
            Note  = reader.ReadWideString();
        }
    }

    /// <summary>0x03C2 FriendshipLib.RemoveByName, client writer: name, realm, friendship type (4 bits).</summary>
    [Message(GameMessageOpcode.ClientFriendshipRemoveByName)]
    public class ClientFriendshipRemoveByName : IReadable
    {
        public string Name { get; private set; }
        public string Realm { get; private set; }
        public byte Type { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Name  = reader.ReadWideString();
            Realm = reader.ReadWideString();
            Type  = reader.ReadByte(4u);
        }
    }
}
