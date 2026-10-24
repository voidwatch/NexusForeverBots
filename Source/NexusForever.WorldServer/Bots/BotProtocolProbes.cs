using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Market;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Probes that drive the client messages recovered from the retail client (docs/PROTOCOL.md "Client binary static analysis"):
    /// requests are built bit by bit as the client writes them and go through the real handlers; every server reply the bots receive is
    /// serialized once to prove its writer works.
    /// </summary>
    public static partial class BotSystems
    {
        /// <summary>Records the server messages a bot receives until disposed.</summary>
        private sealed class Capture : IDisposable
        {
            private readonly BotSession session;
            public readonly List<IWritable> Messages = new();

            public Capture(Player player)
            {
                session = player.Session as BotSession ?? throw new ProbeSkip();
                session.ServerMessage += OnMessage;
            }

            private void OnMessage(IWritable message)
            {
                lock (Messages)
                    Messages.Add(message);
            }

            public List<T> Of<T>() where T : IWritable
            {
                lock (Messages)
                    return Messages.OfType<T>().ToList();
            }

            public void Dispose()
            {
                session.ServerMessage -= OnMessage;
                lock (Messages)
                    foreach (IWritable message in Messages.Where(m => m.GetType().Namespace == typeof(ServerP2PTradeResult).Namespace))
                    {
                        using var stream = new MemoryStream();
                        using var writer = new GamePacketWriter(stream);
                        message.Write(writer);
                        writer.FlushBits();
                    }
            }
        }

        /// <summary>A bot on the same map instance and faction, not trading, with bag room.</summary>
        private static Player NearPartner(Player me)
        {
            return NetworkManager<Network.WorldSession>.Instance
                .GetSessions(s => s.Player != null && s.Player != me && s.Player.Session is BotSession && s.Player.Map == me.Map && s.Player.Map != null
                    && s.Player.Faction1 == me.Faction1 && !TradeManager.Instance.IsTrading(s.Player)
                    && s.Player.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) > 2u)
                .Select(s => s.Player).FirstOrDefault();
        }

        private static Item NewestItem(Player player, uint itemId)
        {
            return player.Inventory.GetItems(InventoryLocation.Inventory).Where(i => i.Info?.Id == itemId).OrderByDescending(i => i.Guid).FirstOrDefault();
        }

        private static T Empty<T>() where T : NexusForever.Shared.Network.Message.IReadable, new() => BotPackets.Build<T>(w => { });

        // invite, decline; invite, accept, offer an item and credits, commit/uncommit/commit on one side, commit on the other: goods swap
        private static void P2PTradeProbe(Player me)
        {
            Player other = NearPartner(me) ?? throw new ProbeSkip();
            if (TradeManager.Instance.IsTrading(me))
                throw new ProbeSkip();
            EnsureRoom(me, 3u);
            EnsureRoom(other, 3u);

            uint itemId = TestItemId();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u);
            Item item = NewestItem(me, itemId);
            Expect(item != null, "test item not created");
            other.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 300ul);
            ulong myCredits = me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul;

            using var mine = new Capture(me);
            using var theirs = new Capture(other);
            void initiate() => TradeHandler.HandleInitiateTrade(me.Session, BotPackets.Build<ClientP2PTradingInitiateTrade>(w => w.Write(other.Guid)));

            try
            {
                initiate();
                Expect(theirs.Of<ServerP2PTradeInvite>().Any(i => i.InviterUnitId == me.Guid), "target got no invite");
                TradeHandler.HandleDeclineInvite(other.Session, Empty<ClientP2PTradingDeclineInvite>());
                Expect(!TradeManager.Instance.IsTrading(me) && mine.Of<ServerP2PTradeResult>().Any(r => r.Result == P2PTradeResult.PlayerDeclinedInvite && r.Cancelled),
                    "decline did not end the trade");

                initiate();
                TradeHandler.HandleAcceptInvite(other.Session, Empty<ClientP2PTradingAcceptInvite>());
                Expect(mine.Of<ServerP2PTradeResult>().Any(r => r.Result == P2PTradeResult.PlayerAcceptedInvite), "initiator not told about the accept");

                TradeHandler.HandleAddItem(me.Session, BotPackets.Build<ClientP2PTradingAddItem>(w => w.Write(item.Guid)));
                Expect(theirs.Of<ServerP2PTradeUpdateItem>().Any(u => u.ItemGuid == item.Guid && u.OwnerUnitId == me.Guid && u.Item2Id == itemId), "partner did not see the offered item");
                TradeHandler.HandleSetMoney(other.Session, BotPackets.Build<ClientP2PTradingSetMoney>(w => w.Write(300ul)));
                Expect(mine.Of<ServerP2PTradeUpdateMoney>().Any(m => m.UnitId == other.Guid && m.Credits == 300ul), "money offer not shown");

                // commit toggles: commit, uncommit, commit again
                var commit = Empty<ClientP2PTradingCommit>();
                TradeHandler.HandleCommit(me.Session, commit);
                TradeHandler.HandleCommit(me.Session, commit);
                Expect(theirs.Of<ServerP2PTradeResult>().Count(r => r.Result is P2PTradeResult.InitiatorCommitted or P2PTradeResult.InitiatorUnCommitted) == 2, "commit toggle not reported");
                TradeHandler.HandleCommit(me.Session, commit);
                Expect(TradeManager.Instance.IsTrading(me), "trade finished on one commit");
                TradeHandler.HandleCommit(other.Session, commit);

                Expect(!TradeManager.Instance.IsTrading(me) && mine.Of<ServerP2PTradeResult>().Any(r => r.Result == P2PTradeResult.FinishedSuccess), "trade did not finish");
                Expect(other.Inventory.GetItem(item.Guid) != null && me.Inventory.GetItem(item.Guid) == null, "item did not change hands");
                Expect((me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul) == myCredits + 300ul, "credits did not change hands");
            }
            finally
            {
                TradeManager.Instance.Cancel(me);
            }
        }

        // post an auction with the item guid, find it by item id, buy it out; post again and cancel it
        private static void AuctionMessageProbe(Player me)
        {
            Player buyer = Partner(me) ?? throw new ProbeSkip();
            EnsureRoom(me, 3u);
            EnsureRoom(buyer, 3u);
            uint itemId = TestItemId();
            buyer.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 5000ul);

            using var seller = new Capture(me);
            using var bidder = new Capture(buyer);
            void post(Item item, ulong buyout) => MarketHandler.HandleAuctionSellOrderSubmit(me.Session, BotPackets.Build<ClientAuctionSellOrderSubmit>(w =>
            {
                w.Write(item.Guid);
                w.Write(100ul);
                w.Write(buyout);
            }));

            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u);
            post(NewestItem(me, itemId) ?? throw new ProbeFailure("test item not created"), 1000ul);
            ServerAuctionPostResult posted = seller.Of<ServerAuctionPostResult>().LastOrDefault();
            Expect(posted != null && posted.Result == Game.Static.GenericError.Ok && posted.Auction.Item2Id == itemId, $"post failed: {posted?.Result}");
            ulong auctionId = posted.Auction.AuctionId;

            MarketHandler.HandleAuctionsByFilterRequest(buyer.Session, BotPackets.Build<ClientAuctionsByFilterRequest>(w =>
            {
                w.Write(0u, 14u);
                w.Write(0u, 14u);
                w.Write(0u, 14u);
                w.Write(1u);
                w.Write(itemId);
                w.Write(1u);
                w.Write(5u, 3u); // buyout maximum filter
                w.Write(2000ul);
                w.Write(0u);
                w.Write(1u, 3u);
                w.Write(0u, 8u);
                w.Write(0u);
            }));
            Expect(bidder.Of<ServerAuctionSearchResults>().LastOrDefault()?.Auctions.Any(a => a.AuctionId == auctionId) == true || bidder.Of<ServerAuctionSearchResults>().LastOrDefault()?.TotalCount > 10u,
                "search by item id did not find the auction");

            MarketHandler.HandleAuctionBuyOrderSubmit(buyer.Session, BotPackets.Build<ClientAuctionBuyOrderSubmit>(w =>
            {
                w.Write(itemId, 18u);
                w.Write(auctionId);
                w.Write(1000ul);
            }));
            Expect(bidder.Of<ServerAuctionBidResult>().LastOrDefault()?.Result == Game.Static.GenericError.Ok, "buyout refused");
            Expect(MarketManager.Instance.Listings.All(l => l.Id != auctionId), "bought auction still listed");

            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u);
            post(NewestItem(me, itemId) ?? throw new ProbeFailure("test item not created"), 0ul);
            ulong second = seller.Of<ServerAuctionPostResult>().Last().Auction.AuctionId;
            MarketHandler.HandleRequestOwnedItemAuctions(me.Session, Empty<ClientRequestOwnedItemAuctions>());
            Expect(seller.Of<ServerOwnedItemAuctions>().LastOrDefault()?.Auctions.Any(a => a.AuctionId == second) == true, "own auction not listed");
            MarketHandler.HandleAuctionCancel(me.Session, BotPackets.Build<ClientAuctionCancel>(w =>
            {
                w.Write(itemId, 18u);
                w.Write(second);
            }));
            Expect(seller.Of<ServerAuctionCancelResult>().LastOrDefault()?.Result == Game.Static.GenericError.Ok, "cancel refused");
            Expect(MarketManager.Instance.Listings.All(l => l.Id != second), "cancelled auction still listed");
            me.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, Math.Min(me.CurrencyManager.GetCurrency(CurrencyType.Credits) ?? 0ul, (ulong)(1000 * (1d - MarketManager.AuctionCut))));
        }

        private static void WriteOrder(GamePacketWriter w, bool buy, uint itemId, uint quantity, ulong price, ulong id = 0ul)
        {
            new CommodityOrderModel { CommodityOrderId = id, Item2Id = itemId, Quantity = quantity, PricePerUnit = price, Price = price * quantity, IsBuyOrder = buy }.Write(w);
        }

        // sell order, crossing buy order, book info, owned orders, cancel the rest
        private static void CommodityMessageProbe(Player me)
        {
            Player buyer = Partner(me) ?? throw new ProbeSkip();
            EnsureRoom(me, 3u);
            EnsureRoom(buyer, 3u);
            uint itemId = TestItemId();
            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 5u);
            buyer.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, 5000ul);
            uint buyerItems = buyer.Inventory.GetItemCount(itemId);

            using var seller = new Capture(me);
            using var other = new Capture(buyer);
            MarketHandler.HandleCommodityOrderSubmit(me.Session, BotPackets.Build<ClientCommodityOrderSubmit>(w => WriteOrder(w, false, itemId, 5u, 10ul)));
            ServerCommodityOrderResult sell = seller.Of<ServerCommodityOrderResult>().LastOrDefault();
            Expect(sell?.Result == Game.Static.GenericError.Ok, $"sell order refused: {sell?.Result}");

            MarketHandler.HandleRequestCommodityInfo(buyer.Session, BotPackets.Build<ClientRequestCommodityInfo>(w => w.Write(itemId, 18u)));
            Expect(other.Of<ServerCommodityInfoResults>().LastOrDefault()?.SellOrderCount >= 5u, "book info lacks the sell order");

            MarketHandler.HandleCommodityOrderSubmit(buyer.Session, BotPackets.Build<ClientCommodityOrderSubmit>(w => WriteOrder(w, true, itemId, 3u, 20ul)));
            Expect(other.Of<ServerCommodityOrderResult>().LastOrDefault()?.Result == Game.Static.GenericError.Ok, "buy order refused");
            Expect(buyer.Inventory.GetItemCount(itemId) >= buyerItems + 3u, "buyer did not get the commodities");

            MarketHandler.HandleRequestOwnedCommodityOrders(me.Session, Empty<ClientRequestOwnedCommodityOrders>());
            CommodityOrderModel rest = seller.Of<ServerOwnedCommodityOrders>().LastOrDefault()?.Orders.FirstOrDefault(o => o.CommodityOrderId == sell.Order.CommodityOrderId);
            Expect(rest != null && rest.Quantity == 2u, "remaining sell order not listed with 2 units");
            MarketHandler.HandleCommodityOrderCancel(me.Session, BotPackets.Build<ClientCommodityOrderCancel>(w =>
            {
                w.Write(rest.CommodityOrderId);
                w.Write(itemId, 18u);
                w.Write(false);
            }));
            Expect(MarketManager.Instance.Orders.All(o => o.Id != rest.CommodityOrderId), "cancelled order still in the book");
        }

        // a lone bot picks veteran, the choice is stored for its next instance, then normal again; reset of empty instances
        private static void InstanceSettingsProbe(Player me)
        {
            if (Game.Groups.GroupManager.Instance.GetGroup(me.CharacterId) != null)
                throw new ProbeSkip();

            ClientSetInstanceSettings settings(byte difficulty) => BotPackets.Build<ClientSetInstanceSettings>(w =>
            {
                w.Write(0u);
                w.Write(difficulty, 2u);
                w.Write((byte)0);
                w.Write((ushort)0, 11u);
            });

            InstanceHandler.HandleSetInstanceSettings(me.Session, settings(1));
            Expect(InstanceSettings.For(DungeonInstancedMap.InstanceIdFor(me))?.Difficulty == InstanceDifficulty.Veteran, "veteran choice not stored");
            InstanceHandler.HandleSetInstanceSettings(me.Session, settings(3));
            Expect(InstanceSettings.For(DungeonInstancedMap.InstanceIdFor(me))?.Difficulty == InstanceDifficulty.Veteran, "invalid difficulty accepted");
            InstanceHandler.HandleSetInstanceSettings(me.Session, settings(0));
            Expect(InstanceSettings.For(DungeonInstancedMap.InstanceIdFor(me))?.Difficulty == InstanceDifficulty.Normal, "normal choice not stored");
            if (me.Map is not DungeonMapInstance)
                InstanceHandler.HandleResetInstances(me.Session, Empty<ClientResetInstances>());
        }

        // ConvertResource through the client message: count in source units
        private static List<(ushort World, uint Creature, System.Numerics.Vector3 Position)> conversionNpcs;

        private static void ConversionMessageProbe(Player me)
        {
            var rows = GameTableManager.Instance.ResourceConversion.Entries
                .Where(r => r != null && r.Id != 0u && r.Id < (1u << 14) && r.SurchargeId == 0u && r.TargetCount <= 100000u && r.ResourceConversionTypeEnum != 1u
                    && GameTableManager.Instance.Item.GetEntry(r.SourceId) != null && GameTableManager.Instance.Item.GetEntry(r.TargetId) != null).ToList();
            // only rows a spawned conversion NPC offers; the bot goes to that NPC first (the handler needs one in reach)
            conversionNpcs ??= Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns(false)
                .Where(s => Game.Rewards.ResourceConversion.RowsOf(s.Creature).Any())
                .Select(s => (s.World, s.Creature, new System.Numerics.Vector3(s.X, s.Y, s.Z))).ToList();
            var offered = rows.Where(r => conversionNpcs.Any(n => Game.Rewards.ResourceConversion.RowsOf(n.Creature).Contains(r.Id))).ToList();
            if (offered.Count == 0)
                throw new ProbeSkip();
            var row = offered.FirstOrDefault(r => Game.Rewards.ResourceConversion.NpcInReach(me, r.Id));
            if (row == null)
            {
                var pick = offered[BotManager.Rng.Next(offered.Count)];
                var npc = conversionNpcs.First(n => Game.Rewards.ResourceConversion.RowsOf(n.Creature).Contains(pick.Id));
                if (!BotQuests.CanVisitWorld(npc.World) || me.Map is MapInstance)
                    throw new ProbeSkip();
                me.TeleportTo(npc.World, npc.Item3.X, npc.Item3.Y, npc.Item3.Z);
                RunNext(me, "conversionmsg");
                throw new ProbeSkip();
            }
            uint stack = Math.Max(1u, GameTableManager.Instance.Item.GetEntry(row.SourceId).MaxStackCount);
            EnsureRoom(me, (Math.Max(1u, row.SourceCount) + stack - 1u) / stack + 2u);

            me.Inventory.ItemCreate(InventoryLocation.Inventory, row.SourceId, Math.Max(1u, row.SourceCount));
            uint sources = me.Inventory.GetItemCount(row.SourceId);
            uint targets = me.Inventory.GetItemCount(row.TargetId);
            InstanceHandler.HandleResourceConversion(me.Session, BotPackets.Build<ClientResourceConversion>(w =>
            {
                w.Write(row.Id, 14u);
                w.Write(Math.Max(1u, row.SourceCount));
                w.Write(0ul);
            }));
            Expect(me.Inventory.GetItemCount(row.SourceId) + Math.Max(1u, row.SourceCount) == sources || row.SourceId == row.TargetId, $"row {row.Id}: sources not consumed");
            Expect(me.Inventory.GetItemCount(row.TargetId) >= targets + Math.Max(1u, row.TargetCount) || row.SourceId == row.TargetId, $"row {row.Id}: target not received");
        }

        private static void DailyLoginMessageProbe(Player me)
        {
            EnsureRoom(me, 2u);
            InstanceHandler.HandleDailyLoginRewardsRequest(me.Session, Empty<ClientDailyLoginRewardsRequest>());
            Expect(!me.DailyLoginManager.CanClaim(DateTime.UtcNow), "request did not claim today's reward");
        }
    }
}
