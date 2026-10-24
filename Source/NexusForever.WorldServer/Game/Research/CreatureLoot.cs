using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Research
{
    /// <summary>
    /// Items and credits a killed creature gives its killer. Researched drop chances where a page exists, otherwise a generic
    /// level based roll from the item table (assumptions in docs/ASSUMPTIONS.md).
    /// </summary>
    public static class CreatureLoot
    {
        private static readonly Random random = new();
        private static Dictionary<int, List<uint>> genericByLevel;
        private static readonly object gate = new();

        public static void Roll(NonPlayer creature, Player killer)
        {
            if (killer == null || creature.Level == 0)
                return;

            // a raid boss already killed this week gives nothing to the locked player
            if (creature.Map is Map.MapInstance lootMap && Encounters.EncounterManager.Instance.IsBoss(creature)
                && GameTableManager.Instance.World.GetEntry(lootMap.Entry.Id)?.Type == (uint)Map.Static.MapType.Raid
                && !Groups.InstanceLockouts.TryLockRaidBoss(killer.CharacterId, (ushort)lootMap.Entry.Id, creature.CreatureId))
                return;

            var info = CreatureResearch.Get(creature.CreatureId, (int)creature.Level);
            // one kind per boss: the category header counts bosses whose loot comes from a researched drop table and bosses on the generic fallback
            if (Encounters.EncounterManager.Instance.IsBoss(creature))
                Diagnostics.FailureLedger.Record(info != null && info.Drops.Count > 0 ? "loot-boss-sourced" : "loot-boss-generic",
                    Encounters.EncounterManager.Instance.Get(creature)?.Boss ?? creature.CreatureId.ToString(), $"{info?.Drops.Count ?? 0} drop rows");
            if (info != null && info.Drops.Count > 0)
            {
                foreach (var (itemId, chance) in info.Drops)
                    if (random.NextDouble() * 100d < chance)
                        Give(creature, killer, itemId, "researched");
            }
            else
            {
                // no data: a small chance of one level appropriate piece of gear, better odds for elites
                double chance = info?.Eliteness is "Group" or "Raid" ? 0.5d : info?.Rank is "Superior" or "Prime" ? 0.25d : 0.08d;
                if (random.NextDouble() < chance)
                {
                    uint item = GenericItem((int)creature.Level);
                    if (item != 0u)
                        Give(creature, killer, item, "generic");
                }
            }

            // credits: about level times 2 to 5, more for elites (assumption, no data)
            ulong credits = (ulong)Math.Max(1d, creature.Level * (2d + random.NextDouble() * 3d) * (info?.Eliteness is "Group" or "Raid" ? 4d : 1d));
            killer.CurrencyManager.CurrencyAddAmount(CurrencyType.Credits, credits, true);
        }

        private static void Give(NonPlayer creature, Player player, uint itemId, string source)
        {
            ItemInfo item = ItemManager.Instance.GetItemInfo(itemId);
            if (item == null)
            {
                Diagnostics.FailureLedger.Record("loot-unknown-item", $"item {itemId} ({source})");
                return;
            }

            // need/greed and master loot wait in a loot window on the corpse; the other rules pick the looter now
            if (Groups.LootWindow.TryOpen(player, creature, item))
                return;

            player = Groups.GroupLoot.Choose(player, item);

            // a full bag drops the item; the inventory error packet is only useful for humans
            if (!player.Inventory.HasFreeSlot() && player.Session is Bots.BotSession && player.Inventory.DiscardJunk())
                Diagnostics.FailureLedger.Record("loot-bot-discard", "bot bag full, lowest gear item deleted");

            if (!player.Inventory.HasFreeSlot())
            {
                Diagnostics.FailureLedger.Record("loot-bag-full", $"item {itemId}", player.Session is Bots.BotSession ? "bot" : "human");
                return;
            }

            player.Inventory.ItemCreate(InventoryLocation.Inventory, item, 1u, ItemUpdateReason.Loot);
            Diagnostics.FailureLedger.Record("loot", $"{source} item drop");
        }

        private static uint GenericItem(int level)
        {
            lock (gate)
            {
                genericByLevel ??= GameTableManager.Instance.Item.Entries
                    .Where(i => (i.Item2FamilyId == 1u || i.Item2FamilyId == 2u) && i.ItemQualityId is 2u or 3u && i.RequiredLevel > 0u && i.ClassRequired == 0u && i.PrerequisiteId == 0u)
                    .GroupBy(i => (int)i.RequiredLevel)
                    .ToDictionary(g => g.Key, g => g.Select(i => i.Id).ToList());

                for (int l = level; l >= Math.Max(1, level - 3); l--)
                    if (genericByLevel.TryGetValue(l, out var list) && list.Count > 0)
                        return list[random.Next(list.Count)];
                return 0u;
            }
        }
    }
}
