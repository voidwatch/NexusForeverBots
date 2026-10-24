using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Static;

namespace NexusForever.WorldServer.Game.Tradeskill
{
    /// <summary>
    /// Salvage (ClientItemSalvage 0x015E). What may be salvaged is the client's rule (certain, Item:CanSalvage and the salvage send path):
    /// Item2.Flags 0x100 is never salvageable (error 0x48); otherwise a Salvage row for the item's Item2Type with Level at most the item's level,
    /// or a SalvageException row for the item (flags 4: needs a salvage key); the character needs GameFormula 1034 dataint0 (level 8, error 0x2A).
    /// What comes out is not in the client: archived Jabbithole item pages list "Salvaged to" with observed counts (scripts/research/jh_salvage.py,
    /// data/research/salvage-outputs.tsv, 476 items). The item's own rows are used, else the pooled rows of its type and level band, else the
    /// nearest band of the type; each output is rolled by its observed chance, one count each, the likeliest output if nothing rolled (same rule
    /// as harvest nodes), and ItemQuality.SalvageCritChance rolls the table once more (guess, wiki "Salvage Critical, which can drop more rare
    /// materials"). docs/ASSUMPTIONS.md "Crafting: catalysts, talents, additives, salvage".
    /// </summary>
    public static class Salvage
    {
        private static readonly object gate = new();
        private static Dictionary<uint, List<(uint Item, float Chance)>> byItem;
        private static Dictionary<(uint Type, uint Level), List<(uint Item, float Chance)>> byBand;
        private static Dictionary<uint, List<uint>> bands;
        private static Dictionary<uint, uint> exceptions;
        [ThreadStatic] private static Random random;

        private static void Load()
        {
            lock (gate)
            {
                if (byItem != null)
                    return;
                var items = new Dictionary<uint, List<(uint, float)>>();
                var pooled = new Dictionary<(uint, uint), List<(uint, float)>>();
                string dir = Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research";
                string path = System.IO.Path.Combine(dir, "salvage-outputs.tsv");
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadLines(path))
                    {
                        string[] f = line.Split('\t');
                        if (f.Length < 7 || line.StartsWith("#") || !uint.TryParse(f[1], out uint key) || !uint.TryParse(f[2], out uint level)
                            || !uint.TryParse(f[3], out uint output) || !float.TryParse(f[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float chance))
                            continue;
                        List<(uint, float)> list;
                        if (f[0] == "item")
                        {
                            if (!items.TryGetValue(key, out list))
                                items[key] = list = new List<(uint, float)>();
                        }
                        else if (!pooled.TryGetValue((key, level), out list))
                            pooled[(key, level)] = list = new List<(uint, float)>();
                        list.Add((output, chance));
                    }
                }
                else
                    FailureLedger.Record("salvage-no-research-file", path);

                bands = GameTableManager.Instance.Salvage.Entries.Where(e => e != null && e.Item2TypeId != 0u)
                    .GroupBy(e => e.Item2TypeId).ToDictionary(g => g.Key, g => g.Select(e => e.Level).OrderBy(l => l).ToList());
                exceptions = GameTableManager.Instance.SalvageException.Entries.Where(e => e != null && e.Item2Id != 0u)
                    .GroupBy(e => e.Item2Id).ToDictionary(g => g.Key, g => g.First().Flags);
                byBand = pooled;
                byItem = items;
            }
        }

        /// <summary>The client's salvage key lookup: the highest Salvage.Level of the item's type that the item's level reaches.</summary>
        private static uint? Band(Item2Entry entry)
        {
            if (!bands.TryGetValue(entry.Item2TypeId, out List<uint> levels))
                return null;
            uint? best = null;
            foreach (uint level in levels)
                if (level <= entry.PowerLevel)
                    best = level;
            return best;
        }

        /// <summary>Why an item cannot be salvaged by the player, or null (the client's Item:CanSalvage rules).</summary>
        public static GenericError? CanSalvage(Player player, Item2Entry entry)
        {
            Load();
            if (entry == null)
                return GenericError.ItemBadId;
            if ((entry.Flags & 0x100u) != 0u)
                return GenericError.ItemCannotBeSalvaged;
            if (Band(entry) == null && !exceptions.ContainsKey(entry.Id))
                return GenericError.ItemCannotBeSalvaged;
            uint minLevel = GameTableManager.Instance.GameFormula.GetEntry(1034u)?.Dataint0 ?? 0u;
            if (player.Level < minLevel)
                return GenericError.ItemLevelToLow;
            return null;
        }

        /// <summary>Items with their own researched salvage rows (for the QA probes).</summary>
        public static IReadOnlyCollection<uint> ResearchedItems
        {
            get
            {
                Load();
                return byItem.Keys;
            }
        }

        public static bool NeedsKey(Item2Entry entry)
        {
            Load();
            return exceptions.TryGetValue(entry.Id, out uint flags) && (flags & 4u) != 0u;
        }

        private static List<(uint Item, float Chance)> Outputs(Item2Entry entry, out string source)
        {
            if (byItem.TryGetValue(entry.Id, out var own))
            {
                source = "item";
                return own;
            }
            uint? band = Band(entry);
            if (band != null && byBand.TryGetValue((entry.Item2TypeId, band.Value), out var pooled))
            {
                source = "type band";
                return pooled;
            }
            // nearest band of the same type with data (likely the same materials one tier up or down)
            var near = byBand.Where(k => k.Key.Type == entry.Item2TypeId).OrderBy(k => Math.Abs((int)k.Key.Level - (int)(band ?? entry.PowerLevel))).Select(k => k.Value).FirstOrDefault();
            source = near != null ? "nearest band" : "none";
            return near;
        }

        /// <summary>Salvages one item of a stack; returns the outputs given (empty when refused).</summary>
        public static List<uint> SalvageItem(Player player, Item item)
        {
            var given = new List<uint>();
            Item2Entry entry = item?.Info?.Entry;
            GenericError? error = CanSalvage(player, entry);
            if (error == null && NeedsKey(entry))
            {
                // salvage keys come from the store (GameLib.SalvageKeyCount, StorefrontLib lockbox key); none exist on this server
                FailureLedger.Record("salvage-needs-key", $"item {entry.Id}", "SalvageException flag 4");
                error = GenericError.ItemCannotBeSalvaged;
            }
            if (error != null)
            {
                FailureLedger.Record("salvage-refused", error.Value.ToString(), $"item {entry?.Id}");
                player.SendGenericError(error.Value);
                return given;
            }

            List<(uint Item, float Chance)> outputs = Outputs(entry, out string source);
            if (outputs == null || outputs.Count == 0)
            {
                FailureLedger.Record("salvage-no-output-data", $"type {entry.Item2TypeId}", $"item {entry.Id} level {entry.PowerLevel}");
                player.SendGenericError(GenericError.ItemCannotBeSalvaged);
                return given;
            }

            random ??= new Random();
            float crit = GameTableManager.Instance.ItemQuality.GetEntry(entry.ItemQualityId)?.SalvageCritChance ?? 0f;
            int rolls = random.NextDouble() < crit ? 2 : 1;
            for (int roll = 0; roll < rolls; roll++)
            {
                int before = given.Count;
                foreach ((uint output, float chance) in outputs)
                    if (random.NextDouble() < chance)
                        given.Add(HarvestUnit.CurrentMaterial(output));
                if (given.Count == before)
                    given.Add(HarvestUnit.CurrentMaterial(outputs.OrderByDescending(o => o.Chance).First().Item));
            }

            player.Inventory.ItemDelete(entry.Id, 1u);
            foreach (uint output in given)
            {
                if (ItemManager.Instance.GetItemInfo(output) == null)
                {
                    FailureLedger.Record("loot-unknown-item", $"item {output} (salvage)");
                    continue;
                }
                player.Inventory.ItemCreate(InventoryLocation.Inventory, output, 1u, ItemUpdateReason.Salvage);
            }

            FailureLedger.Record("salvage-done", source, $"item {entry.Id} type {entry.Item2TypeId} level {entry.PowerLevel}: {given.Count} outputs{(rolls > 1 ? " (critical)" : "")}");
            return given;
        }
    }
}
