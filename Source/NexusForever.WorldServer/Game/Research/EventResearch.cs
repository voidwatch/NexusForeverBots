using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Static;

namespace NexusForever.WorldServer.Game.Research
{
    /// <summary>
    /// Creatures involved in public events (event name to creature names, from the archived Jabbithole event pages, data/research/event-npcs.tsv),
    /// resolved to Creature2 ids by name.
    /// </summary>
    public static class EventResearch
    {
        private static Dictionary<string, List<string>> npcsByEvent;
        private static Dictionary<string, List<uint>> idsByName;
        private static readonly object gate = new();

        private static void Load()
        {
            npcsByEvent = new Dictionary<string, List<string>>();
            idsByName = new Dictionary<string, List<uint>>();
            string file = Path.Combine(Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research", "event-npcs.tsv");
            if (!File.Exists(file))
                return;

            foreach (string line in File.ReadLines(file))
            {
                string[] f = line.Split('\t');
                if (f.Length < 2)
                    continue;

                string key = CreatureResearch.Slug(f[0]);
                if (!npcsByEvent.TryGetValue(key, out var list))
                    npcsByEvent[key] = list = new List<string>();
                list.Add(f[1]);
            }

            var text = GameTableManager.Instance.GetTextTable(Language.English);
            var wanted = new HashSet<string>(npcsByEvent.Values.SelectMany(v => v).Select(CreatureResearch.Slug));
            foreach (var creature in GameTableManager.Instance.Creature2.Entries)
            {
                string slug = CreatureResearch.Slug(text?.GetEntry(creature.LocalizedTextIdName));
                if (slug.Length > 0 && wanted.Contains(slug))
                {
                    if (!idsByName.TryGetValue(slug, out var ids))
                        idsByName[slug] = ids = new List<uint>();
                    ids.Add(creature.Id);
                }
            }
        }

        /// <summary>
        /// Creature ids of the NPCs listed for the event name, hostile combatants only (vendors, objects and friendly NPCs are dropped by the caller's use).
        /// </summary>
        public static List<uint> GetCreatures(string eventName, uint zoneId)
        {
            lock (gate)
            {
                if (npcsByEvent == null)
                    Load();

                if (!npcsByEvent.TryGetValue(CreatureResearch.Slug(eventName), out var names))
                    return new List<uint>();

                return names.Select(CreatureResearch.Slug).Distinct()
                    .SelectMany(n => idsByName.TryGetValue(n, out var ids) ? ids : new List<uint>())
                    .Where(id => GameTableManager.Instance.Creature2.GetEntry(id) is { } c && c.QuestIdGiven.All(q => q == 0u) && c.QuestIdReceive.All(q => q == 0u))
                    .ToList();
            }
        }
    }
}
