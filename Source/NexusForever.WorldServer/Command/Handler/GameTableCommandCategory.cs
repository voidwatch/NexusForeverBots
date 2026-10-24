using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Command.Context;
using NexusForever.WorldServer.Game.RBAC.Static;
using NLog;

namespace NexusForever.WorldServer.Command.Handler
{
    /// <summary>
    /// Read-only inspection of the client game tables, for working out what undocumented ids and types refer to.
    /// </summary>
    [Command(Permission.Character, "Inspect the loaded game tables: show an entry, or find which entries contain a value.", "gt")]
    public partial class GameTableCommandCategory : CommandCategory
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();
        private const int MaxHits = 25;

        [Command(Permission.Character, "Print every field of one table entry.", "show")]
        public void HandleShow(ICommandContext context,
            [Parameter("Table name, for example Creature2 or TargetGroup.")]
            string table,
            [Parameter("Entry id.")]
            uint id)
        {
            object entry = FindTable(table)?.Entries.Cast<object>().FirstOrDefault(e => GetId(e) == id);
            if (entry == null)
            {
                context.SendMessage($"No entry {id} in {table}.");
                return;
            }

            Send(context, $"{table} {id}: {Describe(entry)}");
        }

        [Command(Permission.Character, "Print the entries of one table, one per line (first 'count', default 60).", "list")]
        public void HandleList(ICommandContext context,
            [Parameter("Table name, for example GameFormula.")]
            string table,
            [Parameter("Number of entries to show.")]
            uint count = 60u)
        {
            TableAccess access = FindTable(table);
            if (access == null)
            {
                context.SendMessage($"No table named {table}.");
                return;
            }

            int total = 0;
            foreach (object entry in access.Entries)
                if (++total <= count)
                    Send(context, $"{table} {GetId(entry)}: {Describe(entry)}");

            context.SendMessage($"{total} entries in {table}.");
        }

        [Command(Permission.Character, "List the entries of one table where any field equals the value.", "find")]
        public void HandleFind(ICommandContext context,
            [Parameter("Table name, for example Creature2.")]
            string table,
            [Parameter("Value to look for.")]
            uint value)
        {
            TableAccess access = FindTable(table);
            if (access == null)
            {
                context.SendMessage($"No table named {table}.");
                return;
            }

            int hits = 0;
            foreach (object entry in access.Entries)
            {
                string field = MatchingField(entry, value);
                if (field == null)
                    continue;

                if (++hits <= MaxHits)
                    Send(context, $"{table} {GetId(entry)} field {field}");
            }

            context.SendMessage($"{hits} match(es) in {table}{(hits > MaxHits ? $", first {MaxHits} shown" : "")}.");
        }

        [Command(Permission.Character, "Search every loaded table for a value and list the tables and fields that contain it.", "findall")]
        public void HandleFindAll(ICommandContext context,
            [Parameter("Value to look for.")]
            uint value)
        {
            var counts = new SortedDictionary<string, int>();
            foreach (TableAccess access in AllTables())
            {
                foreach (object entry in access.Entries)
                {
                    string field = MatchingField(entry, value);
                    if (field == null)
                        continue;

                    string key = $"{access.Name}.{field}";
                    counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
                }
            }

            if (counts.Count == 0)
            {
                context.SendMessage($"{value} does not appear in any table.");
                return;
            }

            foreach (KeyValuePair<string, int> pair in counts.OrderBy(p => p.Value).Take(MaxHits))
                Send(context, $"{pair.Key}: {pair.Value} entr{(pair.Value == 1 ? "y" : "ies")}");
        }

        [Command(Permission.Character, "List spell effects that were cast but have no handler, most frequent first.", "effects")]
        public void HandleEffects(ICommandContext context)
        {
            var list = NexusForever.WorldServer.Game.Spell.Spell.UnhandledEffects.OrderByDescending(p => p.Value.Count).ToList();
            if (list.Count == 0)
            {
                context.SendMessage("No unhandled spell effects have been cast since the server started.");
                return;
            }

            foreach (var pair in list.Take(MaxHits))
                Send(context, $"{pair.Key}: {pair.Value.Count}x, example spell {pair.Value.ExampleSpellId}");
        }

        [Command(Permission.Character, "Dump a spell with all its effects and the prerequisite rows it depends on.", "spell")]
        public void HandleSpell(ICommandContext context,
            [Parameter("Spell4 id.")]
            uint id)
        {
            var spell = FindTable("Spell4")?.Entries.OfType<NexusForever.Shared.GameTable.Model.Spell4Entry>().FirstOrDefault(e => e.Id == id);
            if (spell == null)
            {
                context.SendMessage($"No Spell4 {id}.");
                return;
            }

            Send(context, $"Spell4 {id}: {Describe(spell)}");
            foreach (var effect in NexusForever.WorldServer.Game.Spell.GlobalSpellManager.Instance.GetSpell4EffectEntries(id))
                Send(context, $"  effect {(NexusForever.WorldServer.Game.Spell.Static.SpellEffectType)effect.EffectType}: {Describe(effect)}");

            var prerequisites = FindTable("Prerequisite")?.Entries.OfType<NexusForever.Shared.GameTable.Model.PrerequisiteEntry>().ToDictionary(e => e.Id);
            if (prerequisites == null)
                return;

            foreach ((string label, uint prerequisiteId) in new[]
            {
                ("caster cast", spell.PrerequisiteIdCasterCast), ("target cast", spell.PrerequisiteIdTargetCast),
                ("caster persistence", spell.PrerequisiteIdCasterPersistence), ("target persistence", spell.PrerequisiteIdTargetPersistence)
            })
            {
                if (prerequisiteId != 0u && prerequisites.TryGetValue(prerequisiteId, out var row))
                    Send(context, $"  prerequisite {label} {prerequisiteId}: {Describe(row)}");
            }

            foreach (uint runnerId in spell.PrerequisiteIdRunners.Where(r => r != 0u))
                if (prerequisites.TryGetValue(runnerId, out var runner))
                    Send(context, $"  prerequisite runner {runnerId}: {Describe(runner)}");
        }

        [Command(Permission.Character, "Show prerequisite rows that use a prerequisite type and the spells that depend on them, to work out what the type means.", "prereq")]
        public void HandlePrereq(ICommandContext context,
            [Parameter("Prerequisite type id.")]
            uint type)
        {
            var rows = FindTable("Prerequisite")?.Entries.OfType<NexusForever.Shared.GameTable.Model.PrerequisiteEntry>()
                .Where(r => r.PrerequisiteTypeId.Contains(type)).ToList();
            if (rows == null || rows.Count == 0)
            {
                context.SendMessage($"No prerequisite row uses type {type}.");
                return;
            }

            var ids = rows.Select(r => r.Id).ToHashSet();
            var spells = FindTable("Spell4").Entries.OfType<NexusForever.Shared.GameTable.Model.Spell4Entry>()
                .Where(sp => ids.Contains(sp.PrerequisiteIdCasterCast) || ids.Contains(sp.PrerequisiteIdTargetCast)).ToList();

            Send(context, $"Type {type}: {rows.Count} prerequisite row(s), {spells.Count} spell(s) use them as cast prerequisite.");
            foreach (var row in rows.Take(8))
                Send(context, $"  row {row.Id}: {Describe(row)}");
            foreach (var sp in spells.Take(12))
                Send(context, $"  spell {sp.Id}: {sp.Description}");
        }

        [Command(Permission.Character, "Group all effects of one spell effect type by a data field, with example spell names, to work out what the field means.", "effectdata")]
        public void HandleEffectData(ICommandContext context,
            [Parameter("Spell effect type name (VitalModifier) or number.")]
            string type,
            [Parameter("Field to group by, for example DataBits00 (default).")]
            string field = null)
        {
            field ??= "DataBits00";
            uint typeId;
            if (!uint.TryParse(type, out typeId))
            {
                if (!Enum.TryParse(type, true, out NexusForever.WorldServer.Game.Spell.Static.SpellEffectType parsed))
                {
                    context.SendMessage($"Unknown effect type {type}.");
                    return;
                }

                typeId = (uint)parsed;
            }

            var fieldInfo = typeof(NexusForever.Shared.GameTable.Model.Spell4EffectsEntry).GetField(field);
            if (fieldInfo == null)
            {
                context.SendMessage($"Spell4Effects has no field {field}.");
                return;
            }

            var spells = FindTable("Spell4").Entries.OfType<NexusForever.Shared.GameTable.Model.Spell4Entry>().ToDictionary(sp => sp.Id);
            var effects = FindTable("Spell4Effects").Entries.OfType<NexusForever.Shared.GameTable.Model.Spell4EffectsEntry>().Where(e => e.EffectType == typeId).ToList();

            Send(context, $"{effects.Count} effect(s) of type {typeId}, grouped by {field}:");
            foreach (var group in effects.GroupBy(e => fieldInfo.GetValue(e)?.ToString()).OrderByDescending(g => g.Count()).Take(14))
            {
                string examples = string.Join(" | ", group.Select(e => e.SpellId).Distinct().Take(3)
                    .Select(id => spells.TryGetValue(id, out var sp) ? $"{id} {sp.Description}" : id.ToString()));
                Send(context, $"  {field}={group.Key}: {group.Count()}x, e.g. {examples}");
            }
        }

        [Command(Permission.Character, "Group effects of one type by up to two data fields and show decoded example rows (raw and float) with the spell names, including the spell a Proc/Proxy casts.", "effectdump")]
        public void HandleEffectDump(ICommandContext context,
            [Parameter("Spell effect type name (Proc) or number.")]
            string type,
            [Parameter("First field to group by, for example DataBits00.")]
            string fieldA = null,
            [Parameter("Optional second field to group by.")]
            string fieldB = null,
            [Parameter("Number of groups to show (default 12).")]
            string count = null)
        {
            fieldA ??= "DataBits00";
            int groups = int.TryParse(count, out int n) ? n : 12;
            uint typeId;
            if (!uint.TryParse(type, out typeId))
            {
                if (!Enum.TryParse(type, true, out NexusForever.WorldServer.Game.Spell.Static.SpellEffectType parsed))
                {
                    context.SendMessage($"Unknown effect type {type}.");
                    return;
                }

                typeId = (uint)parsed;
            }

            var entryType = typeof(NexusForever.Shared.GameTable.Model.Spell4EffectsEntry);
            var infoA = entryType.GetField(fieldA);
            var infoB = fieldB != null ? entryType.GetField(fieldB) : null;
            if (infoA == null || (fieldB != null && infoB == null))
            {
                context.SendMessage("Spell4Effects has no such field.");
                return;
            }

            var spells = FindTable("Spell4").Entries.OfType<NexusForever.Shared.GameTable.Model.Spell4Entry>().ToDictionary(sp => sp.Id);
            var effects = FindTable("Spell4Effects").Entries.OfType<NexusForever.Shared.GameTable.Model.Spell4EffectsEntry>().Where(e => e.EffectType == typeId).ToList();

            string Name(uint id) => spells.TryGetValue(id, out var sp) ? sp.Description : "?";
            string Bits(uint raw)
            {
                float f = BitConverter.Int32BitsToSingle((int)raw);
                bool sane = raw > 0x1000000u && !float.IsNaN(f) && Math.Abs(f) > 1e-6f && Math.Abs(f) < 1e7f;
                return sane ? $"{raw}({f:0.###}f)" : raw.ToString();
            }

            Send(context, $"{effects.Count} effect(s) of type {typeId} grouped by {fieldA}{(fieldB != null ? " + " + fieldB : "")}:");
            foreach (var group in effects.GroupBy(e => $"{infoA.GetValue(e)}{(infoB != null ? "/" + infoB.GetValue(e) : "")}").OrderByDescending(g => g.Count()).Take(groups))
            {
                Send(context, $"  {group.Key}: {group.Count()}x");
                foreach (var e in group.OrderBy(e => e.SpellId).GroupBy(e => e.SpellId).Select(g => g.First()).Take(3))
                {
                    string child = (typeId == 14u || typeId == 26u) && spells.ContainsKey(typeId == 14u ? e.DataBits01 : e.DataBits00)
                        ? $" -> casts {(typeId == 14u ? e.DataBits01 : e.DataBits00)} {Name(typeId == 14u ? e.DataBits01 : e.DataBits00)}"
                        : "";
                    Send(context, $"    spell {e.SpellId} {Name(e.SpellId)} | dur {e.DurationTime} tick {e.TickTime} target {e.TargetFlags} | D0 {e.DataBits00} D1 {Bits(e.DataBits01)} D2 {Bits(e.DataBits02)} D3 {Bits(e.DataBits03)} D4 {Bits(e.DataBits04)} D5 {Bits(e.DataBits05)}{child}");
                }
            }
        }

        [Command(Permission.Character, "Show a quest objective, and for target group objectives the creatures the group contains, to see why kills do not count.", "objective")]
        public void HandleObjective(ICommandContext context,
            [Parameter("QuestObjective id.")]
            uint id)
        {
            var objective = FindTable("QuestObjective")?.Entries.OfType<NexusForever.Shared.GameTable.Model.QuestObjectiveEntry>().FirstOrDefault(e => e.Id == id);
            if (objective == null)
            {
                context.SendMessage($"No QuestObjective {id}.");
                return;
            }

            Send(context, $"QuestObjective {id}: type {(NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType)objective.Type} ({objective.Type}), data {objective.Data}, count {objective.Count}, flags {objective.Flags} | {Text(objective.LocalizedTextIdFull)} | {Text(objective.LocalizedTextIdShort)}");
            if (objective.TargetGroupIdRewardPane != 0u)
            {
                Send(context, $"  reward pane structure: {NexusForever.WorldServer.Game.Quest.TargetGroupHelper.Structure(objective.TargetGroupIdRewardPane)}");
                var pane = NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(objective.TargetGroupIdRewardPane);
                Send(context, $"  reward pane resolves to {pane.Count} creature(s), {NexusForever.WorldServer.Bots.BotQuests.CountSpawns(pane)} spawn(s) in the world database");
                foreach (uint creature in pane.Take(4))
                    Send(context, $"  {NexusForever.WorldServer.Game.Quest.TargetGroupHelper.DescribeCreature(creature)}");
            }

            if (objective.Type != 8u && objective.Type != 16u && objective.Type != 14u && objective.Type != 18u && objective.Type != 23u)
                return;

            Send(context, $"  structure: {NexusForever.WorldServer.Game.Quest.TargetGroupHelper.Structure(objective.Data)}");
            var creatures = NexusForever.WorldServer.Game.Quest.TargetGroupHelper.GetCreatures(objective.Data);
            Send(context, $"  target group {objective.Data}: {creatures.Count} explicit creature(s), {NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(objective.Data).Count} in all (10 = all of the groups below, 11 = any, 3/4/12 = faction, not faction, race)");
            var all = NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(objective.Data);
            Send(context, $"  spawns in the world database for the {all.Count} resolved creature(s): {NexusForever.WorldServer.Bots.BotQuests.CountSpawns(all)}; creature levels {(all.Count == 0 ? "-" : string.Join(",", all.Select(c => GameTableManager.Instance.Creature2.GetEntry(c)).Where(c => c != null).Select(c => c.MinLevel).Distinct().OrderBy(l => l).Take(12)))}");
            foreach (uint creature in creatures.Take(6))
                Send(context, $"  {NexusForever.WorldServer.Game.Quest.TargetGroupHelper.DescribeCreature(creature)}");
        }

        [Command(Permission.Character, "Summarise the objectives of one type: reward pane group, where it resolves, world location indicators.", "objstats")]
        public void HandleObjStats(ICommandContext context,
            [Parameter("QuestObjective type id.")]
            uint type)
        {
            var gtm = NexusForever.Shared.GameTable.GameTableManager.Instance;
            var objectives = FindTable("QuestObjective").Entries.OfType<NexusForever.Shared.GameTable.Model.QuestObjectiveEntry>().Where(e => e.Type == type).ToList();
            int withPane = objectives.Count(o => o.TargetGroupIdRewardPane != 0u);
            int paneResolves = objectives.Count(o => o.TargetGroupIdRewardPane != 0u && NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(o.TargetGroupIdRewardPane).Count > 0);
            int indicator = objectives.Count(o => o.WorldLocationsIdIndicator00 != 0u);
            int dataIsCreature = objectives.Count(o => gtm.Creature2.GetEntry(o.Data) != null);
            int dataIsGroup = objectives.Count(o => NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(o.Data).Count > 0);
            Send(context, $"data as id: Item {objectives.Count(o => gtm.Item.GetEntry(o.Data) != null)}, VirtualItem {objectives.Count(o => gtm.VirtualItem.GetEntry(o.Data) != null)}, Spell4 {objectives.Count(o => gtm.Spell4.GetEntry(o.Data) != null)}, WorldZone {objectives.Count(o => gtm.WorldZone.GetEntry(o.Data) != null)}, Quest2 {objectives.Count(o => gtm.Quest2.GetEntry(o.Data) != null)}, VirtualItem table size {gtm.VirtualItem.Entries.Count()}");
            Send(context, $"type {type}: {objectives.Count} objectives, reward pane set {withPane} (resolves to creatures {paneResolves}), world location indicator {indicator}, data is a creature id {dataIsCreature}, data is a non-empty target group {dataIsGroup}, count>1 {objectives.Count(o => o.Count > 1u)}, with time limit {objectives.Count(o => o.MaxTimeAllowedMS != 0u)}");
        }

        [Command(Permission.Character, "Summarise quest rewards: types, flags and how many quests offer a choice.", "rewardstats")]
        public void HandleRewardStats(ICommandContext context)
        {
            var rewards = FindTable("Quest2Reward").Entries.OfType<NexusForever.Shared.GameTable.Model.Quest2RewardEntry>().ToList();
            Send(context, $"{rewards.Count} rewards for {rewards.Select(r => r.Quest2Id).Distinct().Count()} quests");
            Send(context, "by type and flags: " + string.Join(", ", rewards.GroupBy(r => (r.Quest2RewardTypeId, r.Flags)).OrderBy(g => g.Key).Select(g => $"type {g.Key.Item1} flags {g.Key.Item2}: {g.Count()}")));
            var perQuest = rewards.GroupBy(r => r.Quest2Id).ToList();
            Send(context, "rewards per quest: " + string.Join(", ", perQuest.GroupBy(g => g.Count()).OrderBy(g => g.Key).Select(g => $"{g.Key} rewards x{g.Count()}")));
            Send(context, "quests with 2+ item rewards all flags 0: " + perQuest.Count(g => g.Count(r => r.Quest2RewardTypeId == 1u && r.Flags == 0u) >= 2));
            var example = perQuest.FirstOrDefault(g => g.Count(r => r.Quest2RewardTypeId == 1u) >= 3);
            if (example != null)
                Send(context, $"example quest {example.Key}: " + string.Join("; ", example.Select(r => $"id {r.Id} type {r.Quest2RewardTypeId} obj {r.ObjectId} x{r.ObjectAmount} flags {r.Flags}")));
            var objectives = FindTable("QuestObjective").Entries.OfType<NexusForever.Shared.GameTable.Model.QuestObjectiveEntry>().ToDictionary(o => o.Id);
            int pushedMatches = 0, pushedTotal = 0;
            foreach (var quest in FindTable("Quest2").Entries.OfType<NexusForever.Shared.GameTable.Model.Quest2Entry>().Where(q => q.VirtualItemIdPushed00 != 0u))
            {
                pushedTotal++;
                var virtualCollect = quest.Objectives.Where(o => objectives.ContainsKey(o)).Select(o => objectives[o]).Where(o => o.Type == 32u).Select(o => o.Data).ToList();
                if (virtualCollect.Contains(quest.VirtualItemIdPushed00) || virtualCollect.Contains(quest.VirtualItemIdPushed01))
                    pushedMatches++;
                if (pushedTotal <= 3)
                    Send(context, $"pushed example quest {quest.Id}: items {quest.VirtualItemIdPushed00}x{quest.VirtualItemPushedCount00}, {quest.VirtualItemIdPushed01}x{quest.VirtualItemPushedCount01} flags {quest.VirtualItemPushedObjectiveFlagsEnum00}, VirtualCollect data {string.Join(",", virtualCollect)}");
            }
            Send(context, $"quests pushing virtual items {pushedTotal}, of which the item is the VirtualCollect data of the quest: {pushedMatches}");
            Send(context, "quest faction reputation fields set: " + FindTable("Quest2").Entries.OfType<NexusForever.Shared.GameTable.Model.Quest2Entry>().Count(q => q.Faction2IdRewardReputation00 != 0u) + ", override set: " + FindTable("Quest2").Entries.OfType<NexusForever.Shared.GameTable.Model.Quest2Entry>().Count(q => q.RewardReputationOverride00 != 0f) + ", virtual items pushed: " + FindTable("Quest2").Entries.OfType<NexusForever.Shared.GameTable.Model.Quest2Entry>().Count(q => q.VirtualItemIdPushed00 != 0u));
        }

        private static bool Untitled(NexusForever.Shared.GameTable.Model.Quest2Entry entry)
        {
            var table = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
            return entry.LocalizedTextIdTitle == 0u || string.IsNullOrWhiteSpace(table?.GetEntry(entry.LocalizedTextIdTitle));
        }

        private static string Text(uint id)
        {
            var table = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
            return id == 0u ? "" : table?.GetEntry(id) ?? $"(no text {id})";
        }

        [Command(Permission.Character, "Show a localised text by id (English).", "text")]
        public void HandleText(ICommandContext context,
            [Parameter("LocalizedText id.")]
            uint id)
        {
            Send(context, $"{id}: {Text(id)}");
        }

        [Command(Permission.Character, "Show a quest with its title, texts and objectives with their texts.", "quest")]
        public void HandleQuest(ICommandContext context,
            [Parameter("Quest2 id.")]
            uint id)
        {
            var quest = GameTableManager.Instance.Quest2.GetEntry(id);
            if (quest == null)
            {
                Send(context, $"No quest {id}.");
                return;
            }

            Send(context, $"Quest {id}: {Text(quest.LocalizedTextIdTitle)} | level {quest.ConLevel} type {quest.Type} zone {quest.WorldZoneId} flags {quest.Flags} prerequisite {quest.PrerequisiteId} prerequisite quests [{string.Join(",", quest.PrerequisiteQuests.Where(q => q != 0u))}]");
            Send(context, $"  text: {Text(quest.LocalizedTextIdText)}");
            foreach (uint objectiveId in quest.Objectives.Where(o => o != 0u))
            {
                var objective = GameTableManager.Instance.QuestObjective.GetEntry(objectiveId);
                if (objective != null)
                    Send(context, $"  objective {objectiveId}: type {(NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType)objective.Type} ({objective.Type}) data {objective.Data} count {objective.Count} flags {objective.Flags} | {Text(objective.LocalizedTextIdFull)} | {Text(objective.LocalizedTextIdShort)}");
            }
        }

        [Command(Permission.Character, "Show a public event with its objectives, or 'stats' (id 0) for the distribution of objective types.", "event")]
        public void HandleEvent(ICommandContext context,
            [Parameter("PublicEvent id, 0 for statistics.")]
            uint id)
        {
            var gtm = GameTableManager.Instance;
            if (id == 0u)
            {
                Send(context, $"{gtm.PublicEvent.Entries.Count()} events, {gtm.PublicEventObjective.Entries.Count()} objectives");
                Send(context, "event types: " + string.Join(", ", gtm.PublicEvent.Entries.GroupBy(e => e.PublicEventTypeEnum).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}")));
                Send(context, "objective types: " + string.Join(", ", gtm.PublicEventObjective.Entries.GroupBy(e => e.PublicEventObjectiveTypeEnum).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}")));
                var eventIds = FindTable("QuestObjective").Entries.OfType<NexusForever.Shared.GameTable.Model.QuestObjectiveEntry>().Where(o => o.Type == 25u).Select(o => o.Data).Distinct().ToList();
                Send(context, $"CompleteEvent objectives name {eventIds.Count} distinct data values, {eventIds.Count(e => gtm.PublicEvent.GetEntry(e) != null)} are PublicEvent ids");
                return;
            }

            var ev = gtm.PublicEvent.GetEntry(id);
            if (ev == null)
            {
                Send(context, $"No public event {id}.");
                return;
            }

            Send(context, $"Event {id}: {Text(ev.LocalizedTextIdName)} | {Describe(ev)}");
            foreach (var o in gtm.PublicEventObjective.Entries.Where(o => o.PublicEventId == id))
                Send(context, $"  objective {o.Id}: type {o.PublicEventObjectiveTypeEnum} count {o.Count} object {o.ObjectId} flags {o.PublicEventObjectiveFlags}/{o.PublicEventObjectiveTypeSpecificFlags} parent {o.PublicEventObjectiveIdParent} location {o.WorldLocation2Id} | {Text(o.LocalizedTextId)}");
        }

        [Command(Permission.Character, "Audit every quest against the world database: giver, receiver and objective targets with no spawn. Full list in /cache/quest-check.txt.", "questcheck")]
        public void HandleQuestCheck(ICommandContext context)
        {
            var spawned = new HashSet<uint>(NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Select(s => s.Creature));
            var lines = new List<string>();
            var counts = new SortedDictionary<string, int>();

            void Bad(string kind, uint quest, string detail)
            {
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                var q = GameTableManager.Instance.Quest2.GetEntry(quest);
                lines.Add($"{kind}\tquest {quest}\t{detail}\ttype {q?.Type} flags {q?.Flags} chained {(q != null && q.PrerequisiteQuests.Any(p => p != 0u) ? 1 : 0)} repeat {q?.QuestRepeatPeriodEnum} zone {q?.WorldZoneId}");
            }

            int total = 0;
            foreach (var info in NexusForever.WorldServer.Game.Quest.GlobalQuestManager.Instance.GetAllQuestInfo())
            {
                total++;
                ushort id = (ushort)info.Entry.Id;
                if (NexusForever.WorldServer.Game.Quest.CutContent.IsCutQuest(info.Entry))
                {
                    Bad("cut", id, "quest in or handed in on Halon Ring or Murkmire (never released), excluded from the other checks");
                    continue;
                }
                if (NexusForever.WorldServer.Game.Quest.CutContent.IsTestQuest(info.Entry))
                {
                    Bad("test-map", id, "handed in on an unnamed developer test map, not released content, excluded from the other checks");
                    continue;
                }
                bool communicator = NexusForever.WorldServer.Game.Quest.GlobalQuestManager.Instance.GetQuestCommunicatorMessages(id).Any();
                var givers = NexusForever.WorldServer.Game.Quest.GlobalQuestManager.Instance.GetQuestGivers(id).ToList();
                var receivers = NexusForever.WorldServer.Game.Quest.GlobalQuestManager.Instance.GetQuestReceivers(id).ToList();
                if (NexusForever.WorldServer.Game.Quest.CutContent.IsDevQuest(info.Entry, givers.Concat(receivers).Distinct().ToList()))
                {
                    Bad("dev-content", id, "Quest Test Island / PublicEventIsland or only placeholder/prototype/test NPCs, excluded from the other checks");
                    continue;
                }
                if (NexusForever.WorldServer.Game.Quest.CutContent.IsUnreleasedZoneQuest(info.Entry))
                {
                    Bad("unreleased-zone", id, "Dreadmoor or Coralus (never released), excluded from the other checks");
                    continue;
                }
                if (Untitled(info.Entry))
                {
                    // 965 quests have no title; none of the 677 without a giver has an archived Jabbithole page, the 38 that have one show
                    // level 0, xp 0 and an object or nothing as starter: hidden helper quests that scripts and objects drive, never offered by an NPC
                    Bad("untitled-internal", id, "no title text: hidden helper quest, excluded from the giver/receiver/objective checks");
                    continue;
                }
                bool contract = info.IsContract();
                if (contract)
                    Bad("contract", id, "contract: taken and handed in at the Contract Board (interaction event 85 ContractBoardOpen, not implemented), giver/receiver checks skipped");
                bool imbuement = NexusForever.WorldServer.Game.Quest.ItemImbuements.IsImbuementQuest(id);
                bool itemStart = imbuement || GameTableManager.Instance.Item.Entries.Any(i => i.Quest2IdActivation == id);

                if (!communicator && !itemStart && !contract)
                {
                    if (givers.Count == 0)
                        Bad("no-giver", id, "no creature gives it and no communicator message or item starts it");
                    else if (!givers.Any(spawned.Contains))
                        Bad("giver-not-spawned", id, $"givers {string.Join(",", givers.Take(5))}");
                }

                if (!info.IsCommunicatorReceived() && !info.IsAutoComplete() && !imbuement && !contract)
                {
                    if (receivers.Count == 0)
                        Bad("no-receiver", id, "no creature receives it");
                    else if (!receivers.Any(spawned.Contains))
                        Bad("receiver-not-spawned", id, $"receivers {string.Join(",", receivers.Take(5))}");
                }

                if (info.Entry.PrerequisiteId != 0u)
                    foreach (uint unhandled in NexusForever.WorldServer.Game.Prerequisite.PrerequisiteManager.Instance.UnhandledTypes(info.Entry.PrerequisiteId))
                        Bad($"prerequisite-type-{unhandled}", id, $"prerequisite row {info.Entry.PrerequisiteId} uses a type without a handler (strict, so bots and players cannot take it)");

                foreach (var objective in info.Objectives)
                {
                    var type = objective.Type;
                    HashSet<uint> targets = type switch
                    {
                        NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.KillCreature or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateEntity or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateEntity2
                            or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.TalkTo or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.SucceedCSI => objective.Entry.Data == 0u ? null : new HashSet<uint> { objective.Entry.Data },
                        NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.KillTargetGroup or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.KillTargetGroups or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateTargetGroup
                            or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateTargetGroupChecklist or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.TalkToTargetGroup => NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(objective.Entry.Data),
                        NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.VirtualCollect => NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(objective.Entry.TargetGroupIdRewardPane),
                        _ => null
                    };

                    if (targets == null)
                        continue;

                    if (targets.Count == 0)
                        Bad("objective-group-empty", id, $"objective {objective.Id} {type} data {objective.Entry.Data}");
                    else if (targets.All(t => GameTableManager.Instance.Creature2.GetEntry(t) == null))
                        Bad("objective-creature-missing", id, $"objective {objective.Id} {type} data {objective.Entry.Data} (no Creature2 row: table gap, nothing to spawn)");
                    else if (!targets.Any(spawned.Contains))
                        Bad("objective-not-spawned", id, $"objective {objective.Id} {type} data {objective.Entry.Data} ({targets.Count} creatures, none spawned)");
                }
            }

            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("CachePath") ?? System.AppContext.BaseDirectory, "quest-check.txt"), lines.OrderBy(l => l));
            Send(context, $"{total} quests checked, {lines.Count} problems: " + string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
        }

        [Command(Permission.Character, "List quests in released zones whose chain leads into cut content (Halon Ring, Murkmire), and released quests that need a cut quest. Writes /cache/cut-entrypoints.txt.", "cutentry")]
        public void HandleCutEntry(ICommandContext context)
        {
            var quests = GameTableManager.Instance.Quest2.Entries.ToList();
            var cut = new HashSet<uint>(quests.Where(q => NexusForever.WorldServer.Game.Quest.CutContent.IsCutQuest(q)).Select(q => q.Id));
            var lines = new List<string>();
            int leadsIn = 0, needsCut = 0, deadEnd = 0;

            foreach (var q in quests.Where(q => !cut.Contains(q.Id)).OrderBy(q => q.Id))
            {
                var next = quests.Where(n => n.PrerequisiteQuests.Contains(q.Id)).ToList();
                var cutNext = next.Where(n => cut.Contains(n.Id)).ToList();
                if (cutNext.Count > 0)
                {
                    leadsIn++;
                    bool onlyCut = next.Count == cutNext.Count;
                    if (onlyCut)
                        deadEnd++;
                    lines.Add($"leads-into-cut\tquest {q.Id}\tzone {q.WorldZoneId}\tnext-cut {string.Join(",", cutNext.Select(n => n.Id))}\tonly-continuation-is-cut {(onlyCut ? 1 : 0)}\tcompletable yes (the quest itself does not need the cut quest)");
                }

                var cutPre = q.PrerequisiteQuests.Where(p => p != 0u && cut.Contains(p)).ToList();
                if (cutPre.Count > 0)
                {
                    needsCut++;
                    lines.Add($"needs-cut-quest\tquest {q.Id}\tzone {q.WorldZoneId}\trequires {string.Join(",", cutPre)}\tcompletable no (prerequisite quest is cut, never offered)");
                }
            }

            var cutWorlds = GameTableManager.Instance.WorldLocation2.Entries.Where(l => NexusForever.WorldServer.Game.Quest.CutContent.IsCutZone(l.WorldZoneId)).Count();
            lines.Add($"summary\tcut quests {cut.Count}\tleads-into-cut {leadsIn}\tonly-continuation-cut {deadEnd}\tneeds-cut-quest {needsCut}\tworld locations in cut zones {cutWorlds}");
            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("CachePath") ?? System.AppContext.BaseDirectory, "cut-entrypoints.txt"), lines);
            Send(context, lines[^1]);
        }

        [Command(Permission.Character, "Write /cache/needed-creatures.tsv (quest creatures without spawn, with name, role and zones) and /cache/zone-worlds.tsv for the spawn research scripts.", "exportneeded")]
        public void HandleExportNeeded(ICommandContext context)
        {
            var qm = NexusForever.WorldServer.Game.Quest.GlobalQuestManager.Instance;
            var spawned = new HashSet<uint>(NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns(false).Select(s => s.Creature));
            var needed = new Dictionary<uint, (HashSet<string> Roles, HashSet<uint> Quests, HashSet<uint> Zones)>();
            var indicatorsByCreature = new Dictionary<uint, HashSet<uint>>();

            void Add(uint creature, string role, uint quest, uint zone)
            {
                if (creature == 0u || spawned.Contains(creature))
                    return;
                if (!needed.TryGetValue(creature, out var n))
                    needed[creature] = n = (new HashSet<string>(), new HashSet<uint>(), new HashSet<uint>());
                n.Roles.Add(role);
                n.Quests.Add(quest);
                if (zone != 0u)
                    n.Zones.Add(zone);
            }

            foreach (var info in qm.GetAllQuestInfo())
            {
                ushort id = (ushort)info.Entry.Id;
                uint zone = info.Entry.WorldZoneId;
                if (NexusForever.WorldServer.Game.Quest.CutContent.IsCutQuest(info.Entry) || NexusForever.WorldServer.Game.Quest.CutContent.IsTestQuest(info.Entry)
                    || NexusForever.WorldServer.Game.Quest.CutContent.IsUnreleasedZoneQuest(info.Entry) || Untitled(info.Entry)
                    || NexusForever.WorldServer.Game.Quest.CutContent.IsDevQuest(info.Entry, qm.GetQuestGivers(id).Concat(qm.GetQuestReceivers(id)).Distinct().ToList()))
                    continue;
                foreach (uint g in qm.GetQuestGivers(id))
                    Add(g, "giver", id, zone);
                foreach (uint r in qm.GetQuestReceivers(id))
                    Add(r, "receiver", id, zone);
                foreach (var objective in info.Objectives)
                {
                    var type = objective.Type;
                    HashSet<uint> targets = type switch
                    {
                        NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.KillCreature or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateEntity or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateEntity2
                            or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.TalkTo or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.SucceedCSI => objective.Entry.Data == 0u ? null : new HashSet<uint> { objective.Entry.Data },
                        NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.KillTargetGroup or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.KillTargetGroups or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateTargetGroup
                            or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.ActivateTargetGroupChecklist or NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.TalkToTargetGroup => NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(objective.Entry.Data),
                        NexusForever.WorldServer.Game.Quest.Static.QuestObjectiveType.VirtualCollect => NexusForever.WorldServer.Game.Quest.TargetGroupHelper.ResolveAll(objective.Entry.TargetGroupIdRewardPane),
                        _ => null
                    };

                    // where the objective points to, for creatures with no spawn at all (placement fallback)
                    uint[] indicators = { objective.Entry.WorldLocationsIdIndicator00, objective.Entry.WorldLocationsIdIndicator01, objective.Entry.WorldLocationsIdIndicator02, objective.Entry.WorldLocationsIdIndicator03 };
                    if (targets != null && targets.Count > 0 && targets.Count <= 60)
                        foreach (uint t in targets)
                        {
                            if (!indicatorsByCreature.TryGetValue(t, out var set))
                                indicatorsByCreature[t] = set = new HashSet<uint>();
                            foreach (uint ind in indicators.Where(x => x != 0u))
                                set.Add(ind);
                        }

                    // a group with a spawned member is already satisfiable
                    if (targets == null || targets.Count == 0 || targets.Any(spawned.Contains))
                        continue;
                    foreach (uint t in targets)
                        Add(t, "target", id, zone);
                }
            }

            string dir = System.Environment.GetEnvironmentVariable("CachePath") ?? System.AppContext.BaseDirectory;
            var lines = needed.OrderBy(n => n.Key).Select(n =>
            {
                var c = GameTableManager.Instance.Creature2.GetEntry(n.Key);
                string name = c == null ? "" : Text(c.LocalizedTextIdName).Replace('\t', ' ');
                return $"{n.Key}\t{name}\t{string.Join(",", n.Value.Roles)}\t{n.Value.Quests.Count}\t{string.Join(",", n.Value.Zones)}\t{c?.FactionId}\t{c?.Creature2DisplayGroupId}\t{c?.MinLevel}\t{c?.MaxLevel}\t{c?.Description?.Replace('\t', ' ')}\t{(indicatorsByCreature.TryGetValue(n.Key, out var inds) ? string.Join(",", inds) : "")}";
            });
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "needed-creatures.tsv"), lines);

            var zones = GameTableManager.Instance.WorldLocation2.Entries.Where(l => l.WorldZoneId != 0u)
                .GroupBy(l => (l.WorldZoneId, l.WorldId)).OrderBy(g => g.Key)
                .Select(g => $"{g.Key.WorldZoneId}\t{Text(GameTableManager.Instance.WorldZone.GetEntry(g.Key.WorldZoneId)?.LocalizedTextIdName ?? 0u)}\t{g.Key.WorldId}\t{g.Count()}");
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "zone-worlds.tsv"), zones);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "zones.tsv"),
                GameTableManager.Instance.WorldZone.Entries.Select(z => $"{z.Id}\t{Text(z.LocalizedTextIdName)}\t{z.ParentZoneId}"));
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "worlds.tsv"),
                GameTableManager.Instance.World.Entries.Select(w => $"{w.Id}\t{w.Type}\t{Text(w.LocalizedTextIdName)}\t{w.AssetPath}"));
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "locations.tsv"),
                GameTableManager.Instance.WorldLocation2.Entries.Where(l => l.WorldId != 0u)
                    .Select(l => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}\t{4}\t{5}", l.Id, l.WorldId, l.WorldZoneId, l.Position0, l.Position1, l.Position2)));
            {
                var textTable = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "creatures.tsv"),
                    GameTableManager.Instance.Creature2.Entries.Select(c => $"{c.Id}\t{textTable?.GetEntry(c.LocalizedTextIdName)?.Replace('\t', ' ')}\t{c.MinLevel}\t{c.MaxLevel}\t{c.Creature2TierId}\t{c.Creature2DifficultyId}\t{c.FactionId}\t{c.Description?.Replace('\t', ' ')}"));
            }
            Send(context, $"{needed.Count} unspawned quest creatures written to needed-creatures.tsv.");
        }

        [Command(Permission.Character, "Turn /cache/research-spawns.tsv (creature, world, x, z, source) into /cache/research-spawns.sql with terrain heights; sibling spawns of the same creature supply type, display and faction.", "importspawns")]
        public void HandleImportSpawns(ICommandContext context)
        {
            string dir = System.Environment.GetEnvironmentVariable("CachePath") ?? System.AppContext.BaseDirectory;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sql = new List<string> { "DELETE FROM entity WHERE id >= 9000000 AND id < 9500000;" };
            uint nextId = 9000000;
            int noHeight = 0;
            int summonSkipped = 0;
            var bySource = new SortedDictionary<string, int>();
            var firstDisplay = new Dictionary<uint, uint>();
            foreach (var e in GameTableManager.Instance.Creature2DisplayGroupEntry.Entries)
                firstDisplay.TryAdd(e.Creature2DisplayGroupId, e.Creature2DisplayInfoId);
            var locationsByWorld = GameTableManager.Instance.WorldLocation2.Entries.GroupBy(l => l.WorldId).ToDictionary(g => g.Key, g => g.ToArray());

            // creatures that spells summon (the whirlwinds of a boss fight, adds) are not standing around in an instance, a script spawns them
            var summoned = new HashSet<uint>(GameTableManager.Instance.Spell4Effects.Entries
                .Where(e => e.EffectType == (uint)NexusForever.WorldServer.Game.Spell.Static.SpellEffectType.SummonCreature && e.DataBits00 != 0u)
                .Select(e => e.DataBits00));
            Send(context, $"importspawns: tables ready after {watch.ElapsedMilliseconds} ms");
            int lineNumber = 0;
            foreach (string line in System.IO.File.ReadLines(System.IO.Path.Combine(dir, "research-spawns.tsv")))
            {
                if (++lineNumber % 5000 == 0)
                    Send(context, $"importspawns: {lineNumber} rows after {watch.ElapsedMilliseconds} ms");
                string[] f = line.Split('\t');
                if (f.Length < 4 || !uint.TryParse(f[0], out uint creature) || !uint.TryParse(f[1], out uint world))
                    continue;
                float x = float.Parse(f[2], inv), z = float.Parse(f[3], inv);
                if (System.Math.Abs(x) > 40000f || System.Math.Abs(z) > 40000f)
                    continue;
                var worldEntry = GameTableManager.Instance.World.GetEntry(world);
                if (worldEntry != null && worldEntry.Type != 0u && summoned.Contains(creature))
                {
                    summonSkipped++;
                    continue;
                }

                // open worlds: height 0 here, the map snaps research rows to the terrain when it spawns them (loading the 200-500 MB map files
                // for this command takes minutes); instances have no height grid, they take the height of the nearest world location of the
                // same world within 600 m (assumption, docs/ASSUMPTIONS.md)
                float y = 0f;
                if (worldEntry != null && worldEntry.Type != 0u)
                {
                    noHeight++;
                    float best = 600f * 600f;
                    if (!locationsByWorld.TryGetValue(world, out var worldLocations))
                        worldLocations = System.Array.Empty<NexusForever.Shared.GameTable.Model.WorldLocation2Entry>();
                    foreach (var location in worldLocations)
                    {
                        float d = (location.Position0 - x) * (location.Position0 - x) + (location.Position2 - z) * (location.Position2 - z);
                        if (d < best)
                        {
                            best = d;
                            y = location.Position1;
                        }
                    }
                }

                var c = GameTableManager.Instance.Creature2.GetEntry(creature);
                if (c == null)
                    continue;
                uint display = 0u;
                if (firstDisplay.TryGetValue(c.Creature2DisplayGroupId, out uint firstInfo))
                    display = firstInfo;
                sql.Add(string.Format(inv, "INSERT INTO entity (id,type,creature,world,area,x,y,z,displayInfo,faction1,faction2) VALUES ({0},0,{1},{2},0,{3},{4},{5},{6},{7},{7});",
                    nextId++, creature, world, x, y, z, display, c.FactionId));
                // source column: page, instance, indicator-<location>, questloc-<location>; counted without the location id
                string source = f.Length > 4 ? System.Text.RegularExpressions.Regex.Replace(f[4], "-[0-9]+$", "") : "unknown";
                bySource[source] = bySource.GetValueOrDefault(source) + 1;
            }

            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "research-spawns.sql"), sql);
            string sources = string.Join(", ", bySource.Select(b => $"{b.Key} {b.Value}"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "research-spawns-sources.txt"), string.Join("\n", bySource.Select(b => $"{b.Key}\t{b.Value}")) + "\n");
            Send(context, $"{sql.Count - 1} spawn rows written to research-spawns.sql ({noHeight} without a height grid, {summonSkipped} summoned creatures left out of instances). Imported by source: {sources}.");
        }

        [Command(Permission.Character, "Show what the public event engine does with an event (0 = all startable events): location, objectives, parents, timers.", "eventplan")]
        public void HandleEventPlan(ICommandContext context,
            [Parameter("PublicEvent id, 0 for all.")]
            uint id)
        {
            int lines = 0;
            foreach (string line in NexusForever.WorldServer.Game.Events.PublicEventEngine.Instance.Plan(id))
            {
                if (++lines <= 80)
                    Send(context, line);
            }

            context.SendMessage($"{lines} line(s).");
        }

        [Command(Permission.Character, "List the worlds with their type, name, map file and spawn count (to see which dungeons and instances have data).", "worlds")]
        public void HandleWorlds(ICommandContext context)
        {
            var spawns = NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().GroupBy(sp => (uint)sp.World).ToDictionary(g => g.Key, g => g.Count());
            foreach (var world in GameTableManager.Instance.World.Entries.OrderBy(w => w.Type).ThenBy(w => w.Id))
            {
                bool map = NexusForever.WorldServer.Game.Map.BaseMapManager.Instance.HasBaseMap(world.AssetPath);
                int count = spawns.GetValueOrDefault(world.Id);
                if (!map && count == 0 && world.Type == 0u)
                    continue;

                Send(context, $"world {world.Id} type {world.Type} {Text(world.LocalizedTextIdName)} | map {(map ? "yes" : "NO")} spawns {count} | item level {world.MinItemLevel}-{world.MaxItemLevel} | {world.AssetPath}");
            }
        }

        [Command(Permission.Character, "List the prerequisite types with the client's error text, and whether the server has a handler.", "prereqtypes")]
        public void HandlePrereqTypes(ICommandContext context)
        {
            foreach (var type in GameTableManager.Instance.PrerequisiteType.Entries.OrderBy(t => t.Id))
                Send(context, $"{type.Id}: {Text(type.LocalizedTextIdError)}");
        }

        [Command(Permission.Character, "List entries whose text field contains a substring (case insensitive), for example: grep Spell4 Description Darkice.", "grep")]
        public void HandleGrep(ICommandContext context,
            [Parameter("Table name.")]
            string table,
            [Parameter("String field name.")]
            string field,
            [Parameter("Text to look for.")]
            string text,
            [Parameter("Number of entries to show.")]
            uint count = 25u)
        {
            TableAccess access = FindTable(table);
            if (access == null)
            {
                context.SendMessage($"No table named {table}.");
                return;
            }

            int hits = 0;
            foreach (object entry in access.Entries)
            {
                if (entry.GetType().GetField(field)?.GetValue(entry) is string value && value.Contains(text, StringComparison.OrdinalIgnoreCase) && ++hits <= count)
                    Send(context, $"{table} {GetId(entry)}: {value}");
            }

            context.SendMessage($"{hits} match(es) in {table}.{field}.");
        }

        [Command(Permission.Character, "Why doable quests are still open (never accepted, chain blocked, accepted but stalled).", "questopen")]
        public void HandleQuestOpen(ICommandContext context)
        {
            foreach (string line in Bots.BotQuests.OpenQuestReport())
                Send(context, line);
        }

        [Command(Permission.Character, "For every open doable quest, what each online bot says to it (window, refusal, candidate); writes /cache/pick-why.tsv.", "pickwhy")]
        public void HandlePickWhy(ICommandContext context)
        {
            var players = Bots.BotManager.Instance.Bots.Select(b => b.Session?.Player).Where(p => p != null).ToList();
            List<string> lines = Bots.BotQuests.PickWhy(players, out List<string> summary);
            System.IO.File.WriteAllLines("/cache/pick-why.tsv", lines);
            context.SendMessage($"{lines.Count} open quests, {players.Count} bots, written to /cache/pick-why.tsv");
            foreach (string line in summary.Take(15))
                context.SendMessage(line);
        }

        [Command(Permission.Character, "Clean-bot quest truth: flush /cache/truth/quest-truth.tsv (one row per completion by a clean bot) and write /cache/truth/quest-truth-open.tsv (doable quests no clean bot completed, with the reason).", "questtruth")]
        public void HandleQuestTruth(ICommandContext context)
        {
            Diagnostics.QuestTruth.Flush();
            var players = Bots.BotManager.Instance.Bots.Where(b => b.IsClean).Select(b => b.Session?.Player).Where(p => p != null).ToList();
            List<string> lines = Bots.BotQuests.TruthOpen(players, out List<string> summary);
            System.IO.File.WriteAllLines(Diagnostics.QuestTruth.OpenPath,
                new[] { "quest\tpick_level\treason\tdetail\tlast_stall_any_bot" }.Concat(lines));
            context.SendMessage($"clean mode {(Bots.BotManager.CleanMode ? "on" : "off")}, {players.Count} clean bots online; quest-truth-open.tsv: {lines.Count} quests");
            foreach (string line in summary.Take(20))
                context.SendMessage(line);
        }

        [Command(Permission.Character, "Write the combat stats of every online bot (health, shield, armor, power, crit, mitigation) to /cache/bot-stats.tsv (scripts/research/ttk.py reads it).", "botstats")]
        public void HandleBotStats(ICommandContext context)
        {
            var players = Bots.BotManager.Instance.Bots.Select(b => b.Session?.Player).Where(p => p != null && p.Map != null).ToList();
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string> { "name\tclass\tlevel\thealth\tshield\tshield_mitigation\tarmor\tmitigation_physical\tassault_rating\tsupport_rating\tcrit_chance\tcrit_severity\tequipped\tresource1" };
            foreach (var p in players)
            {
                float[] stats = p.CombatStats;
                float assault = p.GetModifiedProperty(Game.Entity.Static.Property.AssaultRating, Game.Combat.UnitStats.Get(stats, Game.Entity.Static.Property.AssaultRating));
                float support = p.GetModifiedProperty(Game.Entity.Static.Property.SupportRating, Game.Combat.UnitStats.Get(stats, Game.Entity.Static.Property.SupportRating));
                int equipped = p.Inventory.Where(b => b.Location == Game.Entity.Static.InventoryLocation.Equipped).Sum(b => b.Count(i => i != null));
                lines.Add(string.Join("\t", p.Name, p.Class, p.Level, p.MaxHealth, p.GetShieldMax().ToString("0", ic),
                    Game.Combat.DamageMath.Value(p, Game.Entity.Static.Property.ShieldMitigationMax).ToString("0.###", ic),
                    Game.Combat.DamageMath.Value(p, Game.Entity.Static.Property.Armor).ToString("0", ic), Game.Combat.DamageMath.Mitigation(p, 1u).ToString("0.####", ic),
                    assault.ToString("0", ic), support.ToString("0", ic), Game.Combat.DamageMath.CritChance(p).ToString("0.####", ic),
                    Game.Combat.DamageMath.CritSeverity(p).ToString("0.###", ic), equipped, p.GetResource(1u).ToString("0.##", ic)));
            }
            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("CachePath") ?? System.AppContext.BaseDirectory, "bot-stats.tsv"), lines);
            context.SendMessage($"{players.Count} bots written to bot-stats.tsv");
        }

        [Command(Permission.Character, "Write every quest with its prerequisites, objectives and bot history to /cache/quest-dump.tsv.", "questdump")]
        public void HandleQuestDump(ICommandContext context)
        {
            List<string> lines = Bots.BotQuests.QuestDump();
            System.IO.File.WriteAllLines("/cache/quest-dump.tsv", lines);
            context.SendMessage($"{lines.Count} quests written to /cache/quest-dump.tsv");
        }

        [Command(Permission.Character, "Draw the hit area of a TelegraphDamage row as text (caster = first bot in the world, facing up, 1 char = scale m).", "telegraph")]
        public void HandleTelegraph(ICommandContext context, uint telegraphDamageId, float scale = 2f)
        {
            var entry = GameTableManager.Instance.TelegraphDamage.GetEntry(telegraphDamageId);
            var caster = Bots.BotManager.Instance.Bots.Select(b => b.Session.Player).FirstOrDefault(p => p?.Map != null);
            if (entry == null || caster == null)
            {
                context.SendMessage(entry == null ? "no such TelegraphDamage row" : "no bot in the world to use as caster");
                return;
            }

            var origin    = new System.Numerics.Vector3(0f, 0f, 0f);
            var telegraph = new Game.Spell.Telegraph(entry, caster, origin, System.Numerics.Vector3.Zero);
            context.SendMessage($"telegraph {entry.Id} shape {(Game.Spell.Static.DamageShape)entry.DamageShapeEnum} params {entry.Param00}/{entry.Param01}/{entry.Param02}/{entry.Param03} rotation {entry.RotationDegrees}, X = hit, 1 char = {scale} m, rows are Z");
            for (int row = 12; row >= -12; row--)
            {
                var line = new System.Text.StringBuilder();
                for (int col = -24; col <= 24; col++)
                    line.Append(row == 0 && col == 0 ? 'o' : telegraph.InsideTelegraph(new System.Numerics.Vector3(col * scale, 0f, row * scale), 0f) ? 'X' : '.');
                context.SendMessage(line.ToString());
            }
        }

        [Command(Permission.Character, "Summarise path missions by path and mission type.", "paths")]
        public void HandlePaths(ICommandContext context)
        {
            var missions = GameTableManager.Instance.PathMission.Entries.ToList();
            Send(context, $"{missions.Count} path missions, {GameTableManager.Instance.PathEpisode.Entries.Count()} episodes");
            var support = Game.Entity.PathMissionManager.AllMissions().GroupBy(d => (d.Entry.PathTypeEnum, d.Entry.PathMissionTypeEnum, d.Kind)).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2);
            var (supported, unsupported) = Game.Entity.PathMissionManager.CountSupport();
            Send(context, $"playable by the server: {supported}, not yet: {unsupported} (missions without an episode are not counted)");
            foreach (var group in support)
                Send(context, $"  path {group.Key.Item1} type {group.Key.Item2}: {group.Key.Item3} x{group.Count()}");
            foreach (var path in missions.GroupBy(m => m.PathTypeEnum).OrderBy(g => g.Key))
                Send(context, $"path {path.Key}: {path.Count()} missions; types " + string.Join(", ", path.GroupBy(m => m.PathMissionTypeEnum).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}")));
            foreach (var type in missions.GroupBy(m => (m.PathTypeEnum, m.PathMissionTypeEnum)).OrderBy(g => g.Key))
                foreach (var m in type.Take(2))
                    Send(context, $"path {type.Key.PathTypeEnum} type {type.Key.PathMissionTypeEnum} display {m.PathMissionDisplayTypeEnum} object {m.ObjectId} flags {m.PathMissionFlags}: {Text(m.LocalizedTextIdName)} | {Text(m.LocalizedTextIdSummary)}");
        }

        [Command(Permission.Character, "Unit properties an item gives when equipped (Game/Combat/ItemStats.cs). Lines 'value item.<id>.<property id> <number>' are read by scripts/combat-check.sh.", "itemstats")]
        public void HandleItemStats(ICommandContext context,
            [Parameter("Item2 ids separated by commas.")]
            string items)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string part in (items ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!uint.TryParse(part.Trim(), out uint id))
                    continue;
                var entry = GameTableManager.Instance.Item.GetEntry(id);
                float[] values = Game.Combat.ItemStats.For(entry);
                sb.Append($"item {id} ilvl {entry?.PowerLevel} score {Game.Combat.ItemStats.Score(entry):0}\n");
                if (values == null)
                    continue;
                for (int i = 0; i < values.Length; i++)
                    if (values[i] != 0f)
                        sb.Append($"value item.{id}.{i} {values[i].ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}\n");
            }
            Send(context, sb.ToString().TrimEnd());
        }

        [Command(Permission.Character, "Combat numbers the server computes: level stats, crit, armor resist, a spell's damage/heal per effect, a creature's stats. Lines 'value <key> <number>' are read by scripts/combat-check.sh.", "combat")]
        public void HandleCombat(ICommandContext context,
            [Parameter("Caster level.")]
            uint level,
            [Parameter("Spell4 id (0 = none).")]
            string spell = null,
            [Parameter("Creature2 id to show stats for (0 = none).")]
            string creature = null,
            [Parameter("Assault and support power to use instead of the level row (0 = level row).")]
            string power = null)
        {
            var sb = new System.Text.StringBuilder();
            void Value(string key, double v) => sb.Append($"value {key} {v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}\n");

            float[] row = (float[])Game.Combat.UnitStats.LevelRow(level).Clone();
            if (float.TryParse(power, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float p) && p > 0f)
            {
                row[(int)Game.Entity.Static.Property.AssaultRating] = p;
                row[(int)Game.Entity.Static.Property.SupportRating] = p;
            }
            Value($"player.L{level}.health", Game.Combat.UnitStats.Get(row, Game.Entity.Static.Property.BaseHealth));
            Value($"player.L{level}.shield", Game.Combat.UnitStats.Get(row, Game.Entity.Static.Property.ShieldCapacityMax));
            Value($"player.L{level}.assault", Game.Combat.UnitStats.Get(row, Game.Entity.Static.Property.AssaultRating));
            Value($"player.L{level}.support", Game.Combat.UnitStats.Get(row, Game.Entity.Static.Property.SupportRating));
            float armor = Game.Combat.UnitStats.Get(row, Game.Entity.Static.Property.Armor);
            Value($"player.L{level}.armor", armor);
            Value($"player.L{level}.resist", Game.Combat.DamageMath.ArmorResist(armor, level));

            // rating curves (client code): value of 1000/5000/15000 rating at this level, without base
            foreach ((string name, uint formula) in new[] { ("crit", Game.Combat.DamageMath.FormulaCritChance), ("severity", Game.Combat.DamageMath.FormulaCritSeverity),
                ("deflect", Game.Combat.DamageMath.FormulaDeflect), ("strikethrough", Game.Combat.DamageMath.FormulaStrikethrough), ("mitigation", Game.Combat.DamageMath.FormulaMitigation),
                ("multihit", Game.Combat.DamageMath.FormulaMultiHitChance), ("glance", Game.Combat.DamageMath.FormulaGlanceChance), ("pierce", Game.Combat.DamageMath.FormulaArmorPierce),
                ("lifesteal", Game.Combat.DamageMath.FormulaLifesteal) })
                foreach (float r in new[] { 1000f, 5000f, 15000f })
                    Value($"curve.{name}.L{level}.r{r}", Game.Combat.DamageMath.Curve(formula, r, level, 0f));
            const int rolls = 200000;
            double sum = 0d;
            for (int i = 0; i < rolls; i++)
                sum += Game.Combat.DamageMath.RollCrit(Game.Combat.DamageMath.DefaultCritChance, Game.Combat.DamageMath.DefaultCritSeverity);
            Value("crit.chance", Game.Combat.DamageMath.DefaultCritChance);
            Value("crit.severity", Game.Combat.DamageMath.DefaultCritSeverity);
            Value("crit.sampled-multiplier", sum / rolls);

            if (uint.TryParse(spell, out uint spellId) && spellId != 0u)
            {
                foreach (var e in GameTableManager.Instance.Spell4Effects.Entries.Where(e => e.SpellId == spellId))
                {
                    var explain = new System.Text.StringBuilder();
                    float? amount = Game.Combat.SpellFormula.Amount(e, level, row, (_, v) => v, false, explain);
                    sb.Append($"effect {e.Id} type {e.EffectType} damage type {e.DamageType}:{explain}\n");
                    if (amount != null)
                        Value($"spell.{spellId}.effect.{e.Id}", amount.Value);
                }
            }

            if (uint.TryParse(creature, out uint creatureId) && creatureId != 0u)
            {
                float[] c = Game.Combat.UnitStats.Creature(creatureId, level);
                Value($"creature.{creatureId}.L{level}.health", Game.Combat.UnitStats.Get(c, Game.Entity.Static.Property.BaseHealth));
                Value($"creature.{creatureId}.L{level}.shield", Game.Combat.UnitStats.Get(c, Game.Entity.Static.Property.ShieldCapacityMax));
                Value($"creature.{creatureId}.L{level}.assault", Game.Combat.UnitStats.Get(c, Game.Entity.Static.Property.AssaultRating));
                Value($"creature.{creatureId}.L{level}.strength", Game.Combat.UnitStats.Get(c, Game.Entity.Static.Property.Strength));
                float cArmor = Game.Combat.UnitStats.Get(c, Game.Entity.Static.Property.Armor);
                Value($"creature.{creatureId}.L{level}.armor", cArmor);
                Value($"creature.{creatureId}.L{level}.resist", Game.Combat.DamageMath.ArmorResist(cArmor, level));
            }

            Send(context, sb.ToString().TrimEnd());
        }

        [Command(Permission.Character, "Show a creature table row in the same terms the target groups use.", "creature")]
        public void HandleCreature(ICommandContext context,
            [Parameter("Creature2 id.")]
            uint id)
        {
            Send(context, NexusForever.WorldServer.Game.Quest.TargetGroupHelper.DescribeCreature(id));
        }

        [Command(Permission.Character, "Show a prerequisite row and where it is used (spells, quests, creatures), to work out what an unhandled type means.", "prereqrow")]
        public void HandlePrereqRow(ICommandContext context,
            [Parameter("Prerequisite id.")]
            uint id)
        {
            var row = FindTable("Prerequisite")?.Entries.OfType<NexusForever.Shared.GameTable.Model.PrerequisiteEntry>().FirstOrDefault(e => e.Id == id);
            if (row == null)
            {
                context.SendMessage($"No Prerequisite {id}.");
                return;
            }

            Send(context, $"Prerequisite {id}: {Describe(row)}");
            foreach (string table in new[] { "Spell4", "Spell4Effects", "Quest2", "Creature2", "QuestObjective" })
            {
                var hits = FindTable(table)?.Entries.Cast<object>()
                    .Select(e => (Entry: e, Field: MatchingField(e, id)))
                    .Where(h => h.Field != null && h.Field.StartsWith("Prerequisite"))
                    .Take(6).ToList();
                if (hits == null)
                    continue;

                foreach (var hit in hits)
                {
                    string description = hit.Entry.GetType().GetField("Description")?.GetValue(hit.Entry)?.ToString();
                    Send(context, $"  used by {table} {GetId(hit.Entry)} ({hit.Field}) {description}");
                }
            }
        }

        [Command(Permission.Character, "Clear the counters behind 'gt report'.", "reset")]
        public void HandleReset(ICommandContext context)
        {
            NexusForever.WorldServer.Diagnostics.FailureLedger.Reset();
            context.SendMessage("Failure report cleared.");
        }

        [Command(Permission.Character, "Summarise what the server could not handle since start: unhandled effects, prerequisites, failed casts, stalled quests.", "report")]
        public void HandleReport(ICommandContext context)
        {
            var entries = NexusForever.WorldServer.Diagnostics.FailureLedger.Snapshot();
            if (entries.Count == 0)
            {
                context.SendMessage("Nothing recorded.");
                return;
            }

            foreach (var group in entries.GroupBy(e => e.Category))
            {
                Send(context, $"== {group.Key} ({group.Count()} kinds, {group.Sum(e => e.Count)} total)");
                foreach (var entry in group.OrderByDescending(e => e.Count).Take(12))
                    Send(context, $"  {entry.Count}x {entry.Key}{(entry.Example != null ? $" [{entry.Example}]" : "")}");
            }
        }

        [Command(Permission.Character, "Internal: marks the end of a scripted command.", "done")]
        public void HandleDone(ICommandContext context,
            [Parameter("Command id.")]
            string id)
        {
            (context as NexusForever.WorldServer.Command.Context.FileCommandContext)?.Complete();
        }

        [Command(Permission.Character, "Zone lookup check: the zone the world's map file resolves at every WorldLocation2 of the world against the location's own zone (or a parent/child of it).", "zonecheck")]
        public void HandleZoneCheck(ICommandContext context,
            [Parameter("World id.")]
            uint worldId)
        {
            var world = GameTableManager.Instance.World.GetEntry(worldId);
            var file = world == null ? null : NexusForever.WorldServer.Game.Map.BaseMapManager.Instance.GetBaseMap(world.AssetPath);
            if (file == null)
            {
                Send(context, $"world {worldId}: no map file");
                return;
            }

            bool Related(uint a, uint b)
            {
                for (uint z = a, d = 0; z != 0u && d < 8; z = GameTableManager.Instance.WorldZone.GetEntry(z)?.ParentZoneId ?? 0u, d++)
                    if (z == b)
                        return true;
                for (uint z = b, d = 0; z != 0u && d < 8; z = GameTableManager.Instance.WorldZone.GetEntry(z)?.ParentZoneId ?? 0u, d++)
                    if (z == a)
                        return true;
                return false;
            }

            int ok = 0, bad = 0, none = 0, okSwapped = 0;
            foreach (var l in GameTableManager.Instance.WorldLocation2.Entries.Where(l => l != null && l.WorldId == worldId && l.WorldZoneId != 0u))
            {
                uint? zone = null, swapped = null;
                try { zone = file.GetWorldAreaId(new System.Numerics.Vector3(l.Position0, l.Position1, l.Position2)); } catch (ArgumentOutOfRangeException) { }
                try { swapped = file.GetWorldAreaId(new System.Numerics.Vector3(l.Position2, l.Position1, l.Position0)); } catch (ArgumentOutOfRangeException) { }
                if (zone == null) none++;
                else if (Related(zone.Value, l.WorldZoneId)) ok++;
                else bad++;
                if (swapped != null && Related(swapped.Value, l.WorldZoneId)) okSwapped++;
            }
            Send(context, $"world {worldId}: {ok} agree, {bad} differ, {none} no zone data; with x/z swapped {okSwapped} agree");
        }

        [Command(Permission.Character, "Show how long each map takes to update and how many grids are active, to find what makes the world slow.", "maps")]
        public void HandleMaps(ICommandContext context)
        {
            foreach (var pair in NexusForever.WorldServer.Game.Map.MapManager.Instance.Profiles.OrderByDescending(p => p.Value.TotalMs))
            {
                var profile = pair.Value;
                Send(context, $"world {pair.Key}: avg {profile.TotalMs / Math.Max(1, profile.Ticks):F2} ms, max {profile.MaxMs:F0} ms, {profile.ActiveGrids} active grids, {profile.Ticks} ticks");
            }
        }

        [Command(Permission.Character, "Memory: managed heap before and after a full collection ('collect'), committed and fragmented bytes, process working set, loaded maps and entities, ledger size.", "memory")]
        public void HandleMemory(ICommandContext context,
            [Parameter("Leave empty to show, 'collect' to force a full blocking collection first.")]
            string action)
        {
            long before = GC.GetTotalMemory(false);
            if (action == "collect")
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            long after = GC.GetTotalMemory(false);
            var info = GC.GetGCMemoryInfo();
            Send(context, $"managed heap {before / 1048576} MB, after collection {after / 1048576} MB; committed {info.TotalCommittedBytes / 1048576} MB, fragmented {info.FragmentedBytes / 1048576} MB, heap size at last GC {info.HeapSizeBytes / 1048576} MB; collections gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}; working set {Environment.WorkingSet / 1048576} MB");
            Send(context, $"memory: {NexusForever.WorldServer.Diagnostics.MemoryWatch.Line()}");
            int maps = 0, instances = 0, entities = 0;
            foreach (var map in NexusForever.WorldServer.Game.Map.MapManager.Instance.LoadedMaps)
            {
                maps++;
                int mapInstances = 0, mapEntities = 0, mapPending = 0;
                int models = 0;
                string mapName = map.ToString();
                if (map is NexusForever.WorldServer.Game.Map.BaseMap baseMap)
                {
                    mapEntities += baseMap.EntityCount;
                    mapPending += baseMap.PendingActionCount;
                    models = baseMap.EntityCountSpawnModels;
                    mapName = baseMap.Entry?.Id.ToString() ?? mapName;
                }
                if (map is NexusForever.WorldServer.Game.Map.IInstancedMap instanced)
                    foreach (var instance in instanced.GetInstances())
                    {
                        instances++;
                        mapInstances++;
                        mapEntities += instance.EntityCount;
                        mapPending += instance.PendingActionCount;
                    }

                entities += mapEntities;
                Send(context, $"  map {mapName}: {mapInstances} instances, {mapEntities} entities, {mapPending} pending actions, {models} spawn models");
            }

            Send(context, $"{maps} base maps, {instances} instances, {entities} entities, {NexusForever.WorldServer.Diagnostics.FailureLedger.Snapshot().Count} ledger entries");
        }

        [Command(Permission.Character, "Test: place creatures next to a bot like world spawns (they despawn after 10 min). 'unguarded' turns off the self-summon ability filter until 'gt spawnnear 0 0 any guarded'; the summon caps stay on.", "spawnnear")]
        public void HandleSpawnNear(ICommandContext context,
            [Parameter("Creature2 id (0 only changes the guard).")]
            uint creatureId,
            [Parameter("How many.")]
            uint count,
            [Parameter("Bot name or 'any'.")]
            string name,
            [Parameter("'unguarded' or 'guarded' (optional).")]
            string guard)
        {
            if (guard == "unguarded" || guard == "guarded")
            {
                NexusForever.WorldServer.Game.Entity.NonPlayer.SelfSummonFilter = guard == "guarded";
                Send(context, $"self-summon ability filter {(NexusForever.WorldServer.Game.Entity.NonPlayer.SelfSummonFilter ? "on" : "off")}");
            }
            if (creatureId == 0u || count == 0u)
                return;

            var bot = Bots.BotManager.Instance.Bots.Select(b => b.Session?.Player)
                .Where(p => p != null && p.Map != null && !p.IsDead && (name == "any" || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();
            if (bot == null)
            {
                Send(context, "no such bot in a world");
                return;
            }

            int placed = 0;
            for (uint i = 0u; i < Math.Min(count, 20u); i++)
            {
                var at = bot.Position + new System.Numerics.Vector3(1f + i * 0.5f, 0f, 1f);
                if (NexusForever.WorldServer.Game.Entity.CreatureSpawner.Spawn(bot.Map, creatureId, at, 600d) != null)
                    placed++;
            }
            Send(context, $"{placed} x creature {creatureId} next to {bot.Name} in world {bot.Map.Entry.Id} at {bot.Position}");
        }

        [Command(Permission.Character, "Show where world update time goes (accumulating timers since start or the last reset); 'reset' as parameter clears them.", "profile")]
        public void HandleProfile(ICommandContext context,
            [Parameter("Leave empty to show, 'reset' to clear.")]
            string action)
        {
            if (action == "reset")
            {
                NexusForever.WorldServer.Diagnostics.Profiler.Reset();
                context.SendMessage("Profiler reset.");
                return;
            }

            foreach (var row in NexusForever.WorldServer.Diagnostics.Profiler.Snapshot().Take(25))
                Send(context, $"{row.Name}: {row.MsPerSecond:F1} ms/s, {row.CallsPerSecond:F0} calls/s, {row.MicrosPerCall:F1} us/call");
        }

        [Command(Permission.Character, "Count the loaded boss encounter definitions by source and list the bosses that have one.", "encounters")]
        public void HandleEncounters(ICommandContext context)
        {
            var defs = NexusForever.WorldServer.Game.Encounters.EncounterManager.Instance.Definitions.ToList();
            foreach (var group in defs.GroupBy(d => (d.Source ?? "hand written").Split(' ', ':', '(')[0]).OrderBy(g => g.Key))
                Send(context, $"{group.Key}: {group.Count()} definitions, {group.Sum(d => d.Phases.Count)} phases, {group.Sum(d => d.Phases.Sum(p => p.Abilities.Count))} ability timers");
            foreach (var d in defs.OrderBy(d => d.World).ThenBy(d => d.Boss))
                Send(context, $"  world {d.World} {d.Boss} ({d.Phases.Count} phases, enrage {d.Enrage} s) [{d.Source}]");
            context.SendMessage($"{defs.Count} encounter definition(s); creatures with prime rank or elite group/raid status and no definition get a generated one when they fight.");
        }

        [Command(Permission.Character, "List the researched abilities (Jabbithole) of every creature with the given name: spell id, text name, cast time, cooldown, description.", "abilities")]
        public void HandleAbilities(ICommandContext context,
            [Parameter("Creature name (client text, case and punctuation ignored).")]
            string name)
        {
            var text = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
            string wanted = NexusForever.WorldServer.Game.Research.CreatureResearch.Slug(name);
            uint creatureNumber = uint.TryParse(name, out uint parsed) ? parsed : 0u;
            foreach (var creature in GameTableManager.Instance.Creature2.Entries.Where(c => creatureNumber != 0u ? c.Id == creatureNumber : NexusForever.WorldServer.Game.Research.CreatureResearch.Slug(text?.GetEntry(c.LocalizedTextIdName)) == wanted).Take(6))
            {
                var info = NexusForever.WorldServer.Game.Research.CreatureResearch.Get(creature.Id, (int)creature.MaxLevel);
                Send(context, $"creature {creature.Id} level {creature.MinLevel}-{creature.MaxLevel} {creature.Description}: page level {info?.Level} rank {info?.Rank} eliteness {info?.Eliteness}, {info?.Abilities.Count ?? 0} abilities, {info?.Drops.Count ?? 0} drops");
                foreach (uint id in info?.Abilities ?? new System.Collections.Generic.List<uint>())
                {
                    var spell = GameTableManager.Instance.Spell4.GetEntry(id);
                    var baseEntry = spell == null ? null : GameTableManager.Instance.Spell4Base.GetEntry(spell.Spell4BaseIdBaseSpell);
                    Send(context, $"  {id}: {text?.GetEntry(baseEntry?.LocalizedTextIdName ?? 0u)} | cast {spell?.CastTime} ms cooldown {spell?.SpellCoolDown} ms range {spell?.TargetMaxRange} | {spell?.Description}");
                }
            }
        }

        [Command(Permission.Character, "List the loaded dungeon, raid, adventure and residence instances with their entity count and players.", "instances")]
        public void HandleInstances(ICommandContext context)
        {
            int count = 0;
            foreach (var map in NexusForever.WorldServer.Game.Map.MapManager.Instance.LoadedMaps)
            {
                if (map is not NexusForever.WorldServer.Game.Map.IInstancedMap instanced)
                    continue;

                foreach (var instance in instanced.GetInstances())
                {
                    count++;
                    Send(context, $"world {instance.Entry.Id} instance {instance.InstanceId}: {instance.EntityCount} entities, {instance.ActiveGridCount} active grids, unload {instance.UnloadStatus?.ToString() ?? "no"}");
                    var text = GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
                    var alive = instance.AllEntities().OfType<NexusForever.WorldServer.Game.Entity.NonPlayer>().Where(n => !n.IsDead).GroupBy(n => n.CreatureId).OrderByDescending(g => g.Count()).Take(12);
                    foreach (var g in alive)
                        Send(context, $"    {g.Count()} x creature {g.Key} {text?.GetEntry(GameTableManager.Instance.Creature2.GetEntry(g.Key)?.LocalizedTextIdName ?? 0u)} level {g.First().Level} health {g.First().Health}/{g.First().MaxHealth} faction {g.First().Faction1} at {g.First().Position.X:0},{g.First().Position.Y:0},{g.First().Position.Z:0}");
                }
            }

            context.SendMessage($"{count} instance(s).");
        }

        [Command(Permission.Character, "Compare the height of every creature and player in the loaded instances with the floor lookup (terrain plus .nffloor prop floors): on floor (<1 m), near (<3 m), off, no data.", "floorcheck")]
        public void HandleFloorCheck(ICommandContext context)
        {
            foreach (var map in NexusForever.WorldServer.Game.Map.MapManager.Instance.LoadedMaps)
            {
                if (map is not NexusForever.WorldServer.Game.Map.IInstancedMap instanced)
                    continue;

                foreach (var instance in instanced.GetInstances())
                {
                    var file = instance.File;
                    foreach (var group in instance.AllEntities().OfType<NexusForever.WorldServer.Game.Entity.UnitEntity>()
                        .GroupBy(u => u is NexusForever.WorldServer.Game.Entity.Player ? "players" : "creatures"))
                    {
                        int on = 0, near = 0, off = 0, none = 0, terrainDiffers = 0;
                        string example = null;
                        foreach (var unit in group)
                        {
                            if (file == null || !file.TryGetFloorHeight(unit.Position.X, unit.Position.Z, unit.Position.Y, out float floor))
                            {
                                none++;
                                continue;
                            }

                            // what the terrain grid alone (the behaviour before the prop floors) would have given
                            if (file.TryGetTerrainHeight(new System.Numerics.Vector3(unit.Position.X, 0f, unit.Position.Z), out float terrain) && MathF.Abs(terrain - floor) > 3f)
                                terrainDiffers++;

                            float dy = MathF.Abs(floor - unit.Position.Y);
                            if (dy < 1f)
                                on++;
                            else if (dy < 3f)
                                near++;
                            else
                            {
                                off++;
                                example ??= $"{unit.Position.X:0},{unit.Position.Y:0.0},{unit.Position.Z:0} floor {floor:0.0}";
                            }
                        }

                        Send(context, $"world {instance.Entry.Id} instance {instance.InstanceId} {group.Key}: on floor {on}, near {near}, off {off}, no data {none}, terrain-only height differs >3 m {terrainDiffers}, prop floors {(file?.Floor != null ? "yes" : "no")}{(example != null ? $", e.g. {example}" : "")}");
                    }
                }
            }

            context.SendMessage("floorcheck done.");
        }

        private static void Send(ICommandContext context, string text)
        {
            log.Info(text);
            context.SendMessage(text);
        }

        private sealed class TableAccess
        {
            public string Name;
            public IEnumerable Entries;
        }

        private static IEnumerable<TableAccess> AllTables()
        {
            foreach (PropertyInfo property in typeof(GameTableManager).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Type type = property.PropertyType;
                if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(GameTable<>))
                    continue;

                object table = property.GetValue(GameTableManager.Instance);
                if (table == null)
                    continue;

                if (type.GetProperty("Entries")?.GetValue(table) is IEnumerable entries)
                    yield return new TableAccess { Name = property.Name, Entries = entries };
            }
        }

        private static TableAccess FindTable(string name)
        {
            return AllTables().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static uint GetId(object entry)
        {
            object id = entry.GetType().GetField("Id")?.GetValue(entry);
            return id is uint value ? value : uint.MaxValue;
        }

        private static string MatchingField(object entry, uint value)
        {
            foreach (FieldInfo field in entry.GetType().GetFields())
            {
                object content = field.GetValue(entry);
                switch (content)
                {
                    case uint number when number == value:
                        return field.Name;
                    case uint[] array when Array.IndexOf(array, value) >= 0:
                        return $"{field.Name}[{Array.IndexOf(array, value)}]";
                }
            }

            return null;
        }

        private static string Describe(object entry)
        {
            return string.Join(", ", entry.GetType().GetFields()
                .Select(f => (f.Name, Value: f.GetValue(entry)))
                .Where(f => f.Value != null && !(f.Value is uint zero && zero == 0u) && !(f.Value is float fzero && fzero == 0f))
                .Select(f => f.Value is Array array
                    ? $"{f.Name}=[{string.Join(",", array.Cast<object>().Where(o => !(o is uint u && u == 0u)))}]"
                    : $"{f.Name}={f.Value}"));
        }
    }
}
