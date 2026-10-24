using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Research
{
    /// <summary>
    /// Creature drops and abilities gathered from archived Jabbithole pages (data/research, see docs/ASSUMPTIONS.md).
    /// The client tables have neither; entries are matched to a creature by its name and the closest level.
    /// </summary>
    public static class CreatureResearch
    {
        public sealed class Info
        {
            public string Name;
            public string Zone;
            public int Level;
            public string Rank;
            public string Eliteness;
            public List<(uint ItemId, float Chance)> Drops = new();
            public List<uint> Abilities = new();
        }

        private static readonly ILogger log = LogManager.GetCurrentClassLogger();
        private static Dictionary<string, List<Info>> byName;
        private static readonly Dictionary<uint, Info> cache = new();
        private static readonly object gate = new();

        public static string Slug(string name) => Regex.Replace((name ?? "").ToLowerInvariant().Replace("'", ""), "[^a-z0-9]+", "-").Trim('-');

        private static void Load()
        {
            byName = new Dictionary<string, List<Info>>();
            string dir = Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research";
            if (!Directory.Exists(dir))
            {
                log.Warn($"Research data folder {dir} not found, creatures have no researched drops or abilities.");
                return;
            }

            var pages = new Dictionary<(string, string), Info>();
            Info Page(string name, string zone, string level)
            {
                if (!pages.TryGetValue((name, zone), out Info info))
                {
                    int.TryParse(level, out int lvl);
                    info = new Info { Name = name, Zone = zone, Level = lvl };
                    pages[(name, zone)] = info;
                    string slug = Slug(name);
                    if (!byName.TryGetValue(slug, out var list))
                        byName[slug] = list = new List<Info>();
                    list.Add(info);
                }

                return info;
            }

            foreach (string line in File.ReadLines(Path.Combine(dir, "creature-info.tsv")))
            {
                string[] f = line.Split('\t');
                if (f.Length >= 5)
                {
                    Info info = Page(f[0], f[1], f[2]);
                    info.Rank = f[3];
                    info.Eliteness = f[4];
                }
            }

            foreach (string line in File.ReadLines(Path.Combine(dir, "creature-drops.tsv")))
            {
                string[] f = line.Split('\t');
                if (f.Length >= 6 && uint.TryParse(f[3], out uint item) && float.TryParse(f[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float chance))
                    Page(f[0], f[1], f[2]).Drops.Add((item, chance));
            }

            foreach (string line in File.ReadLines(Path.Combine(dir, "creature-abilities.tsv")))
            {
                string[] f = line.Split('\t');
                if (f.Length >= 6 && uint.TryParse(f[3], out uint spell))
                    Page(f[0], f[1], f[2]).Abilities.Add(spell);
            }

            log.Info($"Research data: {pages.Count} creature pages, {pages.Values.Sum(p => p.Drops.Count)} drops, {pages.Values.Sum(p => p.Abilities.Count)} abilities.");
        }

        /// <summary>
        /// Researched data for a creature, null when no page has its name.
        /// </summary>
        public static Info Get(uint creatureId, int level)
        {
            lock (gate)
            {
                if (byName == null)
                    Load();

                if (cache.TryGetValue(creatureId, out Info cached))
                    return cached;

                Info best = null;
                var entry = GameTableManager.Instance.Creature2.GetEntry(creatureId);
                var text = GameTableManager.Instance.GetTextTable(Language.English);
                string name = entry == null ? null : text?.GetEntry(entry.LocalizedTextIdName);
                // plural markup and harvest node disguises ("Bladeleaf{p:Bladeleaves}", "Iron Node - Disguise 2") are not part of the page name
                if (name != null)
                    name = Regex.Replace(Regex.Replace(name, @"\{p:[^}]*\}", ""), @" - Disguise \d+$", "").Trim();
                if (!string.IsNullOrEmpty(name) && byName.TryGetValue(Slug(name), out var pages))
                {
                    // the page whose level is closest, and one with data over one without
                    best = pages.OrderBy(p => (p.Drops.Count + p.Abilities.Count == 0 ? 1000 : 0) + Math.Abs(p.Level - level)).First();
                }

                cache[creatureId] = best;
                return best;
            }
        }
    }
}
