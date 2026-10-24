using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Prerequisite;
using NexusForever.WorldServer.Game.Spell;
using NexusForever.WorldServer.Game.Static;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class ItemHandler
    {
        [MessageHandler(GameMessageOpcode.ClientItemMove)]
        public static void HandleItemMove(WorldSession session, ClientItemMove itemMove)
        {
            Item item = session.Player.Inventory.GetItem(itemMove.From);
            if (item == null)
                throw new InvalidPacketValueException();

            GenericError? result = session.Player.Inventory.CanMoveItem(item, itemMove.To);
            if (result.HasValue)
            {
                session.EnqueueMessageEncrypted(new ServerItemError
                {
                    ItemGuid  = item.Guid,
                    ErrorCode = result.Value
                });
                return;
            }

            session.Player.Inventory.ItemMove(item, itemMove.To);
        }

        [MessageHandler(GameMessageOpcode.ClientItemSplit)]
        public static void HandleItemSplit(WorldSession session, ClientItemSplit itemSplit)
        {
            session.Player.Inventory.ItemSplit(itemSplit.Guid, itemSplit.Location, itemSplit.Count);
        }

        /// <summary>ClientItemSalvage 0x015E: salvage one item (Game/Tradeskill/Salvage.cs). The guid must match the item at the location.</summary>
        [MessageHandler(GameMessageOpcode.ClientItemSalvage)]
        public static void HandleItemSalvage(WorldSession session, ClientItemSalvage salvage)
        {
            Item item = session.Player.Inventory.GetItem(salvage.Location);
            if (item == null || (salvage.Guid != 0ul && item.Guid != salvage.Guid))
            {
                NexusForever.WorldServer.Diagnostics.FailureLedger.Record("salvage-refused", "no such item", $"location {salvage.Location.Location} {salvage.Location.BagIndex} guid {salvage.Guid}");
                session.Player.SendGenericError(GenericError.ItemBadId);
                return;
            }
            Game.Tradeskill.Salvage.SalvageItem(session.Player, item);
        }

        [MessageHandler(GameMessageOpcode.ClientItemDelete)]
        public static void HandleItemDelete(WorldSession session, ClientItemDelete itemDelete)
        {
            session.Player.Inventory.ItemDelete(itemDelete.From);
        }

        [MessageHandler(GameMessageOpcode.ClientItemUse)]
        public static void HandleItemUse(WorldSession session, ClientItemUse itemUse)
        {
            Item item = session.Player.Inventory.GetItem(itemUse.Location);
            if (item == null)
                throw new InvalidPacketValueException();

            ItemSpecialEntry itemSpecial = GameTableManager.Instance.ItemSpecial.GetEntry(item.Info.Entry.ItemSpecialId00);
            if (itemSpecial == null)
                throw new InvalidPacketValueException();

            if (itemSpecial.Spell4IdOnActivate > 0u)
            {
                if (itemSpecial.PrerequisiteIdGeneric00 > 0 && !PrerequisiteManager.Instance.Meets(session.Player, itemSpecial.PrerequisiteIdGeneric00))
                {
                    session.Player.SendGenericError(Game.Static.GenericError.UnlockItemFailed); // TODO: Confirm right error message.
                    return;
                }

                if (session.Player.Inventory.ItemUse(item))
                {
                    session.Player.CastSpell(itemSpecial.Spell4IdOnActivate, new SpellParameters
                    {
                        PrimaryTargetId = itemUse.TargetUnitId,
                        Position        = itemUse.Position
                    });
                }
            }
        }

        [MessageHandler(GameMessageOpcode.ClientItemGenericUnlock)]
        public static void HandleItemGenericUnlock(WorldSession session, ClientItemGenericUnlock itemGenericUnlock)
        {
            Item item = session.Player.Inventory.GetItem(itemGenericUnlock.Location);
            if (item == null)
                throw new InvalidPacketValueException();

            // Item2.GenericUnlockSetId is a GenericUnlockSet id (all 418 such items are dye unlocks; 39 of their sets list an entry id that
            // differs from the set id, so the set is resolved instead of reading the set id as an entry id)
            GenericUnlockSetEntry set = GameTableManager.Instance.GenericUnlockSet.GetEntry(item.Info.Entry.GenericUnlockSetId);
            if (set == null)
                throw new InvalidPacketValueException();

            var entries = new[] { set.GenericUnlockEntryId00, set.GenericUnlockEntryId01, set.GenericUnlockEntryId02, set.GenericUnlockEntryId03,
                    set.GenericUnlockEntryId04, set.GenericUnlockEntryId05 }
                .Select(id => GameTableManager.Instance.GenericUnlockEntry.GetEntry(id))
                .Where(e => e != null)
                .ToList();
            if (entries.Count == 0)
                throw new InvalidPacketValueException();

            // everything in the set is already unlocked: the item is kept
            if (entries.All(e => session.GenericUnlockManager.IsUnlocked((GenericUnlockType)e.GenericUnlockTypeEnum, e.UnlockObject)))
            {
                session.GenericUnlockManager.SendUnlockResult(GenericUnlockResult.AlreadyUnlocked);
                Diagnostics.FailureLedger.Record("generic-unlock", "refused: already unlocked", $"set {set.Id}");
                return;
            }

            if (!session.Player.Inventory.ItemUse(item))
            {
                Diagnostics.FailureLedger.Record("generic-unlock", "refused: item use failed", $"item {item.Info.Id}");
                return;
            }

            foreach (GenericUnlockEntryEntry entry in entries)
                if (!session.GenericUnlockManager.IsUnlocked((GenericUnlockType)entry.GenericUnlockTypeEnum, entry.UnlockObject))
                    session.GenericUnlockManager.Unlock((ushort)entry.Id);
            Diagnostics.FailureLedger.Record("generic-unlock", "unlocked", $"set {set.Id}, {entries.Count} entries");
        }

        [MessageHandler(GameMessageOpcode.ClientItemUseDecor)]
        public static void HandleItemUseDecor(WorldSession session, ClientItemUseDecor itemUseDecor)
        {
            Item item = session.Player.Inventory.GetItem(itemUseDecor.ItemGuid);
            if (item == null)
                throw new InvalidPacketValueException();

            HousingDecorInfoEntry entry = GameTableManager.Instance.HousingDecorInfo.GetEntry(item.Info.Entry.HousingDecorInfoId);
            if (entry == null)
                throw new InvalidPacketValueException();

            if (session.Player.Inventory.ItemUse(item))
                session.Player.ResidenceManager.DecorCreate(entry);
        }

        [MessageHandler(GameMessageOpcode.ClientItemMoveToSupplySatchel)]
        public static void HandleClientItemMoveToSupplySatchel(WorldSession session, ClientItemMoveToSupplySatchel moveToSupplySatchel)
        {
            Item item = session.Player.Inventory.GetItem(moveToSupplySatchel.ItemGuid);
            if (item == null)
                throw new InvalidPacketValueException();

            session.Player.Inventory.ItemMoveToSupplySatchel(item, moveToSupplySatchel.Amount);
        }

        [MessageHandler(GameMessageOpcode.ClientItemMoveFromSupplySatchel)]
        public static void HandleClientItemMoveFromSupplySatchel(WorldSession session, ClientItemMoveFromSupplySatchel request)
        {
            session.Player.SupplySatchelManager.MoveToInventory(request.MaterialId, request.Amount);
        }
    }
}
