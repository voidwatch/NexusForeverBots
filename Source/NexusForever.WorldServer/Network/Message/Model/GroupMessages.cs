using System.Collections.Generic;
using System.Numerics;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Layouts follow the WildStar client, taken from the NexusForever group implementation.

    [Message(GameMessageOpcode.ClientGroupInvite)]
    public class ClientGroupInvite : IReadable
    {
        public string Name { get; private set; }
        public string RealmName { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Name      = reader.ReadWideString();
            RealmName = reader.ReadWideString();
        }
    }

    [Message(GameMessageOpcode.ClientGroupInviteResponse)]
    public class ClientGroupInviteResponse : IReadable
    {
        public ulong GroupId { get; set; }
        public bool Response { get; set; }
        public uint Unk1 { get; set; }

        public void Read(GamePacketReader reader)
        {
            GroupId  = reader.ReadULong();
            Response = reader.ReadBit();
            Unk1     = reader.ReadUInt();
        }
    }

    [Message(GameMessageOpcode.ClientGroupLeave)]
    public class ClientGroupLeave : IReadable
    {
        public ulong GroupId { get; private set; }
        public bool ShouldDisband { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId       = reader.ReadULong();
            ShouldDisband = reader.ReadBit();
        }
    }

    [Message(GameMessageOpcode.ClientGroupKick)]
    public class ClientGroupKick : IReadable
    {
        public ulong GroupId { get; private set; }
        public Identity TargetedPlayer { get; } = new();

        public void Read(GamePacketReader reader)
        {
            GroupId = reader.ReadULong();
            TargetedPlayer.Read(reader);
        }
    }

    [Message(GameMessageOpcode.ClientGroupPromote)]
    public class ClientGroupPromote : IReadable
    {
        public ulong GroupId { get; private set; }
        public Identity TargetedPlayer { get; } = new();

        public void Read(GamePacketReader reader)
        {
            GroupId = reader.ReadULong();
            TargetedPlayer.Read(reader);
        }
    }

    // Field order matches the client UI call GroupLib.SetLootRules(eNormalRule, eThresholdRule, eThresholdQuality, eHarvestRule);
    // the 3/3/4/2 bit widths match the loot rule fields of ServerGroupJoin in the retail sniff parser (docs/PROTOCOL.md).
    [Message(GameMessageOpcode.ClientGroupLootRulesChange)]
    public class ClientGroupLootRulesChange : IReadable
    {
        public ulong GroupId { get; private set; }
        public LootRule NormalRule { get; private set; }
        public LootRule ThresholdRule { get; private set; }
        public LootThreshold ThresholdQuality { get; private set; }
        public HarvestLootRule HarvestRule { get; private set; }

        public void Read(GamePacketReader reader)
        {
            GroupId          = reader.ReadULong();
            NormalRule       = reader.ReadEnum<LootRule>(3u);
            ThresholdRule    = reader.ReadEnum<LootRule>(3u);
            ThresholdQuality = reader.ReadEnum<LootThreshold>(4u);
            HarvestRule      = reader.ReadEnum<HarvestLootRule>(2u);
        }
    }

    [Message(GameMessageOpcode.ServerGroupLootRulesChange)]
    public class ServerGroupLootRulesChange : IWritable
    {
        public ulong GroupId { get; set; }
        public uint Unknown0 { get; set; } // 0 in the NexusForever implementation, meaning unknown
        public LootRule NormalRule { get; set; }
        public LootRule ThresholdRule { get; set; }
        public LootThreshold ThresholdQuality { get; set; }
        public HarvestLootRule HarvestRule { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(Unknown0);
            writer.Write(NormalRule, 3u);
            writer.Write(ThresholdRule, 3u);
            writer.Write(ThresholdQuality, 4u);
            writer.Write(HarvestRule, 2u);
        }
    }

    [Message(GameMessageOpcode.ServerGroupInviteReceived)]
    public class ServerGroupInviteReceived : IWritable
    {
        public ulong GroupId { get; set; }
        public uint LeaderIndex { get; set; }
        public uint InviterIndex { get; set; }
        public List<GroupCharacter> Members { get; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(LeaderIndex);
            writer.Write(InviterIndex);
            writer.Write(Members.Count);
            foreach (GroupCharacter member in Members)
                member.Write(writer);
        }
    }

    [Message(GameMessageOpcode.ServerGroupInviteResult)]
    public class ServerGroupInviteResult : IWritable
    {
        public ulong GroupId { get; set; }
        public string Name { get; set; }
        public GroupInviteResult Result { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.WriteStringWide(Name);
            writer.Write(Result, 5u);
        }
    }

    [Message(GameMessageOpcode.ServerGroupJoin)]
    public class ServerGroupJoin : IWritable
    {
        public Identity TargetPlayer { get; set; } = new();
        public GroupInfo Group { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            TargetPlayer.Write(writer);
            Group.Write(writer);
        }
    }

    [Message(GameMessageOpcode.ServerGroupLeave)]
    public class ServerGroupLeave : IWritable
    {
        public ulong GroupId { get; set; }
        public RemoveReason Reason { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(Reason, 4u);
        }
    }

    [Message(GameMessageOpcode.ServerGroupMemberAdd)]
    public class ServerGroupMemberAdd : IWritable
    {
        public ulong GroupId { get; set; }
        public uint Unknown0 { get; set; }
        public GroupMemberInfo AddedMember { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(Unknown0);
            AddedMember.Write(writer);
        }
    }

    [Message(GameMessageOpcode.ServerGroupRemove)]
    public class ServerGroupRemove : IWritable
    {
        public ulong GroupId { get; set; }
        public uint Unk0 { get; set; }
        public Identity TargetPlayer { get; set; } = new();
        public RemoveReason Reason { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(Unk0);
            TargetPlayer.Write(writer);
            writer.Write(Reason, 4u);
        }
    }

    [Message(GameMessageOpcode.ServerGroupPromote)]
    public class ServerGroupPromote : IWritable
    {
        public ulong GroupId { get; set; }
        public uint LeaderIndex { get; set; }
        public Identity NewLeader { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(LeaderIndex);
            NewLeader.Write(writer);
        }
    }

    [Message(GameMessageOpcode.ServerGroupActionResult)]
    public class ServerGroupActionResult : IWritable
    {
        public ulong GroupId { get; set; }
        public Identity Identity { get; set; } = new();
        public GroupActionResult Result { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            Identity.Write(writer);
            writer.Write(Result, 32u);
        }
    }

    [Message(GameMessageOpcode.ServerGroupMemberFlagsChanged)]
    public class ServerGroupMemberFlagsChanged : IWritable
    {
        public ulong GroupId { get; set; }
        public uint MemberIndex { get; set; }
        public Identity TargetedPlayer { get; set; } = new();
        public GroupMemberInfoFlags ChangedFlags { get; set; }
        public bool IsFromPromotion { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(MemberIndex);
            TargetedPlayer.Write(writer);
            writer.Write(ChangedFlags, 32u);
            writer.Write(IsFromPromotion);
        }
    }

    [Message(GameMessageOpcode.ServerGroupMemberStatUpdate)]
    public class ServerGroupMemberStatUpdate : IWritable
    {
        public ulong GroupId { get; set; }
        public Identity TargetPlayer { get; set; } = new();
        public byte Level { get; set; }
        public byte EffectiveLevel { get; set; }
        public ushort GroupMemberId { get; set; }
        public float Health { get; set; }
        public float HealthMax { get; set; }
        public float Shield { get; set; }
        public float ShieldMax { get; set; }
        public float Mana { get; set; }
        public float ManaMax { get; set; }
        public Path Path { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            TargetPlayer.Write(writer);
            writer.Write(Level, 7u);
            writer.Write(EffectiveLevel, 7u);
            writer.Write(0u, 17u);
            writer.Write(GroupMemberId);

            for (int i = 0; i < 5; i++)
            {
                writer.Write((ushort)0);
                writer.Write((byte)48);
            }

            writer.WritePackedFloat(Health);
            writer.WritePackedFloat(HealthMax);
            writer.WritePackedFloat(Shield);
            writer.WritePackedFloat(ShieldMax);
            writer.WritePackedFloat(0f); // interrupt armor
            writer.WritePackedFloat(0f);
            writer.WritePackedFloat(0f); // absorption
            writer.WritePackedFloat(0f);
            writer.WritePackedFloat(Mana);
            writer.WritePackedFloat(ManaMax);
            writer.WritePackedFloat(0f); // healing absorb
            writer.WritePackedFloat(0f);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(Path, 3u);
        }
    }

    [Message(GameMessageOpcode.ServerGroupPositionUpdate)]
    public class ServerGroupPositionUpdate : IWritable
    {
        public class Update
        {
            public Identity Identity { get; set; } = new();
            public Vector3 Position { get; set; }
            public uint WorldZoneId { get; set; }
        }

        public ulong GroupId { get; set; }
        public uint WorldId { get; set; }
        public List<Update> Updates { get; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(WorldId, 15u);
            writer.Write((uint)Updates.Count);

            foreach (Update update in Updates)
                update.Identity.Write(writer);
            foreach (Update update in Updates)
                new Position(update.Position).Write(writer);
            foreach (Update update in Updates)
                writer.Write(update.WorldZoneId);
            foreach (Update update in Updates)
                writer.Write(0u); // combat flags
        }
    }

    [Message(GameMessageOpcode.ServerEntityGroupAssociation)]
    public class ServerEntityGroupAssociation : IWritable
    {
        public uint UnitId { get; set; }
        public ulong GroupId { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(UnitId);
            writer.Write(GroupId);
        }
    }
}
