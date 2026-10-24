using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.GameTable.Static;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Research;
using NLog;

namespace NexusForever.WorldServer.Game.Encounters
{
    /// <summary>
    /// Loads the encounter definitions and hands a controller to a boss when it enters combat. Bosses without a file get a generated default
    /// when the research data marks them as elite (prime rank, group or raid elite): three phases and their researched abilities on timers.
    /// </summary>
    public sealed class EncounterManager : Singleton<EncounterManager>
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();
        private readonly Dictionary<string, List<EncounterDefinition>> byBoss = new();
        private bool loaded;
        private readonly object gate = new();

        public int DefinitionCount { get { lock (gate) { Load(); return byBoss.Values.Sum(v => v.Count); } } }

        public IEnumerable<EncounterDefinition> Definitions { get { lock (gate) { Load(); return byBoss.Values.SelectMany(v => v).ToList(); } } }

        private void Load()
        {
            if (loaded)
                return;

            loaded = true;
            string dir = Environment.GetEnvironmentVariable("NF_ENCOUNTER_PATH") ?? "/data/encounters";
            if (!Directory.Exists(dir))
            {
                log.Warn($"Encounter folder {dir} not found, only generated default encounters are used.");
                return;
            }

            var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            foreach (string file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f))
            {
                try
                {
                    string text = File.ReadAllText(file);
                    var list = text.TrimStart().StartsWith("[")
                        ? JsonSerializer.Deserialize<List<EncounterDefinition>>(text, options)
                        : new List<EncounterDefinition> { JsonSerializer.Deserialize<EncounterDefinition>(text, options) };
                    foreach (EncounterDefinition def in list.Where(d => !string.IsNullOrWhiteSpace(d?.Boss)))
                    {
                        string key = CreatureResearch.Slug(def.Boss);
                        if (!byBoss.TryGetValue(key, out var defs))
                            byBoss[key] = defs = new List<EncounterDefinition>();
                        defs.Add(def);
                    }
                }
                catch (Exception e)
                {
                    log.Error(e, $"Could not read encounter file {file}.");
                    Diagnostics.FailureLedger.Record("encounter-file-error", Path.GetFileName(file), e.Message);
                }
            }

            // hand written definitions win over drafts taken from boss mods
            foreach (string key in byBoss.Keys.ToList())
                byBoss[key] = byBoss[key].OrderBy(d => (d.Source ?? "").StartsWith("raidcore") ? 1 : 0).ToList();

            // how many definitions have sourced timers, partly sourced timers, or only guide names on assumed timers (docs/ENCOUNTER-SOURCES.md)
            var classes = byBoss.Values.SelectMany(v => v).GroupBy(EncounterClass.Of).ToDictionary(g => g.Key, g => g.Count());
            foreach (var (cls, count) in classes)
                Diagnostics.FailureLedger.Record("encounter-definitions", cls, $"{count} definitions");
            log.Info($"Encounters: {byBoss.Values.Sum(v => v.Count)} definitions for {byBoss.Count} bosses ({string.Join(", ", classes.Select(c => $"{c.Value} {c.Key}"))}).");
        }

        private readonly Dictionary<(uint, bool), EncounterDefinition> cache = new();

        /// <summary>True when a boss of this creature name has a hand written or boss mod definition for the world (generated defaults do not count).</summary>
        public bool HasDefinition(string creatureName, uint world)
        {
            lock (gate)
            {
                Load();
                return !string.IsNullOrEmpty(creatureName) && byBoss.TryGetValue(CreatureResearch.Slug(creatureName), out var defs) && defs.Any(d => d.World == world || d.World == 0u);
            }
        }

        /// <summary>True for a creature that has an encounter (a boss in the sense of dungeon clears).</summary>
        public bool IsBoss(NonPlayer creature)
        {
            return Get(creature) != null;
        }

        /// <summary>The definition for a creature, null when it is no boss.</summary>
        public EncounterDefinition Get(NonPlayer boss)
        {
            lock (gate)
            {
                Load();
                bool veteran = IsVeteran(boss);
                if (cache.TryGetValue((boss.CreatureId, veteran), out EncounterDefinition cached))
                    return cached;

                EncounterDefinition def = null;
                var entry = GameTableManager.Instance.Creature2.GetEntry(boss.CreatureId);
                string name = entry == null ? null : GameTableManager.Instance.GetTextTable(Language.English)?.GetEntry(entry.LocalizedTextIdName);
                // a row without a difficulty rank is a flavor copy of the boss (comm call, intro, "Flavor Only": 58332, 62377, 63151, the
                // 3 of 3 spawned rank-0 rows that share a boss name); it never fights, so it gets no encounter
                if (entry != null && entry.Creature2DifficultyId == 0u && !string.IsNullOrEmpty(name) && byBoss.ContainsKey(CreatureResearch.Slug(name)))
                {
                    Diagnostics.FailureLedger.Record("encounter-flavor-copy", $"creature {boss.CreatureId} {name}", "no difficulty rank: no encounter");
                    cache[(boss.CreatureId, veteran)] = null;
                    return null;
                }

                if (!string.IsNullOrEmpty(name) && byBoss.TryGetValue(CreatureResearch.Slug(name), out var defs))
                {
                    uint world = boss.Map?.Entry.Id ?? 0u;
                    // a definition for another world (the same boss name in a raid and in a dungeon) is not used here
                    def = defs.FirstOrDefault(d => d.World == world) ?? defs.FirstOrDefault(d => d.World == 0u);
                }

                // a definition without abilities (names and thresholds taken from a boss mod) gets the researched abilities of the creature
                if (def != null && def.Phases.All(p => p.Abilities.Count == 0))
                {
                    EncounterDefinition generated = Generated(boss, name, force: true);
                    if (generated != null)
                    {
                        for (int i = 0; i < def.Phases.Count; i++)
                            def.Phases[i].Abilities = generated.Phases[Math.Min(i, generated.Phases.Count - 1)].Abilities;
                    }
                }

                def ??= Generated(boss, name);
                cache[(boss.CreatureId, veteran)] = def;
                return def;
            }
        }

        public static bool IsVeteran(NonPlayer creature)
        {
            return creature.Map is Map.DungeonMapInstance { Difficulty: Map.InstanceDifficulty.Veteran };
        }

        private static readonly System.Text.RegularExpressions.Regex creatureTag = new(@"^(?:\[Dungeon\])?\[(?<tag>[A-Z0-9]{2,4})\]\s*e(?<enc>\d+)\s*-\s*(?:\((?<mode>[NV/]+)\)\s*)?(?<name>.+)$");
        private static Dictionary<(string Tag, uint Encounter, string Boss), List<uint>> tagSpells;

        /// <summary>
        /// The creature is the named boss of its designer encounter: "[SK] e323 - Bosun Octog - Veteran" (boss part plus at most a difficulty
        /// word), not a unit of it ("[SK] e329 - Molten Chasm - Magma Wave- Veteran", a hazard with the same Group Prime rank).
        /// </summary>
        private static (string Tag, uint Encounter, string Boss)? BossTag(string description)
        {
            var m = creatureTag.Match(description ?? "");
            if (!m.Success)
                return null;
            string[] parts = m.Groups["name"].Value.Split(" - ");
            string rest = string.Join(" - ", parts.Skip(1)).Trim();
            if (rest.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(rest, @"^(\(?(N|V|N/V)\)?|Normal|Veteran|Prime|Base|Boss)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return null;
            return (m.Groups["tag"].Value, uint.Parse(m.Groups["enc"].Value), parts[0].Trim().ToLowerInvariant());
        }

        /// <summary>Spell4 rows whose designer description names the same instance tag, encounter number and boss (the boss's spell list in the tables).</summary>
        private static List<uint> TableSpells((string Tag, uint Encounter, string Boss) key)
        {
            if (tagSpells == null)
            {
                var index = new Dictionary<(string, uint, string), List<uint>>();
                foreach (var spell in GameTableManager.Instance.Spell4.Entries)
                {
                    DesignerTag tag = spell == null ? null : DesignerTag.Parse(spell.Description);
                    if (tag == null)
                        continue;
                    var k = (tag.Tag, tag.Encounter, tag.Boss.Trim().ToLowerInvariant());
                    if (!index.TryGetValue(k, out var list))
                        index[k] = list = new List<uint>();
                    list.Add(spell.Id);
                }
                tagSpells = index;
            }
            return tagSpells.TryGetValue(key, out var found) ? found : new List<uint>();
        }

        /// <summary>Creature2Difficulty rank Prime (rankValue 2) of a group or raid difficulty (groupValue 5, 20, 40): certain from the table titles.</summary>
        private static bool TablePrime(Creature2Entry entry)
        {
            var difficulty = entry == null ? null : GameTableManager.Instance.Creature2Difficulty.GetEntry(entry.Creature2DifficultyId);
            return difficulty != null && difficulty.RankValue == 2u && difficulty.GroupValue >= 5u;
        }

        private static EncounterDefinition Generated(NonPlayer boss, string name, bool force = false)
        {
            CreatureResearch.Info info = CreatureResearch.Get(boss.CreatureId, (int)boss.Level);
            List<uint> abilities = info?.Abilities.ToList() ?? new List<uint>();
            string names = "research";
            if (abilities.Count == 0)
            {
                // no researched casts: the spells the tables tie to this boss through the designer tag, for a group/raid Prime that is the
                // named boss of its encounter
                Creature2Entry entry = GameTableManager.Instance.Creature2.GetEntry(boss.CreatureId);
                var key = entry == null ? null : BossTag(entry.Description);
                if (!TablePrime(entry) || key == null)
                    return null;
                abilities = TableSpells(key.Value);
                names = "tables";
                if (abilities.Count == 0)
                    return null;
                force = true;
            }

            bool elite = force || info.Rank == "Prime" || info.Eliteness is "Group" or "Raid";
            if (!elite)
                return null;

            // names of the researched abilities, one per base spell (auto-attacks stay with the melee formula)
            var spells = new List<string>();
            var seen = new HashSet<uint>();
            bool veteran = IsVeteran(boss);
            int filtered = 0;
            foreach (uint id in abilities)
            {
                var spell = GameTableManager.Instance.Spell4.GetEntry(id);
                if (spell == null)
                    continue;
                string description = spell.Description ?? "";
                // the designer description settles what the boss starts itself: no proxies, messaging, visuals, and only spells of this difficulty
                DesignerTag tag = DesignerTag.Parse(description);
                if ((tag != null && (tag.IsHelper || !tag.FitsMode(veteran)))
                    || description.Contains("[TEST]", StringComparison.Ordinal) || description.Contains("DEPRECATE", StringComparison.Ordinal))
                {
                    filtered++;
                    continue;
                }
                if (!seen.Add(spell.Spell4BaseIdBaseSpell))
                    continue;
                if (description.Contains("Auto-Attack", StringComparison.OrdinalIgnoreCase) || description.Contains("Basic Attack", StringComparison.OrdinalIgnoreCase))
                    continue;
                spells.Add(id.ToString());
            }

            if (filtered > 0)
                Diagnostics.FailureLedger.Record("encounter-abilities-filtered", name ?? boss.CreatureId.ToString(), $"{filtered} helper or other-difficulty spells left out, {spells.Count} kept");
            if (spells.Count == 0)
                return null;

            if (names == "tables")
                Diagnostics.FailureLedger.Record("encounter-spells-from-tags", name ?? boss.CreatureId.ToString(), $"creature {boss.CreatureId}: {spells.Count} spells from its designer tag");
            var def = new EncounterDefinition { Boss = name ?? "", Enrage = 600d, Leash = 120f, Source = "generated", Names = names == "tables" ? "tables (designer tag)" : "tables", Confidence = "guess" };
            double[] speed = { 1d, 0.8d, 0.6d };
            double[] health = { 100d, 60d, 30d };
            for (int p = 0; p < 3; p++)
            {
                var phase = new PhaseDefinition { Name = $"Phase {p + 1}", Health = health[p] };
                for (int i = 0; i < spells.Count; i++)
                    phase.Abilities.Add(new AbilityDefinition
                    {
                        Spell  = spells[i],
                        First  = (6d + i * 5d) * speed[p],
                        Every  = (20d + spells.Count * 2d) * speed[p],
                        Target = i % 3 == 2 ? "random" : "tank"
                    });
                def.Phases.Add(phase);
            }

            return def;
        }
    }
}
