using System;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.Shared.GameTable.Model;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Account.Static;
using NexusForever.WorldServer.Game.Static;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class PathHandler
    {
        [MessageHandler(GameMessageOpcode.ClientPathActivate)]
        public static void HandlePathActivate(WorldSession session, ClientPathActivate clientPathActivate)
        {
            uint activateCooldown = GameTableManager.Instance.GameFormula.GetEntry(2366).Dataint0;
            uint bypassCost       = GameTableManager.Instance.GameFormula.GetEntry(2366).Dataint01;
            bool needToUseTokens  = DateTime.UtcNow.Subtract(session.Player.PathActivatedTime).TotalSeconds < activateCooldown;

            GenericError CanActivatePath()
            {
                if (needToUseTokens && !clientPathActivate.UseTokens)
                    return GenericError.PathChangeOnCooldown;

                bool hasEnoughTokens = session.AccountCurrencyManager.CanAfford(AccountCurrencyType.ServiceToken, bypassCost); // TODO: Check user has enough tokens
                if (needToUseTokens && clientPathActivate.UseTokens && !hasEnoughTokens)
                    return GenericError.PathChangeInsufficientFunds;

                if (!session.Player.PathManager.IsPathUnlocked(clientPathActivate.Path))
                    return GenericError.PathChangeNotUnlocked;

                if (session.Player.PathManager.IsPathActive(clientPathActivate.Path))
                    return GenericError.PathChangeRequested;

                return GenericError.Ok;
            }

            GenericError result = CanActivatePath();
            if (result != GenericError.Ok)
            {
                session.Player.PathManager.SendServerPathActivateResult(result);
                return;
            }

            if (needToUseTokens && clientPathActivate.UseTokens)
                session.AccountCurrencyManager.CurrencySubtractAmount(AccountCurrencyType.ServiceToken, bypassCost);

            session.Player.PathManager.ActivatePath(clientPathActivate.Path);
        }

        [MessageHandler(GameMessageOpcode.ClientPathUnlock)]
        public static void HandlePathUnlock(WorldSession session, ClientPathUnlock clientPathUnlock)
        {
            uint unlockCost = GameTableManager.Instance.GameFormula.GetEntry(2365).Dataint0;

            GenericError CanUnlockPath()
            {
                bool hasEnoughTokens = session.AccountCurrencyManager.CanAfford(AccountCurrencyType.ServiceToken, unlockCost);
                if (!hasEnoughTokens)
                    return GenericError.PathInsufficientFunds;

                if (session.Player.PathManager.IsPathUnlocked(clientPathUnlock.Path))
                    return GenericError.PathAlreadyUnlocked;

                return GenericError.Ok;
            }

            GenericError result = CanUnlockPath();
            if (result != GenericError.Ok)
            {
                session.Player.PathManager.SendServerPathUnlockResult(result);
                return;
            }

            session.Player.PathManager.UnlockPath(clientPathUnlock.Path);
            session.AccountCurrencyManager.CurrencySubtractAmount(AccountCurrencyType.ServiceToken, unlockCost);
        }

        [MessageHandler(GameMessageOpcode.ClientPathScientistRequestScanbot)]
        public static void HandlePathScientistRequestScanbot(WorldSession session, ClientPathScientistRequestScanbot request)
        {
            Player player = session.Player;
            if (player == null || player.Path != Path.Scientist || player.Map == null)
                return;

            // already out: tell the client again instead of making a second one
            if (player.ScanbotGuid != null && player.GetVisible<Scanbot>(player.ScanbotGuid.Value) != null)
            {
                session.EnqueueMessageEncrypted(new ServerPathScientistScanbotState { ScanbotUnitId = player.ScanbotGuid.Value });
                return;
            }

            PathScientistScanBotProfileEntry profile = GameTableManager.Instance.PathScientistScanBotProfile.GetEntry(request.ScanbotProfile)
                ?? GameTableManager.Instance.PathScientistScanBotProfile.Entries.FirstOrDefault(e => e.Creature2Id != 0u);
            if (profile == null || GameTableManager.Instance.Creature2.GetEntry(profile.Creature2Id) == null)
            {
                Diagnostics.FailureLedger.Record("scanbot", "no usable scanbot profile", $"profile {request.ScanbotProfile}");
                return;
            }

            var scanbot  = new Scanbot(player, profile.Creature2Id);
            var position = new MapPosition { Position = player.Position };
            if (player.Map.CanEnter(scanbot, position))
            {
                player.Map.EnqueueAdd(scanbot, position);
                Diagnostics.FailureLedger.Record("scanbot", "deployed", $"profile {profile.Id} creature {profile.Creature2Id}");
            }
        }

        [MessageHandler(GameMessageOpcode.ClientPathScientistDismissScanbot)]
        public static void HandlePathScientistDismissScanbot(WorldSession session, ClientPathScientistDismissScanbot _)
        {
            DismissScanbot(session);
        }

        [MessageHandler(GameMessageOpcode.ClientPathScientistDismissScanbotPathAction)]
        public static void HandlePathScientistDismissScanbotPathAction(WorldSession session, ClientPathScientistDismissScanbotPathAction _)
        {
            DismissScanbot(session);
        }

        private static void DismissScanbot(WorldSession session)
        {
            Player player = session.Player;
            if (player?.ScanbotGuid == null)
                return;

            player.GetVisible<Scanbot>(player.ScanbotGuid.Value)?.RemoveFromMap();
            player.ScanbotGuid = null;

            uint cooldown = GameTableManager.Instance.PathScientistScanBotProfile.Entries.FirstOrDefault(e => e.Creature2Id != 0u)?.MinCooldownTimeMs ?? 0u;
            session.EnqueueMessageEncrypted(new ServerPathScientistScanbotState { ScanbotUnitId = 0u, ScanbotCooldownMS = cooldown });
            Diagnostics.FailureLedger.Record("scanbot", "dismissed");
        }
    }
}
