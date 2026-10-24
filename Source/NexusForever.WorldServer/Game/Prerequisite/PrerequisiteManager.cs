using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Prerequisite.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Prerequisite
{
    public sealed partial class PrerequisiteManager : Singleton<PrerequisiteManager>
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        private delegate bool PrerequisiteCheckDelegate(Player player, PrerequisiteComparison comparison, uint value, uint objectId);
        private ImmutableDictionary<PrerequisiteType, PrerequisiteCheckDelegate> prerequisiteCheckHandlers;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<PrerequisiteType, bool> warnedTypes = new();

        private PrerequisiteManager()
        {
        }

        public void Initialise()
        {
            var builder = ImmutableDictionary.CreateBuilder<PrerequisiteType, PrerequisiteCheckDelegate>();
            foreach (MethodInfo method in Assembly.GetExecutingAssembly().GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)))
            {
                PrerequisiteCheckAttribute attribute = method.GetCustomAttribute<PrerequisiteCheckAttribute>();
                if (attribute == null)
                    continue;

                PrerequisiteCheckDelegate handler = (PrerequisiteCheckDelegate)Delegate.CreateDelegate(typeof(PrerequisiteCheckDelegate), method);
                builder.Add(attribute.Type, handler);
            }

            prerequisiteCheckHandlers = builder.ToImmutable();

            log.Info($"Initialised {prerequisiteCheckHandlers.Count} prerequisite handler(s).");
        }

        /// <summary>
        /// Checks if <see cref="Player"/> meets supplied prerequisite.
        /// </summary>
        [ThreadStatic]
        private static bool lenientScope;

        // the prerequisite row being evaluated, so the report can say which row an unhandled type came from
        [ThreadStatic]
        private static uint currentRow;

        /// <summary>
        /// True while a spell cast is being checked: rules that depend on state the server does not track yet do not block the cast.
        /// </summary>
        internal static bool LenientScope => lenientScope;

        /// <summary>The term that failed the last strict evaluation on this thread (row, type, comparison, value, object), for refusal reasons.</summary>
        [ThreadStatic] public static string LastFailure;

        public bool Meets(Player player, uint prerequisiteId, bool lenient = false)
        {
            PrerequisiteEntry entry = GameTableManager.Instance.Prerequisite.GetEntry(prerequisiteId);
            if (entry == null)
            {
                Diagnostics.FailureLedger.Record("prerequisite-row-missing", $"row {prerequisiteId} not in the tables", "");
                return lenient || lenientScope;
            }

            // nested prerequisites (type 11) call back into this method, so the lenient mode is kept for the whole evaluation
            bool previous = lenientScope;
            uint previousRow = currentRow;
            lenientScope |= lenient;
            currentRow = prerequisiteId;
            try
            {
                switch ((EvaluationMode)entry.Flags)
                {
                    case EvaluationMode.EvaluateAND:
                        return MeetsEvaluateAnd(player, prerequisiteId, entry, lenient);
                    case EvaluationMode.EvaluateOR:
                        return MeetsEvaluateOr(player, prerequisiteId, entry, lenient);
                    default:
                        log.Trace($"Unhandled EvaluationMode {entry.Flags}");
                        return false;
                }
            }
            finally
            {
                currentRow = previousRow;
                lenientScope = previous;
            }
        }

        /// <summary>
        /// Type ids in a prerequisite row (following nested prerequisites) that have no handler, for the quest audit.
        /// </summary>
        public System.Collections.Generic.HashSet<uint> UnhandledTypes(uint prerequisiteId, System.Collections.Generic.HashSet<uint> visited = null)
        {
            var result = new System.Collections.Generic.HashSet<uint>();
            visited ??= new System.Collections.Generic.HashSet<uint>();
            PrerequisiteEntry entry = GameTableManager.Instance.Prerequisite.GetEntry(prerequisiteId);
            if (entry == null || !visited.Add(prerequisiteId))
                return result;

            for (int i = 0; i < entry.PrerequisiteTypeId.Length; i++)
            {
                var type = (PrerequisiteType)entry.PrerequisiteTypeId[i];
                if (type == PrerequisiteType.None)
                    continue;

                if (type == PrerequisiteType.Prerequisite)
                    result.UnionWith(UnhandledTypes(entry.ObjectId[i], visited));
                else if (!prerequisiteCheckHandlers.ContainsKey(type))
                    result.Add(entry.PrerequisiteTypeId[i]);
            }

            return result;
        }

        private bool MeetsEvaluateAnd(Player player, uint prerequisiteId, PrerequisiteEntry entry, bool lenient)
        {
            for (int i = 0; i < entry.PrerequisiteTypeId.Length; i++)
            {
                var type = (PrerequisiteType)entry.PrerequisiteTypeId[i];
                if (type == PrerequisiteType.None)
                    continue;

                PrerequisiteComparison comparison = (PrerequisiteComparison)entry.PrerequisiteComparisonId[i];
                if (!Meets(player, type, comparison, entry.Value[i], entry.ObjectId[i], lenient))
                {
                    log.Trace($"Player {player.Name} failed prerequisite AND check ({prerequisiteId}) {type}, {comparison}, {entry.Value[i]}, {entry.ObjectId[i]}");
                    // a nested row (type 11) already named its own failing term
                    if (type != PrerequisiteType.Prerequisite)
                        LastFailure = $"row {prerequisiteId} t{(uint)type} {comparison} v{entry.Value[i]} o{entry.ObjectId[i]}";
                    return false;
                }
            }

            return true;
        }

        private bool MeetsEvaluateOr(Player player, uint prerequisiteId, PrerequisiteEntry entry, bool lenient)
        {
            for (int i = 0; i < entry.PrerequisiteTypeId.Length; i++)
            {
                var type = (PrerequisiteType)entry.PrerequisiteTypeId[i];
                if (type == PrerequisiteType.None)
                    continue;

                PrerequisiteComparison comparison = (PrerequisiteComparison)entry.PrerequisiteComparisonId[i];
                if (Meets(player, type, comparison, entry.Value[i], entry.ObjectId[i], lenient))
                    return true;
            }

            log.Trace($"Player {player.Name} failed prerequisite OR check ({prerequisiteId})");
            LastFailure = $"row {prerequisiteId} none of " + string.Join("|", Enumerable.Range(0, entry.PrerequisiteTypeId.Length)
                .Where(i => entry.PrerequisiteTypeId[i] != 0u).Select(i => $"t{entry.PrerequisiteTypeId[i]} v{entry.Value[i]} o{entry.ObjectId[i]}"));
            return false;
        }

        private bool Meets(Player player, PrerequisiteType type, PrerequisiteComparison comparison, uint value, uint objectId, bool lenient)
        {
            if (!prerequisiteCheckHandlers.TryGetValue(type, out PrerequisiteCheckDelegate handler))
            {
                Diagnostics.FailureLedger.Record("unhandled-prerequisite-type", $"{(uint)type} row {currentRow} {(lenientScope ? "in a spell cast" : "strict")}", $"comparison {comparison}, value {value}, object {objectId}");
                if (warnedTypes.TryAdd(type, true))
                    log.Warn($"Unhandled PrerequisiteType {type}! Further occurrences are only counted (gt report).");

                // spell casts treat a check they cannot evaluate as met, so an unknown rule does not disable the whole ability;
                // quests and everything else stay strict
                return lenient || lenientScope;
            }

            return handler.Invoke(player, comparison, value, objectId);
        }
    }
}
