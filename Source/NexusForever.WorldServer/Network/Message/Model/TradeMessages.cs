using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Player to player trade. Every layout is read from the retail client (docs/PROTOCOL.md "Client binary static analysis"):
    // client writers (u32, u64, empty); client readers.

    public class EmptyClientMessage : IReadable
    {
        public void Read(GamePacketReader reader)
        {
        }
    }

    [Message(GameMessageOpcode.ClientP2PTradingInitiateTrade)]
    public class ClientP2PTradingInitiateTrade : IReadable
    {
        public uint TargetUnitId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            TargetUnitId = reader.ReadUInt();
        }
    }

    [Message(GameMessageOpcode.ClientP2PTradingAcceptInvite)]
    public class ClientP2PTradingAcceptInvite : EmptyClientMessage
    {
    }

    [Message(GameMessageOpcode.ClientP2PTradingDeclineInvite)]
    public class ClientP2PTradingDeclineInvite : EmptyClientMessage
    {
    }

    [Message(GameMessageOpcode.ClientP2PTradingCancelTrade)]
    public class ClientP2PTradingCancelTrade : EmptyClientMessage
    {
    }

    /// <summary>The client sends this for P2PTrading Commit and for P2PTrading.UnCommit: it toggles the sender's commit.</summary>
    [Message(GameMessageOpcode.ClientP2PTradingCommit)]
    public class ClientP2PTradingCommit : EmptyClientMessage
    {
    }

    [Message(GameMessageOpcode.ClientP2PTradingAddItem)]
    public class ClientP2PTradingAddItem : IReadable
    {
        public ulong ItemGuid { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ItemGuid = reader.ReadULong();
        }
    }

    [Message(GameMessageOpcode.ClientP2PTradingRemoveItem)]
    public class ClientP2PTradingRemoveItem : IReadable
    {
        public ulong ItemGuid { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ItemGuid = reader.ReadULong();
        }
    }

    [Message(GameMessageOpcode.ClientP2PTradingSetMoney)]
    public class ClientP2PTradingSetMoney : IReadable
    {
        public ulong Credits { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Credits = reader.ReadULong();
        }
    }

    [Message(GameMessageOpcode.ServerP2PTradeInvite)]
    public class ServerP2PTradeInvite : IWritable
    {
        public uint InviterUnitId { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(InviterUnitId);
        }
    }

    /// <summary>
    /// One offered item (client list entry of 0x50 bytes, Lua P2PTrading.GetTradeItems). The four fields after Quantity and the 20 trailing
    /// bytes build the client's item instance (circuit/glyph style data); their meaning is not decoded, they are sent as zero.
    /// </summary>
    [Message(GameMessageOpcode.ServerP2PTradeUpdateItem)]
    public class ServerP2PTradeUpdateItem : IWritable
    {
        public uint TradeIndex { get; set; }
        public uint OwnerUnitId { get; set; }
        public uint Item2Id { get; set; }
        public ulong ItemGuid { get; set; }
        public uint Quantity { get; set; }
        public ulong Unknown0 { get; set; }
        public uint Unknown1 { get; set; }
        public ulong Unknown2 { get; set; }
        public uint Unknown3 { get; set; }
        public byte[] Unknown4 { get; set; } = new byte[20];

        public void Write(GamePacketWriter writer)
        {
            writer.Write(TradeIndex);
            writer.Write(OwnerUnitId);
            writer.Write(Item2Id, 18u);
            writer.Write(ItemGuid);
            writer.Write(Quantity);
            writer.Write(Unknown0);
            writer.Write(Unknown1);
            writer.Write(Unknown2);
            writer.Write(Unknown3, 18u);
            for (int i = 0; i < 20; i++)
                writer.Write(i < Unknown4.Length ? Unknown4[i] : (byte)0);
        }
    }

    [Message(GameMessageOpcode.ServerP2PTradeItemRemoved)]
    public class ServerP2PTradeItemRemoved : IWritable
    {
        public ulong ItemGuid { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(ItemGuid);
        }
    }

    /// <summary>Unit id first: the client compares it with the partner and its own unit to pick whose offer changed.</summary>
    [Message(GameMessageOpcode.ServerP2PTradeUpdateMoney)]
    public class ServerP2PTradeUpdateMoney : IWritable
    {
        public uint UnitId { get; set; }
        public ulong Credits { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(UnitId);
            writer.Write(Credits);
        }
    }

    public enum P2PTradeResult
    {
        ErrorInitiating          = 0,
        PlayerLogOut             = 1,
        PlayerCanceled           = 2,
        PlayerAcceptedInvite     = 3,
        PlayerDeclinedInvite     = 4,
        MissingPlayer            = 5,
        InitiatorCommitted       = 6,
        InitiatorUnCommitted     = 7,
        TargetCommitted          = 8,
        TargetUnCommitted        = 9,
        NothingToTrade           = 10,
        FinishedSuccess          = 11,
        DbFailedToInit           = 12,
        DbFailed                 = 13,
        ErrorAddingItem          = 14,
        ErrorRemovingItem        = 15,
        TargetBusy               = 16,
        TargetNotAllowedToTrade  = 17,
        InviteFailedNoTrading    = 18
    }

    /// <summary>Client handler: 3 accepted, 6-9 commit flags, 11 finished, others show an error; Cancelled fires P2PCancelTrade.</summary>
    [Message(GameMessageOpcode.ServerP2PTradeResult)]
    public class ServerP2PTradeResult : IWritable
    {
        public P2PTradeResult Result { get; set; }
        public bool Cancelled { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Result, 5u);
            writer.Write(Cancelled);
        }
    }
}
