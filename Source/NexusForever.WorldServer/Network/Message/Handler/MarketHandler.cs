using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Market;
using NexusForever.WorldServer.Game.Static;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    /// <summary>
    /// Auction house and commodity exchange messages (layouts from the retail client, docs/PROTOCOL.md) on top of <see cref="MarketManager"/>.
    /// </summary>
    public static class MarketHandler
    {
        /// <summary>Auctions per search page (the message carries the page, not its size; guess).</summary>
        public const int PageSize = 10;

        public static ItemAuction BuildAuction(AuctionListing listing)
        {
            if (listing == null)
                return new ItemAuction();

            return new ItemAuction
            {
                AuctionId            = listing.Id,
                OwnerCharacterId     = listing.Seller,
                MinimumBid           = listing.MinBid,
                BuyoutPrice          = listing.Buyout,
                CurrentBid           = listing.HighBid,
                TopBidderCharacterId = listing.HighBidder,
                ExpirationTime       = (ulong)Math.Max(0d, (listing.Expires - DateTime.UtcNow).TotalSeconds),
                Item2Id              = listing.ItemId,
                Quantity             = listing.Count
            };
        }

        public static CommodityOrderModel BuildOrder(CommodityOrder order)
        {
            if (order == null)
                return new CommodityOrderModel();

            return new CommodityOrderModel
            {
                CommodityOrderId = order.Id,
                Item2Id          = order.ItemId,
                Quantity         = order.Count,
                PricePerUnit     = order.UnitPrice,
                Price            = order.UnitPrice * order.Count,
                IsBuyOrder       = !order.Sell
            };
        }

        [MessageHandler(GameMessageOpcode.ClientAuctionSellOrderSubmit)]
        public static void HandleAuctionSellOrderSubmit(WorldSession session, ClientAuctionSellOrderSubmit message)
        {
            Player player = session.Player;
            Item item = player.Inventory.GetItem(message.ItemGuid);
            AuctionListing listing = item?.Info == null ? null
                : MarketManager.Instance.CreateAuction(player, item.Info.Id, item.StackCount, message.MinimumBid, message.BuyoutPrice, MarketManager.AuctionDuration);
            session.EnqueueMessageEncrypted(new ServerAuctionPostResult
            {
                Result  = item?.Info == null ? GenericError.ItemUnknownItem : listing == null ? GenericError.Params : GenericError.Ok,
                Auction = BuildAuction(listing)
            });
        }

        [MessageHandler(GameMessageOpcode.ClientAuctionBuyOrderSubmit)]
        public static void HandleAuctionBuyOrderSubmit(WorldSession session, ClientAuctionBuyOrderSubmit message)
        {
            AuctionListing listing = MarketManager.Instance.Listings.FirstOrDefault(l => l.Id == message.AuctionId);
            bool ok = listing != null && listing.ItemId == message.Item2Id && (listing.Buyout != 0ul && message.Amount >= listing.Buyout
                ? MarketManager.Instance.Buyout(session.Player, listing.Id)
                : MarketManager.Instance.Bid(session.Player, listing.Id, message.Amount));
            session.EnqueueMessageEncrypted(new ServerAuctionBidResult
            {
                Result  = listing == null || listing.ItemId != message.Item2Id ? GenericError.ItemUnknownItem : ok ? GenericError.Ok : GenericError.Params,
                Auction = BuildAuction(MarketManager.Instance.Listings.FirstOrDefault(l => l.Id == message.AuctionId) ?? listing)
            });
        }

        [MessageHandler(GameMessageOpcode.ClientAuctionCancel)]
        public static void HandleAuctionCancel(WorldSession session, ClientAuctionCancel message)
        {
            AuctionListing listing = MarketManager.Instance.Listings.FirstOrDefault(l => l.Id == message.AuctionId);
            bool ok = listing != null && MarketManager.Instance.CancelAuction(session.Player, listing.Id);
            session.EnqueueMessageEncrypted(new ServerAuctionCancelResult
            {
                Auction = BuildAuction(listing),
                Result  = ok ? GenericError.Ok : GenericError.Params
            });
        }

        private static bool Matches(AuctionListing listing, ClientAuctionsByFilterRequest request)
        {
            Item2Entry entry = GameTableManager.Instance.Item.GetEntry(listing.ItemId);
            if (entry == null)
                return false;
            if (request.Item2Ids.Count > 0 && !request.Item2Ids.Contains(listing.ItemId))
                return false;
            if (request.Item2FamilyId != 0u && entry.Item2FamilyId != request.Item2FamilyId)
                return false;
            if (request.Item2CategoryId != 0u && entry.Item2CategoryId != request.Item2CategoryId)
                return false;
            if (request.Item2TypeId != 0u && entry.Item2TypeId != request.Item2TypeId)
                return false;

            foreach (AuctionFilter filter in request.Filters)
            {
                switch (filter.Type)
                {
                    case 2u when filter.Values.Count == 2:
                        if (entry.RequiredLevel < filter.Values[0] || entry.RequiredLevel > filter.Values[1])
                            return false;
                        break;
                    case 3u when filter.Values.Count == 2:
                        if (entry.ItemQualityId < filter.Values[0] || entry.ItemQualityId > filter.Values[1])
                            return false;
                        break;
                    case 5u when filter.Values.Count == 1:
                        if (listing.Buyout == 0ul || listing.Buyout > filter.Values[0])
                            return false;
                        break;
                    default:
                        // property, rune slot and equippable-by filters need item stats per listing, not kept here
                        FailureLedger.Record("auction-filter-ignored", $"type {filter.Type}");
                        break;
                }
            }
            return true;
        }

        [MessageHandler(GameMessageOpcode.ClientAuctionsByFilterRequest)]
        public static void HandleAuctionsByFilterRequest(WorldSession session, ClientAuctionsByFilterRequest message)
        {
            List<AuctionListing> found = MarketManager.Instance.Listings.Where(l => Matches(l, message)).ToList();
            IEnumerable<AuctionListing> sorted = message.AuctionSort switch
            {
                0u => found.OrderBy(l => Math.Max(l.MinBid, l.HighBid)),
                1u => found.OrderBy(l => l.Buyout),
                2u => found.OrderBy(l => l.Expires),
                _  => found.OrderBy(l => l.Id)
            };
            if ((message.ReverseSort & 1u) != 0u)
                sorted = sorted.Reverse();

            session.EnqueueMessageEncrypted(new ServerAuctionSearchResults
            {
                TotalCount = (uint)found.Count,
                Page       = message.Page,
                Auctions   = sorted.Skip((int)Math.Min(message.Page, 100000u) * PageSize).Take(PageSize).Select(BuildAuction).ToList()
            });
        }

        [MessageHandler(GameMessageOpcode.ClientRequestOwnedItemAuctions)]
        public static void HandleRequestOwnedItemAuctions(WorldSession session, ClientRequestOwnedItemAuctions message)
        {
            ulong me = session.Player.CharacterId;
            MarketManager.Instance.Claim(session.Player);
            session.EnqueueMessageEncrypted(new ServerOwnedItemAuctions
            {
                // the player's listings and the auctions they lead the bidding on
                Auctions = MarketManager.Instance.Listings.Where(l => l.Seller == me || l.HighBidder == me).Select(BuildAuction).ToList()
            });
        }

        [MessageHandler(GameMessageOpcode.ClientRequestOwnedCommodityOrders)]
        public static void HandleRequestOwnedCommodityOrders(WorldSession session, ClientRequestOwnedCommodityOrders message)
        {
            ulong me = session.Player.CharacterId;
            MarketManager.Instance.Claim(session.Player);
            session.EnqueueMessageEncrypted(new ServerOwnedCommodityOrders
            {
                Orders = MarketManager.Instance.Orders.Where(o => o.Owner == me).Select(BuildOrder).ToList()
            });
        }

        /// <summary>Unit price that fills <paramref name="quantity"/> units from the best orders, 0 when the book is too thin (guess at the bucket meaning).</summary>
        private static ulong FillPrice(IEnumerable<CommodityOrder> best, uint quantity)
        {
            uint have = 0u;
            foreach (CommodityOrder order in best)
            {
                have += order.Count;
                if (have >= quantity)
                    return order.UnitPrice;
            }
            return 0ul;
        }

        [MessageHandler(GameMessageOpcode.ClientRequestCommodityInfo)]
        public static void HandleRequestCommodityInfo(WorldSession session, ClientRequestCommodityInfo message)
        {
            List<CommodityOrder> orders = MarketManager.Instance.Orders.Where(o => o.ItemId == message.Item2Id).ToList();
            List<CommodityOrder> buys  = orders.Where(o => !o.Sell).OrderByDescending(o => o.UnitPrice).ToList();
            List<CommodityOrder> sells = orders.Where(o => o.Sell).OrderBy(o => o.UnitPrice).ToList();
            uint[] buckets = { 1u, 10u, 50u };
            session.EnqueueMessageEncrypted(new ServerCommodityInfoResults
            {
                Item2Id         = message.Item2Id,
                BuyOrderCount   = (uint)buys.Sum(o => (long)o.Count),
                BuyOrderPrices  = buckets.Select(b => FillPrice(buys, b)).ToArray(),
                SellOrderCount  = (uint)sells.Sum(o => (long)o.Count),
                SellOrderPrices = buckets.Select(b => FillPrice(sells, b)).ToArray()
            });
        }

        [MessageHandler(GameMessageOpcode.ClientCommodityOrderSubmit)]
        public static void HandleCommodityOrderSubmit(WorldSession session, ClientCommodityOrderSubmit message)
        {
            CommodityOrderModel order = message.Order;
            if (order.ForceImmediate)
                FailureLedger.Record("commodity-force-immediate", "treated as a normal limit order");

            MarketManager.Instance.PlaceOrder(session.Player, !order.IsBuyOrder, order.Item2Id, order.Quantity, order.PricePerUnit, out CommodityOrder posted);
            session.EnqueueMessageEncrypted(new ServerCommodityOrderResult
            {
                Result = posted != null ? GenericError.Ok : GenericError.Params,
                Order  = posted != null ? BuildOrder(posted) : order
            });
        }

        [MessageHandler(GameMessageOpcode.ClientCommodityOrderCancel)]
        public static void HandleCommodityOrderCancel(WorldSession session, ClientCommodityOrderCancel message)
        {
            if (!MarketManager.Instance.CancelOrder(session.Player, message.CommodityOrderId))
                FailureLedger.Record("commodity-cancel-refused", "no such order of this player");
            HandleRequestOwnedCommodityOrders(session, new ClientRequestOwnedCommodityOrders());
        }
    }
}
