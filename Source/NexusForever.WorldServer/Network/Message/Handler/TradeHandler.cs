using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Market;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    /// <summary>Player to player trade (ClientP2PTrading*, layouts from the retail client, docs/PROTOCOL.md).</summary>
    public static class TradeHandler
    {
        [MessageHandler(GameMessageOpcode.ClientP2PTradingInitiateTrade)]
        public static void HandleInitiateTrade(WorldSession session, ClientP2PTradingInitiateTrade message)
        {
            TradeManager.Instance.Initiate(session.Player, message.TargetUnitId);
        }

        [MessageHandler(GameMessageOpcode.ClientP2PTradingAcceptInvite)]
        public static void HandleAcceptInvite(WorldSession session, ClientP2PTradingAcceptInvite message)
        {
            TradeManager.Instance.Accept(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientP2PTradingDeclineInvite)]
        public static void HandleDeclineInvite(WorldSession session, ClientP2PTradingDeclineInvite message)
        {
            TradeManager.Instance.Decline(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientP2PTradingCancelTrade)]
        public static void HandleCancelTrade(WorldSession session, ClientP2PTradingCancelTrade message)
        {
            TradeManager.Instance.Cancel(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientP2PTradingCommit)]
        public static void HandleCommit(WorldSession session, ClientP2PTradingCommit message)
        {
            TradeManager.Instance.ToggleCommit(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientP2PTradingAddItem)]
        public static void HandleAddItem(WorldSession session, ClientP2PTradingAddItem message)
        {
            TradeManager.Instance.AddItem(session.Player, message.ItemGuid);
        }

        [MessageHandler(GameMessageOpcode.ClientP2PTradingRemoveItem)]
        public static void HandleRemoveItem(WorldSession session, ClientP2PTradingRemoveItem message)
        {
            TradeManager.Instance.RemoveItem(session.Player, message.ItemGuid);
        }

        [MessageHandler(GameMessageOpcode.ClientP2PTradingSetMoney)]
        public static void HandleSetMoney(WorldSession session, ClientP2PTradingSetMoney message)
        {
            TradeManager.Instance.SetMoney(session.Player, message.Credits);
        }
    }
}
