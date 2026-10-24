using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.CharacterCache;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Market
{
    public sealed class AuctionListing
    {
        public ulong Id;
        public ulong Seller;
        public uint ItemId;
        public uint Count;
        public ulong MinBid;
        public ulong Buyout;
        public ulong HighBid;
        public ulong HighBidder;
        public DateTime Expires;
    }

    public sealed class CommodityOrder
    {
        public ulong Id;
        public ulong Owner;
        public bool Sell;
        public uint ItemId;
        public uint Count;
        public ulong UnitPrice;
    }

    /// <summary>
    /// Auction house (item auctions with bids and buyout), commodity exchange (limit order book per item) and direct player trade.
    /// None of the client messages for these systems exist in this protocol layer, so the managers are the whole server side and bots
    /// call them directly. The money flow is escrowed: bids, buy orders and listed items are taken when placed and returned on outbid,
    /// cancel or expiry. Cut and durations are assumptions (docs/ASSUMPTIONS.md "Market"). State is in memory only.
    /// </summary>
    public sealed class MarketManager : Singleton<MarketManager>
    {
        /// <summary>Share of the sale price the house keeps (assumed).</summary>
        public const double AuctionCut = 0.05d;

        /// <summary>Listing time of an auction posted through ClientAuctionSellOrderSubmit (the message has no duration field; guess).</summary>
        public static readonly TimeSpan AuctionDuration = TimeSpan.FromHours(24d);

        private readonly object gate = new();
        private readonly List<AuctionListing> listings = new();
        private readonly List<CommodityOrder> orders = new();
        private readonly Dictionary<ulong, List<(uint Item, uint Count, ulong Credits)>> claims = new();
        private ulong nextId = 1ul;

        public IReadOnlyList<AuctionListing> Listings { get { lock (gate) return listings.ToList(); } }
        public IReadOnlyList<CommodityOrder> Orders { get { lock (gate) return orders.ToList(); } }

        private static Player Online(ulong character) => CharacterManager.Instance.GetPlayer(character);

        /// <summary>Deliver to an online player at once, otherwise keep as a claim handed over by <see cref="Claim"/>.</summary>
        private void Deliver(ulong character, uint item, uint count, ulong credits)
        {
            Player player = Online(character);
            if (player != null && (item == 0u || player.Inventory.HasFreeSlot()))
            {
                if (item != 0u)
                    player.Inventory.ItemCreate(InventoryLocation.Inventory, item, count);
                if (credits > 0ul)
                    player.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, credits);
                return;
            }

            if (!claims.TryGetValue(character, out var list))
                claims[character] = list = new();
            list.Add((item, count, credits));
            FailureLedger.Record("market-claim-stored", player == null ? "recipient offline" : "recipient bag full");
        }

        /// <summary>Hand over everything stored for a player (called when they use the market and at login).</summary>
        public void Claim(Player player)
        {
            List<(uint Item, uint Count, ulong Credits)> mine;
            lock (gate)
            {
                if (!claims.Remove(player.CharacterId, out mine))
                    return;
            }

            lock (gate)
                foreach (var (item, count, credits) in mine)
                    Deliver(player.CharacterId, item, count, credits);
        }

        public AuctionListing CreateAuction(Player seller, uint itemId, uint count, ulong minBid, ulong buyout, TimeSpan duration)
        {
            if (count == 0u || minBid == 0ul || (buyout != 0ul && buyout < minBid) || seller.Inventory.GetItemCount(itemId) < count)
            {
                FailureLedger.Record("auction-create-refused", $"item {itemId} x{count}");
                return null;
            }

            lock (gate)
            {
                seller.Inventory.ItemDelete(itemId, count);
                var listing = new AuctionListing { Id = nextId++, Seller = seller.CharacterId, ItemId = itemId, Count = count, MinBid = minBid, Buyout = buyout, Expires = DateTime.UtcNow + duration };
                listings.Add(listing);
                FailureLedger.Record("auction-listed", $"item {itemId}");
                return listing;
            }
        }

        public bool Bid(Player bidder, ulong listingId, ulong amount)
        {
            lock (gate)
            {
                AuctionListing listing = listings.FirstOrDefault(l => l.Id == listingId);
                if (listing == null || listing.Seller == bidder.CharacterId || amount < listing.MinBid || amount <= listing.HighBid
                    || !bidder.CurrencyManager.CanAfford(CurrencyType.Credits, amount))
                {
                    FailureLedger.Record("auction-bid-refused", listing == null ? "no listing" : "amount, seller or funds");
                    return false;
                }

                if (listing.Buyout != 0ul && amount >= listing.Buyout)
                    return Buyout(bidder, listingId);

                bidder.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, amount);
                if (listing.HighBidder != 0ul)
                    Deliver(listing.HighBidder, 0u, 0u, listing.HighBid);

                listing.HighBid = amount;
                listing.HighBidder = bidder.CharacterId;
                FailureLedger.Record("auction-bid", $"item {listing.ItemId}");
                return true;
            }
        }

        public bool Buyout(Player buyer, ulong listingId)
        {
            lock (gate)
            {
                AuctionListing listing = listings.FirstOrDefault(l => l.Id == listingId);
                if (listing == null || listing.Buyout == 0ul || listing.Seller == buyer.CharacterId
                    || !buyer.CurrencyManager.CanAfford(CurrencyType.Credits, listing.Buyout))
                {
                    FailureLedger.Record("auction-buyout-refused", listing == null ? "no listing" : "no buyout, seller or funds");
                    return false;
                }

                buyer.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, listing.Buyout);
                if (listing.HighBidder != 0ul)
                    Deliver(listing.HighBidder, 0u, 0u, listing.HighBid);

                listing.HighBid = listing.Buyout;
                listing.HighBidder = buyer.CharacterId;
                Settle(listing);
                return true;
            }
        }

        private void Settle(AuctionListing listing)
        {
            listings.Remove(listing);
            if (listing.HighBidder != 0ul)
            {
                Deliver(listing.HighBidder, listing.ItemId, listing.Count, 0ul);
                Deliver(listing.Seller, 0u, 0u, (ulong)(listing.HighBid * (1d - AuctionCut)));
                FailureLedger.Record("auction-sold", $"item {listing.ItemId}");
            }
            else
            {
                Deliver(listing.Seller, listing.ItemId, listing.Count, 0ul);
                FailureLedger.Record("auction-expired-unsold", $"item {listing.ItemId}");
            }
        }

        public void Update(DateTime now)
        {
            lock (gate)
                foreach (AuctionListing listing in listings.Where(l => l.Expires <= now).ToList())
                    Settle(listing);
        }

        /// <summary>
        /// Places a limit order and matches it against the book (price priority, then age). Buyers pay the resting order's price.
        /// Returns the quantity traded at once.
        /// </summary>
        public uint PlaceOrder(Player owner, bool sell, uint itemId, uint count, ulong unitPrice)
        {
            return PlaceOrder(owner, sell, itemId, count, unitPrice, out _);
        }

        /// <summary><paramref name="posted"/> is the order as placed (its Count is what still rests in the book), null when it was refused.</summary>
        public uint PlaceOrder(Player owner, bool sell, uint itemId, uint count, ulong unitPrice, out CommodityOrder posted)
        {
            posted = null;
            if (count == 0u || unitPrice == 0ul)
                return 0u;

            lock (gate)
            {
                if (sell)
                {
                    if (owner.Inventory.GetItemCount(itemId) < count)
                    {
                        FailureLedger.Record("commodity-order-refused", "not enough items");
                        return 0u;
                    }

                    owner.Inventory.ItemDelete(itemId, count);
                }
                else
                {
                    if (!owner.CurrencyManager.CanAfford(CurrencyType.Credits, unitPrice * count))
                    {
                        FailureLedger.Record("commodity-order-refused", "not enough credits");
                        return 0u;
                    }

                    owner.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, unitPrice * count);
                }

                var order = new CommodityOrder { Id = nextId++, Owner = owner.CharacterId, Sell = sell, ItemId = itemId, Count = count, UnitPrice = unitPrice };
                posted = order;
                uint traded = 0u;
                while (order.Count > 0u)
                {
                    CommodityOrder match = orders
                        .Where(o => o.ItemId == itemId && o.Sell != sell && o.Owner != order.Owner && (sell ? o.UnitPrice >= unitPrice : o.UnitPrice <= unitPrice))
                        .OrderBy(o => sell ? ulong.MaxValue - o.UnitPrice : o.UnitPrice).ThenBy(o => o.Id).FirstOrDefault();
                    if (match == null)
                        break;

                    uint quantity = Math.Min(order.Count, match.Count);
                    ulong price = match.UnitPrice;
                    CommodityOrder buyOrder = sell ? match : order;
                    CommodityOrder sellOrder = sell ? order : match;

                    Deliver(buyOrder.Owner, itemId, quantity, (buyOrder.UnitPrice - price) * quantity);
                    Deliver(sellOrder.Owner, 0u, 0u, price * quantity);
                    order.Count -= quantity;
                    match.Count -= quantity;
                    traded += quantity;
                    if (match.Count == 0u)
                        orders.Remove(match);
                    FailureLedger.Record("commodity-trade", $"item {itemId}", $"{quantity} at {price}");
                }

                if (order.Count > 0u)
                    orders.Add(order);

                return traded;
            }
        }

        /// <summary>The seller withdraws an auction: the item goes back, a standing bid is refunded.</summary>
        public bool CancelAuction(Player seller, ulong listingId)
        {
            lock (gate)
            {
                AuctionListing listing = listings.FirstOrDefault(l => l.Id == listingId && l.Seller == seller.CharacterId);
                if (listing == null)
                {
                    FailureLedger.Record("auction-cancel-refused", "no listing of this seller");
                    return false;
                }

                listings.Remove(listing);
                if (listing.HighBidder != 0ul)
                    Deliver(listing.HighBidder, 0u, 0u, listing.HighBid);
                Deliver(seller.CharacterId, listing.ItemId, listing.Count, 0ul);
                FailureLedger.Record("auction-cancelled", $"item {listing.ItemId}");
                return true;
            }
        }

        public bool CancelOrder(Player owner, ulong orderId)
        {
            lock (gate)
            {
                CommodityOrder order = orders.FirstOrDefault(o => o.Id == orderId && o.Owner == owner.CharacterId);
                if (order == null)
                    return false;

                orders.Remove(order);
                if (order.Sell)
                    Deliver(owner.CharacterId, order.ItemId, order.Count, 0ul);
                else
                    Deliver(owner.CharacterId, 0u, 0u, order.UnitPrice * order.Count);
                return true;
            }
        }

        /// <summary>
        /// Direct trade between two players once both accepted: all or nothing, checked first.
        /// </summary>
        public bool Trade(Player a, uint itemA, uint countA, ulong creditsA, Player b, uint itemB, uint countB, ulong creditsB)
        {
            bool ok = a != b
                && (itemA == 0u || a.Inventory.GetItemCount(itemA) >= countA)
                && (itemB == 0u || b.Inventory.GetItemCount(itemB) >= countB)
                && a.CurrencyManager.CanAfford(CurrencyType.Credits, creditsA)
                && b.CurrencyManager.CanAfford(CurrencyType.Credits, creditsB)
                && a.Inventory.HasFreeSlot() && b.Inventory.HasFreeSlot();
            if (!ok)
            {
                FailureLedger.Record("trade-refused", "items, credits or space missing");
                return false;
            }

            if (itemA != 0u) { a.Inventory.ItemDelete(itemA, countA); b.Inventory.ItemCreate(InventoryLocation.Inventory, itemA, countA); }
            if (itemB != 0u) { b.Inventory.ItemDelete(itemB, countB); a.Inventory.ItemCreate(InventoryLocation.Inventory, itemB, countB); }
            if (creditsA > 0ul) { a.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, creditsA); b.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, creditsA); }
            if (creditsB > 0ul) { b.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, creditsB); a.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, creditsB); }
            FailureLedger.Record("trade-done", "player trade");
            return true;
        }
    }
}
