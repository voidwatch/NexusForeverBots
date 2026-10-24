using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;

namespace NexusForever.WorldServer.Game.Spell
{
    /// <summary>
    /// Class builders whose resource generator is linked only by their tooltip. Engineer Pulse Blast 42276 reads "... and generate
    /// $(eff=95337.data02rawint) Volatility": effect 95337 is the VitalModifier row of 42148 "[Hidden] Engineer - Add 15 Volatility", and no
    /// proxy, proc or cast event of any table reaches 42148. Live abilities in that state: Engineer Pulse Blast, Bio Shell, Ricochet, Volatile
    /// Injection, Disruptive Module, Medic Triage, Esper Epiphany (scripts/research/tooltip_generators.py,
    /// research/decode/resources/tooltip-generators.tsv). Certain that the tooltip names the generator and its amount; likely that it is
    /// granted once per completed cast (the tooltips say "deal damage ... and generate N"), how the retail server linked it is not in the data.
    /// Abilities that reach their generator through the tables are left alone.
    /// </summary>
    public static class TooltipGenerators
    {
        private static readonly Regex citation = new(@"\$\(eff=(\d+)\.data0[12]raw", RegexOptions.IgnoreCase);
        private static readonly HashSet<uint> resourceVitals = new() { 4u, 6u, 7u, 8u, 9u, 10u, 11u, 12u, 18u, 19u, 26u };
        private static Dictionary<uint, uint[]> generators;
        private static readonly object buildLock = new();

        /// <summary>Generator spells to cast for a completed cast of the spell, empty when it has none.</summary>
        public static uint[] For(uint spell4Id)
        {
            if (generators == null)
                lock (buildLock)
                    generators ??= Build();
            return generators.TryGetValue(spell4Id, out uint[] list) ? list : System.Array.Empty<uint>();
        }

        private static Dictionary<uint, uint[]> Build()
        {
            var result = new Dictionary<uint, uint[]>();
            try
            {
                var effects = new Dictionary<uint, Spell4EffectsEntry>();
                var bySpell = new Dictionary<uint, List<Spell4EffectsEntry>>();
                foreach (Spell4EffectsEntry e in GameTableManager.Instance.Spell4Effects.Entries)
                {
                    effects[e.Id] = e;
                    if (!bySpell.TryGetValue(e.SpellId, out var list))
                        bySpell[e.SpellId] = list = new List<Spell4EffectsEntry>();
                    list.Add(e);
                }

                foreach (Spell4Entry spell in GameTableManager.Instance.Spell4.Entries)
                {
                    Spell4BaseEntry baseEntry = GameTableManager.Instance.Spell4Base.GetEntry(spell.Spell4BaseIdBaseSpell);
                    if (baseEntry == null || baseEntry.ClassIdPlayer == 0u || spell.LocalizedTextIdActionBarTooltip == 0u)
                        continue;
                    string tooltip = GameTableManager.Instance.TextEnglish.GetEntry(spell.LocalizedTextIdActionBarTooltip);
                    if (string.IsNullOrEmpty(tooltip) || !tooltip.Contains("$(eff="))
                        continue;

                    HashSet<uint> reached = null;
                    var found = new List<uint>();
                    foreach (Match match in citation.Matches(tooltip))
                    {
                        if (!uint.TryParse(match.Groups[1].Value, out uint effectId) || !effects.TryGetValue(effectId, out Spell4EffectsEntry row))
                            continue;
                        if (row.EffectType != 1u || !resourceVitals.Contains(row.DataBits00) || (int)row.DataBits01 <= 0 || row.SpellId == spell.Id)
                            continue;
                        reached ??= Reached(spell.Id, bySpell);
                        if (!reached.Contains(row.SpellId) && !found.Contains(row.SpellId))
                            found.Add(row.SpellId);
                    }
                    if (found.Count > 0)
                        result[spell.Id] = found.ToArray();
                }
                Diagnostics.FailureLedger.Record("system-info", "tooltip-only resource generators linked", $"{result.Count} spells");
            }
            catch (System.Exception exception)
            {
                Diagnostics.FailureLedger.Record("tooltip-generator-error", $"{exception.GetType().Name}: {exception.Message}");
            }
            return result;
        }

        /// <summary>Spells a cast reaches through proxies (types 19, 25, 26, 94, 96), procs and cast events, 5 levels deep.</summary>
        private static HashSet<uint> Reached(uint spell4Id, Dictionary<uint, List<Spell4EffectsEntry>> bySpell)
        {
            var seen = new HashSet<uint>();
            var queue = new Queue<(uint, int)>();
            queue.Enqueue((spell4Id, 0));
            while (queue.Count > 0)
            {
                (uint id, int depth) = queue.Dequeue();
                if (!seen.Add(id) || depth > 4)
                    continue;
                if (bySpell.TryGetValue(id, out var rows))
                    foreach (Spell4EffectsEntry e in rows)
                    {
                        if (e.EffectType is 0x13u or 0x19u or 0x1Au or 0x5Eu or 0x60u)
                        {
                            if (e.DataBits00 != 0u) queue.Enqueue((e.DataBits00, depth + 1));
                            if (e.DataBits01 != 0u) queue.Enqueue((e.DataBits01, depth + 1));
                        }
                        else if (e.EffectType == 0x0Eu && e.DataBits01 != 0u)
                            queue.Enqueue((e.DataBits01, depth + 1));
                    }
                Spell4Entry spell = GameTableManager.Instance.Spell4.GetEntry(id);
                if (spell != null)
                    foreach (uint ev in new[] { spell.Spell4IdCastEvent00, spell.Spell4IdCastEvent01, spell.Spell4IdCastEvent02, spell.Spell4IdCastEvent03 })
                        if (ev != 0u)
                            queue.Enqueue((ev, depth + 1));
            }
            return seen;
        }
    }
}
