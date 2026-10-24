using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    /// <summary>Rune slot messages (Game/Entity/RuneManager.cs). The item must be the player's own; unknown items go to the ledger.</summary>
    public static class RuneHandler
    {
        private static Item Own(WorldSession session, ulong guid)
        {
            Item item = session.Player?.Inventory.GetItem(guid);
            if (item == null)
                Diagnostics.FailureLedger.Record("rune-refused", "item not in the player's inventory", $"guid {guid}");
            return item;
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingRuneInstall)]
        public static void HandleRuneInstall(WorldSession session, ClientCraftingRuneInstall install)
        {
            Item item = Own(session, install.ItemGuid);
            if (item != null)
                session.Player.RuneManager.Install(item, install.Runes);
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingRuneSlotClear)]
        public static void HandleRuneSlotClear(WorldSession session, ClientCraftingRuneSlotClear clear)
        {
            Item item = Own(session, clear.ItemGuid);
            if (item != null)
                session.Player.RuneManager.Clear(item, clear.Slot);
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingRuneSlotAdd)]
        public static void HandleRuneSlotAdd(WorldSession session, ClientCraftingRuneSlotAdd add)
        {
            Item item = Own(session, add.ItemGuid);
            if (item != null)
                session.Player.RuneManager.AddSlot(item, add.RuneType);
        }

        [MessageHandler(GameMessageOpcode.ClientCraftingRuneSlotReroll)]
        public static void HandleRuneSlotReroll(WorldSession session, ClientCraftingRuneSlotReroll reroll)
        {
            Item item = Own(session, reroll.ItemGuid);
            if (item != null)
                session.Player.RuneManager.Reroll(item, reroll.Slot, reroll.RuneType);
        }
    }
}
