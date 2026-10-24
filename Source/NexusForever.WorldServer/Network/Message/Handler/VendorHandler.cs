using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Network.Message.Model;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Database.World.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class VendorHandler
    {   
        public static void HandleClientVendor(WorldSession session, ClientEntityInteract vendor)
        {
            var vendorEntity = session.Player.Map.GetEntity<NonPlayer>(vendor.Guid);
            if (vendorEntity == null)
            {
                Diagnostics.FailureLedger.Record("vendor-interact-no-entity", "vendor interact with a guid that is not on the map");
                return;
            }

            if (vendorEntity.VendorInfo == null)
            {
                Diagnostics.FailureLedger.Record("vendor-interact-not-a-vendor", $"creature {vendorEntity.CreatureId}");
                return;
            }

            session.Player.SelectedVendorInfo = vendorEntity.VendorInfo;
            var serverVendor = new ServerVendorItemsUpdated
            {
                Guid = vendor.Guid,
                SellPriceMultiplier = vendorEntity.VendorInfo.SellPriceMultiplier,
                BuyPriceMultiplier = vendorEntity.VendorInfo.BuyPriceMultiplier,
                Unknown2 = true,
                Unknown3 = true,
                Unknown4 = false
            };

            foreach (EntityVendorCategoryModel category in vendorEntity.VendorInfo.Categories)
            {
                serverVendor.Categories.Add(new ServerVendorItemsUpdated.Category
                {
                    Index = category.Index,
                    LocalisedTextId = category.LocalisedTextId
                });
            }
            foreach (EntityVendorItemModel item in vendorEntity.VendorInfo.Items)
            {
                serverVendor.Items.Add(new ServerVendorItemsUpdated.Item
                {
                    Index = item.Index,
                    ItemId = item.ItemId,
                    CategoryIndex = item.CategoryIndex,
                    Unknown6 = 0,
                    UnknownB = new[]
                    {
                        new ServerVendorItemsUpdated.Item.UnknownItemStructure(),
                        new ServerVendorItemsUpdated.Item.UnknownItemStructure()
                    }
                });
            }
            session.EnqueueMessageEncrypted(serverVendor);
        }

        [MessageHandler(GameMessageOpcode.ClientVendorPurchase)]
        public static void HandleVendorPurchase(WorldSession session, ClientVendorPurchase vendorPurchase)
        {
            VendorInfo vendorInfo = session.Player.SelectedVendorInfo;
            if (vendorInfo == null)
            {
                Diagnostics.FailureLedger.Record("vendor-purchase-no-vendor", "purchase without a selected vendor");
                return;
            }

            EntityVendorItemModel vendorItem = vendorInfo.GetItemAtIndex(vendorPurchase.VendorIndex);
            if (vendorItem == null)
            {
                Diagnostics.FailureLedger.Record("vendor-purchase-bad-index", $"index {vendorPurchase.VendorIndex}");
                return;
            }

            Item2Entry itemEntry = GameTableManager.Instance.Item.GetEntry(vendorItem.ItemId);
            float costMultiplier = vendorInfo.BuyPriceMultiplier * vendorPurchase.VendorItemQty;

            // do all sanity checks before modifying currency
            var currencyChanges = new List<(CurrencyType CurrencyTypeId, ulong CurrencyAmount)>();
            for (int i = 0; i < itemEntry.CurrencyTypeId.Length; i++)
            {
                CurrencyType currencyId = (CurrencyType)itemEntry.CurrencyTypeId[i];
                if (currencyId == CurrencyType.None)
                    continue;

                ulong currencyAmount = (ulong)(itemEntry.CurrencyAmount[i] * costMultiplier);
                if (!session.Player.CurrencyManager.CanAfford(currencyId, currencyAmount))
                {
                    Diagnostics.FailureLedger.Record("vendor-purchase-cannot-afford", $"item {itemEntry.Id} currency {currencyId}");
                    return;
                }

                currencyChanges.Add((currencyId, currencyAmount));
            }
            
            if (!session.Player.Inventory.HasFreeSlot())
            {
                session.EnqueueMessageEncrypted(new ServerItemError { ErrorCode = NexusForever.WorldServer.Game.Static.GenericError.ItemInventoryFull });
                Diagnostics.FailureLedger.Record("vendor-purchase-inventory-full", $"item {itemEntry.Id}");
                return;
            }

            foreach ((CurrencyType currencyTypeId, ulong currencyAmount) in currencyChanges)
                session.Player.CurrencyManager.CurrencySubtractAmount(currencyTypeId, currencyAmount);

            session.Player.Inventory.ItemCreate(InventoryLocation.Inventory, itemEntry.Id, vendorPurchase.VendorItemQty * itemEntry.BuyFromVendorStackCount);
        }

        [MessageHandler(GameMessageOpcode.ClientSellItemToVendor)]
        public static void HandleVendorSell(WorldSession session, ClientVendorSell vendorSell)
        {
            VendorInfo vendorInfo = session.Player.SelectedVendorInfo;
            if (vendorInfo == null)
                return;

            SellItem(session, vendorInfo, vendorSell.ItemLocation, vendorSell.Quantity);
        }

        /// <summary>
        /// SellJunkToVendor (0x0167): every backpack item of category Junk (Item2Category 94; the client's vendor UI counts junk with
        /// GetItemCategory() == Item.CodeEnumItem2Category.Junk over the bag) is sold to the open vendor with its whole stack.
        /// </summary>
        [MessageHandler(GameMessageOpcode.ClientSellJunkToVendor)]
        public static void HandleSellJunkToVendor(WorldSession session, ClientSellJunkToVendor sellJunk)
        {
            VendorInfo vendorInfo = session.Player.SelectedVendorInfo;
            if (vendorInfo == null)
            {
                Diagnostics.FailureLedger.Record("vendor-sell-junk-refused", "no vendor open");
                return;
            }

            var junk = session.Player.Inventory.GetItems(InventoryLocation.Inventory)
                .Where(i => i?.Info?.Entry != null && i.Info.Entry.Item2CategoryId == JunkCategory)
                .Select(i => (new Network.Message.Model.Shared.ItemLocation { Location = i.Location, BagIndex = i.BagIndex }, i.StackCount))
                .ToList();
            foreach ((Network.Message.Model.Shared.ItemLocation location, uint count) in junk)
                SellItem(session, vendorInfo, location, count);
            Diagnostics.FailureLedger.Record("vendor-sell-junk", junk.Count > 0 ? "sold" : "nothing to sell", $"{junk.Count} stacks");
        }

        public const uint JunkCategory = 94u;

        private static void SellItem(WorldSession session, VendorInfo vendorInfo, Network.Message.Model.Shared.ItemLocation itemLocation, uint quantity)
        {
            var vendorSell = new { ItemLocation = itemLocation, Quantity = quantity };
            ItemInfo info = session.Player.Inventory.GetItem(vendorSell.ItemLocation)?.Info;
            if (info == null)
            {
                Diagnostics.FailureLedger.Record("vendor-sell-no-item", $"location {vendorSell.ItemLocation.Location} index {vendorSell.ItemLocation.BagIndex}");
                return;
            }

            float costMultiplier = vendorInfo.SellPriceMultiplier * vendorSell.Quantity;

            // do all sanity checks before modifying currency
            var currencyChange = new List<(CurrencyType CurrencyTypeId, ulong CurrencyAmount)>();
            for (int i = 0; i < info.Entry.CurrencyTypeIdSellToVendor.Length; i++)
            {
                CurrencyType currencyId = (CurrencyType)info.Entry.CurrencyTypeIdSellToVendor[i];
                if (currencyId == CurrencyType.None)
                    continue;

                ulong currencyAmount = (ulong)(info.Entry.CurrencyAmountSellToVendor[i] * costMultiplier);
                currencyChange.Add((currencyId, currencyAmount));
            }

            // TODO Insert calculation for cost here

            foreach ((CurrencyType currencyTypeId, ulong currencyAmount) in currencyChange)
                session.Player.CurrencyManager.CurrencyAddAmount(currencyTypeId, currencyAmount);

            // TODO Figure out why this is showing "You deleted [item]"
            Item soldItem = session.Player.Inventory.ItemDelete(vendorSell.ItemLocation);
            BuybackManager.Instance.AddItem(session.Player, soldItem, vendorSell.Quantity, currencyChange);
        }

        /// <summary>
        /// 0x014C RepairItemVendor (item guid) / RepairAllItemsVendor (guid 0): pays the client's repair cost (<see cref="ItemDurability.RepairCost"/>)
        /// in credits and restores durability, with the vendor window open.
        /// </summary>
        [MessageHandler(GameMessageOpcode.ClientVendorRepair)]
        public static void HandleVendorRepair(WorldSession session, ClientVendorRepair repair)
        {
            Player player = session.Player;
            if (player.SelectedVendorInfo == null)
            {
                Diagnostics.FailureLedger.Record("vendor-repair", "refused: no vendor open");
                return;
            }

            List<Item> items;
            if (repair.ItemGuid != 0ul)
            {
                Item item = player.Inventory.GetItem(repair.ItemGuid);
                if (item == null || !ItemDurability.HasDurability(item))
                {
                    Diagnostics.FailureLedger.Record("vendor-repair", "refused: item missing or without durability", $"guid {repair.ItemGuid}");
                    return;
                }
                items = new List<Item> { item };
            }
            else
                items = ItemDurability.Damaged(player).ToList();

            ulong cost = 0ul;
            foreach (Item item in items)
                cost += ItemDurability.RepairCost(item);

            if (cost > 0ul && !player.CurrencyManager.CanAfford(CurrencyType.Credits, cost))
            {
                Diagnostics.FailureLedger.Record("vendor-repair", "refused: cannot afford", $"{cost} credits for {items.Count} items");
                return;
            }

            if (cost > 0ul)
                player.CurrencyManager.CurrencySubtractAmount(CurrencyType.Credits, cost);
            foreach (Item item in items)
                ItemDurability.Set(player, item, 1f);

            Diagnostics.FailureLedger.Record("vendor-repair", "repaired", $"{items.Count} items, {cost} credits{(items.Any(i => ItemDurability.Value(i) == 0ul) ? ", some without a table value" : "")}");
        }

        [MessageHandler(GameMessageOpcode.ClientBuybackItemFromVendor)]
        public static void HandleBuybackItemFromVendor(WorldSession session, ClientBuybackItemFromVendor buybackItemFromVendor)
        {
            BuybackItem buybackItem = BuybackManager.Instance.GetItem(session.Player, buybackItemFromVendor.UniqueId);
            if (buybackItem == null)
            {
                Diagnostics.FailureLedger.Record("vendor-buyback-no-item", $"unique id {buybackItemFromVendor.UniqueId}");
                return;
            }

            //TODO Ensure player has room in inventory

            // do all sanity checks before modifying currency
            foreach ((CurrencyType currencyTypeId, ulong currencyAmount) in buybackItem.CurrencyChange)
                if (!session.Player.CurrencyManager.CanAfford(currencyTypeId, currencyAmount))
                {
                    Diagnostics.FailureLedger.Record("vendor-buyback-cannot-afford", $"currency {currencyTypeId}");
                    return;
                }

            foreach ((CurrencyType currencyTypeId, ulong currencyAmount) in buybackItem.CurrencyChange)
                session.Player.CurrencyManager.CurrencySubtractAmount(currencyTypeId, currencyAmount);

            session.Player.Inventory.AddItem(buybackItem.Item, InventoryLocation.Inventory);
            BuybackManager.Instance.RemoveItem(session.Player, buybackItem);
        }
    }
}
