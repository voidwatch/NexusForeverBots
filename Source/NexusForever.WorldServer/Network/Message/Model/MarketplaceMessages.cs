using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Static;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Auction house and commodity exchange. Layouts read from the retail client (docs/PROTOCOL.md "Client binary static analysis"):
    // ItemAuction client reader, CommodityOrder client writer / client reader, filter client writer.

    public class ItemAuction : IWritable
    {
        public ulong AuctionId { get; set; }
        public ulong OwnerCharacterId { get; set; }
        public ulong MinimumBid { get; set; }
        public ulong BuyoutPrice { get; set; }
        public ulong CurrentBid { get; set; }
        public ulong TopBidderCharacterId { get; set; }
        public ulong ExpirationTime { get; set; }
        public uint Item2Id { get; set; }
        public uint Quantity { get; set; }
        public uint WorldRequirementItem2Id { get; set; }
        public List<uint> Unknown0 { get; set; } = new(); // count is 3 bits, meaning not decoded
        public ulong CircuitData { get; set; }
        public uint GlyphData { get; set; }
        public ulong ThresholdData { get; set; }
        public uint Unknown1 { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(AuctionId);
            writer.Write(OwnerCharacterId);
            writer.Write(MinimumBid);
            writer.Write(BuyoutPrice);
            writer.Write(CurrentBid);
            writer.Write(TopBidderCharacterId);
            writer.Write(ExpirationTime);
            writer.Write(Item2Id, 18u);
            writer.Write(Quantity);
            writer.Write(WorldRequirementItem2Id, 18u);
            writer.Write((uint)Unknown0.Count, 3u);
            Unknown0.ForEach(u => writer.Write(u));
            writer.Write(CircuitData);
            writer.Write(GlyphData);
            writer.Write(ThresholdData);
            writer.Write(Unknown1);
        }
    }

    public class CommodityOrderModel : IReadable, IWritable
    {
        public ulong CommodityOrderId { get; set; }
        public uint Item2Id { get; set; }
        public uint Quantity { get; set; }
        public ulong PricePerUnit { get; set; }
        public ulong Price { get; set; }
        public bool IsBuyOrder { get; set; }
        public bool ForceImmediate { get; set; }
        public ulong ListTime { get; set; }
        public ulong ExpirationTime { get; set; }

        public void Read(GamePacketReader reader)
        {
            CommodityOrderId = reader.ReadULong();
            Item2Id          = reader.ReadUInt(18u);
            Quantity         = reader.ReadUInt();
            PricePerUnit     = reader.ReadULong();
            Price            = reader.ReadULong();
            IsBuyOrder       = reader.ReadBit();
            ForceImmediate   = reader.ReadBit();
            ListTime         = reader.ReadULong();
            ExpirationTime   = reader.ReadULong();
        }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(CommodityOrderId);
            writer.Write(Item2Id, 18u);
            writer.Write(Quantity);
            writer.Write(PricePerUnit);
            writer.Write(Price);
            writer.Write(IsBuyOrder);
            writer.Write(ForceImmediate);
            writer.Write(ListTime);
            writer.Write(ExpirationTime);
        }
    }

    [Message(GameMessageOpcode.ClientAuctionSellOrderSubmit)]
    public class ClientAuctionSellOrderSubmit : IReadable
    {
        public ulong ItemGuid { get; private set; }
        public ulong MinimumBid { get; private set; }
        public ulong BuyoutPrice { get; private set; }

        public void Read(GamePacketReader reader)
        {
            ItemGuid    = reader.ReadULong();
            MinimumBid  = reader.ReadULong();
            BuyoutPrice = reader.ReadULong();
        }
    }

    [Message(GameMessageOpcode.ClientAuctionBuyOrderSubmit)]
    public class ClientAuctionBuyOrderSubmit : IReadable
    {
        public uint Item2Id { get; private set; }
        public ulong AuctionId { get; private set; }
        public ulong Amount { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Item2Id   = reader.ReadUInt(18u);
            AuctionId = reader.ReadULong();
            Amount    = reader.ReadULong();
        }
    }

    /// <summary>Item id first, then the auction id (client writer; the upstream model reads them the other way round).</summary>
    [Message(GameMessageOpcode.ClientAuctionCancel)]
    public class ClientAuctionCancel : IReadable
    {
        public uint Item2Id { get; private set; }
        public ulong AuctionId { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Item2Id   = reader.ReadUInt(18u);
            AuctionId = reader.ReadULong();
        }
    }

    /// <summary>Type 0/1 property min/max (property, float), 2 level range, 3 quality range, 4 rune slot, 5 buyout maximum, 6 equippable by.</summary>
    public class AuctionFilter
    {
        public uint Type { get; set; }
        public List<ulong> Values { get; } = new();
        public float ValueFloat { get; set; }
    }

    [Message(GameMessageOpcode.ClientAuctionsByFilterRequest)]
    public class ClientAuctionsByFilterRequest : IReadable
    {
        public uint Item2FamilyId { get; private set; }
        public uint Item2CategoryId { get; private set; }
        public uint Item2TypeId { get; private set; }
        public List<uint> Item2Ids { get; } = new();
        public List<AuctionFilter> Filters { get; } = new();
        public uint ReverseSort { get; private set; }
        public uint AuctionSort { get; private set; }
        public uint SortPropertyId { get; private set; }
        public uint Page { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Item2FamilyId   = reader.ReadUInt(14u);
            Item2CategoryId = reader.ReadUInt(14u);
            Item2TypeId     = reader.ReadUInt(14u);
            uint count = reader.ReadUInt();
            for (uint i = 0u; i < count && i < 1024u; i++)
                Item2Ids.Add(reader.ReadUInt());
            count = reader.ReadUInt();
            for (uint i = 0u; i < count && i < 64u; i++)
            {
                var filter = new AuctionFilter { Type = reader.ReadUInt(3u) };
                switch (filter.Type)
                {
                    case 0u: // property minimum
                    case 1u: // property maximum
                        filter.Values.Add(reader.ReadUInt(8u));
                        filter.ValueFloat = reader.ReadSingle();
                        break;
                    case 2u: // level range
                        filter.Values.Add(reader.ReadUInt());
                        filter.Values.Add(reader.ReadUInt());
                        break;
                    case 3u: // quality range
                        filter.Values.Add(reader.ReadUInt(4u));
                        filter.Values.Add(reader.ReadUInt(4u));
                        break;
                    case 4u: // rune slot: microchip type 5 bits, minimum 32 bits in the client writer
                        filter.Values.Add(reader.ReadUInt(5u));
                        filter.Values.Add(reader.ReadUInt());
                        break;
                    case 5u: // buyout maximum
                        filter.Values.Add(reader.ReadULong());
                        break;
                    case 6u: // equippable by race, class, faction (14 bits each), level
                        filter.Values.Add(reader.ReadUInt(14u));
                        filter.Values.Add(reader.ReadUInt(14u));
                        filter.Values.Add(reader.ReadUInt(14u));
                        filter.Values.Add(reader.ReadUInt());
                        break;
                    default: // the client table has 7 entries (0-6), it never writes another type
                        Filters.Add(filter);
                        return;
                }
                Filters.Add(filter);
            }
            ReverseSort    = reader.ReadUInt();
            AuctionSort    = reader.ReadUInt(3u);
            SortPropertyId = reader.ReadUInt(8u);
            Page           = reader.ReadUInt();
        }
    }

    [Message(GameMessageOpcode.ClientRequestOwnedItemAuctions)]
    public class ClientRequestOwnedItemAuctions : EmptyClientMessage
    {
    }

    [Message(GameMessageOpcode.ClientRequestOwnedCommodityOrders)]
    public class ClientRequestOwnedCommodityOrders : EmptyClientMessage
    {
    }

    [Message(GameMessageOpcode.ClientRequestCommodityInfo)]
    public class ClientRequestCommodityInfo : IReadable
    {
        public uint Item2Id { get; private set; }

        public void Read(GamePacketReader reader)
        {
            Item2Id = reader.ReadUInt(18u);
        }
    }

    /// <summary>Buy and sell orders (IsBuyOrder in the order).</summary>
    [Message(GameMessageOpcode.ClientCommodityOrderSubmit)]
    public class ClientCommodityOrderSubmit : IReadable
    {
        public CommodityOrderModel Order { get; } = new();

        public void Read(GamePacketReader reader)
        {
            Order.Read(reader);
        }
    }

    [Message(GameMessageOpcode.ClientCommodityOrderCancel)]
    public class ClientCommodityOrderCancel : IReadable
    {
        public ulong CommodityOrderId { get; private set; }
        public uint Item2Id { get; private set; }
        public bool IsBuyOrder { get; private set; }

        public void Read(GamePacketReader reader)
        {
            CommodityOrderId = reader.ReadULong();
            Item2Id          = reader.ReadUInt(18u);
            IsBuyOrder       = reader.ReadBit();
        }
    }

    [Message(GameMessageOpcode.ServerAuctionPostResult)]
    public class ServerAuctionPostResult : IWritable
    {
        public GenericError Result { get; set; }
        public ItemAuction Auction { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Result, 8u);
            Auction.Write(writer);
        }
    }

    [Message(GameMessageOpcode.ServerAuctionBidResult)]
    public class ServerAuctionBidResult : IWritable
    {
        public GenericError Result { get; set; }
        public ItemAuction Auction { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Result, 8u);
            Auction.Write(writer);
        }
    }

    /// <summary>Auction first, then the result (client reader).</summary>
    [Message(GameMessageOpcode.ServerAuctionCancelResult)]
    public class ServerAuctionCancelResult : IWritable
    {
        public ItemAuction Auction { get; set; } = new();
        public GenericError Result { get; set; }

        public void Write(GamePacketWriter writer)
        {
            Auction.Write(writer);
            writer.Write(Result, 8u);
        }
    }

    [Message(GameMessageOpcode.ServerAuctionSearchResults)]
    public class ServerAuctionSearchResults : IWritable
    {
        public uint TotalCount { get; set; }
        public uint Page { get; set; }
        public List<ItemAuction> Auctions { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(TotalCount);
            writer.Write(Page);
            writer.Write((uint)Auctions.Count);
            Auctions.ForEach(a => a.Write(writer));
        }
    }

    [Message(GameMessageOpcode.ServerOwnedItemAuctions)]
    public class ServerOwnedItemAuctions : IWritable
    {
        public List<ItemAuction> Auctions { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write((uint)Auctions.Count);
            Auctions.ForEach(a => a.Write(writer));
        }
    }

    [Message(GameMessageOpcode.ServerOwnedCommodityOrders)]
    public class ServerOwnedCommodityOrders : IWritable
    {
        public List<CommodityOrderModel> Orders { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write((uint)Orders.Count);
            Orders.ForEach(o => o.Write(writer));
        }
    }

    [Message(GameMessageOpcode.ServerCommodityOrderResult)]
    public class ServerCommodityOrderResult : IWritable
    {
        public GenericError Result { get; set; }
        public CommodityOrderModel Order { get; set; } = new();
        public ulong CostToPost { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Result, 8u);
            Order.Write(writer);
            writer.Write(CostToPost);
        }
    }

    /// <summary>Three price buckets per side (1, 10 and 50 units in the upstream comment; the client reads 3 x u64 raw).</summary>
    [Message(GameMessageOpcode.ServerCommodityInfoResults)]
    public class ServerCommodityInfoResults : IWritable
    {
        public uint Item2Id { get; set; }
        public uint BuyOrderCount { get; set; }
        public ulong[] BuyOrderPrices { get; set; } = new ulong[3];
        public uint SellOrderCount { get; set; }
        public ulong[] SellOrderPrices { get; set; } = new ulong[3];
        public List<CommodityOrderModel> Orders { get; set; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write(Item2Id, 18u);
            writer.Write(BuyOrderCount);
            for (int i = 0; i < 3; i++)
                writer.Write(BuyOrderPrices[i]);
            writer.Write(SellOrderCount);
            for (int i = 0; i < 3; i++)
                writer.Write(SellOrderPrices[i]);
            writer.Write((uint)Orders.Count);
            Orders.ForEach(o => o.Write(writer));
        }
    }
}
