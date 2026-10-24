using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Tradeskill and crafting messages. Layouts are the retail client's serializers/readers (docs/PROTOCOL.md "Tradeskills"), names and
    // field meanings from NexusForever game_rework (research/protocol/game_rework) where the client code agrees with them.

    /// <summary>Shared by ServerProfessionsLoad and ServerProfessionUpdate: client reader reads five u32 and 40 raw bytes (ten u32 talent tiers).</summary>
    public class TradeskillInfo : IWritable
    {
        public uint TradeskillId { get; set; }
        public uint Xp { get; set; }
        public uint IsActive { get; set; }
        public uint ProficiencyFlags { get; set; }
        public uint TalentPoints { get; set; }
        public uint[] TalentTierIds { get; } = new uint[10];

        public void Write(GamePacketWriter writer)
        {
            writer.Write(TradeskillId);
            writer.Write(Xp);
            writer.Write(IsActive);
            writer.Write(ProficiencyFlags);
            writer.Write(TalentPoints);
            foreach (uint tier in TalentTierIds)
                writer.Write(tier);
        }
    }

    /// <summary>Client reader: u32 count + TradeskillInfo, u32 count + u32 schematics, u32 count + (u32, f32, f32) discoveries, u32 count + u32 flag words, u32 relearn cooldown ms.</summary>
    [Message(GameMessageOpcode.ServerProfessionsLoad)]
    public class ServerProfessionsLoad : IWritable
    {
        public List<TradeskillInfo> Tradeskills { get; } = new();
        public List<uint> LearnedSchematics { get; } = new();
        public List<bool> LearnedSchematicDiscovered { get; } = new();
        public uint RelearnCooldownMs { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Tradeskills.Count);
            foreach (TradeskillInfo info in Tradeskills)
                info.Write(writer);
            writer.Write(LearnedSchematics.Count);
            foreach (uint schematic in LearnedSchematics)
                writer.Write(schematic);
            writer.Write(0u); // discovery coordinates (schematic, x, y): none tracked
            var flags = new uint[(LearnedSchematicDiscovered.Count + 31) / 32];
            for (int i = 0; i < LearnedSchematicDiscovered.Count; i++)
                if (LearnedSchematicDiscovered[i])
                    flags[i / 32] |= 1u << (i % 32);
            writer.Write(flags.Length);
            foreach (uint word in flags)
                writer.Write(word);
            writer.Write(RelearnCooldownMs);
        }
    }

    /// <summary>Client reader (same as the entries of ServerProfessionsLoad).</summary>
    [Message(GameMessageOpcode.ServerProfessionUpdate)]
    public class ServerProfessionUpdate : IWritable
    {
        public TradeskillInfo Tradeskill { get; set; } = new();

        public void Write(GamePacketWriter writer) => Tradeskill.Write(writer);
    }

    /// <summary>Client reader: u32 skill, u32 schematic, f32 x, f32 y (discovery coordinates).</summary>
    [Message(GameMessageOpcode.ServerSchematicAddLearned)]
    public class ServerSchematicAddLearned : IWritable
    {
        public uint TradeskillId { get; set; }
        public uint SchematicId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(TradeskillId);
            writer.Write(SchematicId);
            writer.Write(X);
            writer.Write(Y);
        }
    }

    /// <summary>Client reader: pass (1 bit), schematic (15), item (18), u32 unused, u32 hot/cold, direction (4), u32 xp, u32 count + u32 returned materials.</summary>
    [Message(GameMessageOpcode.ServerCraftingFinish)]
    public class ServerCraftingFinish : IWritable
    {
        public bool Pass { get; set; }
        public uint SchematicId { get; set; }
        public uint ItemId { get; set; }
        public uint HotOrCold { get; set; }
        public uint Direction { get; set; }
        public uint EarnedXp { get; set; }
        public List<uint> MaterialsReturned { get; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Pass);
            writer.Write(SchematicId, 15u);
            writer.Write(ItemId, 18u);
            writer.Write(0u);
            writer.Write(HotOrCold);
            writer.Write(Direction, 4u);
            writer.Write(EarnedXp);
            writer.Write(MaterialsReturned.Count);
            foreach (uint item in MaterialsReturned)
                writer.Write(item);
        }
    }

    /// <summary>CraftingLib.LearnTradeskill(id, dropId), client writer: u32 skill, u32 skill to drop (0 = none, TradeskillTrainer.lua swap button).</summary>
    [Message(GameMessageOpcode.ClientTradeskillLearn)]
    public class ClientTradeskillLearn : IReadable
    {
        public uint TradeskillId { get; private set; }
        public uint DropTradeskillId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            TradeskillId     = reader.ReadUInt();
            DropTradeskillId = reader.ReadUInt();
        }
    }

    /// <summary>CraftingLib.PickTradeskillTalent, client writer: u32 skill, u32 talent tier row, u32 bonus.</summary>
    [Message(GameMessageOpcode.ClientTradeskillPickTalent)]
    public class ClientTradeskillPickTalent : IReadable
    {
        public uint TradeskillId { get; private set; }
        public uint TalentTierId { get; private set; }
        public uint BonusId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            TradeskillId = reader.ReadUInt();
            TalentTierId = reader.ReadUInt();
            BonusId      = reader.ReadUInt();
        }
    }

    /// <summary>CraftingLib.ResetTradeskillTalents, client writer: u32 skill.</summary>
    [Message(GameMessageOpcode.ClientTradeskillResetTalents)]
    public class ClientTradeskillResetTalents : IReadable
    {
        public uint TradeskillId { get; private set; }

        public void Read(GamePacketReader reader) => TradeskillId = reader.ReadUInt();
    }

    /// <summary>
    /// CraftingLib.CraftItem for a profession without the coordinate grid (the client sends 0x0852 when Tradeskill.Flags has no bit 8),
    /// client writer: u32 client spell cast id, u32 crafting station unit, u32 schematic, u32 count.
    /// </summary>
    [Message(GameMessageOpcode.ClientCraftingCraftItemSimple)]
    public class ClientCraftingCraftItemSimple : IReadable
    {
        public uint ClientSpellcastId { get; private set; }
        public uint StationUnitId { get; private set; }
        public uint SchematicId { get; private set; }
        public uint Count { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ClientSpellcastId = reader.ReadUInt();
            StationUnitId     = reader.ReadUInt();
            SchematicId       = reader.ReadUInt();
            Count             = reader.ReadUInt();
        }
    }

    /// <summary>
    /// CraftingLib.CraftItem(schematic, catalyst) for a coordinate profession (Tradeskill.Flags bit 8) starts a craft: client writer = the
    /// four u32 of 0x0852 then an 18 bit catalyst item.
    /// </summary>
    [Message(GameMessageOpcode.ClientCraftingCraftItem)]
    public class ClientCraftingCraftItem : IReadable
    {
        public uint ClientSpellcastId { get; private set; }
        public uint StationUnitId { get; private set; }
        public uint SchematicId { get; private set; }
        public uint Count { get; private set; }
        public uint CatalystItemId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ClientSpellcastId = reader.ReadUInt();
            StationUnitId     = reader.ReadUInt();
            SchematicId       = reader.ReadUInt();
            Count             = reader.ReadUInt();
            CatalystItemId    = reader.ReadUInt(18u);
        }
    }

    /// <summary>CraftingLib.AddAdditive (client writer): u32 station unit, 18 bit additive item, 18 bit catalyst item.</summary>
    [Message(GameMessageOpcode.ClientCraftingAdditive)]
    public class ClientCraftingAdditive : IReadable
    {
        public uint StationUnitId { get; private set; }
        public uint AdditiveItemId { get; private set; }
        public uint CatalystItemId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            StationUnitId  = reader.ReadUInt();
            AdditiveItemId = reader.ReadUInt(18u);
            CatalystItemId = reader.ReadUInt(18u);
        }
    }

    /// <summary>CraftingLib.CompleteCraft for a coordinate craft without power core data (sent with no extra struct), client writer: u32 spell cast, u32 station, u32 schematic.</summary>
    [Message(GameMessageOpcode.ClientCraftingSimpleCraft)]
    public class ClientCraftingSimpleCraft : IReadable
    {
        public uint ClientSpellcastId { get; private set; }
        public uint StationUnitId { get; private set; }
        public uint SchematicId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ClientSpellcastId = reader.ReadUInt();
            StationUnitId     = reader.ReadUInt();
            SchematicId       = reader.ReadUInt();
        }
    }

    /// <summary>
    /// CraftingLib.CompleteCraft with socket/power core data, client writer: the three u32 of 0x0850, u64 craft stats, 18 bit power core,
    /// u32 assault/support split, 3 bit count + count x u32 charges. Only the first three fields are used by the server.
    /// </summary>
    [Message(GameMessageOpcode.ClientCraftingComplexCraft)]
    public class ClientCraftingComplexCraft : IReadable
    {
        public uint ClientSpellcastId { get; private set; }
        public uint StationUnitId { get; private set; }
        public uint SchematicId { get; private set; }
        public ulong CraftStats { get; private set; }
        public uint PowerCoreItemId { get; private set; }
        public uint ApSpSplit { get; private set; }
        public uint[] Charges { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ClientSpellcastId = reader.ReadUInt();
            StationUnitId     = reader.ReadUInt();
            SchematicId       = reader.ReadUInt();
            CraftStats        = reader.ReadULong();
            PowerCoreItemId   = reader.ReadUInt(18u);
            ApSpSplit         = reader.ReadUInt();
            uint count        = reader.ReadUInt(3u);
            Charges = new uint[count];
            for (int i = 0; i < count; i++)
                Charges[i] = reader.ReadUInt();
        }
    }

    /// <summary>CraftingLib craft cancel, 1 byte struct, nothing read by the server.</summary>
    [Message(GameMessageOpcode.ClientCraftingAbandon)]
    public class ClientCraftingAbandon : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }
}
