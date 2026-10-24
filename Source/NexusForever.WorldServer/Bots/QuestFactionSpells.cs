using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Game.Spell.Static;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Quest scripts that turn a friendly quest target hostile: spells with a FactionSet effect on the target whose description names the quest
    /// ("Q7658 - Reading Vitals - Quest Spell", effect FactionSet 218 on the target). A bot whose kill target is friendly casts the quest's spell first;
    /// the old fallback (a quest holder may damage the friendly target) stays for quests without such a spell.
    /// </summary>
    public static class QuestFactionSpells
    {
        private static readonly object gate = new();
        private static Dictionary<uint, List<uint>> byQuest;

        private static void EnsureIndex()
        {
            if (byQuest != null)
                return;
            lock (gate)
            {
                if (byQuest != null)
                    return;

                var index = new Dictionary<uint, List<uint>>();
                var table = GameTableManager.Instance;
                var questPattern = new Regex(@"\bQ(\d{2,5})\b");
                foreach (var effect in table.Spell4Effects.Entries.Where(e => e != null && e.EffectType == (uint)SpellEffectType.FactionSet && (e.TargetFlags & 2u) != 0u && e.DataBits00 != 219u))
                {
                    var spell = table.Spell4.GetEntry(effect.SpellId);
                    if (spell?.Description == null)
                        continue;
                    foreach (Match m in questPattern.Matches(spell.Description))
                    {
                        uint quest = uint.Parse(m.Groups[1].Value);
                        if (table.Quest2.GetEntry(quest) == null)
                            continue;
                        if (!index.TryGetValue(quest, out var list))
                            index[quest] = list = new List<uint>();
                        if (!list.Contains(spell.Id))
                            list.Add(spell.Id);
                    }
                }

                byQuest = index;
                Diagnostics.FailureLedger.Record("quest-faction-spells", "quests with a FactionSet target spell", $"{index.Count} quests, {index.Values.Sum(l => l.Count)} spells");
            }
        }

        public static IReadOnlyList<uint> For(uint questId)
        {
            EnsureIndex();
            return byQuest.TryGetValue(questId, out var list) ? list : (IReadOnlyList<uint>)System.Array.Empty<uint>();
        }
    }
}
