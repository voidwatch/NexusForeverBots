using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using NexusForever.Shared.Database;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Map.Static;
using NLog;
using NexusForever.Database.World.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Tradeskill;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// What bots know about quests. Bots only take quests made entirely of kill objectives (a creature, or a target group of creatures),
    /// because hunting is the only thing they can do. They skip the quest giver and turn-in checks, so nobody needs to walk to an NPC.
    /// </summary>
    public static class BotQuests
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        // creature id -> kill quests that need it
        private static Dictionary<uint, List<QuestInfo>> byCreature;
        private static readonly Dictionary<ushort, QuestInfo> questInfos = new();

        // creature id -> spawn points in every world, built once in the background from the world database
        private static volatile Dictionary<uint, List<(ushort World, Vector3 Position)>> globalSpawns;
        private static int globalSpawnsLoading;

        // how often each quest has been started by any bot since the server started, so bots spread over different quests
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, int> attempts = new();

        public static void NoteAttempt(ushort questId)
        {
            attempts.AddOrUpdate(questId, 1, (_, count) => count + 1);
            Diagnostics.QuestOutcomes.Accepted(questId);
        }

        // NF_BOTS_FOCUS=12,32: objective type ids; bots take quests with one of those types first (to test one kind of objective at a time)
        private static readonly HashSet<uint> focusTypes = (Environment.GetEnvironmentVariable("NF_BOTS_FOCUS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => uint.TryParse(t, out uint id) ? id : 0u).Where(id => id != 0u).ToHashSet();

        /// <summary>
        /// Level a bot takes the quest at: the con level, or lower when a level prerequisite caps it (comparison 5 LessThan, 6 LessThanOrEqual).
        /// </summary>
        private static long PickLevel(QuestInfo info)
        {
            // the quest's own minimum level wins over a lower con level: quests 3001/3011 (con level 1, PrerequisiteLevel 34) were only ever
            // looked at by level 1-9 bots, which QuestManager refuses ("level"), so no bot could take them (gt pickwhy)
            long level = Math.Max(info.Entry.ConLevel, info.Entry.PrerequisiteLevel);
            PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
            if (prerequisite == null || prerequisite.Flags != 0u)
                return level;

            for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
            {
                if (prerequisite.PrerequisiteTypeId[i] != 1u)
                    continue;

                long cap = prerequisite.PrerequisiteComparisonId[i] switch
                {
                    1u or 6u => prerequisite.Value[i],
                    5u       => (long)prerequisite.Value[i] - 1,
                    _        => long.MaxValue
                };
                if (cap >= 1 && cap < level)
                    level = cap;
            }

            return level;
        }

        private static bool NeverMeetsLevel(uint prerequisiteId)
        {
            PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(prerequisiteId);
            if (prerequisite == null || prerequisite.Flags != 0u) // flags 1 = any condition (OR)
                return false;

            for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
                if (prerequisite.PrerequisiteTypeId[i] == 1u && prerequisite.PrerequisiteComparisonId[i] == 5u && prerequisite.Value[i] <= 1u)
                    return true;
            return false;
        }

        private static Dictionary<ushort, List<ushort>> followUpsOf;

        /// <summary>
        /// Never completed by any bot, or completed but it leads (through its follow-ups) to a doable quest nobody has completed. A chain is
        /// done by one character, so after the bot that did the first quest leaves, another bot has to start the chain again: with 30 bots
        /// online, 637 open quests were refused by every bot for a prerequisite quest that only an offline bot had done (gt pickwhy).
        /// </summary>
        private static bool IsOpen(ushort questId)
        {
            if (!Diagnostics.QuestOutcomes.IsCompleted(questId))
                return true;

            if (opensCache.TryGetValue(questId, out (bool Opens, DateTime At) cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30))
                return cached.Opens;

            bool opens = OpensOpenQuest(questId, 0);
            opensCache[questId] = (opens, DateTime.UtcNow);
            return opens;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, (bool Opens, DateTime At)> opensCache = new();

        private static bool OpensOpenQuest(ushort questId, int depth)
        {
            if (depth > 12)
                return false;

            if (followUpsOf == null)
            {
                var map = new Dictionary<ushort, List<ushort>>();
                foreach (QuestInfo info in questInfos.Values)
                {
                    var before = info.Entry.PrerequisiteQuests.Where(q => q != 0u).Select(q => (ushort)q).ToList();
                    PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
                    if (prerequisite != null)
                        for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
                            if (prerequisite.PrerequisiteTypeId[i] == 6u && prerequisite.PrerequisiteComparisonId[i] == 1u)
                                before.Add((ushort)prerequisite.ObjectId[i]);

                    foreach (ushort previous in before.Distinct())
                    {
                        if (!map.TryGetValue(previous, out List<ushort> list))
                            map.Add(previous, list = new List<ushort>());
                        list.Add((ushort)info.Entry.Id);
                    }
                }

                followUpsOf = map;
            }

            Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex = globalSpawns;
            if (spawnIndex == null || !followUpsOf.TryGetValue(questId, out List<ushort> next))
                return false;

            foreach (ushort followUp in next)
            {
                if (!targetsByQuest.TryGetValue(followUp, out HashSet<uint> targets) || !HasOpenSpawn(followUp, targets, spawnIndex))
                    continue;

                if (!Diagnostics.QuestOutcomes.IsCompleted(followUp) || OpensOpenQuest(followUp, depth + 1))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The quest needs another quest done or held: listed prerequisite quests, or a prerequisite row with a quest state check
        /// (type 6 Equal, e.g. quest 3304 needs quest 3302 accepted, achieved or completed).
        /// </summary>
        private static bool IsFollowUp(QuestInfo info)
        {
            if (info.Entry.PrerequisiteQuests.Any(q => q != 0u))
                return true;

            PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
            if (prerequisite == null)
                return false;

            for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
                if (prerequisite.PrerequisiteTypeId[i] == 6u && prerequisite.PrerequisiteComparisonId[i] == 1u)
                    return true;
            return false;
        }

        private static QuestInfo LeastTried(List<QuestInfo> candidates)
        {
            // quests nobody has completed yet come first, once everything on offer has been done the rest is repeated
            List<QuestInfo> untouched = candidates.Where(q => IsOpen((ushort)q.Entry.Id)).ToList();
            if (untouched.Count > 0)
                candidates = untouched;

            if (focusTypes.Count > 0)
            {
                List<QuestInfo> focused = candidates.Where(q => q.Objectives.Any(o => focusTypes.Contains(o.Entry.Type))).ToList();
                if (focused.Count > 0)
                    candidates = focused;
            }

            // chain follow-ups first: only the bot that finished the previous quest can take them, so a random pick among everything on offer
            // left 182 follow-ups never accepted although their prerequisite quest had been completed (quest dump)
            List<QuestInfo> followUps = candidates.Where(IsFollowUp).ToList();
            if (followUps.Count > 0)
                candidates = followUps;

            int least = candidates.Min(q => attempts.GetValueOrDefault((ushort)q.Entry.Id));
            List<QuestInfo> fresh = candidates.Where(q => attempts.GetValueOrDefault((ushort)q.Entry.Id) == least).ToList();
            return fresh[BotManager.Rng.Next(fresh.Count)];
        }
        private static readonly Dictionary<ushort, HashSet<uint>> targetsByQuest = new();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> visitableWorlds = new();

        /// <summary>
        /// An open world with map data that a bot can be sent to (instances, residences and worlds without a map file would fail or crash the teleport).
        /// </summary>
        public static bool CanVisitWorld(uint worldId)
        {
            return visitableWorlds.GetOrAdd(worldId, id =>
            {
                WorldEntry world = GameTableManager.Instance.World.GetEntry(id);
                return world != null && world.Type == 0u && BaseMapManager.Instance.HasBaseMap(world.AssetPath);
            });
        }

        // world id -> creature id -> spawn positions
        private static readonly Dictionary<ushort, Dictionary<uint, List<Vector3>>> spawns = new();

        private static void EnsureIndex()
        {
            if (byCreature != null)
                return;

            Diagnostics.QuestOutcomes.LoadDatabaseHistory();

            // build on the side and publish at the end, so a failure never leaves a half filled index behind
            var index = new Dictionary<uint, List<QuestInfo>>();
            foreach (QuestInfo info in GlobalQuestManager.Instance.GetAllQuestInfo())
            {
                // Halon Ring and Murkmire were never released: bots do not pick their quests and the truth does not count them (11 of the
                // 124 "stalled" quests of the 36 h soak were cut content in zone 1480)
                if (Game.Quest.CutContent.IsCutQuest(info.Entry))
                    continue;

                HashSet<uint> targets = ResolveTargets(info);
                if (targets == null)
                    continue;

                // quests made only of synthetic objectives have no creature to go to, bots take them where they stand
                if (targets.Count == 0)
                {
                    targetsByQuest[(ushort)info.Entry.Id] = targets;
                    questInfos[(ushort)info.Entry.Id] = info;
                    continue;
                }

                targetsByQuest[(ushort)info.Entry.Id] = targets;
                questInfos[(ushort)info.Entry.Id] = info;
                foreach (uint creatureId in targets)
                {
                    if (!index.TryGetValue(creatureId, out List<QuestInfo> list))
                    {
                        list = new List<QuestInfo>();
                        index.Add(creatureId, list);
                    }

                    list.Add(info);
                }
            }

            byCreature = index;

            try
            {
                QuestCoverage.Write();
            }
            catch (Exception exception)
            {
                log.Error(exception);
            }
        }

        // null when the quest has anything a bot cannot do
        private const long MaxLevel = 50; // XpManager maxLevel

        private static HashSet<uint> ResolveTargets(QuestInfo info)
        {
            // PrerequisiteLevel above the level cap (quest 3303: 99, 3503: 90) can never be met by any character
            if (info.Entry.PrerequisiteLevel > MaxLevel)
                return null;

            // a level prerequisite no character meets: Level LessThan 1 (rows 4106, 6232, 14643; 52 quests: Everstar Grove, Base Drop, The Ravenous
            // Grove, Ability Training 101, the old path faction intros). Their givers still exist; the content was retired, nobody can take them.
            if (NeverMeetsLevel(info.Entry.PrerequisiteId))
                return null;

            // breadcrumb quests (no objectives: take from the giver, hand in at the receiver) are done by taking and handing in; 78 of them open the
            // chains of 211 doable quests. Untitled ones without a giver creature or communicator message are hidden flag quests that scripts grant,
            // bots leave those alone.
            if (info.Objectives.Count == 0)
            {
                bool titled = !string.IsNullOrEmpty(GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(info.Entry.LocalizedTextIdTitle));
                ushort id = (ushort)info.Entry.Id;
                return titled || GlobalQuestManager.Instance.GetQuestGivers(id).Any() || GlobalQuestManager.Instance.GetQuestCommunicatorMessages(id).Any()
                    ? new HashSet<uint>() : null;
            }

            var targets = new HashSet<uint>();
            var required = new List<HashSet<uint>>();
            foreach (QuestObjectiveInfo objective in info.Objectives)
            {
                if (QuestCoverage.IsSynthetic(objective.Entry))
                {
                    if (QuestCoverage.SyntheticEnabled)
                        continue;

                    return null;
                }

                var own = new HashSet<uint>();
                switch (objective.Type)
                {
                    case QuestObjectiveType.KillCreature:
                        own.Add(objective.Entry.Data);
                        break;
                    case QuestObjectiveType.KillTargetGroup:
                    case QuestObjectiveType.KillTargetGroups:
                    case QuestObjectiveType.ActivateTargetGroup:
                    case QuestObjectiveType.ActivateTargetGroupChecklist:
                        own.UnionWith(TargetGroupHelper.ResolveSpawned(objective.Entry.Data));
                        break;
                    case QuestObjectiveType.ActivateEntity:
                    case QuestObjectiveType.ActivateEntity2:
                    case QuestObjectiveType.SucceedCSI:
                    case QuestObjectiveType.TalkTo:
                        if (objective.Entry.Data == 0u)
                            own.UnionWith(QuestCoverage.TextCreatures(objective.Entry));
                        else
                            own.Add(objective.Entry.Data);
                        break;
                    case QuestObjectiveType.TalkToTargetGroup:
                        own.UnionWith(TargetGroupHelper.ResolveSpawned(objective.Entry.Data));
                        break;
                    case QuestObjectiveType.VirtualCollect:
                        own.UnionWith(TargetGroupHelper.ResolveSpawned(objective.Entry.TargetGroupIdRewardPane));
                        own.UnionWith(QuestCoverage.TextCreatures(objective.Entry));
                        break;
                    case QuestObjectiveType.CompleteQuest:
                        // credited when the other quest is completed, nothing to hunt
                        break;
                    case QuestObjectiveType.EnterArea:
                    case QuestObjectiveType.EnterZone:
                    case QuestObjectiveType.SpellSuccess:
                    case QuestObjectiveType.SpellSuccess2:
                    case QuestObjectiveType.SpellSuccess3:
                    case QuestObjectiveType.SpellSuccess4:
                    case QuestObjectiveType.CompleteEvent:
                        // the driver in CreditSynthetic travels to the place itself (indicator, zone location, cast group, event location)
                        break;
                    case QuestObjectiveType.CollectItem:
                        own.UnionWith(QuestCoverage.CollectSources(objective.Entry));
                        break;
                    case QuestObjectiveType.Unknown10:
                    case QuestObjectiveType.Unknown15:
                        own.UnionWith(QuestCoverage.ScriptedTargets(objective.Entry).Targets.Where(TargetGroupHelper.IsSpawned));
                        break;
                    default:
                        return null;
                }

                // a creature objective whose creatures have no spawn anywhere cannot be done (quest 1806: group 307 names creatures 5006/5007 that do
                // not exist); without this the quest had no targets and counted as doable anywhere. Optional objectives do not block the quest.
                bool needsCreature = objective.Type is not (QuestObjectiveType.CompleteQuest or QuestObjectiveType.EnterArea or QuestObjectiveType.EnterZone
                    or QuestObjectiveType.SpellSuccess or QuestObjectiveType.SpellSuccess2 or QuestObjectiveType.SpellSuccess3 or QuestObjectiveType.SpellSuccess4
                    or QuestObjectiveType.CompleteEvent) && QuestCoverage.EquipItem(objective.Entry) == 0u;
                if (needsCreature && !objective.IsOptional() && !own.Any(TargetGroupHelper.IsSpawned))
                    return null;

                if (needsCreature && !objective.IsOptional())
                    required.Add(own);
                targets.UnionWith(own);
            }

            requiredTargets[(ushort)info.Entry.Id] = required;
            return targets;
        }

        // the creatures of each required creature objective, filled by ResolveTargets (HasOpenSpawn)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, List<HashSet<uint>>> requiredTargets = new();

        private static void EnsureGlobalSpawns()
        {
            if (globalSpawns != null || Interlocked.Exchange(ref globalSpawnsLoading, 1) == 1)
                return;

            EnsureIndex();

            Task.Run(() =>
            {
                try
                {
                    var index = new Dictionary<uint, List<(ushort, Vector3)>>();
                    foreach (var spawn in DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns())
                    {
                        if (!byCreature.ContainsKey(spawn.Creature))
                            continue;

                        // residences and communities are instanced per player, everything else can be entered directly
                        var world = GameTableManager.Instance.World.GetEntry(spawn.World);
                        if (world == null || (MapType)world.Type == MapType.Residence || (MapType)world.Type == MapType.Community || !BaseMapManager.Instance.HasBaseMap(world.AssetPath))
                            continue;

                        if (!index.TryGetValue(spawn.Creature, out List<(ushort, Vector3)> list))
                        {
                            list = new List<(ushort, Vector3)>();
                            index.Add(spawn.Creature, list);
                        }

                        list.Add((spawn.World, new Vector3(spawn.X, spawn.Y, spawn.Z)));
                    }

                    globalSpawns = index;
                    log.Info($"Bot quest index ready: {index.Count} quest target creatures have spawn points.");
                }
                catch (Exception exception)
                {
                    log.Error(exception);
                    globalSpawns = new Dictionary<uint, List<(ushort, Vector3)>>();
                }
            });
        }

        /// <summary>
        /// Pick any quest that suits the bot's level and that has a target spawn somewhere, whichever world that is.
        /// Returns null while the spawn index is still loading.
        /// </summary>
        /// <summary>
        /// Zone 12 (WorldZone AllowAccess 0, no name) holds 128 developer and beta quests: "Level 15", "[Beta] Weekly Costume Piece!",
        /// "Ready Your Character for the Malgrave Trail" ("Receive items and be leveled up"), "Help is needed in Whitevale 4!" with
        /// RewardXpOverride 1.5-66 million (all 12 quests with an override of 1 million or more are in zone 12). A fresh character has no way to reach
        /// them; a clean bot that took one went from level 3 to 50 in one turn-in. Clean bots never pick them (likely, docs/ASSUMPTIONS.md).
        /// </summary>
        private static bool CleanSkips(Player me, QuestInfo info) => info.Entry.WorldZoneId == 12u && Diagnostics.QuestTruth.IsClean(me);

        public static QuestInfo PickAnywhere(Player me, out ushort world, out Vector3 position)
        {
            world    = 0;
            position = default;

            EnsureGlobalSpawns();
            Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex = globalSpawns;
            if (spawnIndex == null)
                return null;

            var candidates = new List<QuestInfo>();
            int outOfWindow = 0, noSpawn = 0, cannotAccept = 0, done = 0;
            bool travelled = false;
            foreach ((ushort questId, HashSet<uint> targets) in targetsByQuest)
            {
                QuestInfo info = questInfos[questId];
                if (CleanSkips(me, info))
                    continue;

                long level = PickLevel(info);
                if (level != 0 && ((level < (long)me.Level - 5 && !IsFollowUp(info)) || level > (long)me.Level + 3))
                {
                    outOfWindow++;
                    continue;
                }

                if (!HasOpenSpawn(questId, targets, spawnIndex))
                {
                    noSpawn++;
                    continue;
                }

                if (!me.QuestManager.CanAcceptWithoutGiver(info) && !(LearnTradeskillFor(me, info) && me.QuestManager.CanAcceptWithoutGiver(info)))
                {
                    cannotAccept++;
                    travelled = travelled || TravelToQuestZone(me, info);
                    Diagnostics.FailureLedger.Record("pick-refused", Game.Entity.QuestManager.LastRefusal ?? "?", $"quest {questId}");
                    continue;
                }

                if (Diagnostics.QuestOutcomes.IsCompleted(questId))
                    done++;
                candidates.Add(info);
            }

            // diagnostic: where the quests of the table drop out of the pick (one entry per call, counts in the example)
            Diagnostics.FailureLedger.Record("pick-filter", $"bot level {me.Level / 10 * 10}s", $"window-out {outOfWindow}, no-open-spawn {noSpawn}, cannot-accept {cannotAccept}, candidates {candidates.Count} (completed by anyone {done})");

            if (candidates.Count == 0)
                return null;

            // everything within reach is already done (by anyone): go and look for open quests at another level before repeating one
            // (also when only a handful of never-completed quests are left here: the rest of the level range is repeats)
            // (the last few never-completed quests of a level range are still tried until each has had three attempts this run: rotating away
            // whenever fewer than three were left kept 652 doable quests from ever being accepted in a 1 h soak)
            List<QuestInfo> undone = candidates.Where(c => IsOpen((ushort)c.Entry.Id)).ToList();
            List<QuestInfo> fresh = undone.Where(c => attempts.GetValueOrDefault((ushort)c.Entry.Id) < 3).ToList();
            if (fresh.Count == 0 && SuggestLevel(me) > 0)
                return null;

            QuestInfo pick = LeastTried(fresh.Count > 0 ? fresh : undone.Count > 0 ? undone : candidates);
            if (targetsByQuest[(ushort)pick.Entry.Id].Count == 0)
                return pick;

            return TryFindSpawnAnywhere(targetsByQuest[(ushort)pick.Entry.Id], out world, out position) ? pick : null;
        }

        /// <summary>
        /// A bot past the quests of its level range gets sent back to the level range with the most quests nobody has completed yet
        /// (otherwise every bot ends at the level cap and the low and mid level quests are never exercised). 0 when there is nothing better.
        /// </summary>
        public static bool SpawnIndexReady => globalSpawns != null;

        /// <summary>
        /// One report line: quests completed at least once / quests bots can do (a target spawn exists), plus completed overall.
        /// </summary>
        /// <summary>
        /// Why doable quests are still open: never accepted (split by whether a prerequisite quest is itself still open) or accepted without a completion.
        /// </summary>
        public static List<string> OpenQuestReport()
        {
            var lines = new List<string>();
            var spawnIndex = globalSpawns;
            if (spawnIndex == null)
            {
                lines.Add("spawn index loading");
                return lines;
            }

            var never = new List<QuestInfo>();
            var tried = new List<(QuestInfo Info, int Accepted, int Stalled, string Stall)>();
            foreach ((ushort questId, HashSet<uint> targets) in targetsByQuest.ToArray())
            {
                if (!HasOpenSpawn(questId, targets, spawnIndex))
                    continue;
                if (Diagnostics.QuestOutcomes.IsCompleted(questId))
                    continue;

                QuestInfo info = questInfos[questId];
                if (Diagnostics.QuestOutcomes.HasHistory(questId, out int accepted, out int stalled, out string stall) && accepted > 0)
                    tried.Add((info, accepted, stalled, stall));
                else
                    never.Add(info);
            }

            int chain = never.Count(q => WaitsForQuests(q, Diagnostics.QuestOutcomes.IsCompleted));
            lines.Add($"open doable quests: {never.Count + tried.Count}; never accepted {never.Count} (of which a prerequisite quest is still open: {chain}); accepted but never completed {tried.Count}");
            foreach (var band in never.GroupBy(q => q.Entry.ConLevel / 10).OrderBy(g => g.Key))
                lines.Add($"  never accepted, level {band.Key * 10}-{band.Key * 10 + 9}: {band.Count()} (chain-blocked {band.Count(q => WaitsForQuests(q, Diagnostics.QuestOutcomes.IsCompleted))})");
            var free = never.Where(q => !WaitsForQuests(q, Diagnostics.QuestOutcomes.IsCompleted)).OrderBy(q => q.Entry.Id).ToList();
            lines.Add($"  never accepted and not chain-blocked: {free.Count}; examples {string.Join(", ", free.Take(12).Select(q => $"{q.Entry.Id}(L{q.Entry.ConLevel})"))}");
            foreach (var group in tried.GroupBy(t => System.Text.RegularExpressions.Regex.Replace(t.Stall ?? "", @"quest \d+|objective \d+|data \d+|\d+/\d+", "#")).OrderByDescending(g => g.Count()).Take(12))
                lines.Add($"  accepted, not completed x{group.Count()}: {group.Key} (e.g. quest {group.First().Info.Entry.Id})");

            return lines;
        }

        /// <summary>
        /// One tab separated line per quest in the table, for offline first-failing-step analysis (docs/QUEST-COVERAGE.md):
        /// id, indexed (bots can resolve every objective), doable (open spawn), completed, accepted, stalled, con level, prerequisite level,
        /// faction, race, class, prerequisite flags, prerequisite quests, prerequisite id, periodic group, type, objectives (type:data:count), last stall.
        /// </summary>
        /// <summary>
        /// For every doable quest nobody has completed: what each online bot would say to it (level window, or the refusal of
        /// CanAcceptWithoutGiver, or candidate). Lines: quest, pick level, verdict, reason counts. Diagnostics only (gt pickwhy).
        /// </summary>
        public static List<string> PickWhy(IReadOnlyList<Player> players, out List<string> summary)
        {
            EnsureIndex();
            var lines = new List<string>();
            var verdicts = new Dictionary<string, int>();
            Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex = globalSpawns;
            foreach ((ushort questId, HashSet<uint> targets) in targetsByQuest)
            {
                if (Diagnostics.QuestOutcomes.IsCompleted(questId) || spawnIndex == null || !HasOpenSpawn(questId, targets, spawnIndex))
                    continue;

                QuestInfo info = questInfos[questId];
                long level = PickLevel(info);
                var reasons = new Dictionary<string, int>();
                foreach (Player player in players)
                {
                    string reason;
                    try
                    {
                        if (level != 0 && ((level < (long)player.Level - 5 && !IsFollowUp(info)) || level > (long)player.Level + 3))
                            reason = "window";
                        else if (player.QuestManager.CanAcceptWithoutGiver(info))
                            reason = "candidate";
                        else
                            reason = Game.Entity.QuestManager.LastRefusal ?? "?";
                    }
                    catch (Exception)
                    {
                        reason = "error";
                    }

                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }

                // the verdict is the most hopeful outcome: a candidate, else what stops the bots of the right faction in the window,
                // else the faction, else the window
                string verdict = reasons.ContainsKey("candidate") ? "candidate"
                    : reasons.Where(r => r.Key != "window" && !r.Key.StartsWith("faction")).OrderByDescending(r => r.Value).Select(r => r.Key).FirstOrDefault()
                    ?? reasons.Where(r => r.Key != "window").OrderByDescending(r => r.Value).Select(r => r.Key).FirstOrDefault() ?? "window";
                verdicts[verdict] = verdicts.GetValueOrDefault(verdict) + 1;
                lines.Add(string.Join("\t", questId, level, verdict, string.Join(",", reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key}:{r.Value}"))));
            }

            summary = verdicts.OrderByDescending(v => v.Value).Select(v => $"{v.Value} {v.Key}").ToList();
            summary.Add("bots by faction: " + string.Join(", ", players.GroupBy(p => p.Faction1).Select(g => $"{g.Key} {g.Count()}")));
            return lines;
        }

        /// <summary>
        /// Doable quests no clean bot has completed (gt questtruth), one line each: quest, pick level, reason, detail. Reason: "in-progress" (a clean
        /// bot holds it), "stalled" (clean bots took it and gave up, detail = last objective states), "taken" (taken, dropped without a stall), else
        /// what the online clean bots say to it like gt pickwhy: "candidate" (one may take it, not picked yet), the refusal of the quest manager
        /// (prerequisite, level...), "faction", or "window" (outside every clean bot's pick window, detail = their level range).
        /// The last column is the last stall any bot reported for the quest (quest-outcomes.txt), older runs included.
        /// </summary>
        public static List<string> TruthOpen(IReadOnlyList<Player> players, out List<string> summary)
        {
            EnsureIndex();
            var lines = new List<string>();
            var reasons = new Dictionary<string, int>();
            Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex = globalSpawns;
            int doable = 0;
            string levels = players.Count == 0 ? "no clean bot online" : $"clean bots level {players.Min(p => p.Level)}-{players.Max(p => p.Level)}";
            foreach ((ushort questId, HashSet<uint> targets) in targetsByQuest.ToArray())
            {
                if (spawnIndex == null || !HasOpenSpawn(questId, targets, spawnIndex))
                    continue;
                doable++;
                if (Diagnostics.QuestTruth.CleanCompleted(questId))
                    continue;

                QuestInfo info = questInfos[questId];
                long level = PickLevel(info);
                Diagnostics.QuestTruth.QuestStat stat = Diagnostics.QuestTruth.Stat(questId);
                string reason, detail = "";
                if (Diagnostics.QuestTruth.IsHeld(questId))
                    reason = "in-progress";
                else if (stat != null && stat.LastDrop.Length > 0)
                {
                    reason = "stalled";
                    detail = $"taken {stat.Accepted}, dropped {stat.Abandoned}: {stat.LastDrop}";
                }
                else if (stat != null && stat.Accepted > 0)
                {
                    reason = "taken";
                    detail = $"taken {stat.Accepted}, dropped {stat.Abandoned}";
                }
                else
                {
                    var verdicts = new Dictionary<string, int>();
                    foreach (Player player in players)
                    {
                        string verdict;
                        try
                        {
                            if (level != 0 && ((level < (long)player.Level - 5 && !IsFollowUp(info)) || level > (long)player.Level + 3))
                                verdict = "window";
                            else if (player.QuestManager.CanAcceptWithoutGiver(info))
                                verdict = "candidate";
                            else
                                verdict = Game.Entity.QuestManager.LastRefusal ?? "?";
                        }
                        catch (Exception)
                        {
                            verdict = "error";
                        }
                        verdicts[verdict] = verdicts.GetValueOrDefault(verdict) + 1;
                    }

                    reason = verdicts.ContainsKey("candidate") ? "candidate"
                        : verdicts.Where(r => r.Key != "window" && !r.Key.StartsWith("faction")).OrderByDescending(r => r.Value).Select(r => r.Key).FirstOrDefault()
                        ?? verdicts.Where(r => r.Key != "window").OrderByDescending(r => r.Value).Select(r => r.Key).FirstOrDefault() ?? "window";
                    detail = reason == "window" ? levels : string.Join(",", verdicts.OrderByDescending(r => r.Value).Select(r => $"{r.Key}:{r.Value}"));
                }

                // refusals carry ids ("prerequisite quest 1234"): the summary groups by the words before the first number
                string group = System.Text.RegularExpressions.Regex.Replace(reason, @"\s*\d.*$", "");
                reasons[group] = reasons.GetValueOrDefault(group) + 1;
                Diagnostics.QuestOutcomes.HasHistory(questId, out _, out _, out string lastStall);
                lines.Add(string.Join("\t", questId, level, reason, detail.Replace('\t', ' '), (lastStall ?? "").Replace('\t', ' ')));
            }

            summary = new List<string> { $"doable {doable}, completed by a clean bot {doable - lines.Count}, not completed {lines.Count}; {levels}" };
            summary.AddRange(reasons.OrderByDescending(r => r.Value).Select(r => $"  {r.Value} {r.Key}"));
            return lines;
        }

        /// <summary>
        /// Test command (bot quest): hand the quest to a quest bot, which applies it on its next think (Bot.ApplyForcedQuest).
        /// </summary>
        public static string ForceQuest(ushort questId, string name)
        {
            EnsureIndex();
            QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questId);
            if (info == null)
                return $"No quest {questId}.";

            Bot chosen = null;
            foreach (Bot bot in BotManager.Instance.Bots.Where(b => b.IsAmbient && b.Role == BotRole.Quest && !b.IsForced && b.Session?.Player != null))
            {
                Player player = bot.Session.Player;
                if (!name.Equals("any", StringComparison.OrdinalIgnoreCase))
                {
                    if (bot.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        chosen = bot;
                    continue;
                }

                if ((info.Entry.QuestPlayerFactionEnum == 0u && player.Faction1 != Game.Reputation.Static.Faction.Exile)
                    || (info.Entry.QuestPlayerFactionEnum == 1u && player.Faction1 != Game.Reputation.Static.Faction.Dominion)
                    || (info.Entry.PrerequisiteRace != 0u && player.Race != (Race)info.Entry.PrerequisiteRace)
                    || (info.Entry.PrerequisiteClass != 0u && player.Class != (Class)info.Entry.PrerequisiteClass))
                    continue;

                chosen = bot;
                break;
            }

            if (chosen == null)
                return $"No free quest bot fits quest {questId}.";

            chosen.ForcedQuest = questId;
            return $"Bot {chosen.Name} takes quest {questId} on its next think.";
        }

        /// <summary>Level a bot is put at for a forced test quest.</summary>
        public static long TestLevel(QuestInfo info) => Math.Clamp(PickLevel(info), 1, MaxLevel);

        /// <summary>Targets of a quest for travel, empty when it has none or is not indexed.</summary>
        public static HashSet<uint> IndexedTargets(ushort questId)
        {
            EnsureIndex();
            return targetsByQuest.TryGetValue(questId, out HashSet<uint> targets) ? targets : new HashSet<uint>();
        }

        public static List<string> QuestDump()
        {
            EnsureIndex();
            var spawnIndex = globalSpawns ?? new Dictionary<uint, List<(ushort World, Vector3 Position)>>();
            var lines = new List<string>();
            foreach (QuestInfo info in GlobalQuestManager.Instance.GetAllQuestInfo())
            {
                ushort id = (ushort)info.Entry.Id;
                bool indexed = targetsByQuest.TryGetValue(id, out HashSet<uint> targets);
                bool doable = indexed && HasOpenSpawn(id, targets, spawnIndex);
                Diagnostics.QuestOutcomes.HasHistory(id, out int accepted, out int stalled, out string stall);
                string objectives = string.Join(",", info.Objectives.Select(o => $"{(uint)o.Type}:{o.Entry.Data}:{o.Entry.Count}"));
                string prereqs = string.Join(",", info.Entry.PrerequisiteQuests.Where(q => q != 0u));
                lines.Add(string.Join("\t", id, indexed ? 1 : 0, doable ? 1 : 0, Diagnostics.QuestOutcomes.IsCompleted(id) ? 1 : 0, accepted, stalled,
                    info.Entry.ConLevel, info.Entry.PrerequisiteLevel, info.Entry.QuestPlayerFactionEnum, info.Entry.PrerequisiteRace, info.Entry.PrerequisiteClass,
                    info.Entry.PrerequisiteFlags, prereqs, info.Entry.PrerequisiteId, info.Entry.PeriodicQuestGroupId, info.Entry.Type, objectives, stall ?? "",
                    QuestCoverage.HeuristicKind(info)));
            }

            return lines;
        }

        /// <summary>
        /// The quest still waits for prerequisite quests (any of them when flag 1 is set, else all), judged by the supplied completion check.
        /// </summary>
        private static bool WaitsForQuests(QuestInfo info, Func<ushort, bool> completed)
        {
            var needed = info.Entry.PrerequisiteQuests.Where(p => p != 0u).Select(p => (ushort)p).ToList();
            if (needed.Count == 0)
                return false;

            return (info.Entry.PrerequisiteFlags & 1u) != 0u ? !needed.Any(completed) : !needed.All(completed);
        }

        /// <summary>Open doable quests (doable: every objective resolves and has an open-world spawn; open: nobody completed it), for the forced sweep.</summary>
        public static List<QuestInfo> OpenDoable()
        {
            EnsureIndex();
            var spawnIndex = globalSpawns;
            if (spawnIndex == null)
                return new List<QuestInfo>();
            return targetsByQuest.ToArray()
                .Where(t => HasOpenSpawn(t.Key, t.Value, spawnIndex) && !Diagnostics.QuestOutcomes.IsCompleted(t.Key))
                .Select(t => questInfos[t.Key]).OrderBy(q => q.Entry.Id).ToList();
        }

        /// <summary>The quest waits for a prerequisite quest that no character has completed.</summary>
        public static bool WaitsForOpenQuests(QuestInfo info) => WaitsForQuests(info, Diagnostics.QuestOutcomes.IsCompleted);

        /// <summary>Quests that list this one as a prerequisite (Quest2 prerequisite quests, or a type 6 row on the quest's prerequisite).</summary>
        public static List<ushort> FollowUps(ushort questId)
        {
            EnsureIndex();
            OpensOpenQuest(questId, 12); // builds followUpsOf on first use, depth 12 stops the walk after one level
            return followUpsOf != null && followUpsOf.TryGetValue(questId, out List<ushort> next) ? next : new List<ushort>();
        }

        /// <summary>Faction, race and class of the quest fit the character (the forced path skips every other prerequisite).</summary>
        public static bool FitsIdentity(QuestInfo info, Player me)
        {
            return !(info.Entry.QuestPlayerFactionEnum == 0u && me.Faction1 != Game.Reputation.Static.Faction.Exile)
                && !(info.Entry.QuestPlayerFactionEnum == 1u && me.Faction1 != Game.Reputation.Static.Faction.Dominion)
                && !(info.Entry.PrerequisiteRace != 0u && me.Race != (Race)info.Entry.PrerequisiteRace)
                && !(info.Entry.PrerequisiteClass != 0u && me.Class != (Class)info.Entry.PrerequisiteClass);
        }

        private static int targetsDumped;

        /// <summary>
        /// Once per run: /cache/quest-targets.tsv (quest, doable, completed, objective target creatures, worlds with a spawn of them) so the
        /// doable set of two builds can be compared offline (docs/QUEST-COVERAGE.md).
        /// </summary>
        private static void DumpQuestTargets(Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex)
        {
            // targetsByQuest fills while the index is built; only a finished index (byCreature published) gives the whole set
            if (byCreature == null || System.Threading.Interlocked.Exchange(ref targetsDumped, 1) == 1)
                return;
            try
            {
                var lines = new List<string> { "quest\tdoable\tcompleted\ttargets\tspawn worlds" };
                foreach ((ushort questId, HashSet<uint> targets) in targetsByQuest.ToArray())
                    lines.Add($"{questId}\t{(HasOpenSpawn(questId, targets, spawnIndex) ? 1 : 0)}\t{(Diagnostics.QuestOutcomes.IsCompleted(questId) ? 1 : 0)}\t"
                        + $"{string.Join(",", targets.OrderBy(t => t))}\t"
                        + string.Join(",", targets.SelectMany(t => spawnIndex.TryGetValue(t, out var list) ? list.Select(o => o.World) : Enumerable.Empty<ushort>()).Distinct().OrderBy(w => w)));
                System.IO.File.WriteAllLines("/cache/quest-targets.tsv", lines);
            }
            catch (Exception exception)
            {
                Diagnostics.FailureLedger.Record("quest-targets-dump", exception.GetType().Name, exception.Message);
            }
        }

        public static string CoverageLine()
        {
            var spawnIndex = globalSpawns;
            if (spawnIndex == null)
                return "quest-coverage: spawn index loading";

            int doable = 0, done = 0, instanceOnly = 0;
            var heuristic = new Dictionary<string, int>();
            int heuristicDone = 0;
            foreach ((ushort questId, HashSet<uint> targets) in targetsByQuest.ToArray())
            {
                if (!HasOpenSpawn(questId, targets, spawnIndex))
                {
                    // some objective only has targets in dungeon/raid/expedition worlds: needs a team run, not counted as doable for quest bots
                    if (targets.Any(t => spawnIndex.TryGetValue(t, out var list) && list.Any(o => IsInstancedWorld(o.World))))
                        instanceOnly++;
                    continue;
                }

                doable++;
                if (!Diagnostics.QuestOutcomes.IsCompleted(questId))
                    continue;

                done++;
                string kind = QuestCoverage.HeuristicKind(questInfos[questId]);
                if (kind.Length == 0)
                    continue;

                heuristicDone++;
                foreach (string k in kind.Split(','))
                    heuristic[k] = heuristic.GetValueOrDefault(k) + 1;
            }

            DumpQuestTargets(spawnIndex);
            return $"quest-coverage: {done} / {doable} doable quests completed at least once, {done - heuristicDone} without QA heuristics, {heuristicDone} rely on one "
                + $"({string.Join(", ", heuristic.OrderBy(h => h.Key).Select(h => $"{h.Key} {h.Value}"))}) ({Diagnostics.QuestOutcomes.CompletedCount} distinct completed in all history; {instanceOnly} more need an objective in a dungeon/raid world)";
        }

        /// <summary>
        /// The quest is meant for this character apart from its level: faction, race, class, and a prerequisite row without a level check
        /// (rows with a level check are placed by <see cref="PickLevel"/>). Level rotation counted every open quest at a level, whatever its
        /// faction, so Dominion quests at levels 11-19 had no Dominion bot in their window (gt pickwhy: 26 faction-dominion, 36 level verdicts).
        /// </summary>
        private static bool FitsCharacter(Player me, QuestInfo info)
        {
            if (info.Entry.QuestPlayerFactionEnum == 0u && me.Faction1 != Game.Reputation.Static.Faction.Exile)
                return false;
            if (info.Entry.QuestPlayerFactionEnum == 1u && me.Faction1 != Game.Reputation.Static.Faction.Dominion)
                return false;
            if (info.Entry.PrerequisiteRace != 0u && me.Race != (Race)info.Entry.PrerequisiteRace)
                return false;
            if (info.Entry.PrerequisiteClass != 0u && me.Class != (Class)info.Entry.PrerequisiteClass)
                return false;

            if (info.Entry.PrerequisiteId == 0u)
                return true;
            PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
            if (prerequisite == null || prerequisite.PrerequisiteTypeId.Contains(1u))
                return true;
            return Game.Prerequisite.PrerequisiteManager.Instance.Meets(me, info.Entry.PrerequisiteId);
        }

        /// <summary>
        /// Levels of the other online quest bots of a faction, so rotation spreads bots over the open level ranges instead of sending them all
        /// to the one with the most open quests.
        /// </summary>
        private static List<uint> OtherBotLevels(Player me)
        {
            var levels = new List<uint>();
            foreach (Bot bot in BotManager.Instance.Bots)
            {
                Player other = bot.Session?.Player;
                if (other != null && other != me && other.Faction1 == me.Faction1 && bot.Role == BotRole.Quest)
                    levels.Add(other.Level);
            }

            return levels;
        }

        public static byte SuggestLevel(Player me, HashSet<byte> exhausted = null)
        {
            Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex = globalSpawns;
            if (spawnIndex == null)
                return 0;

            var open = new List<long>();
            foreach ((ushort questId, HashSet<uint> targets) in targetsByQuest)
            {
                if (!IsOpen(questId) || me.QuestManager.GetQuestState(questId) == Game.Quest.Static.QuestState.Completed)
                    continue;

                if (!HasOpenSpawn(questId, targets, spawnIndex))
                    continue;

                // quests behind a prerequisite quest this bot has not completed cannot be taken yet
                if (WaitsForQuests(questInfos[questId], p => me.QuestManager.GetQuestState(p) == Game.Quest.Static.QuestState.Completed))
                    continue;

                if (!FitsCharacter(me, questInfos[questId]))
                    continue;

                long level = PickLevel(questInfos[questId]);
                if (level != 0 && level <= MaxLevel)
                    open.Add(level);
            }

            // score: open quests in the window, shared with the bots of the same faction already standing in it
            List<uint> others = OtherBotLevels(me);
            byte best = 0;
            double bestScore = 0d;
            for (int candidate = 1; candidate <= MaxLevel; candidate++)
            {
                int count = open.Count(l => l >= candidate - 5 && l <= candidate + 3);
                if (count == 0 || (exhausted != null && exhausted.Contains((byte)candidate)))
                    continue;

                int crowd = others.Count(l => l >= candidate - 3 && l <= candidate + 3);
                double score = count / (1d + crowd);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = (byte)candidate;
                }
            }

            return best != 0 && best != me.Level ? best : (byte)0;
        }

        /// <summary>
        /// Number of spawn points of the supplied creatures in the world database, -1 while the index is loading.
        /// </summary>
        public static int CountSpawns(IEnumerable<uint> creatureIds)
        {
            var spawnIndex = globalSpawns;
            if (spawnIndex == null)
            {
                EnsureGlobalSpawns();
                return -1;
            }

            return creatureIds.Sum(id => spawnIndex.TryGetValue(id, out var list) ? list.Count : 0);
        }

        /// <summary>
        /// Find a spawn point of one of the supplied creatures in any world.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, bool> openSpawnCache = new();

        /// <summary>
        /// Every required creature objective of the quest has a target that spawns in a world a lone bot can walk into (not a dungeon, raid,
        /// expedition or adventure). One open target for the whole quest was not enough: quest 9428 (TalkTo 59216 in the open world, SucceedCSI
        /// 58487 only in the Datascape raid 1333) was taken and held for the whole run (111 quest-stalled events in a 47 min 60-bot run).
        /// </summary>
        private static bool HasOpenSpawn(ushort questId, HashSet<uint> targets, Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex)
        {
            if (targets.Count == 0)
                return true;

            bool Open(uint t) => spawnIndex.TryGetValue(t, out var list) && list.Any(o => !IsInstancedWorld(o.World));
            return openSpawnCache.GetOrAdd(questId, _ => requiredTargets.TryGetValue(questId, out var required) && required.Count > 0
                ? required.All(own => own.Count == 0 || own.Any(Open))
                : targets.Any(Open));
        }

        /// <summary>True for a quest that is not doable in the open world because some objective only has targets in an instanced world.</summary>
        public static bool IsInstanceOnly(ushort questId)
        {
            EnsureGlobalSpawns();
            var spawnIndex = globalSpawns;
            if (spawnIndex == null || !targetsByQuest.TryGetValue(questId, out HashSet<uint> targets) || HasOpenSpawn(questId, targets, spawnIndex))
                return false;
            return targets.Any(t => spawnIndex.TryGetValue(t, out var list) && list.Any(o => IsInstancedWorld(o.World)));
        }

        public static bool IsInstancedWorld(ushort world)
        {
            var entry = NexusForever.Shared.GameTable.GameTableManager.Instance.World.GetEntry(world);
            return entry != null && (NexusForever.WorldServer.Game.Map.Static.MapType)entry.Type is NexusForever.WorldServer.Game.Map.Static.MapType.Dungeon or NexusForever.WorldServer.Game.Map.Static.MapType.Raid
                or NexusForever.WorldServer.Game.Map.Static.MapType.Expedition or NexusForever.WorldServer.Game.Map.Static.MapType.Adventure;
        }

        /// <summary>
        /// A spawn point of an open quest whose level fits the bot (level -5..+3, the PickAnywhere window): where a bot stepped down by level
        /// rotation goes instead of idling among creatures far above its new level (L7-L13 bots died to L50 creatures in worlds 426/1061).
        /// </summary>
        public static bool TryFindLevelSpot(Player me, out ushort world, out Vector3 position)
        {
            world    = 0;
            position = default;
            Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex = globalSpawns;
            if (spawnIndex == null)
                return false;

            var fits = targetsByQuest
                .Where(q => q.Value.Count > 0 && IsOpen(q.Key) && PickLevel(questInfos[q.Key]) is long level && level != 0
                    && level >= (long)me.Level - 5 && level <= (long)me.Level + 3
                    && FitsCharacter(me, questInfos[q.Key]) && HasOpenSpawn(q.Key, q.Value, spawnIndex))
                .Select(q => q.Value)
                .ToList();
            if (fits.Count == 0)
                return false;
            return TryFindSpawnAnywhere(fits[BotManager.Rng.Next(fits.Count)], out world, out position);
        }

        public static bool TryFindSpawnAnywhere(HashSet<uint> creatureIds, out ushort world, out Vector3 position)
        {
            world    = 0;
            position = default;

            Dictionary<uint, List<(ushort World, Vector3 Position)>> spawnIndex = globalSpawns;
            if (spawnIndex == null)
            {
                EnsureGlobalSpawns();
                return false;
            }

            var options = new List<(ushort World, Vector3 Position)>();
            foreach (uint creatureId in creatureIds)
                if (spawnIndex.TryGetValue(creatureId, out List<(ushort World, Vector3 Position)> list))
                    options.AddRange(list);

            // dungeon and raid worlds need an instance and a team run, a lone quest bot teleported there is sent straight back out
            options.RemoveAll(o => IsInstancedWorld(o.World));
            if (options.Count == 0)
                return false;

            (world, position) = options[BotManager.Rng.Next(options.Count)];
            return true;
        }

        /// <summary>
        /// Pick a quest the bot may take that needs one of the supplied creatures, close to the bot's level.
        /// </summary>
        public static QuestInfo Pick(Player me, IEnumerable<uint> nearbyCreatureIds)
        {
            EnsureIndex();

            var candidates = new List<QuestInfo>();
            foreach (uint creatureId in nearbyCreatureIds.Distinct())
            {
                if (!byCreature.TryGetValue(creatureId, out List<QuestInfo> quests))
                    continue;

                foreach (QuestInfo info in quests)
                {
                    if (candidates.Contains(info) || CleanSkips(me, info))
                        continue;

                    long level = PickLevel(info);
                    if (level != 0 && (level < (long)me.Level - 5 || level > (long)me.Level + 3))
                        continue;

                    if (!me.QuestManager.CanAcceptWithoutGiver(info))
                        continue;

                    candidates.Add(info);
                }
            }

            if (candidates.Count == 0)
                return null;

            // only repeats nearby: return nothing so the bot ends with no active quest and PickAnywhere / level rotation looks for open quests elsewhere
            List<QuestInfo> undone = candidates.Where(c => IsOpen((ushort)c.Entry.Id)).ToList();
            if (undone.Count == 0 && SuggestLevel(me) > 0)
                return null;

            return LeastTried(undone.Count > 0 ? undone : candidates);
        }

        /// <summary>
        /// Creature ids the bot still has to kill for its active quests.
        /// </summary>
        // quests a bot cannot progress and cannot abandon (Quest2 flag 0x4000, e.g. 9428 "The Great Undiscovered Pro...", whose CSI target only
        // spawns in the Datascape raid): parked per character, they stay in the log but no longer steer targets, travel or the log limit
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, HashSet<ushort>> parked = new();

        public static void Park(Player me, ushort questId)
        {
            HashSet<ushort> set = parked.GetOrAdd(me.CharacterId, _ => new HashSet<ushort>());
            lock (set)
                set.Add(questId);
        }

        public static bool IsParked(Player me, ushort questId)
        {
            if (!parked.TryGetValue(me.CharacterId, out HashSet<ushort> set))
                return false;
            lock (set)
                return set.Contains(questId);
        }

        public static void Unpark(Player me, ushort questId)
        {
            if (parked.TryGetValue(me.CharacterId, out HashSet<ushort> set))
                lock (set)
                    set.Remove(questId);
        }

        /// <summary>The bot's active quests without the parked ones.</summary>
        public static List<Quest> Held(Player me)
        {
            List<Quest> active = me.QuestManager.GetActiveQuests();
            if (!parked.TryGetValue(me.CharacterId, out HashSet<ushort> set))
                return active;
            lock (set)
                return active.Where(q => !set.Contains(q.Id)).ToList();
        }

        public static HashSet<uint> ActiveTargets(Player me)
        {
            EnsureIndex();

            var result = new HashSet<uint>();
            foreach (Quest quest in Held(me))
                AddQuestTargets(me, quest, result);

            return result;
        }

        /// <summary>
        /// Creature ids the bot still has to kill or use for one quest.
        /// </summary>
        public static HashSet<uint> QuestTargets(Player me, Quest quest)
        {
            EnsureIndex();
            var result = new HashSet<uint>();
            AddQuestTargets(me, quest, result);
            return result;
        }

        private static void AddQuestTargets(Player me, Quest quest, HashSet<uint> result)
        {
            {
                if (quest.State != QuestState.Accepted)
                    return;

                foreach (QuestObjective objective in quest)
                {
                    if (objective.IsComplete() || QuestCoverage.IsSynthetic(objective.ObjectiveInfo.Entry) || !quest.CanUpdateObjective(objective))
                        continue;

                    switch (objective.ObjectiveInfo.Type)
                    {
                        case QuestObjectiveType.KillCreature:
                        case QuestObjectiveType.ActivateEntity:
                        case QuestObjectiveType.ActivateEntity2:
                        case QuestObjectiveType.SucceedCSI:
                        case QuestObjectiveType.TalkTo:
                            if (objective.ObjectiveInfo.Entry.Data == 0u)
                                result.UnionWith(QuestCoverage.TextCreatures(objective.ObjectiveInfo.Entry));
                            else
                                result.Add(objective.ObjectiveInfo.Entry.Data);
                            break;
                        case QuestObjectiveType.TalkToTargetGroup:
                        case QuestObjectiveType.KillTargetGroup:
                        case QuestObjectiveType.KillTargetGroups:
                        case QuestObjectiveType.ActivateTargetGroup:
                        case QuestObjectiveType.ActivateTargetGroupChecklist:
                            result.UnionWith(TargetGroupHelper.ResolveSpawned(objective.ObjectiveInfo.Entry.Data));
                            break;
                        case QuestObjectiveType.VirtualCollect:
                            result.UnionWith(TargetGroupHelper.ResolveSpawned(objective.ObjectiveInfo.Entry.TargetGroupIdRewardPane));
                            break;
                        case QuestObjectiveType.CompleteEvent:
                            result.UnionWith(Game.Events.PublicEventEngine.Instance.TargetCreatures(objective.ObjectiveInfo.Entry.Data).Kill);
                            break;
                        case QuestObjectiveType.Unknown15:
                            result.UnionWith(QuestCoverage.ScriptedTargets(objective.ObjectiveInfo.Entry).Targets);
                            break;
                        case QuestObjectiveType.CollectItem:
                            result.UnionWith(QuestCoverage.CollectSources(objective.ObjectiveInfo.Entry));
                            break;
                        case QuestObjectiveType.Unknown10:
                            // hostile ones are destroyed, the rest are used (activate list)
                            result.UnionWith(QuestCoverage.ScriptedTargets(objective.ObjectiveInfo.Entry).Targets.Where(c => !IsActivatable(c) && IsHostile(me, c)));
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Creature ids the bot still has to activate for its active quests, as opposed to kill.
        /// </summary>
        public static HashSet<uint> ActiveActivateTargets(Player me)
        {
            EnsureIndex();

            var result = new HashSet<uint>();
            foreach (Quest quest in Held(me))
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest)
                {
                    if (objective.IsComplete() || QuestCoverage.IsSynthetic(objective.ObjectiveInfo.Entry) || !quest.CanUpdateObjective(objective))
                        continue;

                    switch (objective.ObjectiveInfo.Type)
                    {
                        case QuestObjectiveType.ActivateEntity:
                        case QuestObjectiveType.ActivateEntity2:
                        case QuestObjectiveType.SucceedCSI:
                        case QuestObjectiveType.TalkTo:
                            if (objective.ObjectiveInfo.Entry.Data == 0u)
                                result.UnionWith(QuestCoverage.TextCreatures(objective.ObjectiveInfo.Entry));
                            else
                                result.Add(objective.ObjectiveInfo.Entry.Data);
                            break;
                        case QuestObjectiveType.ActivateTargetGroup:
                        case QuestObjectiveType.ActivateTargetGroupChecklist:
                        case QuestObjectiveType.TalkToTargetGroup:
                            result.UnionWith(TargetGroupHelper.ResolveSpawned(objective.ObjectiveInfo.Entry.Data));
                            break;
                        case QuestObjectiveType.VirtualCollect:
                            // pane creatures that are used rather than killed
                            result.UnionWith(QuestCoverage.TextCreatures(objective.ObjectiveInfo.Entry));
                            result.UnionWith(TargetGroupHelper.ResolveSpawned(objective.ObjectiveInfo.Entry.TargetGroupIdRewardPane).Where(IsActivatable));
                            break;
                        case QuestObjectiveType.CompleteEvent:
                            result.UnionWith(Game.Events.PublicEventEngine.Instance.TargetCreatures(objective.ObjectiveInfo.Entry.Data).Activate);
                            break;
                        case QuestObjectiveType.Unknown10:
                            result.UnionWith(QuestCoverage.ScriptedTargets(objective.ObjectiveInfo.Entry).Targets.Where(IsActivatable));
                            break;
                        case QuestObjectiveType.CollectItem:
                            result.UnionWith(QuestCoverage.CollectSources(objective.ObjectiveInfo.Entry).Where(IsActivatable));
                            break;
                    }
                }
            }

            return result;
        }

        internal static bool IsHostile(Player me, uint creatureId)
        {
            Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(creatureId);
            return creature != null && me.GetDispositionTo((Game.Reputation.Static.Faction)creature.FactionId) == Game.Reputation.Static.Disposition.Hostile;
        }

        private static bool IsActivatable(uint creatureId)
        {
            return (GameTableManager.Instance.Creature2.GetEntry(creatureId)?.Spell4IdActivate00 ?? 0u) != 0u;
        }

        /// <summary>
        /// Creature ids the bot still has to talk to for its active quests.
        /// </summary>
        public static HashSet<uint> ActiveTalkTargets(Player me)
        {
            var result = new HashSet<uint>();
            foreach (Quest quest in Held(me))
            {
                if (quest.State != QuestState.Accepted)
                    continue;

                foreach (QuestObjective objective in quest)
                {
                    if (objective.IsComplete() || QuestCoverage.IsSynthetic(objective.ObjectiveInfo.Entry) || !quest.CanUpdateObjective(objective))
                        continue;

                    if (objective.ObjectiveInfo.Type == QuestObjectiveType.TalkTo)
                        result.Add(objective.ObjectiveInfo.Entry.Data);
                    else if (objective.ObjectiveInfo.Type == QuestObjectiveType.TalkToTargetGroup)
                        result.UnionWith(TargetGroupHelper.ResolveSpawned(objective.ObjectiveInfo.Entry.Data));
                }
            }

            return result;
        }

        /// <summary>
        /// Credit objectives that have no in-world path yet (zones, items, schematics, client side interactions, spells).
        /// Each credit is counted per objective type in the failure report, so it is visible how much of the run was synthetic.
        /// </summary>
        public static void CreditSynthetic(Player me, Quest quest)
        {
            // the drivers below (event, crafting, equip, zone, spell, area) are real and run with BOTS_SYNTHETIC=0 too; only the direct credit is skipped

            foreach (QuestObjective objective in quest.ToList())
            {
                // a sequential objective the server would refuse yet: its driver would spend its trips (and its synthetic fallback) for nothing
                if (!objective.IsComplete() && !quest.CanUpdateObjective(objective))
                    continue;

                // a public event is real: the bot goes to its location (the engine starts it), fights there, and the finished event credits the objective.
                // After a few visits without success the objective is credited directly, so the quest chain goes on.
                if (!objective.IsComplete() && objective.ObjectiveInfo.Type == QuestObjectiveType.CompleteEvent
                    && Game.Events.PublicEventEngine.Instance.TryGetLocation(objective.ObjectiveInfo.Entry.Data, out ushort eventWorld, out Vector3 eventCenter, out float eventRadius))
                {
                    var key = (me.CharacterId, objective.ObjectiveInfo.Id);
                    DateTime since = eventSince.GetOrAdd(key, DateTime.UtcNow);
                    bool near = me.Map != null && me.Map.Entry.Id == eventWorld && Vector3.Distance(me.Position, eventCenter) <= eventRadius * 0.6f;
                    if (DateTime.UtcNow - since < TimeSpan.FromMinutes(8) && CanVisitWorld(eventWorld))
                    {
                        // count only the trips, waiting and fighting at the event does not use them up
                        if (!near && zoneVisits.AddOrUpdate(key, 1, (_, count) => count + 1) <= 5)
                        {
                            Diagnostics.FailureLedger.Record("objective-event-visit", $"event {objective.ObjectiveInfo.Entry.Data}", $"world {eventWorld}");
                            me.TeleportTo(eventWorld, eventCenter.X, eventCenter.Y, eventCenter.Z);
                            continue;
                        }

                        if (near)
                            continue;
                    }

                    if (!QuestCoverage.SyntheticEnabled)
                        continue;
                    Diagnostics.FailureLedger.Record("objective-event-gave-up", $"event {objective.ObjectiveInfo.Entry.Data}", $"quest {quest.Id}");
                    ForceSweep.NoteCredit(me, quest.Id, $"event-gave-up {objective.Index}:{(uint)objective.ObjectiveInfo.Type}");
                    Diagnostics.QuestTruth.NoteCredit(me, quest.Id, objective.Index, "event-gave-up");
                    quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, objective.ObjectiveInfo.Entry.Count);
                    continue;
                }

                // zone, spell and area objectives have drivers that go there for real; they run whatever IsSynthetic says (6215e70 made them
                // non-synthetic, which had switched the drivers off) and only fall through to synthetic credit when the driver has nothing left to try
                QuestObjectiveType driverType = objective.ObjectiveInfo.Type;
                bool hasDriver = driverType == QuestObjectiveType.EnterZone || driverType == QuestObjectiveType.EnterArea || QuestManager.IsSpellSuccess(driverType)
                    || QuestCoverage.EquipItem(objective.ObjectiveInfo.Entry) != 0u;
                if (objective.IsComplete() || (!hasDriver && !QuestCoverage.IsSynthetic(objective.ObjectiveInfo.Entry)))
                    continue;

                // crafting is real: the bot learns the skill, gets the materials (QA grant, counted) and crafts through the tradeskill manager
                if ((objective.ObjectiveInfo.Type == QuestObjectiveType.CraftSchematic || objective.ObjectiveInfo.Type == QuestObjectiveType.ObtainSchematic)
                    && TryCraftObjective(me, objective.ObjectiveInfo.Entry))
                    continue;

                // equipping is real: the bot equips the item the text names and the inventory move credits the objective
                if (QuestCoverage.EquipItem(objective.ObjectiveInfo.Entry) is uint equipItem && equipItem != 0u && TryEquipObjective(me, objective.ObjectiveInfo.Id, equipItem))
                    continue;

                // entering a zone is real: the bot is sent to a known location inside it and the zone update credits the objective
                if (objective.ObjectiveInfo.Type == QuestObjectiveType.EnterZone && TryVisitZone(me, objective.ObjectiveInfo.Id, objective.ObjectiveInfo.Entry.Data))
                    continue;

                // spell objectives are real too: the bot finds the target, casts the spell, and the cast credits the objective
                if (QuestManager.IsSpellSuccess(objective.ObjectiveInfo.Type) && TrySpellObjective(me, objective.ObjectiveInfo.Entry))
                    continue;

                // finding an area is real too: the bot goes to the indicator location or a creature of the group and the quest manager's area check credits it
                if (objective.ObjectiveInfo.Type == QuestObjectiveType.EnterArea && TryVisitArea(me, objective.ObjectiveInfo.Entry))
                    continue;

                if (!QuestCoverage.SyntheticEnabled)
                    continue;

                // sequential objectives wait for the ones before them, only count credit that took effect
                uint before = objective.Progress;
                quest.ObjectiveUpdate(objective.ObjectiveInfo.Id, objective.ObjectiveInfo.Entry.Count);
                if (objective.Progress != before)
                {
                    Diagnostics.FailureLedger.Record("objective-synthetic", $"type {objective.ObjectiveInfo.Type} ({(uint)objective.ObjectiveInfo.Type})", $"quest {quest.Id} objective {objective.ObjectiveInfo.Id} data {objective.ObjectiveInfo.Entry.Data}");
                    ForceSweep.NoteCredit(me, quest.Id, $"synthetic {objective.Index}:{(uint)objective.ObjectiveInfo.Type}");
                    Diagnostics.QuestTruth.NoteCredit(me, quest.Id, objective.Index, "synthetic");
                    QuestCoverage.NoteSynthetic(me, quest.Id);
                }
            }
        }

        /// <summary>
        /// Get the item a quest names (QA grant when the bot has none, counted as quest-qa-item-grant: no vendor or drop source for it is known).
        /// </summary>
        private static Item QuestItem(Player me, uint itemId, uint objectiveId)
        {
            Item item = me.Inventory.GetItems(InventoryLocation.Inventory).Concat(me.Inventory.GetItems(InventoryLocation.Equipped))
                .FirstOrDefault(i => i?.Info?.Entry != null && i.Info.Id == itemId);
            if (item != null)
                return item;

            if (Game.ItemManager.Instance.GetItemInfo(itemId) == null || !me.Inventory.GetFirstFreeBagIndex(InventoryLocation.Inventory).HasValue)
            {
                Diagnostics.FailureLedger.Record("quest-qa-item-grant-failed", $"item {itemId}", $"objective {objectiveId}");
                return null;
            }

            me.Inventory.ItemCreate(InventoryLocation.Inventory, itemId, 1u, ItemUpdateReason.Loot);
            Diagnostics.FailureLedger.Record("quest-qa-item-grant", $"item {itemId}", $"objective {objectiveId}");
            Diagnostics.QuestTruth.Grant(me, "item-grant");
            return me.Inventory.GetItems(InventoryLocation.Inventory).FirstOrDefault(i => i?.Info?.Entry != null && i.Info.Id == itemId);
        }

        /// <summary>
        /// "Equip the $(item=N)": equip the item through the inventory move a client's ClientItemMove reaches (an item already worn is taken off first).
        /// Three tries per objective, then synthetic credit.
        /// </summary>
        private static bool TryEquipObjective(Player me, uint objectiveId, uint itemId)
        {
            if (zoneVisits.AddOrUpdate((me.CharacterId, objectiveId), 1, (_, count) => count + 1) > 3)
                return false;

            Item item = QuestItem(me, itemId, objectiveId);
            if (item?.Info?.SlotEntry == null || !item.Info.IsEquippable())
            {
                Diagnostics.FailureLedger.Record("quest-equip-failed", $"item {itemId} is not equippable", $"objective {objectiveId}");
                return false;
            }

            if (item.Location == InventoryLocation.Equipped)
            {
                uint? free = me.Inventory.GetFirstFreeBagIndex(InventoryLocation.Inventory);
                if (!free.HasValue)
                    return false;
                me.Inventory.ItemMove(item, InventoryLocation.Inventory, free.Value);
            }

            uint flags = item.Info.SlotEntry.EquippedSlotFlags;
            int slot = -1;
            for (int bit = 0; bit < 32 && slot < 0; bit++)
                if ((flags & (1u << bit)) != 0u)
                    slot = bit;
            Game.Static.GenericError? error = slot < 0 ? Game.Static.GenericError.ItemNotValidForSlot : me.Inventory.CanMoveItem(item, InventoryLocation.Equipped, (uint)slot);
            if (error.HasValue)
            {
                Diagnostics.FailureLedger.Record("quest-equip-failed", $"{error}", $"item {itemId} objective {objectiveId}");
                return false;
            }

            me.Inventory.ItemMove(item, InventoryLocation.Equipped, (uint)slot);
            Diagnostics.FailureLedger.Record("objective-equip", $"item {itemId}", $"objective {objectiveId}");
            return true;
        }

        /// <summary>
        /// Craft the schematic of a CraftSchematic or ObtainSchematic objective. The crafting credits the objective itself.
        /// Returns false when the schematic is not in the table or crafting failed, so the objective falls back to synthetic credit.
        /// </summary>
        private static bool TryCraftObjective(Player me, QuestObjectiveEntry entry)
        {
            TradeskillSchematic2Entry schematic = GameTableManager.Instance.TradeskillSchematic2.GetEntry(entry.Data);
            if (schematic == null)
            {
                Diagnostics.FailureLedger.Record("tradeskill-schematic-unknown", $"objective type {entry.Type} data {entry.Data} is not a TradeskillSchematic2 id", "synthetic credit");
                return false;
            }

            var manager = me.TradeskillManager;
            if (!BotTradeskills.Learn(me, schematic.TradeSkillId))
            {
                Diagnostics.FailureLedger.Record("tradeskill-cannot-learn", $"skill {schematic.TradeSkillId}", "bot already has two crafting skills, synthetic credit");
                return false;
            }

            // a schematic above the bot's tier: craft up to it first (replaces the QA tier grant); the objective waits meanwhile
            if (schematic.Tier > manager.GetTier(schematic.TradeSkillId))
            {
                BotTradeskills.SetGoal(me, schematic.TradeSkillId, schematic.Tier, 0u);
                return true;
            }

            // a schematic the bot has not learned (flag 0: taught by a schematic item or a tradeskill achievement) is a QA grant, counted
            if (!manager.KnowsSchematic(schematic))
            {
                manager.LearnSchematic(schematic.TradeskillSchematic2IdParent != 0u && schematic.DiscoverableQuadrant == 0u ? schematic.TradeskillSchematic2IdParent : schematic.Id);
                if (!manager.KnowsSchematic(schematic))
                    manager.LearnSchematic(schematic.Id);
                Diagnostics.FailureLedger.Record("tradeskill-qa-schematic-grant", $"schematic {schematic.Id} skill {schematic.TradeSkillId}", "objective craft");
                Diagnostics.QuestTruth.Grant(me, "schematic-grant", schematic.TradeSkillId);
            }

            WorldEntity station = BotTradeskills.NearStation(me, schematic.TradeSkillId);
            if (station == null)
            {
                if (BotTradeskills.GoToStation(me, schematic.TradeSkillId))
                    return true;
                Diagnostics.FailureLedger.Record("tradeskill-no-station", $"skill {schematic.TradeSkillId}", "objective craft, synthetic credit");
                return false;
            }

            uint crafts = Math.Max(entry.Count, 1u);
            // a variant of a coordinate craft (child schematic with a grid circle) is made from its parent moved by additives
            bool variant = schematic.TradeskillSchematic2IdParent != 0u && schematic.Radius > 0f;
            if (!variant)
                BotTradeskills.EnsureMaterials(me, schematic, crafts);
            uint crafted = 0u;
            for (uint n = 0; n < crafts; n++)
            {
                uint xp = manager.GetXp(schematic.TradeSkillId);
                uint had = me.Inventory.GetItemCount(schematic.Item2IdOutput);
                if (variant)
                {
                    // a variant counts only when its own item came out (a failed parent craft also gives xp)
                    if (!BotTradeskills.CraftVariant(me, schematic, station.Guid) || me.Inventory.GetItemCount(schematic.Item2IdOutput) <= had)
                        break;
                }
                else
                    BotTradeskills.CraftAtStation(me, schematic, station.Guid);
                if (manager.GetXp(schematic.TradeSkillId) == xp && me.Inventory.GetItemCount(schematic.Item2IdOutput) == had)
                    break;
                crafted++;
            }

            bool ok = crafted > 0u;
            Diagnostics.FailureLedger.Record(ok ? "tradeskill-craft-ok" : "tradeskill-craft-failed", $"schematic {schematic.Id} skill {schematic.TradeSkillId}", $"{crafted}/{crafts} crafted through the handler");
            return ok;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Character, uint Objective), DateTime> eventSince = new();
        private static volatile Dictionary<uint, List<(ushort World, Vector3 Position)>> zoneLocations;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Character, uint Objective), (int Count, DateTime At)> areaVisitAt = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Character, uint Objective), int> zoneVisits = new();

        /// <summary>
        /// A quest refused because its prerequisite asks for a tier of a tradeskill (prerequisite type 19, object = TradeskillTier) the bot does
        /// not have: learn the skill like a player would at the trainer, then raise it to the tier. Returns true when something changed.
        /// </summary>
        private static bool LearnTradeskillFor(Player me, QuestInfo info)
        {
            if (Game.Entity.QuestManager.LastRefusal?.StartsWith("prereq-id") != true)
                return false;

            PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
            if (prerequisite == null)
                return false;

            bool learned = false;
            for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
            {
                // the rows that ask for a tier: Equal, GreaterThan, GreaterThanOrEqual the row's tier (PrerequisiteCheckTradeskill; the value is unused)
                if (prerequisite.PrerequisiteTypeId[i] != 19u || prerequisite.PrerequisiteComparisonId[i] is not (1u or 3u or 4u))
                    continue;

                TradeskillTierEntry row = GameTableManager.Instance.TradeskillTier.GetEntry(prerequisite.ObjectId[i]);
                if (row == null)
                    continue;
                // GreaterThan asks for the next tier: the goal works with that tier's row
                TradeskillTierEntry tier = prerequisite.PrerequisiteComparisonId[i] != 3u ? row
                    : GameTableManager.Instance.TradeskillTier.Entries.FirstOrDefault(t => t != null && t.TradeSkillId == row.TradeSkillId && t.Tier == row.Tier + 1u);
                if (tier == null)
                    continue;

                // a higher tier of a learned skill: the bot crafts up to it (BotTradeskills goal; the QA harness used to grant the xp)
                // (162 quests were refused on tiers 2-5 of crafting skills, e.g. quest 5240 TradeskillTier 2 = Weaponsmith tier 2)
                if (me.TradeskillManager.HasLearned(tier.TradeSkillId))
                {
                    // Equal asks for exactly the tier: a bot above it cannot take the quest any more
                    if (me.TradeskillManager.GetTier(tier.TradeSkillId) >= tier.Tier)
                        continue;

                    // crafting up to the tier is real: the bot crafts at a station until the xp is there, then picks the quest again
                    BotTradeskills.SetGoal(me, tier.TradeSkillId, tier.Tier, info.Entry.Id);
                    continue;
                }

                // a character may hold two crafting skills: each bot gets a fixed pair, spread over the bots, so every crafting chain has bots
                TradeskillEntry skill = GameTableManager.Instance.Tradeskill.GetEntry(tier.TradeSkillId);
                if (skill != null && TradeskillManager.IsCraftingSkill(skill) && !AssignedCraftingSkills(me).Contains(tier.TradeSkillId))
                    continue;

                bool ok = BotTradeskills.Learn(me, tier.TradeSkillId);
                if (ok && tier.Tier > 1u)
                    BotTradeskills.SetGoal(me, tier.TradeSkillId, tier.Tier, info.Entry.Id);
                Diagnostics.FailureLedger.Record(ok ? "bot-tradeskill-learned-for-quest" : "bot-tradeskill-learn-refused", $"skill {tier.TradeSkillId}", $"quest {info.Entry.Id}");
                learned |= ok;
            }

            return learned;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong Character, ushort Quest), byte> zoneTrips = new();

        /// <summary>
        /// A quest refused because its prerequisite asks for the character to be in a zone (type 26, the giver stands there): go into the zone
        /// once, like a player walking up to the giver; the next pick can take it.
        /// </summary>
        private static bool TravelToQuestZone(Player me, QuestInfo info)
        {
            if (Game.Entity.QuestManager.LastRefusal?.StartsWith("prereq-id") != true)
                return false;

            PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
            if (prerequisite == null)
                return false;

            for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
            {
                // type 118 Equal 1: a public event objective must be active (16 quests, e.g. 6919 "Before They Escape!" needs objective 893 of
                // event 236). Events start when a player enters their area (PublicEventEngine.PlayerTick): go there once, like a player walking
                // into the event; the next pick can take the quest while it runs. Y 0 makes the bot take the terrain height (bot-ground-snap).
                if (prerequisite.PrerequisiteTypeId[i] == 118u && prerequisite.PrerequisiteComparisonId[i] == 1u && prerequisite.Value[i] == 1u)
                {
                    if (!Game.Events.PublicEventEngine.Instance.TryGetLocation(prerequisite.ObjectId[i], out ushort eventWorld, out Vector3 center, out float radius)
                        || !CanVisitWorld(eventWorld) || IsInstancedWorld(eventWorld)
                        || (me.Map?.Entry.Id == eventWorld && Vector2.Distance(new Vector2(me.Position.X, me.Position.Z), new Vector2(center.X, center.Z)) <= radius)
                        || !zoneTrips.TryAdd((me.CharacterId, (ushort)info.Entry.Id), 0))
                        return false;

                    Diagnostics.FailureLedger.Record("bot-quest-event-trip", $"event objective {prerequisite.ObjectId[i]}", $"quest {info.Entry.Id} world {eventWorld}");
                    me.TeleportTo(eventWorld, center.X, 0f, center.Z);
                    return true;
                }

                if (prerequisite.PrerequisiteTypeId[i] != 26u || prerequisite.PrerequisiteComparisonId[i] != 1u)
                    continue;

                uint zoneId = prerequisite.Value[i];
                if (me.ZoneAndParents().Contains(zoneId) || !HasZoneLocation(zoneId) || !zoneTrips.TryAdd((me.CharacterId, (ushort)info.Entry.Id), 0))
                    return false;

                (ushort world, Vector3 position) = zoneLocations[zoneId][BotManager.Rng.Next(zoneLocations[zoneId].Count)];
                Diagnostics.FailureLedger.Record("bot-quest-zone-trip", $"zone {zoneId}", $"quest {info.Entry.Id} world {world}");
                me.TeleportTo(world, position.X, position.Y, position.Z);
                return true;
            }

            return false;
        }

        private static Dictionary<ushort, List<QuestInfo>> tiedQuests;

        /// <summary>
        /// Quests whose prerequisite row asks for the state of another quest or one of its objectives (types 6 and 68, e.g. quest 3616 needs
        /// quest 3615 accepted): a player takes them while holding that quest. Returns the ones this bot can take now for its active quests.
        /// </summary>
        public static List<QuestInfo> TiedQuests(Player me)
        {
            EnsureIndex();
            if (tiedQuests == null)
            {
                var index = new Dictionary<ushort, List<QuestInfo>>();
                foreach ((ushort questId, QuestInfo info) in questInfos)
                {
                    PrerequisiteEntry prerequisite = GameTableManager.Instance.Prerequisite.GetEntry(info.Entry.PrerequisiteId);
                    if (prerequisite == null)
                        continue;

                    for (int i = 0; i < prerequisite.PrerequisiteTypeId.Length; i++)
                    {
                        bool active = prerequisite.PrerequisiteTypeId[i] == 6u && prerequisite.PrerequisiteComparisonId[i] == 1u && prerequisite.Value[i] is 1u or 2u;
                        bool objective = prerequisite.PrerequisiteTypeId[i] == 68u && prerequisite.PrerequisiteComparisonId[i] == 1u;
                        if (!active && !objective)
                            continue;

                        ushort other = (ushort)prerequisite.ObjectId[i];
                        if (!index.TryGetValue(other, out List<QuestInfo> list))
                            index.Add(other, list = new List<QuestInfo>());
                        if (!list.Contains(info))
                            list.Add(info);
                    }
                }

                tiedQuests = index;
            }

            var result = new List<QuestInfo>();
            foreach (Quest quest in me.QuestManager.GetActiveQuests())
            {
                if (!tiedQuests.TryGetValue(quest.Id, out List<QuestInfo> tied))
                    continue;

                foreach (QuestInfo info in tied)
                    if (!result.Contains(info) && me.QuestManager.CanAcceptWithoutGiver(info))
                        result.Add(info);
            }

            foreach (QuestInfo info in CompleteQuestTies(me))
                if (!result.Contains(info))
                    result.Add(info);

            return result;
        }

        /// <summary>
        /// CompleteQuest objectives (type 24, data = quest id) of the held quests: the named quests the bot can take now (quest 6603 "Lightreach
        /// Mission" needs 6607, 6608 and 6609; a bot holding it had no creature targets and stalled). Also used for forced test quests.
        /// </summary>
        public static List<QuestInfo> CompleteQuestTies(Player me)
        {
            EnsureIndex();
            var result = new List<QuestInfo>();
            foreach (Quest quest in Held(me))
                foreach (var objective in quest)
                {
                    if (objective.ObjectiveInfo.Type != QuestObjectiveType.CompleteQuest || objective.IsComplete())
                        continue;
                    if (!questInfos.TryGetValue((ushort)objective.ObjectiveInfo.Entry.Data, out QuestInfo info) || result.Contains(info))
                        continue;
                    if (me.QuestManager.GetQuestState((ushort)info.Entry.Id) is null or QuestState.Botched or QuestState.Mentioned or QuestState.Abandoned
                        && me.QuestManager.CanAcceptWithoutGiver(info))
                        result.Add(info);
                }

            return result;
        }

        private static uint[] craftingSkills;

        /// <summary>
        /// The two crafting skills a bot learns for quests, chosen from its character id so the pairs spread evenly over all crafting skills.
        /// </summary>
        private static uint[] AssignedCraftingSkills(Player me)
        {
            craftingSkills ??= GameTableManager.Instance.Tradeskill.Entries.Where(t => t != null && TradeskillManager.IsCraftingSkill(t)).Select(t => t.Id).OrderBy(i => i).ToArray();
            int n = craftingSkills.Length;
            if (n < 2)
                return craftingSkills;

            int first  = (int)(me.CharacterId % (ulong)n);
            int second = (first + 1 + (int)(me.CharacterId / (ulong)n % (ulong)(n - 1))) % n;
            return new[] { craftingSkills[first], craftingSkills[second] };
        }

        /// <summary>
        /// The zone has a WorldLocation2 entry in a world bots can visit (TryVisitZone has somewhere to go).
        /// </summary>
        public static bool HasZoneLocation(uint zoneId)
        {
            EnsureZoneLocations();
            return zoneLocations.TryGetValue(zoneId, out var options) && options.Count > 0;
        }

        private static void EnsureZoneLocations()
        {
            if (zoneLocations != null)
                return;

            var index = new Dictionary<uint, List<(ushort, Vector3)>>();
            foreach (WorldLocation2Entry location in GameTableManager.Instance.WorldLocation2.Entries)
            {
                if (!CanVisitWorld(location.WorldId) || location.WorldZoneId == 0u)
                    continue;

                if (!index.TryGetValue(location.WorldZoneId, out List<(ushort, Vector3)> list))
                    index.Add(location.WorldZoneId, list = new List<(ushort, Vector3)>());

                list.Add(((ushort)location.WorldId, new Vector3(location.Position0, location.Position1, location.Position2)));
            }

            // points where the server's own map files resolve a zone (data/research/zone-points.tsv, scripts/research/zonepoints.py). A point
            // counts for its zone and every parent zone, because the zone update credits the parents too (Player.ZoneAndParents); a WorldLocation2
            // entry only names its own zone, and a sub zone the map files do not contain is never resolved there. These points are preferred:
            // the zone update is certain to fire at them.
            var mapPoints = new Dictionary<uint, List<(ushort, Vector3)>>();
            try
            {
                string file = System.IO.Path.Combine(Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research", "zone-points.tsv");
                if (System.IO.File.Exists(file))
                    foreach (string line in System.IO.File.ReadLines(file))
                    {
                        string[] f = line.Split('\t');
                        if (line.StartsWith('#') || f.Length < 5 || !uint.TryParse(f[0], out uint zone) || !ushort.TryParse(f[1], out ushort world) || !CanVisitWorld(world))
                            continue;

                        var point = new Vector3(float.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture));
                        for (int depth = 0; zone != 0u && depth < 8; depth++)
                        {
                            if (!mapPoints.TryGetValue(zone, out List<(ushort, Vector3)> list))
                                mapPoints.Add(zone, list = new List<(ushort, Vector3)>());
                            list.Add((world, point));
                            zone = GameTableManager.Instance.WorldZone.GetEntry(zone)?.ParentZoneId ?? 0u;
                        }
                    }
            }
            catch (Exception exception)
            {
                Diagnostics.FailureLedger.Record("zone-points-unreadable", exception.GetType().Name, exception.Message);
            }

            foreach ((uint zone, List<(ushort, Vector3)> points) in mapPoints)
                index[zone] = points;

            zoneLocations = index;
        }

        /// <summary>
        /// Send the bot to a location inside the zone (at most twice per objective). Returns false when the zone has no usable location
        /// or the bot has already tried, so the objective falls back to synthetic credit.
        /// </summary>
        private static bool TryVisitZone(Player me, uint objectiveId, uint zoneId)
        {
            EnsureZoneLocations();

            // already inside (the zone or one of its sub zones): no zone change will come, credit it like the zone update does
            if (me.ZoneAndParents().Contains(zoneId))
            {
                // the zone change event does not fire for a bot that is already inside: a bot-side credit, tagged for clean runs
                foreach (Quest held in me.QuestManager.GetActiveQuests())
                    foreach (QuestObjective open in held.Where(o => !o.IsComplete() && o.ObjectiveInfo.Id == objectiveId))
                        Diagnostics.QuestTruth.NoteCredit(me, held.Id, open.Index, "zone-inside");
                me.QuestManager.ObjectiveUpdate(QuestObjectiveType.EnterZone, zoneId, 1);
                return true;
            }

            if (!zoneLocations.TryGetValue(zoneId, out List<(ushort World, Vector3 Position)> options) || options.Count == 0)
                return false;

            if (zoneVisits.AddOrUpdate((me.CharacterId, objectiveId), 1, (_, count) => count + 1) > 2)
                return false;

            (ushort world, Vector3 position) = options[BotManager.Rng.Next(options.Count)];
            Diagnostics.FailureLedger.Record("objective-zone-visit", $"zone {zoneId}", $"world {world}");
            me.TeleportTo(world, position.X, position.Y, position.Z);
            return true;
        }

        /// <summary>
        /// Cast the spell of a SpellSuccess objective at a creature of its cast group standing close by, or travel to one (the next pass casts).
        /// Spells without a target group are cast at the indicator location. Returns false when there is nothing to do or the bot has run out of tries.
        /// </summary>
        private static bool TrySpellObjective(Player me, QuestObjectiveEntry entry)
        {
            if (zoneVisits.AddOrUpdate((me.CharacterId, entry.Id), 1, (_, count) => count + 1) > 4 + 3 * Math.Max(1u, entry.Count))
                return false;

            Spell4Entry spell = GameTableManager.Instance.Spell4.GetEntry(entry.Data);
            if (spell == null || me.Map == null)
                return false;

            Spell4BaseEntry spellBase = GameTableManager.Instance.Spell4Base.GetEntry(spell.Spell4BaseIdBaseSpell);
            HashSet<uint> group = spellBase != null && spellBase.TargetGroupIdCastGroup != 0u ? TargetGroupHelper.ResolveSpawned(spellBase.TargetGroupIdCastGroup) : null;

            WorldEntity target = null;
            if (group != null && group.Count > 0)
            {
                me.Map.Search(me.Position, 30f, new NexusForever.WorldServer.Game.Map.Search.SearchCheckRange(me.Position, 30f, me), out List<GridEntity> around);
                target = around.OfType<WorldEntity>()
                    .Where(c => c is not Player && !(c is UnitEntity { IsDead: true }) && group.Contains(c.CreatureId))
                    .OrderBy(c => Vector3.Distance(me.Position, c.Position))
                    .FirstOrDefault();

                if (target == null)
                {
                    if (!TryFindSpawnAnywhere(group, out ushort world, out Vector3 position))
                        return false;

                    Diagnostics.FailureLedger.Record("objective-spell-visit", $"spell {entry.Data} objective {entry.Id}", $"world {world}");
                    me.TeleportTo(world, position.X, position.Y, position.Z);
                    return true;
                }
            }
            else if (entry.WorldLocationsIdIndicator00 != 0u)
            {
                WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(entry.WorldLocationsIdIndicator00);
                if (location == null || !CanVisitWorld(location.WorldId))
                    return false;

                var place = new Vector3(location.Position0, location.Position1, location.Position2);
                if (me.Map.Entry.Id != location.WorldId || Vector3.Distance(me.Position, place) > 15f)
                {
                    Diagnostics.FailureLedger.Record("objective-spell-visit", $"spell {entry.Data} objective {entry.Id} at an indicator location", $"world {location.WorldId}");
                    me.TeleportTo((ushort)location.WorldId, place.X, place.Y, place.Z);
                    return true;
                }
            }

            // wait out the global/spell cooldown instead of failing the cast
            if (me.SpellManager.GetGlobalSpellCooldown() > 0d || me.SpellManager.GetSpellCooldown(entry.Data) > 0d)
                return true;

            // a spell the bot does not know that an item casts: a player uses the item (ClientItemUse -> ItemSpecial spell), so the bot does the same
            uint spellItem = QuestCoverage.SpellItem(entry);
            if (spellItem != 0u && me.SpellManager.GetSpell(spell.Spell4BaseIdBaseSpell) == null)
            {
                Item item = QuestItem(me, spellItem, entry.Id);
                if (item == null)
                    return false;

                Vector3 at = target?.Position ?? me.Position;
                try
                {
                    Network.Message.Handler.ItemHandler.HandleItemUse(me.Session, BotPackets.Build<Network.Message.Model.ClientItemUse>(w =>
                    {
                        w.Write(0u);
                        w.Write((uint)item.Location, 9u);
                        w.Write(item.BagIndex);
                        w.Write(target?.Guid ?? 0u);
                        w.Write((uint)item.Location, 9u);
                        w.Write(item.BagIndex);
                        w.Write(at.X);
                        w.Write(at.Y);
                        w.Write(at.Z);
                    }));
                    Diagnostics.FailureLedger.Record("objective-item-use", $"item {spellItem} spell {entry.Data}", $"objective {entry.Id}");
                }
                catch (Exception exception)
                {
                    Diagnostics.FailureLedger.Record("objective-item-use-error", $"item {spellItem}: {exception.Message}", $"objective {entry.Id}");
                    return false;
                }

                return true;
            }

            try
            {
                me.CastSpell(entry.Data, new NexusForever.WorldServer.Game.Spell.SpellParameters
                {
                    UserInitiatedSpellCast = true,
                    PrimaryTargetId        = target?.Guid ?? 0u
                });
            }
            catch (Exception exception)
            {
                Diagnostics.FailureLedger.Record("objective-spell-cast-error", $"spell {entry.Data}: {exception.Message}", $"objective {entry.Id}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Send the bot to the indicator location of an EnterArea objective, or to a spawn of a creature of its group (at most three times per objective).
        /// Returns false when there is nowhere to go or the bot has tried, so the objective falls back to synthetic credit.
        /// </summary>
        private static bool TryVisitArea(Player me, QuestObjectiveEntry entry)
        {
            var visitKey = (me.CharacterId, entry.Id);
            if (areaVisitAt.TryGetValue(visitKey, out (int Count, DateTime At) last))
            {
                // one trip per minute so the area check gets time to credit it; waiting does not use up tries
                if (DateTime.UtcNow - last.At < TimeSpan.FromSeconds(60))
                {
                    me.QuestManager.CheckAreas();
                    return true;
                }

                if (last.Count >= 3)
                {
                    Diagnostics.FailureLedger.Record("objective-area-gave-up", $"objective {entry.Id} after three visits, bot in world {me.Map?.Entry.Id}", $"group {entry.Data} at {me.Position.X:F0},{me.Position.Y:F0},{me.Position.Z:F0}");
                    return false;
                }
            }

            var options = new List<(ushort World, Vector3 Position)>();
            uint[] indicators = { entry.WorldLocationsIdIndicator00, entry.WorldLocationsIdIndicator01, entry.WorldLocationsIdIndicator02, entry.WorldLocationsIdIndicator03 };
            foreach (uint indicatorId in indicators.Where(i => i != 0u))
            {
                WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(indicatorId);
                if (location != null && CanVisitWorld(location.WorldId))
                    options.Add(((ushort)location.WorldId, new Vector3(location.Position0, location.Position1, location.Position2)));
            }

            if (options.Count == 0 && entry.Data != 0u && TryFindSpawnAnywhere(TargetGroupHelper.ResolveSpawned(entry.Data), out ushort spawnWorld, out Vector3 spawnPosition))
                options.Add((spawnWorld, spawnPosition));

            if (options.Count == 0)
            {
                Diagnostics.FailureLedger.Record("objective-area-no-place", $"objective {entry.Id}: no indicator and no spawn of group {entry.Data}", "synthetic credit follows");
                return false;
            }

            areaVisitAt[visitKey] = (last.Count + 1, DateTime.UtcNow);
            (ushort world, Vector3 position) = options[BotManager.Rng.Next(options.Count)];
            Diagnostics.FailureLedger.Record("objective-area-visit", $"objective {entry.Id}", $"world {world}");
            me.TeleportTo(world, position.X, position.Y, position.Z);
            return true;
        }

        /// <summary>
        /// A position to enter a world at: the first creature spawn the world database has for it (dungeon entrances are not in the tables).
        /// </summary>
        public static bool TryGetWorldEntrance(ushort worldId, out Vector3 position)
        {
            position = default;
            if (!entrances.TryGetValue(worldId, out Vector3? cached))
            {
                var first = DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Where(sp => sp.World == worldId).Select(sp => (Vector3?)new Vector3(sp.X, sp.Y, sp.Z)).FirstOrDefault();
                entrances[worldId] = cached = first;
            }

            if (!cached.HasValue)
                return false;

            position = cached.Value;
            return true;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, Vector3?> entrances = new();

        /// <summary>
        /// Find a spawn point of one of the supplied creatures on the bot's current open world map.
        /// </summary>
        public static bool TryFindSpawn(Player me, HashSet<uint> creatureIds, out Vector3 position)
        {
            position = default;

            if (me.Map == null || me.Map is MapInstance)
                return false;

            ushort worldId = (ushort)me.Map.Entry.Id;
            if (!spawns.TryGetValue(worldId, out Dictionary<uint, List<Vector3>> index))
            {
                index = new Dictionary<uint, List<Vector3>>();
                foreach (EntityModel model in me.Map.GetSpawnModels())
                {
                    if (model.Creature == 0u)
                        continue;

                    if (!index.TryGetValue(model.Creature, out List<Vector3> list))
                    {
                        list = new List<Vector3>();
                        index.Add(model.Creature, list);
                    }

                    list.Add(new Vector3(model.X, model.Y, model.Z));
                }

                spawns.Add(worldId, index);
            }

            var options = new List<Vector3>();
            foreach (uint creatureId in creatureIds)
                if (index.TryGetValue(creatureId, out List<Vector3> list))
                    options.AddRange(list);

            if (options.Count == 0)
                return false;

            position = options[BotManager.Rng.Next(options.Count)];
            return true;
        }
    }
}
