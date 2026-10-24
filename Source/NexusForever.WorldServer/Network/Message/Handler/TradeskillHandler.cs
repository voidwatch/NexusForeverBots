using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Tradeskill;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    /// <summary>Tradeskill trainer, talents and crafting. Every refusal is counted in the ledger as tradeskill-msg-refused.</summary>
    public static class TradeskillHandler
    {
        [MessageHandler(GameMessageOpcode.ClientTradeskillLearn)]
        public static void HandleLearn(WorldSession session, ClientTradeskillLearn learn)
        {
            bool ok = session.Player.TradeskillManager.Learn(learn.TradeskillId, learn.DropTradeskillId);
            FailureLedger.Record(ok ? "tradeskill-msg" : "tradeskill-msg-refused", "learn", ok ? $"skill {learn.TradeskillId} drop {learn.DropTradeskillId}"
                : $"skill {learn.TradeskillId} drop {learn.DropTradeskillId}: {session.Player.TradeskillManager.LastRefusal}");
        }

        [MessageHandler(GameMessageOpcode.ClientTradeskillPickTalent)]
        public static void HandlePickTalent(WorldSession session, ClientTradeskillPickTalent pick)
        {
            bool ok = session.Player.TradeskillManager.PickTalent(pick.TradeskillId, pick.TalentTierId, pick.BonusId);
            FailureLedger.Record(ok ? "tradeskill-msg" : "tradeskill-msg-refused", "pick talent", $"skill {pick.TradeskillId} tier {pick.TalentTierId} bonus {pick.BonusId}");
        }

        [MessageHandler(GameMessageOpcode.ClientTradeskillResetTalents)]
        public static void HandleResetTalents(WorldSession session, ClientTradeskillResetTalents reset)
        {
            bool ok = session.Player.TradeskillManager.ResetTalents(reset.TradeskillId);
            FailureLedger.Record(ok ? "tradeskill-msg" : "tradeskill-msg-refused", "reset talents", $"skill {reset.TradeskillId}");
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingCraftItemSimple)]
        public static void HandleCraftSimple(WorldSession session, ClientCraftingCraftItemSimple craft)
        {
            TradeskillManager manager = session.Player.TradeskillManager;
            var schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(craft.SchematicId);
            if (schematic == null || !manager.AtStation(schematic.TradeSkillId, craft.StationUnitId))
            {
                FailureLedger.Record("tradeskill-msg-refused", "craft", schematic == null ? $"unknown schematic {craft.SchematicId}" : $"no crafting station for skill {schematic.TradeSkillId} near the player");
                return;
            }

            // circuit skills (Weaponsmith, Armorer, Outfitter, Tailor): this starts the craft, ClientCraftingComplexCraft finishes it
            if (TradeskillManager.IsCircuit(schematic))
            {
                CraftResult started = manager.StartCircuitCraft(craft.SchematicId);
                FailureLedger.Record(started == CraftResult.Success ? "tradeskill-msg" : "tradeskill-msg-refused", "circuit craft start", $"schematic {craft.SchematicId}: {started}");
                return;
            }

            CraftResult result = CraftResult.Success;
            uint count = System.Math.Clamp(craft.Count, 1u, 100u);
            for (uint i = 0u; i < count && result is CraftResult.Success or CraftResult.Critical; i++)
                result = manager.Craft(craft.SchematicId);
            FailureLedger.Record(result is CraftResult.Success or CraftResult.Critical ? "tradeskill-msg" : "tradeskill-msg-refused", "craft", $"schematic {craft.SchematicId} x{count}: {result}");
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingCraftItem)]
        public static void HandleCraftStart(WorldSession session, ClientCraftingCraftItem craft)
        {
            TradeskillManager manager = session.Player.TradeskillManager;
            var schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(craft.SchematicId);
            if (schematic == null || !manager.AtStation(schematic.TradeSkillId, craft.StationUnitId))
            {
                FailureLedger.Record("tradeskill-msg-refused", "craft start", schematic == null ? $"unknown schematic {craft.SchematicId}" : $"no crafting station for skill {schematic.TradeSkillId} near the player");
                return;
            }

            CraftResult result = manager.StartCraft(craft.SchematicId, craft.CatalystItemId);
            FailureLedger.Record(result == CraftResult.Success ? "tradeskill-msg" : "tradeskill-msg-refused", "craft start", $"schematic {craft.SchematicId}: {result}");
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingAdditive)]
        public static void HandleAdditive(WorldSession session, ClientCraftingAdditive additive)
        {
            bool ok = session.Player.TradeskillManager.AddAdditive(additive.AdditiveItemId, additive.CatalystItemId);
            FailureLedger.Record(ok ? "tradeskill-msg" : "tradeskill-msg-refused", "additive", $"item {additive.AdditiveItemId} catalyst {additive.CatalystItemId}");
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingSimpleCraft)]
        public static void HandleCraftComplete(WorldSession session, ClientCraftingSimpleCraft craft)
        {
            CraftResult result = session.Player.TradeskillManager.CompleteCraft(craft.SchematicId);
            FailureLedger.Record(result is CraftResult.Success or CraftResult.Critical or CraftResult.Failed ? "tradeskill-msg" : "tradeskill-msg-refused", "craft complete", $"schematic {craft.SchematicId}: {result}");
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingComplexCraft)]
        public static void HandleCraftCompleteComplex(WorldSession session, ClientCraftingComplexCraft craft)
        {
            TradeskillManager manager = session.Player.TradeskillManager;
            string charges = craft.Charges == null || craft.Charges.All(c => c == 0u) ? null : $"split {craft.ApSpSplit} charges {string.Join(",", craft.Charges)}";
            CraftResult result = manager.Current?.Circuit == true ? manager.CompleteCircuitCraft(craft.SchematicId, craft.PowerCoreItemId, charges) : manager.CompleteCraft(craft.SchematicId);
            FailureLedger.Record(result is CraftResult.Success or CraftResult.Critical or CraftResult.Failed ? "tradeskill-msg" : "tradeskill-msg-refused", "craft complete", $"schematic {craft.SchematicId} power core {craft.PowerCoreItemId}: {result}");
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingAbandon)]
        public static void HandleAbandon(WorldSession session, ClientCraftingAbandon abandon)
        {
            session.Player.TradeskillManager.AbandonCraft();
            FailureLedger.Record("tradeskill-msg", "abandon");
        }
    }
}
