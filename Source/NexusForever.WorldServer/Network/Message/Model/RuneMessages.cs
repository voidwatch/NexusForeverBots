using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Rune slot messages, layouts recovered from the client writers (docs/PROTOCOL.md "Lua-reachable client messages", docs/ASSUMPTIONS.md "Runes").

    /// <summary>Client writer: u64 item guid, 1 bit, rune type (5 bits, Item.CodeEnumRuneType Air 7 ... Fusion 13).</summary>
    [Message(GameMessageOpcode.ClientCraftingRuneSlotAdd)]
    public class ClientCraftingRuneSlotAdd : IReadable
    {
        public ulong ItemGuid { get; private set; }
        public bool Flag { get; private set; }
        public byte RuneType { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ItemGuid = reader.ReadULong();
            Flag     = reader.ReadBit();
            RuneType = reader.ReadByte(5u);
        }
    }

    /// <summary>Client writer: u64 item guid, u32 slot, two flag bits.</summary>
    [Message(GameMessageOpcode.ClientCraftingRuneSlotClear)]
    public class ClientCraftingRuneSlotClear : IReadable
    {
        public ulong ItemGuid { get; private set; }
        public uint Slot { get; private set; }
        public bool Flag0 { get; private set; }
        public bool Flag1 { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ItemGuid = reader.ReadULong();
            Slot     = reader.ReadUInt();
            Flag0    = reader.ReadBit();
            Flag1    = reader.ReadBit();
        }
    }

    /// <summary>CraftingLib.InstallRuneIntoSlot, client writer: u64 item guid, u32 count, count rune Item2 ids, one per slot (0 = unchanged/empty).</summary>
    [Message(GameMessageOpcode.ClientCraftingRuneInstall)]
    public class ClientCraftingRuneInstall : IReadable
    {
        public ulong ItemGuid { get; private set; }
        public List<uint> Runes { get; } = new();

        public void Read(GamePacketReader reader)
        {
            ItemGuid = reader.ReadULong();
            uint count = reader.ReadUInt();
            for (uint i = 0u; i < count && i < 8u; i++)
                Runes.Add(reader.ReadUInt());
        }
    }

    /// <summary>Client writer: u64 item guid, u32 slot, rune type (5 bits).</summary>
    [Message(GameMessageOpcode.ClientCraftingRuneSlotReroll)]
    public class ClientCraftingRuneSlotReroll : IReadable
    {
        public ulong ItemGuid { get; private set; }
        public uint Slot { get; private set; }
        public byte RuneType { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ItemGuid = reader.ReadULong();
            Slot     = reader.ReadUInt();
            RuneType = reader.ReadByte(5u);
        }
    }
}
