using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Inventory of every quest in the game tables by objective type, so it is clear what bots can already do and what to build next.
    /// Written to quest-coverage.txt next to the failure report.
    /// </summary>
    public static class QuestCoverage
    {
        // objective types bots can complete on their own; extend this as bot behaviour grows
        public static readonly HashSet<QuestObjectiveType> BotReady = new()
        {
            QuestObjectiveType.KillCreature,
            QuestObjectiveType.KillTargetGroup,
            QuestObjectiveType.KillTargetGroups,
            QuestObjectiveType.ActivateEntity,
            QuestObjectiveType.ActivateEntity2,
            QuestObjectiveType.SucceedCSI,
            QuestObjectiveType.VirtualCollect,
            QuestObjectiveType.CompleteQuest,
            QuestObjectiveType.ActivateTargetGroup,
            QuestObjectiveType.ActivateTargetGroupChecklist,
            QuestObjectiveType.TalkTo,
            QuestObjectiveType.TalkToTargetGroup,
            QuestObjectiveType.EnterArea,
            QuestObjectiveType.EnterZone,
            QuestObjectiveType.CollectItem,
            QuestObjectiveType.CompleteEvent,
            QuestObjectiveType.SpellSuccess,
            QuestObjectiveType.SpellSuccess2,
            QuestObjectiveType.SpellSuccess3,
            QuestObjectiveType.SpellSuccess4,
            QuestObjectiveType.Unknown10,
            QuestObjectiveType.Unknown15,
        };

        // everything else is credited directly by the bot when NF_BOTS_SYNTHETIC is on, which tests the quest flow (accept, complete,
        // rewards, next quest in the chain) but not the objective itself. Player kills are left out, bots never fight players.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint[]> textCreatures = new();

        /// <summary>
        /// Creatures an ActivateEntity objective without data names in its text ("Kill $(creature=53918), $(creature=56829) ..."), empty otherwise.
        /// Killing or activating any of them credits the objective.
        /// </summary>
        public static uint[] TextCreatures(QuestObjectiveEntry entry)
        {
            // VirtualCollect has its item in data; its text creatures ("Hack $(creature=63160) to obtain ...") count only where they have spawns
            bool virtualCollect = (QuestObjectiveType)entry.Type == QuestObjectiveType.VirtualCollect;
            if (!virtualCollect && (entry.Data != 0u || (QuestObjectiveType)entry.Type is not (QuestObjectiveType.ActivateEntity or QuestObjectiveType.ActivateEntity2 or QuestObjectiveType.SucceedCSI)))
                return Array.Empty<uint>();

            return textCreatures.GetOrAdd(entry.Id, _ =>
            {
                // creature macros come as $(creature=N), $m(...) plural, $p(...) and $i(...) (114 data-0 activate objectives only had the
                // prefixed forms and fell back to the indicator heuristic or nothing, e.g. 7224 "capture $i(creature=19949)"); a few texts drop the
                // parentheses ("Collect the $vitem=252 from a $creature=33057", 6 objectives), the client still links them
                string text = NexusForever.Shared.GameTable.GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdFull);
                uint[] named = string.IsNullOrEmpty(text) ? Array.Empty<uint>()
                    : System.Text.RegularExpressions.Regex.Matches(text, @"\$[a-z]?\(?[Cc]reature=(\d+)\)?").Select(m => uint.Parse(m.Groups[1].Value)).Where(c => !virtualCollect || TargetGroupHelper.IsSpawned(c)).Distinct().ToArray();
                if (virtualCollect)
                    return named;

                // the reward pane target group lists the objective's creatures (likely): in 242 of 278 activate/CSI objectives with a creature in
                // data the group (type 1) contains it, in 184 of 252 data-0 objectives it contains the creature the text names
                uint[] group = RewardPaneCreatures(entry);
                uint[] targets = named.Union(group).ToArray();
                // none of them has a spawn: the indicator heuristic may still find the activatable creature at the place (counted as heuristic)
                if (!targets.Any(TargetGroupHelper.IsSpawned) && IndicatorCreature(entry) is { Length: > 0 } atIndicator)
                    return targets.Union(atIndicator).ToArray();
                if (targets.Length > 0)
                {
                    if (named.Length == 0)
                    {
                        rewardPaneResolved[entry.Id] = true;
                        Diagnostics.FailureLedger.Record("objective-reward-pane-target", $"objective {entry.Id}", $"{group.Length} creatures of target group {entry.TargetGroupIdRewardPane}");
                    }
                    return targets;
                }

                return IndicatorCreature(entry);
            });
        }

        // data-0 activate objectives whose creatures came only from the reward pane group (no creature in data or text), counted as heuristic
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> rewardPaneResolved = new();

        private static uint[] RewardPaneCreatures(QuestObjectiveEntry entry)
        {
            var tables = NexusForever.Shared.GameTable.GameTableManager.Instance;
            if (entry.TargetGroupIdRewardPane == 0u || tables.TargetGroup.GetEntry(entry.TargetGroupIdRewardPane) is not { Type: 1u })
                return Array.Empty<uint>();
            return TargetGroupHelper.GetCreatures(entry.TargetGroupIdRewardPane).Where(c => c != 0u && tables.Creature2.GetEntry(c) != null).ToArray();
        }

        private static Dictionary<uint, List<(uint Creature, float X, float Z)>> activatableSpawns;
        private static readonly object activatableGate = new();

        /// <summary>
        /// Activate objective with no creature in data or text: the one activatable creature kind spawned within 30 m of its indicator location.
        /// Evidence: of the 142 activate objectives that name a creature spawned in the indicator's world, 118 have it within 30 m of the indicator.
        /// Two or more kinds there is ambiguous and stays synthetic (counted in objective-indicator-ambiguous).
        /// </summary>
        // objectives whose creature came from the indicator heuristic (nothing in data or text names it)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> indicatorResolved = new();

        /// <summary>
        /// How a completion of the quest depends on a QA heuristic rather than data that names the target, empty when it does not:
        /// "indicator" (activate objective whose creature is the one activatable kind near its indicator), "unknown10" (type 10/15 objective
        /// credited through ScriptedTargets), "craft-materials" (needs a crafting tier above 1: bots craft up to it
        /// for real xp, the schematic materials are QA grants because no vendor or drop source exists for most of them; earlier
        /// this was "tier-grant", the xp itself was granted). Counted apart in
        /// the coverage line and docs/QUEST-COVERAGE.md so real-objective completions are not overstated.
        /// </summary>
        public static string HeuristicKind(Game.Quest.QuestInfo info)
        {
            var kinds = new List<string>();
            foreach (var objective in info.Objectives)
            {
                QuestObjectiveEntry entry = objective.Entry;
                string kind = ObjectiveHeuristic(objective);
                if (kind.Length > 0)
                    kinds.Add(kind);

                // the objective itself is real (equip, item use), but bots get the item from a QA grant: no vendor or drop source is known
                if (EquipItem(entry) != 0u || SpellItem(entry) != 0u)
                    kinds.Add("item-grant");
            }

            if (CraftTierSkills(info).Any() && !craftReal.ContainsKey((ushort)info.Entry.Id))
                kinds.Add("craft-materials");

            return string.Join(",", kinds.Distinct());
        }

        /// <summary>
        /// The QA heuristic an objective's credit depends on, from its data alone (empty when the data names the target): "unknown10" (type 10/15),
        /// "indicator" or "reward-pane" (activate objective with no creature in data or text). Grants are not included: whether a bot needed one
        /// is known only when it happens (Diagnostics.QuestTruth.Grant).
        /// </summary>
        public static string ObjectiveHeuristic(Game.Quest.QuestObjectiveInfo objective)
        {
            QuestObjectiveEntry entry = objective.Entry;
            if (objective.Type is QuestObjectiveType.Unknown10 or QuestObjectiveType.Unknown15)
                return "unknown10";
            if (entry.Data == 0u && objective.Type is QuestObjectiveType.ActivateEntity or QuestObjectiveType.ActivateEntity2 or QuestObjectiveType.SucceedCSI)
            {
                TextCreatures(entry);
                if (indicatorResolved.ContainsKey(entry.Id))
                    return "indicator";
                if (rewardPaneResolved.ContainsKey(entry.Id))
                    return "reward-pane";
            }
            return "";
        }

        public static IEnumerable<uint> CraftTierSkillsOf(Game.Quest.QuestInfo info) => CraftTierSkills(info);

        // quests completed by a bot whose crafting tier came from gathered or quest materials only (no QA material grant since the server start)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, byte> craftReal = new();

        /// <summary>Skills whose tier above 1 the quest's prerequisite asks for (type 19).</summary>
        private static IEnumerable<uint> CraftTierSkills(Game.Quest.QuestInfo info)
        {
            var tables = NexusForever.Shared.GameTable.GameTableManager.Instance;
            PrerequisiteEntry prerequisite = tables.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
            if (prerequisite == null)
                yield break;
            for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
                if (prerequisite.PrerequisiteTypeId[i] == 19u && tables.TradeskillTier.GetEntry(prerequisite.ObjectId[i]) is { Tier: > 1u } tier)
                    yield return tier.TradeSkillId;
        }

        /// <summary>A bot completed the quest: a craft-tier quest counts as real when no skill it needs used QA-granted materials.</summary>
        /// <summary>(character, quest) pairs that got synthetic credit on some objective since the quest was taken.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, ushort), byte> syntheticCredited = new();

        public static void NoteSynthetic(Game.Entity.Player me, ushort questId) => syntheticCredited[(me.CharacterId, questId)] = 1;

        public static void OnCompleted(Game.Entity.Player me, Game.Quest.QuestInfo info)
        {
            // quests that need an objective in a dungeon/raid/expedition world: real (every objective by its driver), with a QA heuristic, or synthetic
            ushort questId = (ushort)info.Entry.Id;
            bool synthetic = syntheticCredited.TryRemove((me.CharacterId, questId), out _);
            if (BotQuests.IsInstanceOnly(questId) || me.Map is Game.Map.DungeonMapInstance)
            {
                string kind = HeuristicKind(info);
                Diagnostics.FailureLedger.Record("instance-quest-completed", $"quest {questId} {(synthetic ? "synthetic" : kind.Length > 0 ? "heuristic " + kind : "real")}",
                    $"world {me.Map?.Entry.Id}, {me.Name}");
            }

            List<uint> skills = CraftTierSkills(info).Distinct().ToList();
            if (skills.Count == 0)
                return;
            bool real = skills.All(s => !me.TradeskillManager.UsedQaMaterials(s));
            if (real)
                craftReal[(ushort)info.Entry.Id] = 1;
            Diagnostics.FailureLedger.Record(real ? "quest-craft-tier-real" : "quest-craft-tier-qa-materials", $"quest {info.Entry.Id}", $"skills {string.Join(",", skills)}");
        }

        private static uint[] IndicatorCreature(QuestObjectiveEntry entry)
        {
            var tables = NexusForever.Shared.GameTable.GameTableManager.Instance;
            WorldLocation2Entry location = tables.WorldLocation2.GetEntry(entry.WorldLocationsIdIndicator00);
            if (location == null)
                return Array.Empty<uint>();

            lock (activatableGate)
            {
                if (activatableSpawns == null)
                {
                    var index = new Dictionary<uint, List<(uint, float, float)>>();
                    foreach (var spawn in NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns())
                    {
                        if (!IsActivatable(spawn.Creature))
                            continue;

                        if (!index.TryGetValue(spawn.World, out List<(uint, float, float)> list))
                            index.Add(spawn.World, list = new List<(uint, float, float)>());
                        list.Add((spawn.Creature, spawn.X, spawn.Z));
                    }

                    activatableSpawns = index;
                }

                if (!activatableSpawns.TryGetValue(location.WorldId, out var spawns))
                    return Array.Empty<uint>();

                uint[] kinds = spawns.Where(sp => (sp.X - location.Position0) * (sp.X - location.Position0) + (sp.Z - location.Position2) * (sp.Z - location.Position2) <= 900f)
                    .Select(sp => sp.Creature).Distinct().ToArray();
                if (kinds.Length == 1)
                {
                    indicatorResolved[entry.Id] = true;
                    Diagnostics.FailureLedger.Record("objective-indicator-target", $"objective {entry.Id}", $"creature {kinds[0]} at indicator {location.Id}");
                    return kinds;
                }

                if (kinds.Length > 1)
                    Diagnostics.FailureLedger.Record("objective-indicator-ambiguous", $"objective {entry.Id}", $"{kinds.Length} activatable creature kinds at indicator {location.Id}");
                return Array.Empty<uint>();
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, (uint[] Targets, uint[] Named)> scriptedTargets = new();

        /// <summary>
        /// Creatures of a type 10 or 15 objective (Unknown10/Unknown15, decoded from the tables, docs/ASSUMPTIONS.md) and the creatures its text
        /// names. Type 10 ("free, scan, overload, secure N of X"): data is a target group, or a creature id where no group has that id; the
        /// reward pane group only when data is 0 (data 1 is the generic group 1). Type 15 ("kill X and Y"): data is a target group in the older
        /// rows; elsewhere the reward pane group plus the creatures named in the text.
        /// </summary>
        public static (uint[] Targets, uint[] Named) ScriptedTargets(QuestObjectiveEntry entry)
        {
            QuestObjectiveType type = (QuestObjectiveType)entry.Type;
            if (type is not (QuestObjectiveType.Unknown10 or QuestObjectiveType.Unknown15))
                return (Array.Empty<uint>(), Array.Empty<uint>());

            return scriptedTargets.GetOrAdd(entry.Id, _ =>
            {
                var tables = NexusForever.Shared.GameTable.GameTableManager.Instance;
                var result = new HashSet<uint>();
                uint[] named = Array.Empty<uint>();
                bool dataIsGroup = entry.Data > 1u && tables.TargetGroup.GetEntry(entry.Data) != null;
                if (dataIsGroup)
                    result.UnionWith(TargetGroupHelper.GetCreatures(entry.Data));
                else if (type == QuestObjectiveType.Unknown10)
                {
                    if (entry.Data > 1u && tables.Creature2.GetEntry(entry.Data) != null)
                        result.Add(entry.Data);
                    else if (entry.TargetGroupIdRewardPane != 0u)
                        result.UnionWith(TargetGroupHelper.GetCreatures(entry.TargetGroupIdRewardPane));
                }
                else
                {
                    if (entry.TargetGroupIdRewardPane != 0u)
                        result.UnionWith(TargetGroupHelper.GetCreatures(entry.TargetGroupIdRewardPane));
                    string text = tables.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdFull);
                    if (!string.IsNullOrEmpty(text))
                        named = System.Text.RegularExpressions.Regex.Matches(text, @"\$[a-z]?\(?[Cc]reature=(\d+)\)?").Select(m => uint.Parse(m.Groups[1].Value)).Distinct().ToArray();
                    result.UnionWith(named);
                }

                return (result.ToArray(), named);
            });
        }

        private static bool IsActivatable(uint creatureId)
        {
            return (NexusForever.Shared.GameTable.GameTableManager.Instance.Creature2.GetEntry(creatureId)?.Spell4IdActivate00 ?? 0u) != 0u;
        }

        /// <summary>
        /// True when the creature's faction is hostile to the Exile or Dominion player faction (faction friendship table, either direction).
        /// </summary>
        public static bool IsHostileToPlayers(uint creatureId)
        {
            Creature2Entry creature = NexusForever.Shared.GameTable.GameTableManager.Instance.Creature2.GetEntry(creatureId);
            if (creature == null || creature.FactionId == 0u)
                return false;

            var factions = Game.Reputation.FactionManager.Instance;
            var creatureFaction = (Game.Reputation.Static.Faction)creature.FactionId;
            foreach (var playerFaction in new[] { Game.Reputation.Static.Faction.Exile, Game.Reputation.Static.Faction.Dominion })
            {
                var level = factions.GetFaction(creatureFaction)?.GetFriendshipFactionLevel(playerFaction)
                    ?? factions.GetFaction(playerFaction)?.GetFriendshipFactionLevel(creatureFaction);
                if (level.HasValue && Game.Reputation.FactionNode.GetDisposition(level.Value) == Game.Reputation.Static.Disposition.Hostile)
                    return true;
            }

            return false;
        }

        public static bool IsSynthetic(QuestObjectiveEntry entry)
        {
            QuestObjectiveType type = (QuestObjectiveType)entry.Type;
            uint data = entry.Data;

            if (TextCreatures(entry).Length > 0 || EquipItem(entry) != 0u)
                return false;

            // type 15 targets are killed; type 10 targets are used (activate spell) or destroyed (hostile), others need a quest item path
            if (type == QuestObjectiveType.Unknown15)
                return !ScriptedTargets(entry).Targets.Any(TargetGroupHelper.IsSpawned);
            if (type == QuestObjectiveType.Unknown10)
                return !ScriptedTargets(entry).Targets.Any(c => TargetGroupHelper.IsSpawned(c) && (IsActivatable(c) || IsHostileToPlayers(c)));

            // virtual items come from the creatures of the reward pane group (kills and activations), without that group there is nothing to do
            if (type == QuestObjectiveType.VirtualCollect)
                return entry.TargetGroupIdRewardPane == 0u || TargetGroupHelper.ResolveSpawned(entry.TargetGroupIdRewardPane).Count == 0;

            // public events are played for real when the engine can run them: the bot goes to the event and fights its units
            if (type == QuestObjectiveType.CompleteEvent && Game.Events.PublicEventEngine.Instance.CanRun(data))
                return false;

            // objectives a bot driver goes and does (BotQuests.CreditSynthetic): synthetic only when the tables give the driver nowhere to go
            switch (type)
            {
                case QuestObjectiveType.EnterArea:
                    return !HasAreaPlace(entry);
                case QuestObjectiveType.EnterZone:
                    return !BotQuests.HasZoneLocation(data);
                case QuestObjectiveType.SpellSuccess:
                case QuestObjectiveType.SpellSuccess2:
                case QuestObjectiveType.SpellSuccess3:
                case QuestObjectiveType.SpellSuccess4:
                    return !HasSpellPlace(entry);
                case QuestObjectiveType.CompleteEvent:
                    return true; // the engine cannot run it (checked above)
                case QuestObjectiveType.CollectItem:
                    return CollectSources(entry).Length == 0;
            }

            if (BotReady.Contains(type))
            {
                // activate and talk objectives without a creature are completed by scripts in the real game
                return data == 0u && type != QuestObjectiveType.KillCreature && type != QuestObjectiveType.KillTargetGroup && type != QuestObjectiveType.KillTargetGroups;
            }

            return type != QuestObjectiveType.PvPKills;
        }

        /// <summary>
        /// The objective ends in synthetic credit: no driver (BotReady or crafting) can reach it. Player kills are left out, bots never fight players.
        /// </summary>
        public static bool NoPath(QuestObjectiveEntry entry)
        {
            var type = (QuestObjectiveType)entry.Type;
            if (type == QuestObjectiveType.PvPKills)
                return false;
            if (type is QuestObjectiveType.CraftSchematic or QuestObjectiveType.ObtainSchematic)
                return NexusForever.Shared.GameTable.GameTableManager.Instance.TradeskillSchematic2.GetEntry(entry.Data) == null;
            // a work order slot becomes a craft objective from its pool when the pool is known (RandomObjectives)
            if (type == QuestObjectiveType.Unknown31)
                return !RandomObjectives.HasPool(entry);
            return IsSynthetic(entry);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> placeCache = new();

        /// <summary>
        /// An EnterArea objective has an indicator location in a world bots can visit, or a spawned creature of its group (QuestManager.IsInArea credits both).
        /// </summary>
        private static bool HasAreaPlace(QuestObjectiveEntry entry) => placeCache.GetOrAdd(entry.Id, _ =>
            Indicators(entry).Any(l => BotQuests.CanVisitWorld(l.WorldId)) || (entry.Data != 0u && TargetGroupHelper.ResolveSpawned(entry.Data).Count > 0));

        /// <summary>
        /// A SpellSuccess objective's spell exists and has a spawned creature of its cast group or an indicator location to be cast at (BotQuests.TrySpellObjective).
        /// </summary>
        private static bool HasSpellPlace(QuestObjectiveEntry entry) => placeCache.GetOrAdd(entry.Id, _ =>
        {
            var tables = NexusForever.Shared.GameTable.GameTableManager.Instance;
            Spell4Entry spell = tables.Spell4.GetEntry(entry.Data);
            if (spell == null)
                return false;

            Spell4BaseEntry spellBase = tables.Spell4Base.GetEntry(spell.Spell4BaseIdBaseSpell);
            if (spellBase != null && spellBase.TargetGroupIdCastGroup != 0u)
                return TargetGroupHelper.ResolveSpawned(spellBase.TargetGroupIdCastGroup).Count > 0;

            WorldLocation2Entry first = tables.WorldLocation2.GetEntry(entry.WorldLocationsIdIndicator00);
            // no place: an item that casts the spell can be used anywhere (BotQuests.TrySpellObjective uses it through ClientItemUse)
            return (first != null && BotQuests.CanVisitWorld(first.WorldId)) || (first == null && SpellItem(entry) != 0u);
        });

        private static IEnumerable<WorldLocation2Entry> Indicators(QuestObjectiveEntry entry)
        {
            var table = NexusForever.Shared.GameTable.GameTableManager.Instance.WorldLocation2;
            foreach (uint id in new[] { entry.WorldLocationsIdIndicator00, entry.WorldLocationsIdIndicator01, entry.WorldLocationsIdIndicator02, entry.WorldLocationsIdIndicator03 })
                if (id != 0u && table.GetEntry(id) is WorldLocation2Entry location)
                    yield return location;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint[]> collectSources = new();
        private static Dictionary<uint, List<uint>> droppers;
        private static readonly object droppersGate = new();

        /// <summary>
        /// Spawned creatures a CollectItem objective's item comes from: the objective's reward pane group, and creatures whose researched drop
        /// table (data/research/creature-drops.tsv, the loot the server rolls in CreatureLoot) has the item. Empty when nothing is known.
        /// </summary>
        public static uint[] CollectSources(QuestObjectiveEntry entry)
        {
            if ((QuestObjectiveType)entry.Type != QuestObjectiveType.CollectItem)
                return Array.Empty<uint>();

            return collectSources.GetOrAdd(entry.Id, _ =>
            {
                var result = new HashSet<uint>();
                if (entry.TargetGroupIdRewardPane != 0u)
                    result.UnionWith(TargetGroupHelper.ResolveSpawned(entry.TargetGroupIdRewardPane));
                result.UnionWith(TextItemSources(entry).Where(TargetGroupHelper.IsSpawned));

                lock (droppersGate)
                {
                    if (droppers == null)
                    {
                        var index = new Dictionary<uint, List<uint>>();
                        var creatures = NexusForever.Shared.GameTable.GameTableManager.Instance.Creature2;
                        foreach (uint creatureId in NexusForever.Shared.Database.DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Select(sp => sp.Creature).Distinct())
                        {
                            var info = Game.Research.CreatureResearch.Get(creatureId, (int)(creatures.GetEntry(creatureId)?.MinLevel ?? 0u));
                            if (info == null)
                                continue;

                            foreach (var (itemId, _) in info.Drops)
                            {
                                if (!index.TryGetValue(itemId, out List<uint> list))
                                    index.Add(itemId, list = new List<uint>());
                                list.Add(creatureId);
                            }
                        }

                        droppers = index;
                    }

                    if (droppers.TryGetValue(entry.Data, out List<uint> from))
                        result.UnionWith(from);
                }

                return result.ToArray();
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> equipItems = new();

        /// <summary>
        /// The item of an activate objective without a creature whose text is "Equip the $(item=N)" (15 objectives, the gathering tool and
        /// item tutorials), 0 otherwise. Equipping the item credits it (Inventory.ItemMove -> QuestManager.OnItemEquipped).
        /// </summary>
        public static uint EquipItem(QuestObjectiveEntry entry)
        {
            if ((QuestObjectiveType)entry.Type is not (QuestObjectiveType.ActivateEntity or QuestObjectiveType.ActivateEntity2) || entry.Data != 0u)
                return 0u;

            return equipItems.GetOrAdd(entry.Id, _ =>
            {
                string text = NexusForever.Shared.GameTable.GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdFull);
                var match = string.IsNullOrEmpty(text) ? null : System.Text.RegularExpressions.Regex.Match(text, @"^\s*Equip (?:the |a |an |your )?\$[a-z]?\(item=(\d+)\)");
                return match is { Success: true } && NexusForever.Shared.GameTable.GameTableManager.Instance.Item.GetEntry(uint.Parse(match.Groups[1].Value)) != null
                    ? uint.Parse(match.Groups[1].Value) : 0u;
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint> spellItems = new();
        private static Dictionary<uint, List<uint>> itemsBySpell;

        /// <summary>
        /// An item whose use casts the spell of a SpellSuccess objective (ItemSpecial.Spell4IdOnActivate, same spell or same base spell), 0 when none.
        /// An item the objective text names ("Obtain a $(item=84594) and use it to upgrade your weapon!") is preferred.
        /// </summary>
        public static uint SpellItem(QuestObjectiveEntry entry)
        {
            if (!Game.Entity.QuestManager.IsSpellSuccess((QuestObjectiveType)entry.Type))
                return 0u;

            return spellItems.GetOrAdd(entry.Id, _ =>
            {
                var tables = NexusForever.Shared.GameTable.GameTableManager.Instance;
                lock (equipItems)
                {
                    if (itemsBySpell == null)
                    {
                        var index = new Dictionary<uint, List<uint>>();
                        foreach (Item2Entry item in tables.Item.Entries)
                        {
                            uint spell = item == null ? 0u : tables.ItemSpecial.GetEntry(item.ItemSpecialId00)?.Spell4IdOnActivate ?? 0u;
                            if (spell == 0u)
                                continue;
                            foreach (uint key in new[] { spell, 0x80000000u | (tables.Spell4.GetEntry(spell)?.Spell4BaseIdBaseSpell ?? 0u) })
                            {
                                if (!index.TryGetValue(key, out List<uint> list))
                                    index.Add(key, list = new List<uint>());
                                list.Add(item.Id);
                            }
                        }
                        itemsBySpell = index;
                    }
                }

                uint baseSpell = tables.Spell4.GetEntry(entry.Data)?.Spell4BaseIdBaseSpell ?? 0u;
                List<uint> items = itemsBySpell.GetValueOrDefault(entry.Data) ?? (baseSpell != 0u ? itemsBySpell.GetValueOrDefault(0x80000000u | baseSpell) : null);
                if (items == null || items.Count == 0)
                    return 0u;

                string text = tables.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdFull) ?? "";
                uint[] named = System.Text.RegularExpressions.Regex.Matches(text, @"\$[a-z]?\(?item=(\d+)\)?").Select(m => uint.Parse(m.Groups[1].Value)).ToArray();
                return items.FirstOrDefault(named.Contains) is uint n && n != 0u ? n : items.Min();
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, uint[]> textItemSources = new();

        /// <summary>
        /// Creatures a CollectItem objective's text names as the source of its item ("Collect $m(item=81821) from $m(creature=18563) in Algoroc"):
        /// the text names both the objective's item and the creature. Killing, activating or gathering one gives the item (QuestManager.OnQuestItemSource).
        /// </summary>
        public static uint[] TextItemSources(QuestObjectiveEntry entry)
        {
            if ((QuestObjectiveType)entry.Type != QuestObjectiveType.CollectItem || entry.Data == 0u)
                return Array.Empty<uint>();

            return textItemSources.GetOrAdd(entry.Id, _ =>
            {
                string text = NexusForever.Shared.GameTable.GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdFull);
                if (string.IsNullOrEmpty(text) || !System.Text.RegularExpressions.Regex.Matches(text, @"\$[a-z]?\(?item=(\d+)\)?").Any(m => m.Groups[1].Value == entry.Data.ToString()))
                    return Array.Empty<uint>();
                return System.Text.RegularExpressions.Regex.Matches(text, @"\$[a-z]?\(?[Cc]reature=(\d+)\)?").Select(m => uint.Parse(m.Groups[1].Value)).Distinct().ToArray();
            });
        }

        public static readonly bool SyntheticEnabled = Environment.GetEnvironmentVariable("NF_BOTS_SYNTHETIC") != "0";

        public static string Path => System.IO.Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "quest-coverage.txt");

        public static void Write()
        {
            List<QuestInfo> quests = GlobalQuestManager.Instance.GetAllQuestInfo().ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Quest coverage: {quests.Count} quests");

            int ready = quests.Count(q => q.Objectives.Count > 0 && q.Objectives.All(o => BotReady.Contains(o.Type) && !IsSynthetic(o.Entry)));
            sb.AppendLine($"Bots can do today: {ready} quests (every objective is one of: {string.Join(", ", BotReady)})");
            int withSynthetic = quests.Count(q => q.Objectives.Count > 0 && q.Objectives.All(o => SyntheticEnabled || (BotReady.Contains(o.Type) && !IsSynthetic(o.Entry))));
            sb.AppendLine($"Bots can take with synthetic credit ({(SyntheticEnabled ? "on" : "off")}): {withSynthetic} quests (everything outside the real list is credited directly, except PvPKills)");
            sb.AppendLine($"Quests without objectives: {quests.Count(q => q.Objectives.Count == 0)}");
            sb.AppendLine();

            sb.AppendLine("== Objective types by number of objectives (marked * = bots can do)");
            foreach (var group in quests.SelectMany(q => q.Objectives).GroupBy(o => o.Type).OrderByDescending(g => g.Count()))
                sb.AppendLine($"  {(BotReady.Contains(group.Key) ? '*' : ' ')} {group.Key,-30} type {(uint)group.Key,3}  {group.Count()} objectives");

            sb.AppendLine();
            sb.AppendLine("== Unlocked by adding one objective type (quests that need only that type on top of what bots do now)");
            // quests that miss exactly one objective type, grouped by that type
            var blockers = new Dictionary<QuestObjectiveType, List<QuestInfo>>();
            foreach (QuestInfo quest in quests.Where(q => q.Objectives.Count > 0))
            {
                List<QuestObjectiveType> missing = quest.Objectives.Select(o => o.Type).Where(t => !BotReady.Contains(t)).Distinct().ToList();
                if (missing.Count != 1)
                    continue;

                if (!blockers.TryGetValue(missing[0], out List<QuestInfo> list))
                    blockers.Add(missing[0], list = new List<QuestInfo>());

                list.Add(quest);
            }

            foreach (var pair in blockers.OrderByDescending(p => p.Value.Count))
                sb.AppendLine($"  {pair.Key,-30} {pair.Value.Count} quests, e.g. {string.Join(", ", pair.Value.Take(3).Select(q => q.Entry.Id))}");

            // objectives no driver can reach (IsSynthetic); "only" = quests where this type is the sole synthetic objective type,
            // i.e. the quests a real path for this type would make complete without any synthetic credit
            sb.AppendLine();
            sb.AppendLine("== Synthetic objectives by type (objectives, quests with one, quests where it is the only synthetic type)");
            var synthetic = quests.Where(q => q.Objectives.Count > 0)
                .Select(q => (Quest: q, Types: q.Objectives.Where(o => NoPath(o.Entry)).Select(o => o.Type).ToList()))
                .Where(x => x.Types.Count > 0)
                .ToList();
            // live = titled, not cut content, level prerequisite reachable (untitled quests are hidden flag quests or retired content)
            var english = NexusForever.Shared.GameTable.GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
            bool Live(QuestInfo q) => !string.IsNullOrEmpty(english?.GetEntry(q.Entry.LocalizedTextIdTitle)) && !CutContent.IsCutQuest(q.Entry) && q.Entry.PrerequisiteLevel <= 50u;
            foreach (var group in synthetic.SelectMany(x => x.Types.Distinct().Select(t => (Type: t, x.Quest, Only: x.Types.Distinct().Count() == 1, Objectives: x.Types.Count(y => y == t))))
                .GroupBy(x => x.Type).OrderByDescending(g => g.Count(x => Live(x.Quest))))
                sb.AppendLine($"  {group.Key,-30} type {(uint)group.Key,3}  {group.Sum(x => x.Objectives)} objectives, {group.Count()} quests ({group.Count(x => Live(x.Quest))} live), {group.Count(x => x.Only)} only ({group.Count(x => x.Only && Live(x.Quest))} live), e.g. {string.Join(", ", group.Where(x => Live(x.Quest)).Take(3).Select(x => x.Quest.Entry.Id))}");

            try
            {
                File.WriteAllText(Path, sb.ToString());
                // one line per objective without a path, for the offline research scripts (scripts/research/synthwhy.py)
                File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path), "synthetic-objectives.tsv"),
                    quests.SelectMany(q => q.Objectives.Select((o, i) => (q, o, i))).Where(x => NoPath(x.o.Entry))
                        .Select(x => $"{x.q.Entry.Id}\t{x.i}\t{x.o.Id}\t{(uint)x.o.Type}\t{x.o.Entry.Data}\t{(Live(x.q) ? 1 : 0)}"));
            }
            catch (Exception)
            {
                // diagnostics only
            }
        }
    }
}
