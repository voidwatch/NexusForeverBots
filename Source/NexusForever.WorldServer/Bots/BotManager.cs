using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexusForever.Database.Auth.Model;
using NexusForever.Shared;
using NexusForever.Shared.Cryptography;
using NexusForever.Shared.Database;
using NexusForever.Shared.Game;
using NexusForever.Shared.Network;
using NexusForever.WorldServer.Game.CharacterCache;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Network;
using NLog;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Owns all bots. Two kinds exist:
    /// companion bots (bot add) belong to a player, follow and assist them and are restored after a restart,
    /// ambient bots are a population that is kept at a target size (NF_BOTS_TARGET or bot target) and wanders the world.
    /// Every bot has its own hidden account, so both kinds persist as ordinary characters in the database.
    /// </summary>
    public sealed class BotManager : Singleton<BotManager>, IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Shared random source, only used from the world update thread.
        /// </summary>
        public static readonly Random Rng = new();

        private const int DefaultMaxCompanions = 8;
        private const double InitialDelay      = 10d;
        private const double SpawnInterval     = 0.25d;
        private const int MaxSpawnFailures     = 20;

        private readonly Dictionary<string, Bot> bots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase); // account email -> owner name
        private readonly Queue<AccountModel> pendingCompanions = new();
        private readonly Queue<AccountModel> pendingAmbient = new();
        private readonly List<string> pendingRemove = new();

        private bool initialised;
        private double spawnTimer = InitialDelay;
        private int failures;

        private BotManager()
        {
        }

        public int TargetAmbient { get; private set; }

        public int MaxCompanions
        {
            get
            {
                string value = Environment.GetEnvironmentVariable("NF_BOTS_MAX");
                return int.TryParse(value, out int max) && max >= 0 ? max : DefaultMaxCompanions;
            }
        }

        /// <summary>
        /// NF_BOTS_IMMORTAL=1: bots take no damage, so long test runs are not interrupted by deaths.
        /// </summary>
        public static bool Immortal { get; } = Environment.GetEnvironmentVariable("NF_BOTS_IMMORTAL") == "1";

        /// <summary>
        /// NF_BOTS_DUNGEON=3404 sends every ambient bot into that world and has them clear it (kills, deaths and whether the map keeps ticking
        /// are counted), to exercise a dungeon with data. 0 (default) leaves bots on their quests.
        /// </summary>
        /// <summary>NF_BOTS_EVENTS=1: bots go from public event to public event and fight there (tests the event engine, quests are ignored).</summary>
        public static bool EventMode { get; } = Environment.GetEnvironmentVariable("NF_BOTS_EVENTS") == "1";

        /// <summary>NF_BOTS_DUNGEON=382 or a list 382,1263,1336: the dungeon, raid or adventure worlds the bot teams run, one after the other.</summary>
        public static ushort[] DungeonWorlds { get; } = (Environment.GetEnvironmentVariable("NF_BOTS_DUNGEON") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => ushort.TryParse(w, out ushort id) ? id : (ushort)0).Where(id => id != 0).ToArray();

        public static ushort DungeonWorld => DungeonWorlds.Length > 0 ? DungeonWorlds[0] : (ushort)0;

        /// <summary>
        /// NF_BOTS_RAID=1333,1462: raid worlds for raid teams of NF_BOTS_RAID_TEAM (default 20) bots, NF_BOTS_RAID_TEAMS of them (default 1),
        /// formed from the dungeon bots before the dungeon teams of NF_BOTS_TEAM.
        /// </summary>
        public static ushort[] RaidWorlds { get; } = (Environment.GetEnvironmentVariable("NF_BOTS_RAID") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => ushort.TryParse(w, out ushort id) ? id : (ushort)0).Where(id => id != 0).ToArray();

        /// <summary>NF_BOTS_DUNGEON_BOTS: how many ambient bots run dungeons (all of them when a dungeon is set and this is not), then NF_BOTS_EVENT_BOTS bots test events, the rest quest.</summary>
        public static int DungeonBots { get; } = int.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_DUNGEON_BOTS"), out int d) ? d : DungeonWorlds.Length > 0 || RaidWorlds.Length > 0 ? int.MaxValue : 0;
        /// <summary>NF_BOTS_PATH_BOTS: quest bots after the system bots that play path missions of their path first (Bots/BotPaths.cs).</summary>
        public static int PathBots { get; } = int.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_PATH_BOTS"), out int pb) ? pb : 0;
        public static int SystemBots { get; } = int.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_SYSTEM_BOTS"), out int sb) ? sb : 0;
        public static int EventBots { get; } = int.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_EVENT_BOTS"), out int e) ? e : EventMode ? int.MaxValue : 0;

        /// <summary>
        /// NF_BOTS_DAMAGE multiplies the damage bots deal (default 1), so a quest campaign is not limited by how long creatures take to die.
        /// </summary>
        public static float DamageBoost { get; } = float.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_DAMAGE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float boost) && boost > 0f ? boost : 1f;

        /// <summary>
        /// NF_BOTS_CLEAN=1: the ambient population uses only clean accounts (bot.clean-N@bots.invalid), made on demand with a new level 1 character
        /// and reused on every later clean run; all of them are quest bots (no dungeon, event or system probe roles, which grant items, credits and
        /// levels). Other runs never log these accounts in, and clean runs never log in any other account (companions included), so the clean
        /// characters carry only what clean runs gave them (Diagnostics.QuestTruth). Nothing is ever deleted.
        /// </summary>
        public static bool CleanMode { get; } = Environment.GetEnvironmentVariable("NF_BOTS_CLEAN") == "1";

        /// <summary>N of bot.clean-N@bots.invalid, -1 for other emails.</summary>
        public static int CleanNumber(string email)
        {
            if (email == null || !email.StartsWith(Diagnostics.QuestTruth.EmailPrefix, StringComparison.OrdinalIgnoreCase) || !email.EndsWith("@bots.invalid", StringComparison.OrdinalIgnoreCase))
                return -1;
            string number = email.Substring(Diagnostics.QuestTruth.EmailPrefix.Length, email.Length - Diagnostics.QuestTruth.EmailPrefix.Length - "@bots.invalid".Length);
            return int.TryParse(number, out int n) && n >= 0 ? n : -1;
        }

        private int nextClean;

        public IEnumerable<Bot> Bots => bots.Values;

        private static string StorePath => System.IO.Path.Combine(Environment.GetEnvironmentVariable("CachePath") ?? AppContext.BaseDirectory, "bots.json");

        private void Initialise()
        {
            initialised = true;

            string target = Environment.GetEnvironmentVariable("NF_BOTS_TARGET");
            TargetAmbient = int.TryParse(target, out int count) && count > 0 ? count : 0;

            try
            {
                if (File.Exists(StorePath))
                {
                    var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath));
                    foreach ((string email, string owner) in stored ?? new Dictionary<string, string>())
                        owners[email] = owner;
                }
            }
            catch (Exception exception)
            {
                log.Warn($"Could not read {StorePath}: {exception.Message}");
            }

            // clean mode: characters that are no longer fresh (QuestTruth.IsRetired) are left alone, new clean accounts replace them
            Dictionary<uint, List<ulong>> characterIds = new();
            if (CleanMode)
            {
                try
                {
                    characterIds = DatabaseManager.Instance.CharacterDatabase.GetCharacterIdsByAccount();
                }
                catch (Exception exception)
                {
                    log.Warn($"Could not read bot character ids: {exception.Message}");
                }
            }
            int retired = 0;
            List<AccountModel> botAccounts = DatabaseManager.Instance.AuthDatabase.GetBotAccounts();
            if (CleanMode)
                Diagnostics.QuestTruth.LoadDevZoneCompletions(botAccounts.Where(a => CleanNumber(a.Email) >= 0)
                    .SelectMany(a => characterIds.TryGetValue(a.Id, out List<ulong> ids) ? ids : new List<ulong>()).ToList());

            var ambient = new List<AccountModel>();
            foreach (AccountModel account in botAccounts)
            {
                // clean accounts are only for clean runs, clean runs use nothing else
                int clean = CleanNumber(account.Email);
                if (clean >= 0)
                    nextClean = Math.Max(nextClean, clean + 1);
                if (CleanMode != (clean >= 0))
                    continue;
                if (CleanMode && characterIds.TryGetValue(account.Id, out List<ulong> ids) && ids.Any(Diagnostics.QuestTruth.IsRetired))
                {
                    retired++;
                    continue;
                }

                if (owners.ContainsKey(account.Email))
                    pendingCompanions.Enqueue(account);
                else
                    ambient.Add(account);
            }

            // alternate the factions: the accounts came back in creation order and 30 bots were 20 Exile / 10 Dominion while the open quests are
            // 437 Exile-only, 478 Dominion-only and 188 for both (gt pickwhy / quest dump)
            Dictionary<uint, ushort> factions;
            try
            {
                factions = DatabaseManager.Instance.CharacterDatabase.GetCharacterFactionsByAccount();
            }
            catch (Exception exception)
            {
                log.Warn($"Could not read bot character factions: {exception.Message}");
                factions = new Dictionary<uint, ushort>();
            }

            if (CleanMode)
                ambient = ambient.OrderBy(a => CleanNumber(a.Email)).ToList();

            var byFaction = ambient.GroupBy(a => factions.TryGetValue(a.Id, out ushort f) ? f : (ushort)0).OrderBy(g => g.Key == 0 ? 1 : 0)
                .Select(g => new Queue<AccountModel>(g)).ToList();
            while (byFaction.Any(q => q.Count > 0))
                foreach (Queue<AccountModel> queue in byFaction.Where(q => q.Count > 0))
                    pendingAmbient.Enqueue(queue.Dequeue());

            log.Info($"Bots: target population {TargetAmbient}, {pendingCompanions.Count} companion(s) and {pendingAmbient.Count} ambient bot account(s) known{(CleanMode ? $" (clean mode, {retired} retired, next new account bot.clean-{nextClean})" : "")}.");
        }

        private void SaveOwners()
        {
            try
            {
                File.WriteAllText(StorePath, JsonSerializer.Serialize(owners));
            }
            catch (Exception exception)
            {
                log.Warn($"Could not write {StorePath}: {exception.Message}");
            }
        }

        /// <summary>
        /// Invoked each world tick with the delta since the previous tick occurred.
        /// </summary>
        public void Update(double lastTick)
        {
            if (!initialised)
                Initialise();

            foreach ((string key, Bot bot) in bots)
            {
                bot.Update(lastTick);

                // a bot that failed, or whose session went away, is removed
                if (bot.State == BotState.Failed)
                {
                    if (bot.IsAmbient)
                        failures++;
                    pendingRemove.Add(key);
                }
                else if (bot.Session.CanDispose())
                    pendingRemove.Add(key);
            }

            foreach (string key in pendingRemove)
            {
                if (bots.Remove(key, out Bot bot))
                    bot.Stop();
            }

            try
            {
                DungeonTeams.Instance.Update(lastTick, bots.Values);
            }
            catch (Exception exception)
            {
                // the team manager runs on the main loop, an exception there must never stop the world
                Diagnostics.FailureLedger.Record("dungeon-team-exception", $"{exception.GetType().Name}: {exception.Message}", (exception.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim());
            }

            pendingRemove.Clear();

            spawnTimer -= lastTick;
            if (spawnTimer <= 0d)
            {
                spawnTimer = SpawnInterval;
                SpawnNext();
            }
        }

        /// <summary>
        /// Start one bot per call, companions first, then ambient bots until the target population is reached.
        /// Spawning is throttled so hundreds of bots do not all log in on the same tick.
        /// </summary>
        private void SpawnNext()
        {
            if (pendingCompanions.TryDequeue(out AccountModel companion))
            {
                if (!bots.ContainsKey(companion.Email))
                    Start(companion, new BotSpec { Owner = owners[companion.Email] });
                return;
            }

            if (failures >= MaxSpawnFailures)
                return;

            if (bots.Values.Count(b => b.IsAmbient) >= TargetAmbient)
                return;

            if (pendingAmbient.TryDequeue(out AccountModel existing))
            {
                if (!bots.ContainsKey(existing.Email))
                    Start(existing, new BotSpec());
                return;
            }

            AccountModel account = GetOrCreateAccount(CleanMode ? $"{Diagnostics.QuestTruth.EmailPrefix}{nextClean++}@bots.invalid" : $"bot.a{Guid.NewGuid():N}@bots.invalid");
            if (account != null)
                Start(account, new BotSpec());

            if (failures >= MaxSpawnFailures)
                log.Error($"Stopped adding ambient bots after {MaxSpawnFailures} failures. Check the log for the reason.");
        }

        private int ambientStarted;

        private void Start(AccountModel account, BotSpec spec)
        {
            var bot = new Bot(account, spec);
            if (bot.IsAmbient && CleanMode)
            {
                // clean bots quest (path missions first for the first NF_BOTS_PATH_BOTS of them, BotPaths grants nothing)
                int index = ambientStarted++;
                bot.Role = BotRole.Quest;
                bot.PlaysPaths = index < PathBots;
            }
            else if (bot.IsAmbient)
            {
                int index = ambientStarted++;
                bot.Role = index < DungeonBots ? BotRole.Dungeon : index < (long)DungeonBots + EventBots ? BotRole.Event : BotRole.Quest;
                bot.RunsSystemProbes = index >= (long)DungeonBots + EventBots && index < (long)DungeonBots + EventBots + SystemBots;
                // path bots come after the system bots (NF_BOTS_PATH_BOTS)
                long firstPath = (long)DungeonBots + EventBots + SystemBots;
                bot.PlaysPaths = bot.Role == BotRole.Quest && index >= firstPath && index < firstPath + PathBots;
            }
            bots.Add(bot.Key, bot);
            NetworkManager<WorldSession>.Instance.AddSession(bot.Session);

            int count = bots.Count;
            if (count % 50 == 0)
                log.Info($"Bots running: {count}.");
        }

        /// <summary>
        /// Change the size of the ambient population. Bots removed by lowering it are kept in the database and reused later.
        /// </summary>
        public void SetTarget(int target)
        {
            if (!initialised)
                Initialise();

            TargetAmbient = Math.Max(0, target);
            failures      = 0;

            List<Bot> ambient = bots.Values.Where(b => b.IsAmbient).ToList();
            foreach (Bot bot in ambient.Skip(TargetAmbient))
            {
                bots.Remove(bot.Key);
                bot.Stop();
                pendingAmbient.Enqueue(bot.Account);
            }
        }

        /// <summary>
        /// Log in a companion bot for the supplied owner. An existing character for that bot name is reused, otherwise one is created.
        /// </summary>
        /// <returns>null on success, otherwise the reason it was refused.</returns>
        public string Add(string ownerName, string botName, string className)
        {
            if (!initialised)
                Initialise();

            if (!Enum.TryParse(className, true, out Class botClass) || !Enum.IsDefined(typeof(Class), botClass))
                return $"Unknown class '{className}'. Valid classes: {string.Join(", ", Enum.GetNames(typeof(Class)))}";

            Player owner = CharacterManager.Instance.GetPlayer(ownerName);
            if (owner?.Map == null)
                return $"Owner '{ownerName}' is not online (names are case sensitive).";

            if (CleanMode)
                return "Companion bots are off in clean mode (NF_BOTS_CLEAN=1): they would play on accounts that are not clean.";

            string email = $"bot.{Regex.Replace(botName.ToLowerInvariant(), "[^a-z0-9]", "")}@bots.invalid";
            if (bots.ContainsKey(email))
                return $"Bot '{botName}' already exists.";

            if (bots.Values.Count(b => !b.IsAmbient) >= MaxCompanions)
                return $"Companion limit reached ({MaxCompanions}). Raise NF_BOTS_MAX to allow more.";

            AccountModel account = GetOrCreateAccount(email);
            if (account == null)
                return "Could not create the bot account.";

            owners[email] = owner.Name;
            SaveOwners();

            Start(account, new BotSpec
            {
                Name    = botName,
                Owner   = owner.Name,
                Class   = botClass,
                Race    = owner.Race,
                Sex     = owner.Sex,
                Faction = owner.Faction
            });

            log.Info($"Bot {botName} ({botClass}) added for {owner.Name}.");
            return null;
        }

        /// <summary>
        /// Log out the bot with the supplied name. "all" logs out every bot and sets the ambient target to 0.
        /// </summary>
        public int Remove(string botName)
        {
            bool all = botName.Equals("all", StringComparison.OrdinalIgnoreCase);
            List<Bot> matches = bots.Values
                .Where(b => all || string.Equals(b.Name, botName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (all)
                TargetAmbient = 0;

            foreach (Bot bot in matches)
            {
                bots.Remove(bot.Key);
                bot.Stop();

                if (!bot.IsAmbient)
                    owners.Remove(bot.Key);
            }

            if (matches.Any(b => !b.IsAmbient))
                SaveOwners();

            return matches.Count;
        }

        /// <summary>
        /// Each bot has its own account, so character slots and cleanup state never interfere between bots.
        /// The password is random and never stored, nobody can log in to these accounts.
        /// </summary>
        private static AccountModel GetOrCreateAccount(string email)
        {
            AccountModel account = DatabaseManager.Instance.AuthDatabase.GetAccountByEmailAsync(email).GetAwaiter().GetResult();
            if (account != null)
                return account;

            string password = BitConverter.ToString(RandomProvider.GetBytes(16u)).Replace("-", "");
            (string salt, string verifier) = PasswordProvider.GenerateSaltAndVerifier(email, password);
            DatabaseManager.Instance.AuthDatabase.CreateAccount(email, salt, verifier);

            return DatabaseManager.Instance.AuthDatabase.GetAccountByEmailAsync(email).GetAwaiter().GetResult();
        }
    }
}
