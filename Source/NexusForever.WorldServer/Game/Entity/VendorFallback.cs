using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Database.World.Model;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// The world database holds no vendor inventories, so creatures whose table description says "vendor" get a generated stock
    /// of credit-priced items near their level (documented in docs/ASSUMPTIONS.md, "Vendor stock").
    /// </summary>
    public static class VendorFallback
    {
        private const int StockSize = 24;
        private static readonly object buildLock = new();
        private static Dictionary<uint, List<uint>> itemsByLevel;

        public static bool IsVendor(Creature2Entry entry)
        {
            string description = entry?.Description;
            if (string.IsNullOrEmpty(description) || description.IndexOf("vendor", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return description.IndexOf("DEPRECATE", StringComparison.OrdinalIgnoreCase) < 0
                && description.IndexOf("For Display", StringComparison.OrdinalIgnoreCase) < 0
                && description.IndexOf("test", StringComparison.OrdinalIgnoreCase) < 0;
        }

        public static EntityModel Build(EntityModel model, Creature2Entry entry)
        {
            Dictionary<uint, List<uint>> byLevel = ItemsByLevel();
            uint level = Math.Max(1u, entry.MinLevel);
            var pool = new List<uint>();
            for (uint l = level > 2u ? level - 2u : 1u; l <= level + 2u; l++)
                if (byLevel.TryGetValue(l, out List<uint> items))
                    pool.AddRange(items);

            var stock = new List<EntityVendorItemModel>();
            if (pool.Count > 0)
            {
                int offset = (int)(entry.Id % (uint)pool.Count);
                for (int i = 0; i < Math.Min(StockSize, pool.Count); i++)
                    stock.Add(new EntityVendorItemModel { Index = (uint)i, CategoryIndex = 0u, ItemId = pool[(offset + i) % pool.Count] });
            }

            return new EntityModel
            {
                Id = model.Id,
                EntityVendor = new EntityVendorModel { Id = model.Id, BuyPriceMultiplier = 1f, SellPriceMultiplier = 1f },
                EntityVendorItem = stock
            };
        }

        private static Dictionary<uint, List<uint>> ItemsByLevel()
        {
            if (itemsByLevel != null)
                return itemsByLevel;

            lock (buildLock)
            {
                itemsByLevel ??= GameTableManager.Instance.Item.Entries
                    .Where(i => i != null && i.BuyFromVendorStackCount > 0u && i.CurrencyTypeId != null && i.CurrencyTypeId[0] == 1u
                        && i.CurrencyAmount[0] > 0u && i.RequiredLevel > 0u && i.ItemQualityId <= 3u && i.Quest2IdActivation == 0u)
                    .GroupBy(i => i.RequiredLevel)
                    .ToDictionary(g => g.Key, g => g.OrderBy(i => i.Id).Select(i => i.Id).ToList());
                return itemsByLevel;
            }
        }
    }
}
