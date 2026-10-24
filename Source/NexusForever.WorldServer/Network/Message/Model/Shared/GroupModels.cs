using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Game.Reputation.Static;

namespace NexusForever.WorldServer.Network.Message.Model.Shared
{
    /// <summary>
    /// Realm and character id pair the client uses to refer to a player in group messages.
    /// </summary>
    public class Identity : IReadable, IWritable
    {
        public ushort RealmId { get; set; }
        public ulong Id { get; set; }

        public void Read(GamePacketReader reader)
        {
            RealmId = reader.ReadUShort(14u);
            Id      = reader.ReadULong();
        }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(RealmId, 14u);
            writer.Write(Id);
        }
    }

    public class GroupCharacter : IWritable
    {
        public string Name { get; set; }
        public Faction Faction { get; set; }
        public Race Race { get; set; }
        public Class Class { get; set; }
        public Sex Sex { get; set; }
        public byte Level { get; set; }
        public byte EffectiveLevel { get; set; }
        public Path Path { get; set; }
        public ushort GroupMemberId { get; set; }
        public ushort Realm { get; set; }
        public ushort WorldZoneId { get; set; }
        public uint MapId { get; set; }
        public uint PhaseId { get; set; } = 1u;
        public bool SyncedToGroup { get; set; } = true;

        public void Write(GamePacketWriter writer)
        {
            writer.WriteStringWide(Name);
            writer.Write(Faction, 14u);
            writer.Write(Race, 14u);
            writer.Write(Class, 14u);
            writer.Write(Sex, 2u);
            writer.Write(Level, 7u);
            writer.Write(EffectiveLevel, 7u);
            writer.Write(Path, 3u);
            writer.Write(0u, 17u);
            writer.Write(GroupMemberId);

            for (int i = 0; i < 5; i++)
            {
                writer.Write((ushort)0);
                writer.Write((byte)48);
            }

            // no mentoring target
            writer.Write((ushort)0, 14u);
            writer.Write(0ul);

            writer.Write(0u);
            for (int i = 0; i < 12; i++)
                writer.Write((ushort)0);

            writer.Write(Realm, 14u);
            writer.Write(WorldZoneId, 15u);
            writer.Write(MapId);
            writer.Write(PhaseId);
            writer.Write(SyncedToGroup);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u); // count of unknown struct list
        }
    }

    public class GroupMemberInfo : IWritable
    {
        public Identity MemberIdentity { get; set; } = new();
        public GroupMemberInfoFlags Flags { get; set; }
        public GroupCharacter Member { get; set; }
        public uint GroupIndex { get; set; }

        public void Write(GamePacketWriter writer)
        {
            MemberIdentity.Write(writer);
            writer.Write(Flags, 32u);
            Member.Write(writer);
            writer.Write(GroupIndex);
        }
    }

    public class GroupInfo : IWritable
    {
        public ulong GroupId { get; set; }
        public GroupFlags Flags { get; set; }
        public List<GroupMemberInfo> MemberInfos { get; set; } = new();
        public uint MaxGroupSize { get; set; }
        public LootRule LootRule { get; set; } = LootRule.NeedBeforeGreed;
        public LootRule LootRuleThreshold { get; set; } = LootRule.NeedBeforeGreed;
        public LootThreshold LootThreshold { get; set; } = LootThreshold.Good;
        public HarvestLootRule LootRuleHarvest { get; set; } = HarvestLootRule.FirstTagger;
        public Identity LeaderIdentity { get; set; } = new();
        public ushort RealmId { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(GroupId);
            writer.Write(Flags, 32u);
            writer.Write(MemberInfos.Count);
            writer.Write(MaxGroupSize);
            writer.Write(LootRule, 3u);
            writer.Write(LootRuleThreshold, 3u);
            writer.Write(LootThreshold, 4u);
            writer.Write(LootRuleHarvest, 2u);

            foreach (GroupMemberInfo member in MemberInfos)
                member.Write(writer);

            LeaderIdentity.Write(writer);
            writer.Write(RealmId, 14u);

            // no raid markers
            writer.Write(0);
        }
    }
}
