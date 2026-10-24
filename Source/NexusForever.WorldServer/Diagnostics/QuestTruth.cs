using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;

namespace NexusForever.WorldServer.Diagnostics
{
    /// <summary>
    /// How each quest of a clean bot (NF_BOTS_CLEAN=1, accounts bot.clean-N@bots.invalid, created fresh at level 1) was completed.
    /// Clean characters carry nothing from earlier runs, so every completion can be traced to the paths that credited it:
    /// real objectives only, a QA heuristic (per objective kind), synthetic credit, or a harness grant used while the quest was in the log.
    /// One row per completion is appended to /cache/truth/quest-truth.tsv; the in-progress state (credits and grants of quests in the log, grants
    /// that stay with the character) is kept in /cache/truth/quest-truth-state.json so it survives a restart. Characters of other accounts are never
    /// tracked, so this costs nothing outside clean runs.
    /// </summary>
    public static class QuestTruth
    {
        public const string EmailPrefix = "bot.clean-";

        public sealed class QuestState
        {
            public DateTime Accepted { get; set; }
            public byte AcceptLevel { get; set; }
            /// <summary>objective index -> credit paths that were not the objective's own event (synthetic, event-gave-up, zone-inside).</summary>
            public Dictionary<string, List<string>> Credits { get; set; } = new();
            /// <summary>harness grant kinds received while the quest was in the log.</summary>
            public List<string> Grants { get; set; } = new();
        }

        public sealed class CharacterState
        {
            public string Name { get; set; } = "";
            /// <summary>grants that stay with the character: "level-set", "&lt;kind&gt;:&lt;tradeskill&gt;".</summary>
            public List<string> Lifetime { get; set; } = new();
            public Dictionary<string, QuestState> Quests { get; set; } = new();
        }

        public sealed class QuestStat
        {
            public int Accepted { get; set; }
            public int Abandoned { get; set; }
            public int Completed { get; set; }
            public int Verified { get; set; }
            public string LastDrop { get; set; } = "";
        }

        public sealed class State
        {
            public Dictionary<string, CharacterState> Characters { get; set; } = new();
            public Dictionary<string, QuestStat> Quests { get; set; } = new();
        }

        private static readonly object gate = new();
        private static State state = new();
        private static readonly List<string> pendingRows = new();
        private static bool dirty;
        private static Timer writer;

        // a sub folder: the game table cache deletes every file directly in /cache when the shared assembly changes (FileCache), which had
        // wiped the state and rows of three clean runs on 2026-10-18 (quest-outcomes.txt goes the same way)
        private static string CacheDir => Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "truth");
        private static string StatePath => Path.Combine(CacheDir, "quest-truth-state.json");
        public static string RowsPath => Path.Combine(CacheDir, "quest-truth.tsv");
        public static string OpenPath => Path.Combine(CacheDir, "quest-truth-open.tsv");

        public const string Header = "time_utc\tquest\tcharacter\tcharacter_id\tlevel\taccept_level\thow\theuristic\tgrants\tobjectives\tturn_in\tsynthetic_on\timmortal\tminutes";

        static QuestTruth()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                if (File.Exists(StatePath))
                    state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State();
                else
                    RebuildFromRows();
            }
            catch (Exception exception)
            {
                FailureLedger.Record("quest-truth-state-unreadable", exception.GetType().Name, exception.Message);
                state = new State();
            }
        }

        /// <summary>
        /// Characters that completed a zone 12 quest before clean bots skipped them (first clean runs) carry its xp and items: mark them from
        /// the rows file, so every later completion of theirs is tagged dev-zone-xp.
        /// </summary>
        private static void MarkDevZoneFromRows()
        {
            if (!File.Exists(RowsPath))
                return;
            foreach (string line in File.ReadLines(RowsPath).Skip(1))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 4 || !uint.TryParse(parts[1], out uint questId)
                    || NexusForever.Shared.GameTable.GameTableManager.Instance.Quest2.GetEntry(questId)?.WorldZoneId != 12u)
                    continue;
                if (!state.Characters.TryGetValue(parts[3], out CharacterState character))
                    state.Characters[parts[3]] = character = new CharacterState { Name = parts[2] };
                if (!character.Lifetime.Contains("dev-zone-xp"))
                    character.Lifetime.Add("dev-zone-xp");
            }
        }

        private static bool devZoneMarked;

        /// <summary>No state file (first start, or it was lost): completion counts per quest come back from the rows file.</summary>
        private static void RebuildFromRows()
        {
            if (!File.Exists(RowsPath))
                return;
            foreach (string line in File.ReadLines(RowsPath).Skip(1))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 7 || !ushort.TryParse(parts[1], out ushort questId))
                    continue;
                QuestStat stat = QuestStatFor(questId);
                stat.Completed++;
                if (parts[6] == "real")
                    stat.Verified++;
            }
        }

        /// <summary>
        /// A clean character that is no longer fresh: it completed a zone 12 developer quest (dev-zone-xp, first clean runs) or the harness set its
        /// level (level-set, forced test quests). Clean mode does not log it in again; a new clean account takes its place. Nothing is deleted.
        /// </summary>
        public static bool IsRetired(ulong characterId)
        {
            if (completedDevZone?.Contains(characterId) ?? false)
                return true;
            lock (gate)
            {
                if (!devZoneMarked)
                {
                    devZoneMarked = true;
                    MarkDevZoneFromRows();
                }
                return state.Characters.TryGetValue(characterId.ToString(), out CharacterState character)
                    && character.Lifetime.Any(l => l is "dev-zone-xp" or "level-set");
            }
        }

        private static HashSet<ulong> completedDevZone;

        /// <summary>Clean characters that completed a zone 12 quest according to the database (independent of the state file).</summary>
        public static void LoadDevZoneCompletions(ICollection<ulong> characterIds)
        {
            try
            {
                var zone12 = NexusForever.Shared.GameTable.GameTableManager.Instance.Quest2.Entries.Where(q => q.WorldZoneId == 12u).Select(q => (ushort)q.Id).ToList();
                completedDevZone = NexusForever.Shared.Database.DatabaseManager.Instance.CharacterDatabase.GetCharactersWithCompletedQuest(characterIds, zone12);
                lock (gate)
                {
                    foreach (ulong id in completedDevZone)
                    {
                        if (!state.Characters.TryGetValue(id.ToString(), out CharacterState character))
                            state.Characters[id.ToString()] = character = new CharacterState();
                        if (!character.Lifetime.Contains("dev-zone-xp"))
                            character.Lifetime.Add("dev-zone-xp");
                    }
                    Touch();
                }
            }
            catch (Exception exception)
            {
                FailureLedger.Record("quest-truth-dev-zone-unreadable", exception.GetType().Name, exception.Message);
            }
        }

        public static bool IsClean(Player player) => player?.Session?.Account?.Email?.StartsWith(EmailPrefix, StringComparison.OrdinalIgnoreCase) ?? false;

        /// <summary>Quests a clean bot completed (any path), for picking and the coverage line in clean mode.</summary>
        public static bool CleanCompleted(ushort questId)
        {
            lock (gate)
                return state.Quests.TryGetValue(questId.ToString(), out QuestStat stat) && stat.Completed > 0;
        }

        public static bool CleanVerified(ushort questId)
        {
            lock (gate)
                return state.Quests.TryGetValue(questId.ToString(), out QuestStat stat) && stat.Verified > 0;
        }

        public static QuestStat Stat(ushort questId)
        {
            lock (gate)
                return state.Quests.TryGetValue(questId.ToString(), out QuestStat stat) ? stat : null;
        }

        private static CharacterState Character(Player player)
        {
            string key = player.CharacterId.ToString();
            if (!state.Characters.TryGetValue(key, out CharacterState character))
                state.Characters[key] = character = new CharacterState();
            character.Name = player.Name ?? "";
            return character;
        }

        private static QuestStat QuestStatFor(ushort questId)
        {
            string key = questId.ToString();
            if (!state.Quests.TryGetValue(key, out QuestStat stat))
                state.Quests[key] = stat = new QuestStat();
            return stat;
        }

        public static void OnAccepted(Player player, ushort questId)
        {
            if (!IsClean(player))
                return;
            lock (gate)
            {
                if (!devZoneMarked)
                {
                    devZoneMarked = true;
                    MarkDevZoneFromRows();
                }
                Character(player).Quests[questId.ToString()] = new QuestState { Accepted = DateTime.UtcNow, AcceptLevel = (byte)player.Level };
                QuestStatFor(questId).Accepted++;
                Touch();
            }
        }

        public static void OnAbandoned(Player player, ushort questId, string detail)
        {
            if (!IsClean(player))
                return;
            lock (gate)
            {
                Character(player).Quests.Remove(questId.ToString());
                QuestStat stat = QuestStatFor(questId);
                stat.Abandoned++;
                if (!string.IsNullOrEmpty(detail))
                    stat.LastDrop = detail.Replace('\t', ' ').Replace('\n', ' ');
                Touch();
            }
        }

        /// <summary>A bot gave up on the quest after no progress (the stall detail lists the objective states).</summary>
        public static void NoteStall(Player player, ushort questId, string detail)
        {
            if (!IsClean(player))
                return;
            lock (gate)
            {
                QuestStatFor(questId).LastDrop = (detail ?? "").Replace('\t', ' ').Replace('\n', ' ');
                Touch();
            }
        }

        /// <summary>An objective was credited by a path other than its own game event (synthetic, event-gave-up, zone-inside).</summary>
        public static void NoteCredit(Player player, ushort questId, int objectiveIndex, string path)
        {
            if (!IsClean(player))
                return;
            lock (gate)
            {
                CharacterState character = Character(player);
                if (!character.Quests.TryGetValue(questId.ToString(), out QuestState quest))
                    character.Quests[questId.ToString()] = quest = new QuestState { Accepted = DateTime.UtcNow, AcceptLevel = (byte)player.Level };
                string key = objectiveIndex.ToString();
                if (!quest.Credits.TryGetValue(key, out List<string> paths))
                    quest.Credits[key] = paths = new List<string>();
                if (!paths.Contains(path))
                    paths.Add(path);
                Touch();
            }
        }

        /// <summary>
        /// A harness grant (item, material, credits, schematic, tool, level...). It is attached to every quest in the character's log
        /// (conservative: a grant may be counted against a quest it did not help), and with a tradeskill or for level changes it also stays with the
        /// character, so later craft-tier quests (tradeskill) or every later quest (level) carry it.
        /// </summary>
        public static void Grant(Player player, string kind, uint tradeskill = 0u)
        {
            if (!IsClean(player))
                return;
            lock (gate)
            {
                CharacterState character = Character(player);
                foreach (QuestState quest in character.Quests.Values)
                    if (!quest.Grants.Contains(kind))
                        quest.Grants.Add(kind);
                string lifetime = tradeskill != 0u ? $"{kind}:{tradeskill}" : kind == "level-set" ? kind : null;
                if (lifetime != null && !character.Lifetime.Contains(lifetime))
                    character.Lifetime.Add(lifetime);
                Touch();
            }
        }

        /// <summary>Called by QuestManager.QuestComplete once the quest is Completed.</summary>
        public static void OnCompleted(Player player, Quest quest, string turnIn)
        {
            if (!IsClean(player))
                return;
            try
            {
                Complete(player, quest, turnIn);
            }
            catch (Exception exception)
            {
                FailureLedger.Record("quest-truth-failed", exception.GetType().Name, $"quest {quest?.Id}: {exception.Message}");
            }
        }

        private static void Complete(Player player, Quest quest, string turnIn)
        {
            ushort questId = quest.Id;
            QuestInfo info = quest.Info;
            List<uint> craftSkills = Bots.QuestCoverage.CraftTierSkillsOf(info).Distinct().ToList();

            lock (gate)
            {
                CharacterState character = Character(player);
                character.Quests.Remove(questId.ToString(), out QuestState held);
                bool known = held != null;
                held ??= new QuestState { Accepted = DateTime.UtcNow, AcceptLevel = (byte)player.Level };

                var heuristics = new List<string>();
                var synthetic = new List<string>();
                var objectives = new List<string>();
                foreach (QuestObjective objective in quest)
                {
                    var paths = new List<string>();
                    if (held.Credits.TryGetValue(objective.Index.ToString(), out List<string> credits))
                        paths.AddRange(credits);
                    string staticKind = Bots.QuestCoverage.ObjectiveHeuristic(objective.ObjectiveInfo);
                    if (staticKind.Length > 0)
                        paths.AddRange(staticKind.Split(',').Where(k => !paths.Contains(k)));
                    if (paths.Count == 0)
                        paths.Add("real");
                    foreach (string path in paths)
                    {
                        if (path is "synthetic" or "event-gave-up")
                        {
                            if (!synthetic.Contains(path))
                                synthetic.Add(path);
                        }
                        else if (path != "real" && !heuristics.Contains(path))
                            heuristics.Add(path);
                    }
                    objectives.Add($"{objective.Index}:{objective.ObjectiveInfo.Type}:{string.Join("+", paths)}");
                }

                // a quest with no objectives is only the walk from giver to receiver: bots skip both, so nothing of it was played
                if (objectives.Count == 0 && turnIn == "receiver-not-near")
                    heuristics.Add("remote-turn-in");
                // no receiver in the data, no communicator, not auto-complete: only the bots' skipped receiver check finished it
                if (turnIn == "no-receiver")
                    heuristics.Add("no-turn-in-path");
                // zone 12 holds developer and beta quests (BotQuests.CleanSkips); their xp and items stay with the character
                if (info.Entry.WorldZoneId == 12u)
                    heuristics.Add("dev-zone");

                var grants = new List<string>(held.Grants);
                // tradeskill grants stay with the skill: a craft-tier quest carries every grant its skills ever had
                foreach (string lifetime in character.Lifetime)
                {
                    string[] parts = lifetime.Split(':');
                    bool applies = parts.Length == 1 || (parts.Length == 2 && uint.TryParse(parts[1], out uint skill) && craftSkills.Contains(skill));
                    if (applies && !grants.Contains(parts[0]))
                        grants.Add(parts[0]);
                }
                // the skill's own flag: QA materials since the server start, or xp from before it (state lost or older than the tracking)
                if (craftSkills.Any(s => player.TradeskillManager.UsedQaMaterials(s)) && !grants.Contains("material-grant"))
                    grants.Add("material-grant");
                if (!known)
                    grants.Add("untracked-accept");

                string how = synthetic.Count > 0 ? "synthetic" : heuristics.Count > 0 ? "heuristic" : grants.Count > 0 ? "grant" : "real";
                QuestStat stat = QuestStatFor(questId);
                stat.Completed++;
                if (how == "real")
                    stat.Verified++;

                double minutes = (DateTime.UtcNow - held.Accepted).TotalMinutes;
                pendingRows.Add(string.Join("\t", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), questId, player.Name, player.CharacterId, player.Level, held.AcceptLevel,
                    how, string.Join(",", heuristics.Concat(synthetic)), string.Join(",", grants), string.Join(" ", objectives), turnIn,
                    Bots.QuestCoverage.SyntheticEnabled ? 1 : 0, Bots.BotManager.Immortal ? 1 : 0, minutes.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
                FailureLedger.Record("clean-quest-completed", how, $"quest {questId} {player.Name}");
                if (info.Entry.WorldZoneId == 12u && !character.Lifetime.Contains("dev-zone-xp"))
                    character.Lifetime.Add("dev-zone-xp");
                Touch();
            }
        }

        private static void Touch()
        {
            dirty = true;
            writer ??= new Timer(_ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        /// <summary>Write pending rows and the state file (timer, and the shutdown save).</summary>
        public static void Flush()
        {
            try
            {
                string json;
                List<string> rows;
                lock (gate)
                {
                    if (!dirty)
                        return;
                    dirty = false;
                    rows = new List<string>(pendingRows);
                    pendingRows.Clear();
                    json = JsonSerializer.Serialize(state);
                }

                if (rows.Count > 0)
                {
                    bool header = !File.Exists(RowsPath);
                    File.AppendAllLines(RowsPath, header ? new[] { Header }.Concat(rows) : rows);
                }

                string temp = StatePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, StatePath, true);
            }
            catch (Exception exception)
            {
                // diagnostics must never take the server down
                FailureLedger.Record("quest-truth-write-failed", exception.GetType().Name, exception.Message);
            }
        }

        /// <summary>Clean bots known to the state file: id, name, quests in the log.</summary>
        public static List<string> Characters()
        {
            lock (gate)
                return state.Characters.Select(c => $"{c.Key}\t{c.Value.Name}\t{c.Value.Quests.Count} in log\t{string.Join(",", c.Value.Lifetime)}").ToList();
        }

        /// <summary>Held quests of a clean character (for the open list: in progress).</summary>
        public static bool IsHeld(ushort questId)
        {
            string key = questId.ToString();
            lock (gate)
                return state.Characters.Values.Any(c => c.Quests.ContainsKey(key));
        }
    }
}
