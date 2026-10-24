using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game;

namespace NexusForever.WorldServer.Bots
{
    public static class BotAppearance
    {
        private const int Attempts = 60;

        /// <summary>
        /// Pick a random but consistent set of customisation label/value pairs for the supplied race and sex.
        /// The pairs are in the same form the character creation screen sends. Every attempt is checked against the
        /// same lookup the create handler performs, so a set that the handler cannot resolve is never returned
        /// (unless none of the attempts resolves, in which case the last one is returned and creation will fail and retry).
        /// </summary>
        public static (List<uint> labels, List<uint> values) Roll(uint race, uint sex, Random random)
        {
            List<CharacterCustomizationEntry> entries = GameTableManager.Instance.CharacterCustomization.Entries
                .Where(e => e.RaceId == race
                    && e.Gender == sex
                    && e.CharacterCustomizationLabelId00 != 0u)
                .ToList();

            var chosen = new Dictionary<uint, uint>();
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                chosen = new Dictionary<uint, uint>();

                // one random row for every primary label (face, hair style, skin and so on), taking its secondary value along
                foreach (IGrouping<uint, CharacterCustomizationEntry> group in entries.GroupBy(e => e.CharacterCustomizationLabelId00))
                {
                    CharacterCustomizationEntry row = group.ElementAt(random.Next(group.Count()));
                    chosen[group.Key] = row.Value00;

                    if (row.CharacterCustomizationLabelId01 != 0u && !chosen.ContainsKey(row.CharacterCustomizationLabelId01))
                        chosen[row.CharacterCustomizationLabelId01] = row.Value01;
                }

                if (Resolves(chosen, race, sex))
                    break;
            }

            return (chosen.Keys.ToList(), chosen.Values.ToList());
        }

        /// <summary>
        /// Mirrors the lookup in the character create handler without keeping its result.
        /// </summary>
        private static bool Resolves(Dictionary<uint, uint> customisations, uint race, uint sex)
        {
            try
            {
                // every label must land on its own item slot, as the create handler stores one appearance row per slot
                var slots = new HashSet<uint>();
                foreach ((uint label, uint value) in customisations)
                {
                    ImmutableList<CharacterCustomizationEntry> found = AssetManager.Instance.GetPrimaryCharacterCustomisation(race, sex, label, value);
                    if (found == null)
                        continue;
                    if (found.Count == 1)
                    {
                        if (!slots.Add(found[0].ItemSlotId))
                            return false;
                        continue;
                    }

                    uint secondaryLabel = found.First(e => e.CharacterCustomizationLabelId01 != 0u).CharacterCustomizationLabelId01;
                    uint secondaryValue = customisations[secondaryLabel];

                    CharacterCustomizationEntry entry = found.SingleOrDefault(e => e.CharacterCustomizationLabelId01 == secondaryLabel && e.Value01 == secondaryValue);
                    entry ??= found.Single(e => e.CharacterCustomizationLabelId01 == 0u && e.Value01 == 0u);
                    if (!slots.Add(entry.ItemSlotId))
                        return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
