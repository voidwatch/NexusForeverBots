using System;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Rewards;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    /// <summary>
    /// Instance settings and resets, group loot roll choices, resource conversion and the daily login claim
    /// (layouts from the retail client, docs/PROTOCOL.md "Client binary static analysis").
    /// </summary>
    public static class InstanceHandler
    {
        [MessageHandler(GameMessageOpcode.ClientSetInstanceSettings)]
        public static void HandleSetInstanceSettings(WorldSession session, ClientSetInstanceSettings message)
        {
            InstanceSettings.Set(session.Player, message.Difficulty, message.PrimeLevel, message.Rally);
        }

        [MessageHandler(GameMessageOpcode.ClientClosedInstanceSettings)]
        public static void HandleClosedInstanceSettings(WorldSession session, ClientClosedInstanceSettings message)
        {
            FailureLedger.Record("instance-settings-closed", "dialog closed without a choice");
        }

        [MessageHandler(GameMessageOpcode.ClientResetInstances)]
        public static void HandleResetInstances(WorldSession session, ClientResetInstances message)
        {
            InstanceSettings.Reset(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientResetSingleInstance)]
        public static void HandleResetSingleInstance(WorldSession session, ClientResetSingleInstance message)
        {
            // the portal unit names the instance; portals are not mapped to their destination world here
            FailureLedger.Record("instance-reset-single-unmapped", "portal unit not mapped to a world");
        }

        // GameLib.RollOnLoot / PassOnLoot and AssignMasterLoot answer a loot window (Game/Groups/LootWindow)
        [MessageHandler(GameMessageOpcode.ClientLootRollAction)]
        public static void HandleLootRollAction(WorldSession session, ClientLootRollAction message)
        {
            Game.Groups.LootWindow.Roll(session.Player, message.OwnerUnitId, message.LootUnitId, message.Action);
        }

        [MessageHandler(GameMessageOpcode.ClientLootAssignMaster)]
        public static void HandleLootAssignMaster(WorldSession session, ClientLootAssignMaster message)
        {
            Game.Groups.LootWindow.AssignMaster(session.Player, message.OwnerUnitId, message.LootUnitId, message.Assignee.Id);
        }

        /// <summary>Count is in source units (the UI sends batches x SourceCount); each batch is one <see cref="ResourceConversion.Convert"/>.</summary>
        [MessageHandler(GameMessageOpcode.ClientResourceConversion)]
        public static void HandleResourceConversion(WorldSession session, ClientResourceConversion message)
        {
            var row = GameTableManager.Instance.ResourceConversion.GetEntry(message.ConversionId);
            if (row == null)
            {
                FailureLedger.Record("conversion-msg-refused", "unknown conversion row");
                return;
            }

            if (!ResourceConversion.NpcInReach(session.Player, row.Id))
            {
                FailureLedger.Record("conversion-msg-refused", "no conversion NPC offering the row in reach", $"row {row.Id}");
                return;
            }

            uint batches = Math.Min(1000u, Math.Max(1u, message.Count / Math.Max(1u, row.SourceCount)));
            for (uint i = 0u; i < batches; i++)
                if (ResourceConversion.Convert(session.Player, row.Id) != ConversionResult.Done)
                    break;
        }

        [MessageHandler(GameMessageOpcode.ClientDailyLoginRewardsRequest)]
        public static void HandleDailyLoginRewardsRequest(WorldSession session, ClientDailyLoginRewardsRequest message)
        {
            DateTime now = DateTime.UtcNow;
            if (!session.Player.DailyLoginManager.CanClaim(now))
            {
                FailureLedger.Record("daily-login-msg", "already claimed today");
                return;
            }

            session.Player.DailyLoginManager.Claim(now);
        }
    }
}
