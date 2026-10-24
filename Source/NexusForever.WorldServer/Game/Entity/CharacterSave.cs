using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NexusForever.Database.Character;
using NexusForever.Database.Character.Model;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Character save failures: the cause, the row that broke the batch and the quest resync that repairs the database afterwards.
    /// </summary>
    public static class CharacterSave
    {
        /// <summary>
        /// The state of one quest as the game holds it, written by a resync.
        /// </summary>
        public sealed class QuestRow
        {
            public ushort QuestId;
            public byte State;
            public byte Flags;
            public uint? Timer;
            public DateTime? Reset;
            public List<(byte Index, uint Progress)> Objectives;
            /// <summary>Changes not yet saved (or queued for delete): not comparable with the database.</summary>
            public bool Pending;
        }

        private static readonly ConcurrentDictionary<ulong, DateTime> saveStarted = new();

        /// <summary>
        /// Remember when a save of the character was built: a character list read before it holds the state from before that save.
        /// </summary>
        public static void MarkSaveStarted(ulong characterId) => saveStarted[characterId] = DateTime.UtcNow;

        public static DateTime LastSaveStarted(ulong characterId) => saveStarted.TryGetValue(characterId, out DateTime time) ? time : DateTime.MinValue;

        private static readonly ConcurrentDictionary<ulong, int> savesInFlight = new();

        public static void MarkSaveInFlight(ulong characterId, bool inFlight) => savesInFlight.AddOrUpdate(characterId, inFlight ? 1 : 0, (_, n) => Math.Max(0, n + (inFlight ? 1 : -1)));

        /// <summary>
        /// A save of the character was built and has not been written yet.
        /// </summary>
        public static bool SaveInFlight(ulong characterId) => savesInFlight.TryGetValue(characterId, out int n) && n > 0;

        /// <summary>
        /// fk, concurrency, duplicate-key, duplicate-tracking or other.
        /// </summary>
        public static string Cause(Exception exception)
        {
            Exception e = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
            if (e is DbUpdateConcurrencyException)
                return "concurrency";
            string message = (e.InnerException?.Message ?? "") + " " + e.Message;
            if (message.Contains("foreign key constraint fails"))
                return "fk";
            if (message.Contains("Duplicate entry"))
                return "duplicate-key";
            if (e is InvalidOperationException && message.Contains("cannot be tracked"))
                return "duplicate-tracking";
            return "other";
        }

        private static readonly object logLock = new();

        /// <summary>
        /// Character save failures and their causes in order (/cache/character-save.log, restarted at 5 MB): the ledger counts them, this shows which came first.
        /// </summary>
        public static void Log(string line)
        {
            try
            {
                string path = System.IO.Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "character-save.log");
                lock (logLock)
                {
                    if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 5_000_000)
                        System.IO.File.Delete(path);
                    System.IO.File.AppendAllText(path, $"{DateTime.UtcNow:HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch (Exception)
            {
                // the log is a diagnostic aid only
            }
        }

        public static void RecordFailure(string part, Exception exception, ulong characterId)
        {
            Exception e = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
            string entries = e is DbUpdateException update
                ? string.Join(",", update.Entries.Take(6).Select(x => x.Metadata.ClrType.Name.Replace("Character", "").Replace("Model", "") + ":" + x.State))
                : "";
            string example = $"character {characterId}: {e.GetType().Name} {(e.InnerException?.Message ?? e.Message).Split('\n')[0]} [{entries}]";
            Diagnostics.FailureLedger.Record("character-save-failed", $"{Cause(e)} {part}", example);
            Log($"FAILED {Cause(e)} {part} {example}");
        }

        private static readonly ConcurrentDictionary<ulong, int> injectedFaults = new();

        /// <summary>
        /// Test aid: the next character batch of the character gets one row that cannot be written (a currency row of a character that does not exist).
        /// </summary>
        public static void InjectFault(ulong characterId) => injectedFaults.AddOrUpdate(characterId, 1, (_, n) => n + 1);

        public static void AddInjectedFault(ulong characterId, CharacterContext context)
        {
            if (!injectedFaults.TryGetValue(characterId, out int n) || n <= 0 || !injectedFaults.TryUpdate(characterId, n - 1, n))
                return;

            context.Add(new CharacterCurrencyModel { Id = ulong.MaxValue - characterId, CurrencyId = 1, Amount = 1 });
        }

        /// <summary>
        /// A failed batch is one rolled back transaction, and the game has already cleared its change flags, so dropping the batch loses every row in it.
        /// The rows are written again one per transaction (twice, so a row that needs another row of the batch first gets it on the second pass);
        /// only rows that still fail are lost, each one named in the ledger (character-save-row-dropped).
        /// Returns the number of rows written and dropped.
        /// </summary>
        public static async Task<(int Written, int Dropped)> Replay(CharacterDatabase database, ulong characterId, CharacterContext failed)
        {
            var pending = failed.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Select(e => (Entity: e.Entity, State: e.State,
                    Modified: e.State == EntityState.Modified ? e.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToArray() : null))
                .ToList();

            int written = 0;
            var errors = new Dictionary<object, Exception>();
            for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
            {
                var failedRows = new List<(object Entity, EntityState State, string[] Modified)>();
                foreach ((object entity, EntityState state, string[] modified) in pending)
                {
                    await using CharacterContext context = database.CreateContext();
                    try
                    {
                        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry = context.Entry(entity);
                        if (state == EntityState.Modified)
                        {
                            entry.State = EntityState.Unchanged;
                            foreach (string property in modified)
                                entry.Property(property).IsModified = true;
                        }
                        else
                            entry.State = state;

                        await context.SaveChangesAsync();
                        written++;
                    }
                    catch (Exception exception)
                    {
                        errors[entity] = exception;
                        failedRows.Add((entity, state, modified));
                    }
                }
                pending = failedRows;
            }

            foreach ((object entity, EntityState state, string[] _) in pending)
            {
                Exception e = errors[entity] is AggregateException aggregate ? aggregate.GetBaseException() : errors[entity];
                string key = string.Join(",", failed.Model.FindEntityType(entity.GetType())?.FindPrimaryKey()?.Properties
                    .Select(p => $"{p.Name}={p.PropertyInfo?.GetValue(entity)}") ?? Enumerable.Empty<string>());
                string type = entity.GetType().Name.Replace("Character", "").Replace("Model", "");
                Diagnostics.FailureLedger.Record("character-save-row-dropped", $"{Cause(e)} {type}:{state}",
                    $"character {characterId}: {key}: {(e.InnerException?.Message ?? e.Message).Split('\n')[0]}");
                Log($"  dropped character {characterId} {type}:{state} {key} ({Cause(e)})");
            }

            Log($"REPLAY character {characterId}: {written} rows written, {pending.Count} dropped");
            return (written, pending.Count);
        }

        /// <summary>
        /// Compare the rows of a failed quest batch with the database (the transaction rolled back, so it holds the state from before the batch)
        /// and name each row that cannot be written, with the transitions of its quest.
        /// </summary>
        public static async Task Diagnose(CharacterDatabase database, ulong characterId, CharacterContext failed, IReadOnlyDictionary<ushort, string> traces)
        {
            var quests = failed.ChangeTracker.Entries<CharacterQuestModel>()
                .Select(e => (e.Entity.QuestId, e.State))
                .ToList();
            var objectives = failed.ChangeTracker.Entries<CharacterQuestObjectiveModel>()
                .Select(e => (e.Entity.QuestId, e.Entity.Index, e.State))
                .ToList();

            await using CharacterContext context = database.CreateContext();
            var dbQuests = (await context.CharacterQuest.AsNoTracking()
                .Where(q => q.Id == characterId)
                .Select(q => q.QuestId)
                .ToListAsync()).ToHashSet();
            var dbObjectives = (await context.CharacterQuestObjective.AsNoTracking()
                .Where(o => o.Id == characterId)
                .Select(o => new { o.QuestId, o.Index })
                .ToListAsync()).Select(o => (o.QuestId, o.Index)).ToHashSet();

            var problems = new List<(ushort QuestId, string Problem)>();
            foreach ((ushort questId, EntityState state) in quests)
            {
                if (state == EntityState.Added && dbQuests.Contains(questId))
                    problems.Add((questId, "quest insert, row exists"));
                else if (state is EntityState.Modified or EntityState.Deleted && !dbQuests.Contains(questId))
                    problems.Add((questId, $"quest {(state == EntityState.Deleted ? "delete" : "update")}, no row"));
            }

            foreach ((ushort questId, byte index, EntityState state) in objectives)
            {
                bool questRow = dbQuests.Contains(questId);
                EntityState? questState = quests.Where(q => q.QuestId == questId).Select(q => (EntityState?)q.State).FirstOrDefault();
                if (state == EntityState.Added)
                {
                    if (dbObjectives.Contains((questId, index)))
                        problems.Add((questId, "objective insert, row exists"));
                    else if (questState == EntityState.Deleted)
                        problems.Add((questId, "objective insert, quest deleted in the batch"));
                    else if (!questRow && questState != EntityState.Added)
                        problems.Add((questId, "objective insert, no quest row"));
                }
                else if (state == EntityState.Modified && !dbObjectives.Contains((questId, index)))
                    problems.Add((questId, $"objective update, no row ({(questRow ? "quest row exists" : "no quest row")})"));
            }

            if (problems.Count == 0)
                Diagnostics.FailureLedger.Record("character-save-cause", "no row conflict found", $"character {characterId}, {quests.Count} quest rows, {objectives.Count} objective rows");

            foreach ((ushort questId, string problem) in problems.GroupBy(p => p.QuestId).Select(g => g.First()))
            {
                string trace = traces.TryGetValue(questId, out string t) ? t : "untraced";
                Diagnostics.FailureLedger.Record("character-save-cause", $"{problem}: {trace}", $"character {characterId} quest {questId}");
                Log($"  cause character {characterId} quest {questId}: {problem}: {trace}");
            }
        }

        /// <summary>
        /// Count the differences between the quests the game holds and the database rows: quest rows missing, extra or with another state,
        /// objective rows missing, extra or with other progress. Returns one line per difference kind.
        /// </summary>
        public static async Task<List<string>> Verify(CharacterDatabase database, ulong characterId, List<QuestRow> rows)
        {
            await using CharacterContext context = database.CreateContext();
            Dictionary<ushort, CharacterQuestModel> stored = await context.CharacterQuest.AsNoTracking()
                .Where(q => q.Id == characterId)
                .Include(q => q.QuestObjective)
                .ToDictionaryAsync(q => q.QuestId);

            var differences = new List<string>();
            var wanted = new HashSet<ushort>();
            foreach (QuestRow row in rows)
            {
                if (!wanted.Add(row.QuestId) || row.Pending)
                    continue;
                if (!stored.TryGetValue(row.QuestId, out CharacterQuestModel model))
                {
                    differences.Add($"quest row missing (state {row.State}) q{row.QuestId}");
                    continue;
                }
                if (model.State != row.State)
                    differences.Add($"quest state {model.State} in the database, {row.State} in the game q{row.QuestId}");
                foreach ((byte index, uint progress) in row.Objectives)
                {
                    CharacterQuestObjectiveModel objective = model.QuestObjective.FirstOrDefault(o => o.Index == index);
                    if (objective == null)
                        differences.Add($"objective row missing q{row.QuestId}");
                    else if (objective.Progress != progress)
                        differences.Add($"objective progress differs q{row.QuestId}");
                }
                if (model.QuestObjective.Count > row.Objectives.Count)
                    differences.Add($"extra objective rows q{row.QuestId}");
            }
            foreach (CharacterQuestModel model in stored.Values.Where(q => !wanted.Contains(q.QuestId)))
                differences.Add($"quest row the game does not hold (state {model.State}) q{model.QuestId}");
            return differences;
        }

        /// <summary>
        /// Write the quests as the game holds them: missing rows are inserted, changed rows updated and rows the game no longer has deleted.
        /// Used after a failed quest batch, whose lost inserts and deletes would otherwise break every later batch of the character.
        /// </summary>
        public static async Task Resync(CharacterDatabase database, ulong characterId, List<QuestRow> rows)
        {
            await using CharacterContext context = database.CreateContext();
            Dictionary<ushort, CharacterQuestModel> stored = await context.CharacterQuest
                .Where(q => q.Id == characterId)
                .Include(q => q.QuestObjective)
                .ToDictionaryAsync(q => q.QuestId);

            int inserted = 0, updated = 0, deleted = 0;
            var wanted = new HashSet<ushort>();
            foreach (QuestRow row in rows)
            {
                if (!wanted.Add(row.QuestId))
                    continue;

                if (!stored.TryGetValue(row.QuestId, out CharacterQuestModel model))
                {
                    model = new CharacterQuestModel { Id = characterId, QuestId = row.QuestId };
                    context.CharacterQuest.Add(model);
                    inserted++;
                }
                else if (model.State != row.State || model.Flags != row.Flags || model.Timer != row.Timer || model.Reset != row.Reset)
                    updated++;

                model.State = row.State;
                model.Flags = row.Flags;
                model.Timer = row.Timer;
                model.Reset = row.Reset;

                var indices = new HashSet<byte>();
                foreach ((byte index, uint progress) in row.Objectives)
                {
                    indices.Add(index);
                    CharacterQuestObjectiveModel objective = model.QuestObjective.FirstOrDefault(o => o.Index == index);
                    if (objective == null)
                        model.QuestObjective.Add(new CharacterQuestObjectiveModel { Id = characterId, QuestId = row.QuestId, Index = index, Progress = progress });
                    else
                        objective.Progress = progress;
                }

                foreach (CharacterQuestObjectiveModel objective in model.QuestObjective.Where(o => !indices.Contains(o.Index)).ToList())
                    context.CharacterQuestObjective.Remove(objective);
            }

            foreach (CharacterQuestModel model in stored.Values.Where(q => !wanted.Contains(q.QuestId)))
            {
                context.CharacterQuest.Remove(model);
                deleted++;
            }

            await context.SaveChangesAsync();
            Log($"resync character {characterId}: {rows.Count} quests, {inserted} inserted, {updated} updated, {deleted} deleted");
            Diagnostics.FailureLedger.Record("character-save-resync", "quests rewritten from the game state",
                $"character {characterId}: {rows.Count} quests, {inserted} inserted, {updated} updated, {deleted} deleted");
        }
    }
}
