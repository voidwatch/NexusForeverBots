using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Rune slots of a character's items (docs/ASSUMPTIONS.md "Runes"). Slot elements are Item.CodeEnumRuneType values from the client's
    /// enum registration (Air 7, Water 8, Earth 9, Fire 10, Logic 11, Life 12, Fusion 13). An item gets its slots the first time
    /// they are asked for: the ItemRuneInstance defined sockets, then random elements up to ItemQuality.DefaultRunes; slots can be added up to
    /// ItemQuality.MaxRunes. Installed runes add their item stats and their ItemRuneInstance set power. Kept in the character JSON store "runes".
    /// </summary>
    public class RuneManager
    {
        public const uint Air = 7u, Water = 8u, Earth = 9u, Fire = 10u, Logic = 11u, Life = 12u, Fusion = 13u;

        public sealed class Slots
        {
            public List<uint> Types { get; set; } = new();
            public List<uint> Runes { get; set; } = new();
        }

        public sealed class Saved
        {
            public Dictionary<string, Slots> Items { get; set; } = new();
        }

        private readonly Player player;
        private readonly CharacterJson<Saved> store;
        private static readonly Random rng = new();
        private static Dictionary<uint, uint> elementOfType;

        public RuneManager(Player player)
        {
            this.player = player;
            store = new CharacterJson<Saved>("runes", player.CharacterId);
        }

        /// <summary>Element of a rune item from its Item2Type name ("Air Rune - Basic", "Life Rune - Set", ...); 0 for other items and the hybrid runes.</summary>
        public static uint RuneElement(Item2Entry rune)
        {
            if (rune == null)
                return 0u;
            if (elementOfType == null)
            {
                var text = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
                var map = new Dictionary<uint, uint>();
                var names = new (string Name, uint Element)[] { ("Air", Air), ("Water", Water), ("Earth", Earth), ("Fire", Fire), ("Logic", Logic), ("Life", Life), ("Fusion", Fusion) };
                foreach (Item2TypeEntry type in GameTableManager.Instance.Item2Type.Entries.Where(t => t != null))
                {
                    string name = text?.GetEntry(type.LocalizedTextId) ?? "";
                    foreach ((string n, uint e) in names)
                        if (name.StartsWith(n + " Rune"))
                            map[type.Id] = e;
                }
                elementOfType = map;
            }
            return elementOfType.TryGetValue(rune.Item2TypeId, out uint element) ? element : 0u;
        }

        /// <summary>A rune fits a slot of its element; Fusion runes fit every slot (guess).</summary>
        public static bool Fits(uint runeElement, uint slotType) => runeElement != 0u && (runeElement == slotType || runeElement == Fusion);

        private static ItemQualityEntry Quality(Item2Entry entry) => GameTableManager.Instance.ItemQuality.GetEntry(entry.ItemQualityId);

        /// <summary>Gear that can hold runes: an equipment slot and a quality with rune slots, or defined sockets.</summary>
        public static bool CanHaveRunes(Item2Entry entry)
        {
            if (entry == null || RuneElement(entry) != 0u)
                return false;
            var type = GameTableManager.Instance.Item2Type.GetEntry(entry.Item2TypeId);
            if (type == null || type.ItemSlotId == 0u)
                return false;
            var instance = entry.ItemRuneInstanceId != 0u ? GameTableManager.Instance.ItemRuneInstance.GetEntry(entry.ItemRuneInstanceId) : null;
            return (instance?.DefinedSocketCount ?? 0u) > 0u || (Quality(entry)?.MaxRunes ?? 0u) > 0u;
        }

        private static uint RandomElement() => (uint)rng.Next((int)Air, (int)Life + 1);

        /// <summary>The item's slots, created on first use; null when the item cannot hold runes.</summary>
        public Slots Get(Item item)
        {
            Item2Entry entry = item?.Info?.Entry;
            if (!CanHaveRunes(entry))
                return null;

            string key = item.Guid.ToString();
            if (store.Value.Items.TryGetValue(key, out Slots slots))
            {
                // slots a finished imbuement step (quality or rune slot reward) added after the slots were made
                uint wanted = DefaultSlots(entry);
                if (slots.Types.Count < wanted)
                {
                    while (slots.Types.Count < wanted)
                    {
                        slots.Types.Add(RandomElement());
                        slots.Runes.Add(0u);
                    }
                    store.Save();
                }
                return slots;
            }

            slots = new Slots();
            var instance = entry.ItemRuneInstanceId != 0u ? GameTableManager.Instance.ItemRuneInstance.GetEntry(entry.ItemRuneInstanceId) : null;
            if (instance != null)
            {
                uint[] defined = { instance.DefinedSocketType00, instance.DefinedSocketType01, instance.DefinedSocketType02, instance.DefinedSocketType03,
                    instance.DefinedSocketType04, instance.DefinedSocketType05, instance.DefinedSocketType06, instance.DefinedSocketType07 };
                for (int i = 0; i < instance.DefinedSocketCount && i < defined.Length; i++)
                    slots.Types.Add(defined[i] != 0u ? defined[i] : RandomElement());
            }
            uint defaults = DefaultSlots(entry);
            while (slots.Types.Count < defaults)
                slots.Types.Add(RandomElement());
            slots.Runes = slots.Types.Select(_ => 0u).ToList();
            store.Value.Items[key] = slots;
            store.Save();
            return slots;
        }

        public uint MaxSlots(Item item)
        {
            Item2Entry entry = item.Info.Entry;
            var instance = entry.ItemRuneInstanceId != 0u ? GameTableManager.Instance.ItemRuneInstance.GetEntry(entry.ItemRuneInstanceId) : null;
            return Math.Max(Math.Min(8u, ImbuedQuality(entry, out uint extra)?.MaxRunes + extra ?? 0u), instance?.DefinedSocketCount ?? 0u);
        }

        private uint DefaultSlots(Item2Entry entry) => Math.Min(8u, ImbuedQuality(entry, out uint extra)?.DefaultRunes + extra ?? 0u);

        /// <summary>
        /// ItemQuality row after finished imbuement steps: type 4 adds quality steps (clamped to 2-7), type 6 adds rune slots (DefaultRunes and
        /// MaxRunes plus the sum, at most 8 in all), as the client's item builder does.
        /// </summary>
        private ItemQualityEntry ImbuedQuality(Item2Entry entry, out uint extraSlots)
        {
            uint quality = Quest.ItemImbuements.Sum(entry, Quest.ItemImbuements.RewardQuality, player.ImbuementQuestDone);
            extraSlots = Quest.ItemImbuements.Sum(entry, Quest.ItemImbuements.RewardRuneSlot, player.ImbuementQuestDone);
            if (quality == 0u)
                return Quality(entry);
            return GameTableManager.Instance.ItemQuality.GetEntry(Math.Clamp(entry.ItemQualityId + quality, 2u, 7u));
        }

        /// <summary>Installed rune items of an item (stats and set power for equipped gear).</summary>
        public IEnumerable<Item2Entry> Installed(Item item)
        {
            if (item == null || !store.Value.Items.TryGetValue(item.Guid.ToString(), out Slots slots))
                yield break;
            foreach (uint rune in slots.Runes)
                if (rune != 0u && GameTableManager.Instance.Item.GetEntry(rune) is Item2Entry entry)
                    yield return entry;
        }

        private void Changed(Item item)
        {
            store.Save();
            if (item.Location == InventoryLocation.Equipped)
                player.MarkGearDirty();
        }

        /// <summary>
        /// ClientCraftingRuneInstall: one rune id per slot. A slot that holds a rune must be cleared first (the client refuses the same),
        /// the rune must fit the slot and be in the bags; it is consumed. Returns the number of runes installed, refusals go to the ledger.
        /// </summary>
        public int Install(Item item, IReadOnlyList<uint> runes)
        {
            Slots slots = Get(item);
            if (slots == null)
            {
                FailureLedger.Record("rune-refused", "item has no rune slots", $"item {item?.Info?.Entry?.Id}");
                return 0;
            }

            int installed = 0;
            for (int i = 0; i < runes.Count && i < slots.Types.Count; i++)
            {
                uint rune = runes[i];
                if (rune == 0u || rune == slots.Runes[i])
                    continue;
                if (slots.Runes[i] != 0u)
                {
                    FailureLedger.Record("rune-refused", "slot holds a rune, clear it first", $"item {item.Info.Entry.Id} slot {i}");
                    continue;
                }
                Item2Entry runeEntry = GameTableManager.Instance.Item.GetEntry(rune);
                uint element = RuneElement(runeEntry);
                if (!Fits(element, slots.Types[i]))
                {
                    FailureLedger.Record("rune-refused", element == 0u ? "not a rune of a known element" : "rune element does not fit the slot", $"rune {rune} element {element} slot type {slots.Types[i]}");
                    continue;
                }
                if (player.Inventory.GetItemCount(rune) == 0u)
                {
                    FailureLedger.Record("rune-refused", "rune not in the bags", $"rune {rune}");
                    continue;
                }
                player.Inventory.ItemDelete(rune, 1u);
                slots.Runes[i] = rune;
                installed++;
            }

            if (installed > 0)
            {
                Changed(item);
                FailureLedger.Record("rune-installed", $"{installed} rune(s)", $"item {item.Info.Entry.Id}");
            }
            return installed;
        }

        /// <summary>ClientCraftingRuneSlotClear: the rune in the slot is destroyed (guess, the two flag bits are not decoded).</summary>
        public bool Clear(Item item, uint slot)
        {
            Slots slots = Get(item);
            if (slots == null || slot >= slots.Runes.Count || slots.Runes[(int)slot] == 0u)
            {
                FailureLedger.Record("rune-refused", "clear: no rune in that slot", $"item {item?.Info?.Entry?.Id} slot {slot}");
                return false;
            }
            slots.Runes[(int)slot] = 0u;
            Changed(item);
            return true;
        }

        /// <summary>ClientCraftingRuneSlotAdd: one more slot up to ItemQuality.MaxRunes, of the sent element (random when it is not one). No cost (unknown).</summary>
        public bool AddSlot(Item item, uint type)
        {
            Slots slots = Get(item);
            if (slots == null || slots.Types.Count >= MaxSlots(item))
            {
                FailureLedger.Record("rune-refused", "add slot: no room", $"item {item?.Info?.Entry?.Id}");
                return false;
            }
            slots.Types.Add(type >= Air && type <= Fusion ? type : RandomElement());
            slots.Runes.Add(0u);
            Changed(item);
            return true;
        }

        /// <summary>ClientCraftingRuneSlotReroll: an empty slot gets the sent element (random when it is not one). No cost (unknown).</summary>
        public bool Reroll(Item item, uint slot, uint type)
        {
            Slots slots = Get(item);
            if (slots == null || slot >= slots.Types.Count || slots.Runes[(int)slot] != 0u)
            {
                FailureLedger.Record("rune-refused", "reroll: no empty slot there", $"item {item?.Info?.Entry?.Id} slot {slot}");
                return false;
            }
            slots.Types[(int)slot] = type >= Air && type <= Fusion ? type : RandomElement();
            Changed(item);
            return true;
        }
    }
}
