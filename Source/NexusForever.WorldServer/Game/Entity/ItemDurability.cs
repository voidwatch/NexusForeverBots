using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Item durability and repair cost, read from the retail client (docs/ASSUMPTIONS.md "Durability and repair"):
    /// <list type="bullet">
    /// <item>only armor and weapons (Item2Family 1 and 2) have durability (client code, family check on the item info);</item>
    /// <item>max durability points = GameFormula 559 dataint01 (20) + the item's power level (same fn; Lua GetPowerLevel reads the same field;
    /// wiki tooltips: Life and Death power level 8 = 28, Biomimetic Receptive Polyboots power level 50 = 70);</item>
    /// <item>current points = max * durability fraction + 0.5, at least 1 unless the fraction is 0 (client code);</item>
    /// <item>repair cost = GameFormula 559 datafloat03 (0.25) * missing points / max points * item value + 0.5 (client code); the item
    /// value is the item's credit sell price.</item>
    /// </list>
    /// The source of durability loss is server side and not in any table or document we have, so loss on death is off unless
    /// NF_DURABILITY_DEATH_LOSS (fraction per death) is set.
    /// </summary>
    public static class ItemDurability
    {
        private const uint FormulaId = 559u;

        public static readonly float DeathLoss = float.TryParse(Environment.GetEnvironmentVariable("NF_DURABILITY_DEATH_LOSS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float loss) ? Math.Clamp(loss, 0f, 1f) : 0f;

        private static GameFormulaEntry Formula => GameTableManager.Instance.GameFormula.GetEntry(FormulaId);

        public static bool HasDurability(Item item)
        {
            Item2Entry entry = item?.Info?.Entry;
            return entry != null && (entry.Item2FamilyId == 1u || entry.Item2FamilyId == 2u);
        }

        public static uint MaxPoints(Item item)
        {
            if (!HasDurability(item))
                return 0u;
            return (Formula?.Dataint01 ?? 0u) + item.Info.Entry.PowerLevel;
        }

        public static uint CurrentPoints(Item item)
        {
            uint max = MaxPoints(item);
            if (max == 0u || item.Durability <= 0f)
                return 0u;
            return Math.Max(1u, (uint)(max * item.Durability + 0.5f));
        }

        /// <summary>Credit value the repair cost scales with: the item's credit sell price (0 when the table has none).</summary>
        public static ulong Value(Item item)
        {
            Item2Entry entry = item.Info.Entry;
            for (int i = 0; i < entry.CurrencyTypeIdSellToVendor.Length; i++)
                if (entry.CurrencyTypeIdSellToVendor[i] == (uint)CurrencyType.Credits)
                    return entry.CurrencyAmountSellToVendor[i];
            return 0ul;
        }

        public static ulong RepairCost(Item item)
        {
            uint max = MaxPoints(item);
            if (max == 0u)
                return 0ul;
            uint current = CurrentPoints(item);
            if (current >= max)
                return 0ul;
            float factor = Formula?.Datafloat03 ?? 0f;
            return (ulong)(factor * ((max - current) / (float)max) * Value(item) + 0.5f);
        }

        /// <summary>Armor and weapons that are equipped or in the bags and miss durability.</summary>
        public static IEnumerable<Item> Damaged(Player player)
        {
            return player.Inventory.GetItems(InventoryLocation.Equipped)
                .Concat(player.Inventory.GetItems(InventoryLocation.Inventory))
                .Where(i => i != null && HasDurability(i) && CurrentPoints(i) < MaxPoints(i));
        }

        public static void Set(Player player, Item item, float durability)
        {
            item.Durability = Math.Clamp(durability, 0f, 1f);
            player.Session?.EnqueueMessageEncrypted(new ServerItemDurabilityUpdate
            {
                ItemGuid   = item.Guid,
                Durability = item.Durability
            });
        }

        /// <summary>Death penalty on equipped armor and weapons, only when NF_DURABILITY_DEATH_LOSS is set (no source for the amount).</summary>
        public static void OnDeath(Player player)
        {
            if (DeathLoss <= 0f)
                return;

            int count = 0;
            foreach (Item item in player.Inventory.GetItems(InventoryLocation.Equipped).Where(i => i != null && HasDurability(i)).ToList())
            {
                Set(player, item, item.Durability - DeathLoss);
                count++;
            }
            Diagnostics.FailureLedger.Record("durability-death-loss", "applied", $"{count} items, {DeathLoss:0.##} each");
        }
    }
}
