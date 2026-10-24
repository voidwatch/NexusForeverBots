using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Combat;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Static;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Bots put on the best gear they carry (quest rewards, loot, vendor items), so a soak exercises equipped item stats: an item is better
    /// when its assault + support rating (shields: capacity) beats the item in that slot. The bot must meet the item's required level, class and
    /// the armor/weapon proficiency of its class (Class.StartingItemProficiencies against the category's ItemProficiency bit).
    /// Ledger: bot-gear-equipped (slot), bot-gear-refused (why an equip failed).
    /// </summary>
    public static class BotGear
    {
        /// <summary>Runes the bot carries go into fitting empty slots of its equipped gear, through ClientCraftingRuneInstall.</summary>
        private static void InstallRunes(Player me)
        {
            var runes = me.Inventory.GetItems(InventoryLocation.Inventory)
                .Where(i => i?.Info?.Entry != null && Game.Entity.RuneManager.RuneElement(i.Info.Entry) != 0u && i.Info.Entry.RequiredLevel <= me.Level)
                .Select(i => i.Info.Entry).ToList();
            if (runes.Count == 0)
                return;

            foreach (Item gear in me.Inventory.GetItems(InventoryLocation.Equipped).ToList())
            {
                var slots = me.RuneManager.Get(gear);
                if (slots == null)
                    continue;
                var wanted = new uint[slots.Runes.Count];
                bool any = false;
                for (int i = 0; i < slots.Runes.Count; i++)
                {
                    if (slots.Runes[i] != 0u)
                        continue;
                    var rune = runes.FirstOrDefault(r => Game.Entity.RuneManager.Fits(Game.Entity.RuneManager.RuneElement(r), slots.Types[i]));
                    if (rune == null)
                        continue;
                    wanted[i] = rune.Id;
                    runes.Remove(rune);
                    any = true;
                }
                if (!any)
                    continue;

                Network.Message.Handler.RuneHandler.HandleRuneInstall(me.Session, BotPackets.Build<Network.Message.Model.ClientCraftingRuneInstall>(w =>
                {
                    w.Write(gear.Guid);
                    w.Write((uint)wanted.Length);
                    foreach (uint id in wanted)
                        w.Write(id);
                }));
                return;
            }
        }

        public static void Think(Player me)
        {
            MakeRoom(me, 4u);
            try
            {
                EquipBest(me);
                InstallRunes(me);
            }
            catch (Exception ex)
            {
                Diagnostics.FailureLedger.Record("bot-gear-exception", ex.GetType().Name, ex.Message);
            }
        }

        private static HashSet<uint> questItems;

        /// <summary>
        /// Free bag slots the way a player would (selling or destroying loot): with fewer than <paramref name="needed"/> free slots, items no quest
        /// uses (not pushed by a quest, not a CollectItem objective, not a quest starter) are destroyed, lowest quality first. Bots never sold loot, so
        /// their 16-slot bags filled and QuestAdd refused every quest that pushes an item (forced sweep: 21 quests "inventory slots 0").
        /// Ledger: bot-bag-cleared.
        /// </summary>
        public static void MakeRoom(Player me, uint needed)
        {
            try
            {
                if (me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) >= needed)
                    return;

                if (questItems == null)
                {
                    var set = new HashSet<uint>();
                    foreach (Game.Quest.QuestInfo info in Game.Quest.GlobalQuestManager.Instance.GetAllQuestInfo())
                    {
                        set.UnionWith(info.Entry.PushedItemIds.Where(i => i != 0u));
                        set.UnionWith(info.Objectives.Where(o => o.Type == Game.Quest.Static.QuestObjectiveType.CollectItem).Select(o => o.Entry.Data));
                    }
                    questItems = set;
                }

                var junk = me.Inventory.GetItems(InventoryLocation.Inventory)
                    .Where(i => i?.Info?.Entry != null && !questItems.Contains(i.Info.Entry.Id) && i.Info.Entry.Quest2IdActivation == 0u)
                    .OrderBy(i => i.Info.Entry.ItemQualityId)
                    .ToList();
                foreach (Item item in junk)
                {
                    if (me.Inventory.GetInventorySlotsRemaining(InventoryLocation.Inventory) >= needed)
                        break;
                    me.Inventory.ItemDelete(new Network.Message.Model.Shared.ItemLocation { Location = InventoryLocation.Inventory, BagIndex = item.BagIndex });
                    Diagnostics.FailureLedger.Record("bot-bag-cleared", "destroyed an item no quest uses to free a bag slot", $"item {item.Info.Entry.Id}");
                }
            }
            catch (Exception ex)
            {
                Diagnostics.FailureLedger.Record("bot-gear-exception", ex.GetType().Name, ex.Message);
            }
        }

        private static void EquipBest(Player me)
        {
            Dictionary<uint, Item> equipped = me.Inventory.GetItems(InventoryLocation.Equipped).Where(i => i != null).ToDictionary(i => i.BagIndex);
            ClassEntry classEntry = GameTableManager.Instance.Class.GetEntry((ulong)me.Class);

            var best = new Dictionary<uint, (Item Item, float Score)>();
            foreach (Item item in me.Inventory.GetItems(InventoryLocation.Inventory).ToList())
            {
                if (item?.Info?.SlotEntry == null || !item.Info.IsEquippable() || item.Info.IsEquippableBag())
                    continue;

                float score = ItemStats.Score(item.Info.Entry);
                if (score <= 0f || !CanUse(me, classEntry, item.Info))
                    continue;

                int bit = BitIndex(item.Info.SlotEntry.EquippedSlotFlags);
                if (bit < 0 || bit > (int)EquippedItem.WeaponPrimary)
                    continue;

                uint slot = (uint)bit;
                // a worn item the bot may no longer use (level rotation went below its required level) gives no stats, so anything usable beats it
                float current = equipped.TryGetValue(slot, out Item worn) && CanUse(me, classEntry, worn.Info) ? ItemStats.Score(worn.Info.Entry) : 0f;
                if (score <= current || (best.TryGetValue(slot, out var other) && other.Score >= score))
                    continue;
                best[slot] = (item, score);
            }

            foreach ((uint slot, (Item item, float _)) in best)
            {
                GenericError? error = me.Inventory.CanMoveItem(item, InventoryLocation.Equipped, slot);
                if (error != null)
                {
                    Diagnostics.FailureLedger.Record("bot-gear-refused", $"{error}", $"item {item.Id} slot {(EquippedItem)slot}");
                    continue;
                }

                me.Inventory.ItemMove(item, InventoryLocation.Equipped, slot);
                Diagnostics.FailureLedger.Record("bot-gear-equipped", $"{(EquippedItem)slot}", $"item {item.Id} ilvl {item.Info.Entry.PowerLevel}");
                Diagnostics.BotFeed.Add(me.Name, me.Level, me.Zone != null ? $"{me.Zone.Id}" : "-", $"equipped item {item.Id} ({(EquippedItem)slot}, ilvl {item.Info.Entry.PowerLevel})");
            }
        }

        private static bool CanUse(Player me, ClassEntry classEntry, ItemInfo info)
        {
            Item2Entry entry = info.Entry;
            if (entry.RequiredLevel > me.Level)
                return false;
            if (entry.ClassRequired != 0u && entry.ClassRequired != (uint)me.Class)
                return false;

            uint proficiency = info.CategoryEntry?.ItemProficiencyId ?? 0u;
            if (proficiency == 0u || classEntry == null)
                return true;
            uint mask = GameTableManager.Instance.ItemProficiency.GetEntry(proficiency)?.BitMask ?? 0u;
            return mask == 0u || (classEntry.StartingItemProficiencies & mask) != 0u;
        }

        private static int BitIndex(uint flags)
        {
            if (flags == 0u || (flags & (flags - 1u)) != 0u)
                return -1;
            int index = 0;
            while ((flags >>= 1) != 0u)
                index++;
            return index;
        }
    }
}
