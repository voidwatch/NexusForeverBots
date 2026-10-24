using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Instance settings, group loot rolls, resource conversion and daily login. Layouts read from the retail client
    // (docs/PROTOCOL.md "Client binary static analysis"; addresses are the client serializers).

    /// <summary>GameLib.SetInstanceSettings(eDifficulty, nRally, nPrimeLevel), client writer.</summary>
    [Message(GameMessageOpcode.ClientSetInstanceSettings)]
    public class ClientSetInstanceSettings : IReadable
    {
        public uint InstancePortalUnitId { get; private set; }
        public byte Difficulty { get; private set; } // 0 normal, 1 veteran
        public byte PrimeLevel { get; private set; }
        public ushort Rally { get; private set; }

        public void Read(GamePacketReader reader)
        {
            InstancePortalUnitId = reader.ReadUInt();
            Difficulty           = reader.ReadByte(2u);
            PrimeLevel           = reader.ReadByte();
            Rally                = reader.ReadUShort(11u);
        }
    }

    [Message(GameMessageOpcode.ClientClosedInstanceSettings)]
    public class ClientClosedInstanceSettings : IReadable
    {
        public uint InstancePortalUnitId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            InstancePortalUnitId = reader.ReadUInt();
        }
    }

    [Message(GameMessageOpcode.ClientResetSingleInstance)]
    public class ClientResetSingleInstance : IReadable
    {
        public uint InstancePortalUnitId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            InstancePortalUnitId = reader.ReadUInt();
        }
    }

    [Message(GameMessageOpcode.ClientResetInstances)]
    public class ClientResetInstances : EmptyClientMessage
    {
    }

    /// <summary>Client reader.</summary>
    [Message(GameMessageOpcode.ServerShowInstanceGameModeDialog)]
    public class ServerShowInstanceGameModeDialog : IWritable
    {
        public uint InstancePortalUnitId { get; set; }
        public byte Difficulty { get; set; }
        public ushort Parameters { get; set; }
        public byte ExistingDifficulty { get; set; }
        public byte MaxPrimeLevelWorld { get; set; }
        public byte MaxPrimeLevelGroup { get; set; }
        public byte ScalingLevel { get; set; }
        public bool ExistingScaling { get; set; }
        public byte ExistingPrimeLevel { get; set; }
        public bool IsRaid { get; set; }
        public bool PrimeAllowed { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(InstancePortalUnitId);
            writer.Write(Difficulty, 2u);
            writer.Write(Parameters, 11u);
            writer.Write(ExistingDifficulty, 2u);
            writer.Write(MaxPrimeLevelWorld);
            writer.Write(MaxPrimeLevelGroup);
            writer.Write(ScalingLevel);
            writer.Write(ExistingScaling);
            writer.Write(ExistingPrimeLevel);
            writer.Write(IsRaid);
            writer.Write(PrimeAllowed);
        }
    }

    /// <summary>The client reads a u32 (portal unit id) here; the upstream model is empty.</summary>
    [Message(GameMessageOpcode.ServerHideInstanceGameModeDialog)]
    public class ServerHideInstanceGameModeDialog : IWritable
    {
        public uint InstancePortalUnitId { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(InstancePortalUnitId);
        }
    }

    /// <summary>GameLib.RollOnLoot / PassOnLoot: the client looks up the owner unit from its loot id. Action 0 need, 1 greed, 2 pass.</summary>
    [Message(GameMessageOpcode.ClientLootRollAction)]
    public class ClientLootRollAction : IReadable
    {
        public uint OwnerUnitId { get; private set; }
        public uint LootUnitId { get; private set; }
        public byte Action { get; private set; }

        public void Read(GamePacketReader reader)
        {
            OwnerUnitId = reader.ReadUInt();
            LootUnitId  = reader.ReadUInt();
            Action      = reader.ReadByte(2u);
        }
    }

    /// <summary>GameLib.AssignMasterLoot(nLootId, unitLooter).</summary>
    [Message(GameMessageOpcode.ClientLootAssignMaster)]
    public class ClientLootAssignMaster : IReadable
    {
        public uint OwnerUnitId { get; private set; }
        public uint LootUnitId { get; private set; }
        public Identity Assignee { get; } = new();

        public void Read(GamePacketReader reader)
        {
            OwnerUnitId = reader.ReadUInt();
            LootUnitId  = reader.ReadUInt();
            Assignee.Read(reader);
        }
    }

    public class LootItemModel : IWritable
    {
        public uint LootUnitId { get; set; }
        public uint Type { get; set; }
        public uint ItemId { get; set; }
        public uint Amount { get; set; }
        public bool CanLoot { get; set; }
        public bool RequiresRoll { get; set; }
        public bool OnlyMasterLootable { get; set; }
        public bool Explosion { get; set; }
        public uint RollTime { get; set; }
        public ulong RandomCircuitData { get; set; }
        public uint RandomGlyphData { get; set; }
        public uint ItemQuality2Id { get; set; }
        public List<Identity> MasterList { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(LootUnitId);
            writer.Write(Type);
            writer.Write(ItemId);
            writer.Write(Amount);
            writer.Write(CanLoot);
            writer.Write(RequiresRoll);
            writer.Write(OnlyMasterLootable);
            writer.Write(Explosion);
            writer.Write(RollTime);
            writer.Write(RandomCircuitData);
            writer.Write(RandomGlyphData);
            writer.Write(ItemQuality2Id);
            writer.Write((uint)MasterList.Count);
            MasterList.ForEach(m => m.Write(writer));
        }
    }

    [Message(GameMessageOpcode.ServerLootRoll)]
    public class ServerLootRoll : IWritable
    {
        public uint LootUnitId { get; set; }
        public Identity Roller { get; set; } = new();
        public uint ItemId { get; set; }
        public uint Action { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(LootUnitId);
            Roller.Write(writer);
            writer.Write(ItemId, 18u);
            writer.Write(Action);
        }
    }

    /// <summary>
    /// ResourceConversion row (14 bits), number of source units (the UI multiplies the batches by the row's source count) and a u64 that the vendor
    /// UI sends as 0 (client writer, built in client code). The creature offering the conversion is checked by the client, not sent.
    /// </summary>
    [Message(GameMessageOpcode.ClientResourceConversion)]
    public class ClientResourceConversion : IReadable
    {
        public uint ConversionId { get; private set; }
        public uint Count { get; private set; }
        public ulong Unknown { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ConversionId = reader.ReadUInt(14u);
            Count        = reader.ReadUInt();
            Unknown      = reader.ReadULong();
        }
    }

    /// <summary>AccountItemLib.RequestDailyLoginRewards, empty.</summary>
    [Message(GameMessageOpcode.ClientDailyLoginRewardsRequest)]
    public class ClientDailyLoginRewardsRequest : EmptyClientMessage
    {
    }
}
