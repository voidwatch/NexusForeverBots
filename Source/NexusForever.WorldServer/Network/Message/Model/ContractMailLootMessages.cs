using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Network.Message.Model.Shared;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Periodic quests / Contract Board, mail take-all and the group loot window. Layouts read from the retail client
    // (docs/PROTOCOL.md "Periodic quests and contracts", "Mail take-all", "Loot window"; addresses are the client serializers).

    /// <summary>Client reader, client handler: a new seed makes the client redraw its rotating dailies and contracts; Flags only feed the Lua event.</summary>
    [Message(GameMessageOpcode.ServerQuestPeriodicReset)]
    public class ServerQuestPeriodicReset : IWritable
    {
        public ulong Seed { get; set; }
        public byte Flags { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Seed);
            writer.Write(Flags, 4u);
        }
    }

    /// <summary>
    /// Client reader reads 12 raw bytes, client handler stores them as u32 per contract type (0 unused, 1 PvE, 2 PvP):
    /// the Good quality contract the board shows for that type (ContractsLib.GetPeriodicContracts).
    /// </summary>
    [Message(GameMessageOpcode.ServerQuestContractGoodQualityChanged)]
    public class ServerQuestContractGoodQualityChanged : IWritable
    {
        public uint[] GoodQuest { get; } = new uint[3];

        public void Write(GamePacketWriter writer)
        {
            foreach (uint quest in GoodQuest)
                writer.Write(quest);
        }
    }

    /// <summary>
    /// MailSystemLib.TakeAllAttachmentsFromMultipleMessages, sent by the client, client writer: u32 count, count x u64 mail id,
    /// u32 mailbox unit id (the same mail state field ClientMailTakeAttachment sends as its unit id).
    /// </summary>
    [Message(GameMessageOpcode.ClientMailTakeAllFromSelection)]
    public class ClientMailTakeAllFromSelection : IReadable
    {
        public List<ulong> MailIds { get; } = new();
        public uint UnitId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            uint count = reader.ReadUInt();
            for (uint i = 0u; i < count && i < 1000u; i++)
                MailIds.Add(reader.ReadULong());
            UnitId = reader.ReadUInt();
        }
    }

    /// <summary>Client reader, client handler: the loot of an owner unit (the corpse), each item with its own loot id.</summary>
    [Message(GameMessageOpcode.ServerLootNotify)]
    public class ServerLootNotify : IWritable
    {
        public uint OwnerUnitId { get; set; }
        public uint ParentUnitId { get; set; }
        public bool Explosion { get; set; }
        public List<LootItemModel> Items { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(OwnerUnitId);
            writer.Write(ParentUnitId);
            writer.Write(Explosion);
            writer.Write((uint)Items.Count);
            Items.ForEach(i => i.Write(writer));
        }
    }

    /// <summary>Client reader: one loot item handed to a looter.</summary>
    [Message(GameMessageOpcode.ServerLootGrant)]
    public class ServerLootGrant : IWritable
    {
        public uint OwnerUnitId { get; set; }
        public uint LooterUnitId { get; set; }
        public LootItemModel Item { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(OwnerUnitId);
            writer.Write(LooterUnitId);
            Item.Write(writer);
        }
    }

    /// <summary>Client reader: the winner of a roll with every roll made.</summary>
    [Message(GameMessageOpcode.ServerLootWinner)]
    public class ServerLootWinner : IWritable
    {
        public class Roll : IWritable
        {
            public Identity Roller { get; set; } = new();
            public uint Value { get; set; }

            public void Write(GamePacketWriter writer)
            {
                Roller.Write(writer);
                writer.Write(Value);
            }
        }

        public uint LootUnitId { get; set; }
        public Roll Winner { get; set; } = new();
        public uint ItemId { get; set; }
        public List<Roll> Rolls { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(LootUnitId);
            Winner.Write(writer);
            writer.Write(ItemId, 18u);
            writer.Write((uint)Rolls.Count);
            Rolls.ForEach(r => r.Write(writer));
        }
    }
}
