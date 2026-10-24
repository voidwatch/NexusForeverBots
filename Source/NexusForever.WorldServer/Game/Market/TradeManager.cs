using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Game.Market
{
    /// <summary>
    /// Player to player trade over the client messages (ClientP2PTrading* 0x018C-0x0197, layouts from the retail client, docs/PROTOCOL.md).
    /// Invite, accept, offer items and credits, both commit (one toggling message), then everything moves at once or nothing does.
    /// Any change to an offer clears both commits, as the client's own handlers do.
    /// </summary>
    public sealed class TradeManager : Singleton<TradeManager>
    {
        private sealed class Side
        {
            public Player Player;
            public readonly List<ulong> Items = new();
            public ulong Credits;
            public bool Committed;
        }

        private sealed class Trade
        {
            public Side Initiator;
            public Side Target;
            public bool Accepted;

            public Side Of(Player player) => Initiator.Player == player ? Initiator : Target;
            public Side Other(Player player) => Initiator.Player == player ? Target : Initiator;
        }

        private readonly object gate = new();
        private readonly Dictionary<ulong, Trade> trades = new();

        private static void Send(Player player, IWritable message)
        {
            player?.Session?.EnqueueMessageEncrypted(message);
        }

        private static void SendResult(Player player, P2PTradeResult result, bool cancelled = false)
        {
            Send(player, new ServerP2PTradeResult { Result = result, Cancelled = cancelled });
        }

        public bool IsTrading(Player player)
        {
            lock (gate)
                return trades.ContainsKey(player.CharacterId);
        }

        private void End(Trade trade, P2PTradeResult result)
        {
            trades.Remove(trade.Initiator.Player.CharacterId);
            trades.Remove(trade.Target.Player.CharacterId);
            bool cancelled = result != P2PTradeResult.FinishedSuccess;
            SendResult(trade.Initiator.Player, result, cancelled);
            SendResult(trade.Target.Player, result, cancelled);
            FailureLedger.Record(cancelled ? "p2p-trade-ended" : "p2p-trade-done", result.ToString());
        }

        /// <summary>The trade of this player if the partner is still in the world; ends it with MissingPlayer otherwise.</summary>
        private Trade Active(Player player, bool needAccepted = true)
        {
            if (!trades.TryGetValue(player.CharacterId, out Trade trade))
            {
                FailureLedger.Record("p2p-trade-refused", "no trade");
                return null;
            }

            Player other = trade.Other(player).Player;
            if (other.Map == null || player.Map == null)
            {
                End(trade, P2PTradeResult.MissingPlayer);
                return null;
            }

            if (needAccepted && !trade.Accepted)
            {
                FailureLedger.Record("p2p-trade-refused", "invite not accepted yet");
                return null;
            }

            return trade;
        }

        private static void ClearCommits(Trade trade)
        {
            trade.Initiator.Committed = false;
            trade.Target.Committed = false;
        }

        public void Initiate(Player initiator, uint targetUnitId)
        {
            Player target = initiator.Map?.GetEntity<Player>(targetUnitId);
            lock (gate)
            {
                if (target == null || target == initiator)
                {
                    SendResult(initiator, P2PTradeResult.ErrorInitiating);
                    FailureLedger.Record("p2p-trade-refused", "no such player");
                    return;
                }

                if (trades.ContainsKey(initiator.CharacterId) || trades.ContainsKey(target.CharacterId))
                {
                    SendResult(initiator, P2PTradeResult.TargetBusy);
                    FailureLedger.Record("p2p-trade-refused", "busy");
                    return;
                }

                // the opposing factions cannot trade (docs/ASSUMPTIONS.md "Player trade")
                if (initiator.Faction1 != target.Faction1)
                {
                    SendResult(initiator, P2PTradeResult.TargetNotAllowedToTrade);
                    FailureLedger.Record("p2p-trade-refused", "other faction");
                    return;
                }

                var trade = new Trade { Initiator = new Side { Player = initiator }, Target = new Side { Player = target } };
                trades[initiator.CharacterId] = trade;
                trades[target.CharacterId] = trade;
                Send(target, new ServerP2PTradeInvite { InviterUnitId = initiator.Guid });
                FailureLedger.Record("p2p-trade-invite", "sent");
            }
        }

        public void Accept(Player target)
        {
            lock (gate)
            {
                Trade trade = Active(target, false);
                if (trade == null)
                    return;
                if (trade.Accepted || trade.Target.Player != target)
                {
                    FailureLedger.Record("p2p-trade-refused", "accept without a pending invite");
                    return;
                }

                trade.Accepted = true;
                SendResult(trade.Initiator.Player, P2PTradeResult.PlayerAcceptedInvite);
                SendResult(target, P2PTradeResult.PlayerAcceptedInvite);
            }
        }

        public void Decline(Player target)
        {
            lock (gate)
            {
                Trade trade = Active(target, false);
                if (trade == null)
                    return;
                if (trade.Accepted || trade.Target.Player != target)
                {
                    FailureLedger.Record("p2p-trade-refused", "decline without a pending invite");
                    return;
                }

                End(trade, P2PTradeResult.PlayerDeclinedInvite);
            }
        }

        public void Cancel(Player player, P2PTradeResult reason = P2PTradeResult.PlayerCanceled)
        {
            lock (gate)
            {
                if (trades.TryGetValue(player.CharacterId, out Trade trade))
                    End(trade, reason);
            }
        }

        public void AddItem(Player player, ulong itemGuid)
        {
            lock (gate)
            {
                Trade trade = Active(player);
                if (trade == null)
                    return;

                Side side = trade.Of(player);
                Item item = player.Inventory.GetItem(itemGuid);
                if (item == null || item.Location != InventoryLocation.Inventory || item.Info == null || side.Items.Contains(itemGuid))
                {
                    SendResult(player, P2PTradeResult.ErrorAddingItem);
                    FailureLedger.Record("p2p-trade-refused", item == null ? "item not owned" : "item not tradeable or already offered");
                    return;
                }

                side.Items.Add(itemGuid);
                ClearCommits(trade);
                var update = new ServerP2PTradeUpdateItem
                {
                    TradeIndex  = (uint)(side.Items.Count - 1),
                    OwnerUnitId = player.Guid,
                    Item2Id     = item.Info.Id,
                    ItemGuid    = item.Guid,
                    Quantity    = item.StackCount
                };
                Send(trade.Initiator.Player, update);
                Send(trade.Target.Player, update);
            }
        }

        public void RemoveItem(Player player, ulong itemGuid)
        {
            lock (gate)
            {
                Trade trade = Active(player);
                if (trade == null)
                    return;

                if (!trade.Of(player).Items.Remove(itemGuid))
                {
                    SendResult(player, P2PTradeResult.ErrorRemovingItem);
                    FailureLedger.Record("p2p-trade-refused", "item not offered");
                    return;
                }

                ClearCommits(trade);
                var removed = new ServerP2PTradeItemRemoved { ItemGuid = itemGuid };
                Send(trade.Initiator.Player, removed);
                Send(trade.Target.Player, removed);
            }
        }

        public void SetMoney(Player player, ulong credits)
        {
            lock (gate)
            {
                Trade trade = Active(player);
                if (trade == null)
                    return;

                Side side = trade.Of(player);
                if (!player.CurrencyManager.CanAfford(CurrencyType.Credits, credits))
                {
                    FailureLedger.Record("p2p-trade-refused", "credits not owned");
                    credits = side.Credits;
                }

                side.Credits = credits;
                ClearCommits(trade);
                var money = new ServerP2PTradeUpdateMoney { UnitId = player.Guid, Credits = credits };
                Send(trade.Initiator.Player, money);
                Send(trade.Target.Player, money);
            }
        }

        public void ToggleCommit(Player player)
        {
            lock (gate)
            {
                Trade trade = Active(player);
                if (trade == null)
                    return;

                Side side = trade.Of(player);
                side.Committed = !side.Committed;
                bool initiator = side == trade.Initiator;
                P2PTradeResult result = initiator
                    ? side.Committed ? P2PTradeResult.InitiatorCommitted : P2PTradeResult.InitiatorUnCommitted
                    : side.Committed ? P2PTradeResult.TargetCommitted : P2PTradeResult.TargetUnCommitted;
                SendResult(trade.Initiator.Player, result);
                SendResult(trade.Target.Player, result);

                if (trade.Initiator.Committed && trade.Target.Committed)
                    Complete(trade);
            }
        }

        private void Complete(Trade trade)
        {
            Side a = trade.Initiator, b = trade.Target;
            if (a.Items.Count == 0 && b.Items.Count == 0 && a.Credits == 0ul && b.Credits == 0ul)
            {
                End(trade, P2PTradeResult.NothingToTrade);
                return;
            }

            bool valid(Side side, Side receiver)
            {
                foreach (ulong guid in side.Items)
                {
                    Item item = side.Player.Inventory.GetItem(guid);
                    if (item == null || item.Location != InventoryLocation.Inventory)
                        return false;
                }
                return side.Player.CurrencyManager.CanAfford(CurrencyType.Credits, side.Credits)
                    && receiver.Player.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) >= (uint)side.Items.Count;
            }

            if (!valid(a, b) || !valid(b, a))
            {
                End(trade, P2PTradeResult.DbFailed);
                return;
            }

            void move(Side from, Side to)
            {
                foreach (Item item in from.Items.Select(g => from.Player.Inventory.GetItem(g)).ToList())
                {
                    from.Player.Inventory.ItemRemove(item);
                    item.CharacterId = to.Player.CharacterId;
                    to.Player.Inventory.AddItem(item, InventoryLocation.Inventory);
                }
                if (from.Credits > 0ul)
                {
                    from.Player.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, from.Credits);
                    to.Player.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, from.Credits);
                }
            }

            move(a, b);
            move(b, a);
            End(trade, P2PTradeResult.FinishedSuccess);
        }
    }
}
