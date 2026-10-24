using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Combat
{
    /// <summary>
    /// Unit properties an item gives when equipped, from the item level (Item2.PowerLevel) and the slot, category, ItemStat and ItemBudget rows.
    /// Fitted against 484 archived Jabbithole item tooltips of 2016-2019 (after the Drop 6 stat revamp, research/decode/items/jh-items.tsv,
    /// scripts/research/item_fit.py) and the greenyneko.com item posts of 2017-2018; see docs/ASSUMPTIONS.md "Equipment stats":
    /// <list type="bullet">
    /// <item>armor slots: max health 60 x ilvl x ItemSlot.ArmorModifier (186 of 186 items), armor ArmorBase + 20 x ilvl x slot x category
    /// ArmorModifier (mode of every slot and category), above item level 50 the level counts 50 + 0.34 x (ilvl - 50)</item>
    /// <item>power (assault + support rating): 3.6 x ilvl x slot for armor, attachment, system and implant slots, 4.5 when the item has no
    /// ItemStat row; weapons 111.375 x ilvl. Support share = (Item2.SupportPowerPercentage + 1) / 2 (-1 all assault ... 1 all support, 389 items)</item>
    /// <item>secondary stats: ItemBudget fraction x 5 x ilvl x slot (weapons 3.75 x ilvl) x UnitProperty2.ValuePerPoint / 10 (within ~10%)</item>
    /// <item>energy shields: capacity 75 x (1.5 + f) x ilvl, mitigation 25% + 75% f, regen 5% + 20% f per tick, reboot 10 s - 10 s x f</item>
    /// </list>
    /// Values are floored as the tooltips show them. Random stat groups (ItemStat type 2) take the group's entries in table order (the rolled
    /// entries of an item instance are not stored). Other ItemStat types are recorded in the ledger (item-stat-type-unknown).
    /// </summary>
    public static class ItemStats
    {
        public const int PropertyCount = 256;

        private const float ArmorPowerPerLevel    = 3.6f;
        private const float ArmorPowerNoStats     = 4.5f;
        private const float WeaponPowerPerLevel   = 111.375f;
        private const float HealthPerLevel        = 60f;
        private const float ArmorPerLevel         = 20f;
        private const float ArmorSlopeAbove50     = 0.34f;
        private const float SecondaryPerLevel     = 5f;
        private const float WeaponSecondaryPerLevel = 3.75f;

        private const uint SlotWeapon = 20u;
        private const uint SlotShield = 43u;

        private static readonly ConcurrentDictionary<uint, float[]> cache = new();
        private static Dictionary<uint, ItemRandomStatEntry[]> randomGroups;

        /// <summary>
        /// Property values of an item (indexed by <see cref="Property"/>), or null for an item that gives none.
        /// </summary>
        public static float[] For(Item2Entry item)
        {
            if (item == null)
                return null;
            return cache.GetOrAdd(item.Id, _ => Calculate(item, item.PowerLevel, true));
        }

        private static readonly ConcurrentDictionary<(uint, uint), float[]> raisedCache = new();

        /// <summary>
        /// Property values of an item whose level is raised by finished imbuement steps of type 5 (the client adds the reward value to the level
        /// its stat budget is computed from, docs/ASSUMPTIONS.md "Imbuement rewards").
        /// </summary>
        public static float[] For(Item2Entry item, uint levelBonus)
        {
            if (item == null || levelBonus == 0u)
                return For(item);
            return raisedCache.GetOrAdd((item.Id, levelBonus), _ => Calculate(item, item.PowerLevel + levelBonus, false));
        }

        private static readonly ConcurrentDictionary<uint, float> secondaryRaw = new();

        /// <summary>
        /// Sum of the item's secondary ratings before rounding (the base of a type 0 imbuement reward: Razorblight 26.25 x 0.15 = 3.9375,
        /// Stardust Blade (72 + 48) x 0.15 = 18.0 on the archived tooltips).
        /// </summary>
        public static float SecondaryRaw(Item2Entry item)
        {
            if (For(item) == null)
                return 0f;
            return secondaryRaw.GetValueOrDefault(item.Id);
        }

        /// <summary>
        /// Assault plus support rating of an item, the score bots use to pick gear (shields: capacity).
        /// </summary>
        public static float Score(Item2Entry item)
        {
            float[] values = For(item);
            if (values == null)
                return 0f;
            return values[(int)Property.AssaultRating] + values[(int)Property.SupportRating] + values[(int)Property.ShieldCapacityMax] / 10f;
        }

        private static float[] Calculate(Item2Entry item, float ilvl, bool baseLevel)
        {
            try
            {
                return CalculateUnsafe(item, ilvl, baseLevel);
            }
            catch (Exception ex)
            {
                Diagnostics.FailureLedger.Record("item-stat-exception", ex.GetType().Name, $"item {item.Id}");
                return null;
            }
        }

        private static float[] CalculateUnsafe(Item2Entry item, float ilvl, bool baseLevel)
        {
            if (ilvl <= 0f)
                return null;

            Item2TypeEntry type = GameTableManager.Instance.Item2Type.GetEntry(item.Item2TypeId);
            ItemSlotEntry slot = type == null ? null : GameTableManager.Instance.ItemSlot.GetEntry(type.ItemSlotId);
            if (slot == null)
                return null;

            bool armorSlot = slot.Id >= 1u && slot.Id <= 6u;
            bool powerSlot = armorSlot || slot.Id == 57u || slot.Id == 58u || slot.Id == 60u;
            bool weapon = slot.Id == SlotWeapon;
            bool shield = slot.Id == SlotShield;
            if (!powerSlot && !weapon && !shield && slot.Id != 46u)
                return null;

            var values = new float[PropertyCount];
            float slotMod = slot.ArmorModifier;

            ItemStatEntry stat = item.ItemStatId != 0u ? GameTableManager.Instance.ItemStat.GetEntry(item.ItemStatId) : null;
            ItemBudgetEntry budget = item.ItemBudgetId != 0u ? GameTableManager.Instance.ItemBudget.GetEntry(item.ItemBudgetId) : null;

            float power = 0f;
            if (weapon)
                power = WeaponPowerPerLevel * ilvl;
            else if (powerSlot)
                power = (stat != null ? ArmorPowerPerLevel : ArmorPowerNoStats) * ilvl * slotMod;
            if (power > 0f)
            {
                float support = Math.Clamp((item.SupportPowerPercentage + 1f) / 2f, 0f, 1f);
                values[(int)Property.AssaultRating] = MathF.Floor(power * (1f - support) + 0.01f);
                values[(int)Property.SupportRating] = MathF.Floor(power * support + 0.01f);
            }

            if (armorSlot)
            {
                values[(int)Property.BaseHealth] = MathF.Floor(HealthPerLevel * ilvl * slotMod + 0.01f);
                Item2CategoryEntry category = GameTableManager.Instance.Item2Category.GetEntry(item.Item2CategoryId);
                // armor grows a third as fast above item level 50 (57 tooltips of item level 52-120: 50 + 0.34 x (ilvl - 50) fits each)
                float armorLevel = ilvl <= 50f ? ilvl : 50f + ArmorSlopeAbove50 * (ilvl - 50f);
                if (category != null)
                    values[(int)Property.Armor] = MathF.Floor(category.ArmorBase + ArmorPerLevel * armorLevel * slotMod * category.ArmorModifier + 0.01f);
            }

            if (stat == null)
                return values;

            var used = new HashSet<uint>();
            float rawSum = 0f;
            for (int i = 0; i < 5; i++)
            {
                (uint statType, uint data) = i switch
                {
                    0 => (stat.ItemStatTypeEnum00, stat.ItemStatData00),
                    1 => (stat.ItemStatTypeEnum01, stat.ItemStatData01),
                    2 => (stat.ItemStatTypeEnum02, stat.ItemStatData02),
                    3 => (stat.ItemStatTypeEnum03, stat.ItemStatData03),
                    _ => (stat.ItemStatTypeEnum04, stat.ItemStatData04)
                };
                if (statType == 0u)
                    continue;

                float fraction = budget == null ? 0f : i switch
                {
                    0 => budget.Budget00,
                    1 => budget.Budget01,
                    2 => budget.Budget02,
                    3 => budget.Budget03,
                    _ => budget.Budget04
                };

                uint property;
                if (statType == 1u)
                    property = data;
                else if (statType == 2u)
                {
                    uint? picked = PickRandom(data, used);
                    if (picked == null)
                        continue;
                    property = picked.Value;
                }
                else if (statType == 4u)
                {
                    // "Random Attribute" slot (Jabbithole tooltips, docs/ASSUMPTIONS.md "Equipment stats"): 531 of 542 such items are
                    // crafted and the crafter fills it; the data is a mask (63/64/127) with no budget, so the slot adds nothing here
                    Diagnostics.FailureLedger.Record("item-stat-random-slot", "type 4 slot, no budget (filled by crafting)", $"data {data}");
                    continue;
                }
                else
                {
                    Diagnostics.FailureLedger.Record("item-stat-type-unknown", $"type {statType}", $"item {item.Id} data {data}");
                    continue;
                }

                used.Add(property);
                if (property >= PropertyCount)
                    continue;

                if (shield && ApplyShield(values, (Property)property, fraction, ilvl))
                    continue;
                if (fraction <= 0f)
                    continue;

                UnitProperty2Entry entry = GameTableManager.Instance.UnitProperty2.GetEntry(property);
                float perPoint = entry?.ValuePerPoint ?? 0f;
                if (perPoint <= 0f)
                {
                    Diagnostics.FailureLedger.Record("item-stat-property-unscaled", $"property {property}", $"item {item.Id}");
                    continue;
                }

                float perLevel = weapon ? WeaponSecondaryPerLevel : SecondaryPerLevel * slotMod;
                float raw = perLevel * ilvl * fraction * perPoint / 10f;
                values[property] += MathF.Floor(raw + 0.01f);
                rawSum += raw;
            }

            if (baseLevel)
                secondaryRaw[item.Id] = rawSum;
            return values;
        }

        private static bool ApplyShield(float[] values, Property property, float fraction, float ilvl)
        {
            switch (property)
            {
                case Property.ShieldCapacityMax:
                    values[(int)property] = MathF.Floor(75f * (1.5f + fraction) * ilvl + 0.01f);
                    return true;
                case Property.ShieldMitigationMax:
                    values[(int)property] = 0.25f + 0.75f * fraction;
                    return true;
                case Property.ShieldRegenPct:
                    values[(int)property] = 0.05f + 0.20f * fraction;
                    return true;
                case Property.ShieldRebootTime:
                    values[(int)property] = 10000f - 10000f * fraction;
                    return true;
                default:
                    return false;
            }
        }

        private static uint? PickRandom(uint group, HashSet<uint> used)
        {
            randomGroups ??= GameTableManager.Instance.ItemRandomStat.Entries
                .Where(e => e.ItemStatTypeEnum == 1u)
                .GroupBy(e => e.ItemRandomStatGroupId)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id).ToArray());

            if (!randomGroups.TryGetValue(group, out ItemRandomStatEntry[] entries))
                return null;
            foreach (ItemRandomStatEntry entry in entries)
                if (!used.Contains(entry.ItemStatData))
                    return entry.ItemStatData;
            return null;
        }
    }
}
