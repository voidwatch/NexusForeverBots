using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Database.Auth.Model;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.CharacterCache;
using NexusForever.WorldServer.Game.Combat;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Movement.Generator;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game.Reputation.Static;
using NexusForever.WorldServer.Game.Spell;
using NexusForever.WorldServer.Game.Spell.Static;
using NexusForever.WorldServer.Game.Static;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;
using NLog;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// What a companion does: Follow trails and assists its owner, Stay holds position but still fights what is in reach,
    /// Passive follows but never attacks or heals.
    /// </summary>
    public enum BotMode
    {
        Follow,
        Stay,
        Passive
    }

    public enum BotRole
    {
        Quest,
        Dungeon,
        Event
    }

    public enum BotState
    {
        Starting,
        ListingCharacters,
        CreatePending,
        SelectPending,
        WaitingForWorld,
        InWorld,
        Failed
    }

    /// <summary>
    /// What a bot should be. Anything left null is chosen at random when the character is created.
    /// A spec with an <see cref="Owner"/> makes a companion bot, without one the bot is an ambient population bot.
    /// </summary>
    public class BotSpec
    {
        public string Name { get; init; }
        public string Owner { get; init; }
        public Class? Class { get; init; }
        public Race? Race { get; init; }
        public Sex? Sex { get; init; }
        public Faction? Faction { get; init; }
    }

    /// <summary>
    /// A server side player. It logs in through the normal character handlers on a socketless <see cref="BotSession"/>.
    /// Companion bots follow and assist their owner, ambient bots wander around where they spawned.
    /// </summary>
    public partial class Bot
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        private const double ThinkInterval    = 0.5d;
        private const float FollowStartRange  = 10f;
        private const float FollowDistance    = 3f;
        private const float TeleportRange     = 60f;
        private const float AttackRange       = 30f;
        private const float WanderRadius      = 40f;
        private const float HuntRange         = 25f;
        private const float QuestHuntRange    = 60f;
        private const int MaxSelectRetries    = 10;
        private const int MaxCreateAttempts   = 5;

        /// <summary>
        /// Unique key of the bot, the email of its hidden account.
        /// </summary>
        public string Key => Account.Email;

        public AccountModel Account { get; }

        /// <summary>N of a clean account bot.clean-N@bots.invalid, -1 for other accounts.</summary>
        public int CleanNumber => BotManager.CleanNumber(Account.Email);
        public string Name { get; private set; }
        public string OwnerName { get; }
        public Class Class { get; private set; }
        public bool IsAmbient => OwnerName == null;

        /// <summary>Clean bot (NF_BOTS_CLEAN=1): an account of its own made for clean runs, started at level 1, never levelled by the harness.</summary>
        public bool IsClean => Account.Email.StartsWith(Diagnostics.QuestTruth.EmailPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>What an ambient bot does when it is not busy: quests, dungeon runs with a team, or public event tests.</summary>
        public BotRole Role { get; set; } = BotRole.Quest;

        /// <summary>Test command (bot quest): quest to take on the next think; while it is in the log the bot takes no other quest.</summary>
        public ushort ForcedQuest { get; set; }
        private ushort forcedHeld;
        public bool IsForced => ForcedQuest != 0 || forcedHeld != 0;

        /// <summary>
        /// Companion behaviour, changed with the bot mode command.
        /// </summary>
        /// <summary>Also runs the non-combat system probes (BotSystems) next to its normal role.</summary>
        public bool RunsSystemProbes { get; set; }

        public BotMode Mode { get; set; } = BotMode.Follow;

        /// <summary>
        /// Make a companion jump to its owner on the next think, wherever it is.
        /// </summary>
        public void Summon()
        {
            summonRequested = true;
        }

        private bool summonRequested;
        public BotSession Session { get; }
        public BotState State { get; private set; } = BotState.Starting;
        public string Error { get; private set; }

        private readonly BotSpec spec;

        private ulong characterId;
        private double retryTimer;
        private int selectRetries;
        private int createAttempts;

        private double thinkTimer;
        private double followTimer;
        private double teleportTimer;
        private double wanderTimer;
        private double questAssignTimer;
        private double busyTimer;
        private readonly Dictionary<uint, double> blockedSpells = new();
        private double clock;
        private double resourceSampleTimer = 30d;
        private bool levelSynced;
        private double groupTimer;
        private ulong pendingInviteGroupId;
        private Vector3? home;
        private double deadTimer;
        private double huntTimer;
        private double fightTraceTimer;
        private double questTimer;
        private double questTeleportTimer;
        private HashSet<uint> questTargets = new();
        private HashSet<uint> killTargets = new();
        private HashSet<uint> activateTargets = new();
        private HashSet<uint> talkTargets = new();
        private bool activating;
        private readonly Dictionary<ushort, DateTime> questStarted = new();
        // levels where this bot found nothing it may accept, so level rotation does not send it back there
        // a level stays off the rotation list for 15 minutes, new quests open up as prerequisites get done
        private readonly Dictionary<byte, DateTime> exhaustedUntil = new();
        private HashSet<byte> exhaustedLevels
        {
            get
            {
                foreach (byte level in exhaustedUntil.Where(e => e.Value <= DateTime.UtcNow).Select(e => e.Key).ToList())
                    exhaustedUntil.Remove(level);
                return exhaustedUntil.Keys.ToHashSet();
            }
        }
        private bool MarkExhausted(byte level) { exhaustedUntil[level] = DateTime.UtcNow.AddMinutes(15); return true; }
        private readonly Dictionary<ushort, uint> questProgressSum = new();

        private const int MaxBotQuests = 2;
        private static readonly TimeSpan QuestBehind = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan QuestTimeout = TimeSpan.FromMinutes(6); // without progress (the clock restarts on every progress step)

        public Bot(AccountModel account, BotSpec spec)
        {
            Account   = account;
            this.spec = spec;
            Name      = spec.Name;
            OwnerName = spec.Owner;

            // spread the bots over the tick so they do not all think on the same one
            thinkTimer = BotManager.Rng.NextDouble() * ThinkInterval;
            wanderTimer = BotManager.Rng.NextDouble() * 10d;

            Session = new BotSession();
            Session.Initialise(account);
            Session.ServerMessage += OnServerMessage;
        }

        public void Stop()
        {
            Session.ServerMessage -= OnServerMessage;
            Session.ForceDisconnect();
        }

        public void Update(double lastTick)
        {
            if (State == BotState.Failed)
                return;

            try
            {
                switch (State)
                {
                    case BotState.Starting:
                        State = BotState.ListingCharacters;
                        CharacterHandler.HandleCharacterList(Session, BotPackets.Build<ClientCharacterList>(_ => { }));
                        break;
                    case BotState.CreatePending:
                        Create();
                        break;
                    case BotState.SelectPending:
                        retryTimer -= lastTick;
                        if (retryTimer <= 0d)
                            Select();
                        break;
                    case BotState.WaitingForWorld:
                        if (Session.Player?.Map != null)
                        {
                            State = BotState.InWorld;
                            log.Debug($"Bot {Name} entered the world (owner {OwnerName ?? "none"}).");
                        }
                        break;
                    case BotState.InWorld:
                        followTimer   -= lastTick;
                        teleportTimer -= lastTick;
                        busyTimer     -= lastTick;
                        clock         += lastTick;
                        thinkTimer    -= lastTick;
                        resourceSampleTimer -= lastTick;
                        if (resourceSampleTimer <= 0d && Session.Player != null)
                        {
                            resourceSampleTimer = 30d;
                            SampleResource(Session.Player);
                        }
                        if (thinkTimer <= 0d)
                        {
                            thinkTimer = ThinkInterval;
                            if (Session.Player?.Map != null)
                                Think(Session.Player);
                        }
                        break;
                }
            }
            catch (Exception exception)
            {
                log.Error(exception);
                Fail(exception.Message, string.Join(" | ", (exception.StackTrace ?? "").Split('\n').Take(4).Select(f => f.Trim())));
            }
        }

        private void Fail(string error, string where = null)
        {
            Diagnostics.FailureLedger.Record("bot-failed", error, $"{Name ?? Key} {where}");
            Error = error;
            State = BotState.Failed;
            log.Warn($"Bot {Name ?? Key} failed: {error}");
        }

        private void OnServerMessage(IWritable message)
        {
            // handlers invoke this from inside their own callbacks, so only record state here and act on the next update
            switch (message)
            {
                case ServerSpellCastResult castResult when castResult.CastResult != CastResult.Ok:
                    // spells whose prerequisites the server cannot evaluate would otherwise be retried every tick
                    blockedSpells[castResult.Spell4Id] = clock + 90d;
                    break;
                case ServerCharacterList:
                {
                    var character = Session.Characters.FirstOrDefault();
                    if (character != null)
                    {
                        characterId = character.Id;
                        Name        = character.Name;
                        BotNames.Reserve(character.Name);
                        Class       = (Class)character.Class;
                        State       = BotState.SelectPending;
                    }
                    else
                        State = BotState.CreatePending;
                    break;
                }
                case ServerCharacterCreate create:
                {
                    if (create.Result == CharacterModifyResult.CreateOk)
                    {
                        characterId = create.CharacterId;
                        State       = BotState.SelectPending;
                    }
                    else if (create.Result == CharacterModifyResult.CreateFailed_UniqueName
                        && spec.Name == null
                        && ++createAttempts < MaxCreateAttempts)
                    {
                        // generated name was taken, roll a new one
                        Name  = null;
                        State = BotState.CreatePending;
                    }
                    else
                        Fail($"character creation failed: {create.Result}");
                    break;
                }
                case ServerGroupInviteReceived invite:
                {
                    pendingInviteGroupId = invite.GroupId;
                    break;
                }
                case ServerCharacterSelectFail fail:
                {
                    // e.g. a previous session of this character is still being cleaned up
                    if (++selectRetries > MaxSelectRetries)
                        Fail($"character select failed: {fail.Result}");
                    else
                    {
                        retryTimer = 1d;
                        State      = BotState.SelectPending;
                    }
                    break;
                }
            }
        }

        private static bool loggedFilterFallback;

        private void Create()
        {
            State = BotState.ListingCharacters; // waiting for the create result

            List<CharacterCreationEntry> candidates = new();
            // clean bots: even numbers start in the tutorial (PreTutorial, world 3460; the server has no start location for Arkship 0), odd ones
            // on Nexus (the skip-tutorial start), the two starts a new player can choose
            CharacterCreationStart[] starts = IsClean && CleanNumber % 2 == 0
                ? new[] { CharacterCreationStart.PreTutorial, CharacterCreationStart.Nexus }
                : new[] { CharacterCreationStart.Nexus, CharacterCreationStart.Arkship };
            foreach (CharacterCreationStart start in starts)
            {
                // strictest filter first, then progressively looser ones if the tables leave nothing
                for (int level = 0; level < 3 && candidates.Count == 0; level++)
                {
                    candidates = GameTableManager.Instance.CharacterCreation.Entries.Where(e =>
                        e.CharacterCreationStartEnum == (uint)start
                        && (spec.Class   == null || e.ClassId   == (uint)spec.Class.Value)
                        && (spec.Race    == null || e.RaceId    == (uint)spec.Race.Value)
                        && (spec.Sex     == null || e.Sex       == (uint)spec.Sex.Value)
                        && (spec.Faction == null || e.FactionId == (uint)spec.Faction.Value)
                        && (level >= 1 || (e.Enabled && e.EntitlementIdRequired == 0u && e.AccountCurrencyAmountCost == 0u))
                        && (level >= 2 || !e.CostumeOnly)).ToList();

                    if (candidates.Count > 0 && level > 0 && !loggedFilterFallback)
                    {
                        loggedFilterFallback = true;
                        log.Warn($"Bot character creation: no entry passed all filters for start {start}, using looser filter level {level} ({candidates.Count} entries).");
                    }
                }

                if (candidates.Count > 0)
                    break;
            }

            if (candidates.Count == 0)
            {
                Fail("no character creation entry matches the requested class, race, sex and faction");
                return;
            }

            CharacterCreationEntry entry = candidates[BotManager.Rng.Next(candidates.Count)];
            Class = (Class)entry.ClassId;
            Name ??= BotNames.GenerateUnique(BotManager.Rng, createAttempts > 0);

            (List<uint> labels, List<uint> values) = BotAppearance.Roll(entry.RaceId, entry.Sex, BotManager.Rng);

            uint creationId = entry.Id;
            string name     = Name;
            ClientCharacterCreate create = BotPackets.Build<ClientCharacterCreate>(writer =>
            {
                writer.Write(creationId);
                writer.WriteStringWide(name);
                writer.Write((byte)0, 3u); // soldier path
                writer.Write((uint)labels.Count);
                foreach (uint label in labels)
                    writer.Write(label);
                foreach (uint value in values)
                    writer.Write(value);
                writer.Write(0u);          // bone count
            });

            CharacterHandler.HandleCharacterCreate(Session, create);
        }

        private void Select()
        {
            State = BotState.WaitingForWorld;
            ulong id = characterId;
            CharacterHandler.HandleCharacterSelect(Session, BotPackets.Build<ClientCharacterSelect>(writer => writer.Write(id)));
        }

        private uint feedLevel;
        private uint feedWorld;
        private bool feedDead;

        private string lastTrace;
        private (string Objectives, string Detail) forcedStall;
        private uint lastWalkGuid;

        // forced test bots (bot quest) explain their quest steps in the feed, once per change; nothing for normal bots
        private void Trace(Player me, string text)
        {
            if (forcedHeld == 0 || text == lastTrace)
                return;
            lastTrace = text;
            Feed(me, "trace: " + text);
        }

        private void Feed(Player me, string text)
        {
            Diagnostics.BotFeed.Add(Name, me.Level, me.Zone != null ? $"{me.Zone.Id}" : "-", text);
        }

        // level, world and death changes for the bot feed, a few integer compares per think
        private void FeedChanges(Player me)
        {
            uint world = me.Map?.Entry.Id ?? 0u;
            if (feedLevel != 0u && me.Level != feedLevel)
                Feed(me, $"level up to {me.Level}");
            if (feedWorld != 0u && world != feedWorld)
                Feed(me, $"changed world {feedWorld} -> {world}");
            if (me.IsDead != feedDead)
                Feed(me, me.IsDead ? "died" : "resurrected");

            feedLevel = me.Level;
            feedWorld = world;
            feedDead  = me.IsDead;
        }

        private uint factionSpellTarget;
        private int factionSpellTries;

        private bool TryFactionSpell(Player me, NonPlayer quarry, float distance)
        {
            ushort quest = me.QuestManager.QuestNeedingKill(quarry.CreatureId);
            if (quest == 0)
                return false;
            var spells = QuestFactionSpells.For(quest);
            if (spells.Count == 0)
                return false;

            if (factionSpellTarget != quarry.Guid)
            {
                factionSpellTarget = quarry.Guid;
                factionSpellTries = 0;
            }
            if (factionSpellTries >= 2 * spells.Count)
                return false;

            if (distance > 8f)
            {
                if (followTimer <= 0d)
                {
                    me.MovementManager.Follow(quarry, 4f);
                    followTimer = 2d;
                }
                return true;
            }

            if (me.SpellManager.GetGlobalSpellCooldown() > 0d || me.IsCasting())
                return true;

            uint spell = spells[factionSpellTries % spells.Count];
            factionSpellTries++;
            try
            {
                me.MovementManager.StopSpline();
                me.CastSpell(spell, new Game.Spell.SpellParameters { UserInitiatedSpellCast = true, PrimaryTargetId = quarry.Guid });
                Diagnostics.FailureLedger.Record("quest-faction-spell-cast", $"quest {quest}", $"spell {spell} on creature {quarry.CreatureId}");
            }
            catch (System.Exception exception)
            {
                Diagnostics.FailureLedger.Record("quest-faction-spell-error", $"spell {spell}: {exception.Message}", $"quest {quest}");
            }
            return true;
        }

        private static void AcceptResurrection(Player me, ResurrectionType type)
        {
            Network.Message.Handler.ResurrectionHandler.HandleResurrectAccept(me.Session, BotPackets.Build<Network.Message.Model.ClientResurrectAccept>(w =>
            {
                w.Write(me.Guid);
                w.Write((uint)type, 7u);
                w.Write(0u);
            }));
        }

        /// <summary>
        /// A caster's offer first; inside a dungeon run in place (the team logic expects it); in the open world Wake Here, then the holocrypt,
        /// and when both are refused the forced resurrection the death timer would give.
        /// </summary>
        private static void ChooseResurrection(Player me)
        {
            if (me.HasCasterRezOffer)
                AcceptResurrection(me, ResurrectionType.SpellCasterLocation);
            if (me.IsDead && me.Map is Game.Map.DungeonMapInstance)
                me.Resurrect();
            if (me.IsDead)
                AcceptResurrection(me, ResurrectionType.WakeHere);
            if (me.IsDead && (me.ResurrectionOptions & ResurrectionType.Holocrypt) != 0)
                AcceptResurrection(me, ResurrectionType.Holocrypt);
            if (me.IsDead)
                me.Resurrect();
        }

        private void Think(Player me)
        {
            FeedChanges(me);
            if (CombatConfig.Enabled && me.IsDead)
            {
                // a dead bot waits a few seconds and then picks a death screen option through the client message
                deadTimer += ThinkInterval;
                if (deadTimer >= CombatConfig.BotResurrectTime || me.HasCasterRezOffer)
                {
                    deadTimer = 0d;
                    ChooseResurrection(me);
                }
                return;
            }

            deadTimer = 0d;
            if (me.Health == 0u && !CombatConfig.Enabled)
                return;

            if (pendingInviteGroupId != 0ul)
                AnswerInvite(me);

            BotSystems.AnswerLoot(me);

            if (IsAmbient)
                ThinkAmbient(me);
            else
                ThinkCompanion(me);
        }

        /// <summary>
        /// Companions accept group invites that come from a group their owner is in, everything else is declined.
        /// </summary>
        private void AnswerInvite(Player me)
        {
            ulong groupId = pendingInviteGroupId;
            pendingInviteGroupId = 0ul;

            bool accept = false;
            if (!IsAmbient)
            {
                Player owner = CharacterManager.Instance.GetPlayer(OwnerName);
                PlayerGroup group = GroupManager.Instance.GetGroupById(groupId);
                accept = owner != null && group?.GetMember(owner.CharacterId) != null;
            }

            GroupManager.Instance.InviteResponse(me, groupId, accept);
        }

        /// <summary>
        /// Keep the companion in its owner's group: invite it every few seconds while it is not in a group.
        /// </summary>
        private void KeepGroup(Player me, Player owner)
        {
            groupTimer -= ThinkInterval;
            if (groupTimer > 0d)
                return;

            groupTimer = 10d;
            if (GroupManager.Instance.GetGroup(me.CharacterId) == null)
                GroupManager.Instance.Invite(owner, me.Name);
        }

        private void ThinkAmbient(Player me)
        {
            home ??= me.Position;

            // researched spawn points have no height (Y 0, ids from 9000000; 24k of them, all of worlds 1421 and 1068) and BaseMap.SpawnGrid puts
            // their creatures on the terrain; a bot sent to such a point stayed at Y 0, hundreds of metres from the creatures, and the 3D 60 m
            // search never found them (forced quest 3623: kill 12211 in world 22, terrain near -880, the bot hopped between worlds for 6 min)
            if (me.Map != null && me.Position.Y == 0f)
            {
                float ground = me.Map.GetTerrainHeight(me.Position.X, me.Position.Z, 0f);
                if (ground != 0f)
                {
                    Diagnostics.FailureLedger.Record("bot-ground-snap", $"world {me.Map.Entry.Id}");
                    me.TeleportTo(me.Map.Entry, new Vector3(me.Position.X, ground, me.Position.Z), (me.Map as MapInstance)?.InstanceId);
                    return;
                }
            }

            if (!levelSynced)
            {
                levelSynced = true;
                // clean bots keep the level they earn
                byte level = IsClean ? (byte)0 : (byte)BotManager.Rng.Next(5, 51);
                if (level > me.Level)
                    me.XpManager.SetLevel(level);
            }

            // a bot that logged in inside a dungeon instance (saved there by an earlier run) and is not part of a run goes back to the open world
            if (me.Map is DungeonMapInstance && !BotSystems.OnPortalTrip(me))
            {
                DungeonTeams.Team runTeam = Role == BotRole.Dungeon ? DungeonTeams.Instance.TeamOf(this) : null;
                if (runTeam == null || runTeam.State is not (DungeonTeams.TeamState.Entering or DungeonTeams.TeamState.Clearing))
                {
                    if (BotQuests.TryGetWorldEntrance(51, out Vector3 outside))
                    {
                        Diagnostics.FailureLedger.Record("bot-left-instance", "bot found inside a dungeon instance outside a run, sent to the open world");
                        me.TeleportTo(51, outside.X, outside.Y, outside.Z);
                    }

                    return;
                }
            }

            // every half minute or so put on better gear the bot carries (equipped item stats, Combat.ItemStats)
            gearTimer -= ThinkInterval;
            if (gearTimer <= 0d)
            {
                gearTimer = 25d + BotManager.Rng.NextDouble() * 10d;
                BotGear.Think(me);
                BotAmps.Think(me);
            }

            if (RunsSystemProbes)
            {
                systemsTimer -= ThinkInterval;
                if (systemsTimer <= 0d)
                {
                    systemsTimer = 5d + BotManager.Rng.NextDouble() * 10d;
                    BotSystems.Think(me);
                }
            }

            // a quest needs a crafting tier: craft up to it at a station (BotTradeskills)
            if (Role == BotRole.Quest && BotTradeskills.Think(me))
                return;

            if (CombatConfig.Enabled && Role == BotRole.Dungeon && (BotManager.DungeonWorlds.Length > 0 || BotManager.RaidWorlds.Length > 0) && ThinkDungeon(me))
                return;

            if (CombatConfig.Enabled && Role == BotRole.Event && ThinkEvents(me))
                return;

            // path bots play the missions of their path first (BotPaths), quests when no mission is open
            if (CombatConfig.Enabled && PlaysPaths && Role == BotRole.Quest && ThinkPath(me))
                return;

            if (CombatConfig.Enabled)
            {
                ThinkQuests(me);
                if (activating)
                    return;

                if (Hunt(me))
                    return;
            }

            wanderTimer -= ThinkInterval;
            if (wanderTimer > 0d)
                return;

            wanderTimer = 8d + BotManager.Rng.NextDouble() * 20d;

            float angle  = (float)(BotManager.Rng.NextDouble() * Math.PI * 2d);
            float radius = (float)(BotManager.Rng.NextDouble() * WanderRadius);

            me.MovementManager.LaunchGenerator(new DirectMovementGenerator
            {
                Begin = me.Position,
                Final = home.Value.GetPoint2D(angle, radius),
                Map   = me.Map
            }, 3f);
        }

        private double dungeonTimer;
        private double systemsTimer;
        private double gearTimer;
        private uint testEvent;
        private DateTime testEventSince;
        private static int eventCursor;

        /// <summary>
        /// Event mode: go to the next public event, fight and activate what the event needs, move on when it ended or after six minutes.
        /// Each hand-over is counted as event-test with the result.
        /// </summary>
        private bool ThinkEvents(Player me)
        {
            if (me.Map == null || me.IsDead)
                return true;

            var engine = Game.Events.PublicEventEngine.Instance;
            string state = testEvent == 0u ? "none" : engine.State(testEvent);
            bool timeout = DateTime.UtcNow - testEventSince > TimeSpan.FromMinutes(6);
            if (testEvent == 0u || timeout || (state != "running" && state != "never" && DateTime.UtcNow - testEventSince > TimeSpan.FromSeconds(20)))
            {
                if (testEvent != 0u)
                {
                    Diagnostics.FailureLedger.Record("event-test", $"event {testEvent}", timeout && state is "running" or "never" ? "timeout " + state : state);
                    if (state == "never" && engine.TryGetLocation(testEvent, out ushort w0, out Vector3 c0, out float r0, true))
                        Diagnostics.FailureLedger.Record("event-never-started", $"event {testEvent}", $"bot in world {me.Map.Entry.Id} (event world {w0}) at {me.Position.X:0},{me.Position.Y:0},{me.Position.Z:0}, center {c0.X:0},{c0.Y:0},{c0.Z:0} radius {r0:0}, level {me.Level}, dead {me.IsDead}, loading {me.IsLoading}");
                }

                var events = engine.TestableEvents(world => BotQuests.CanVisitWorld(world));
                if (events.Count == 0)
                    return false;

                testEvent = events[(int)((uint)System.Threading.Interlocked.Increment(ref eventCursor) % (uint)events.Count)];
                testEventSince = DateTime.UtcNow;
                if (engine.TryGetLocation(testEvent, out ushort world, out Vector3 center, out _, true))
                {
                    home = center;
                    me.TeleportTo(world, center.X, center.Y, center.Z);
                }

                return true;
            }

            // targets: the event's own, plus any hostile creature next to the bot (Hunt)
            (HashSet<uint> kill, HashSet<uint> activate) = engine.TargetCreatures(testEvent, true);
            killTargets = kill;
            activateTargets = activate;
            activating = false;
            if (activate.Count > 0 && ActivateNearby(me))
                return true;

            Hunt(me);

            // fights drag a bot away from the event (a hunt jumps to the next target of the group), bring it back
            if (engine.TryGetLocation(testEvent, out ushort eventWorld, out Vector3 eventCenter, out float eventRadius, true)
                && me.Map.Entry.Id == eventWorld && Vector3.Distance(new Vector3(me.Position.X, 0f, me.Position.Z), new Vector3(eventCenter.X, 0f, eventCenter.Z)) > eventRadius * 0.8f)
                me.TeleportTo(eventWorld, eventCenter.X, eventCenter.Y, eventCenter.Z);

            return true;
        }

        /// <summary>
        /// Dungeon mode: go to the dungeon world, then fight whatever hostile creature is closest and jump to the next one when nothing is in reach.
        /// Returns true when the bot is busy with it.
        /// </summary>
        private double dungeonTurnInTimer;
        private double dungeonActivateTimer;
        private readonly Dictionary<uint, DateTime> questObjectJumps = new();

        private bool ThinkDungeon(Player me)
        {
            // quests the team took for this instance (veteran boss kills: 9342, 9733, ...) are turned in once achieved, like quest bots do
            dungeonTurnInTimer -= ThinkInterval;
            if (dungeonTurnInTimer <= 0d && me.Map != null)
            {
                dungeonTurnInTimer = 10d;
                foreach (Quest quest in me.QuestManager.GetActiveQuests().Where(q => q.State == QuestState.Achieved).ToList())
                {
                    try
                    {
                        var itemRewards = quest.Info.Rewards.Values.Where(r => r.Quest2RewardTypeId == 1u).ToList();
                        ushort pick = itemRewards.Count > 1 ? (ushort)itemRewards[BotManager.Rng.Next(itemRewards.Count)].Id : (ushort)0;
                        me.QuestManager.QuestComplete(quest.Id, pick, false, true);
                        Diagnostics.FailureLedger.Record("dungeon-quest-completed", $"world {me.Map?.Entry.Id}{((me.Map as DungeonMapInstance)?.Difficulty == InstanceDifficulty.Veteran ? " veteran" : "")} quest {quest.Id}");
                        Feed(me, $"completed quest {quest.Id}");
                        Diagnostics.QuestOutcomes.Completed(quest.Id, false);
                        QuestCoverage.OnCompleted(me, quest.Info);
                        if (me.Map is DungeonMapInstance instance)
                            DungeonTeams.GiveFollowUps(me, instance, quest.Id);
                    }
                    catch (Exception exception)
                    {
                        Diagnostics.FailureLedger.Record("dungeon-quest-turn-in-failed", $"quest {quest.Id}", exception.Message);
                    }
                }
            }

            // team runs: the team manager takes the bots in and out, a bot only fights while its team is clearing
            DungeonTeams.Team team = DungeonTeams.Instance.TeamOf(this);
            if (team == null || team.State != DungeonTeams.TeamState.Clearing)
                return true;

            dungeonTimer -= ThinkInterval;
            if (me.Map == null || me.Map is not MapInstance || me.IsDead)
                return true;

            killTargets = new HashSet<uint>();

            // hurt in a fight: use a heal ability on itself before anything else
            if (me.MaxHealth > 0u && me.Health < me.MaxHealth * 0.6d && me.TargetGuid != 0u && CastFirst(me, IsHeal, me))
                return true;

            // quest objectives inside the instance that are not kills (consoles, items lying around, people to talk to): use the nearest
            // one through the same activation as quest bots, between fights
            dungeonActivateTimer -= ThinkInterval;
            if (me.TargetGuid == 0u && dungeonActivateTimer <= 0d)
            {
                // at most every 3 s, so an object that refuses the activation (cooldown, prerequisite) does not keep the bot out of the fight
                dungeonActivateTimer = 3d;
                activateTargets = BotQuests.ActiveActivateTargets(me);
                talkTargets     = BotQuests.ActiveTalkTargets(me);
                if (activateTargets.Count > 0 && ActivateNearby(me))
                    return true;
            }

            if (Hunt(me))
                return true;

            if (dungeonTimer > 0d)
                return true;

            // pull one pack at a time: rest until this bot and its team are healthy again
            if (me.MaxHealth > 0u && me.Health < me.MaxHealth * 0.8d)
            {
                // nothing else regenerates health out of combat for players yet, resting has to heal or the bot waits forever
                huntTimer -= ThinkInterval;
                if (huntTimer <= 0d)
                {
                    huntTimer = 3d;
                    me.Heal(me.MaxHealth / 20u);
                }
                return true;
            }
            if (team.Members.Select(m => m.Session.Player).Any(p => p != null && !p.IsDead && p.MaxHealth > 0u && p.Health < p.MaxHealth * 0.7d) && team.StateTime % 60d < 45d)
                return true;

            // nothing in reach: jump next to the closest quest object of the instance, else the closest living hostile creature
            dungeonTimer = 4d;
            me.Map.Search(me.Position, 4000f, new SearchCheckRange(me.Position, 4000f, me), out List<GridEntity> all);
            if (activateTargets != null && activateTargets.Count > 0)
            {
                WorldEntity use = all.OfType<WorldEntity>().Where(e => e is not Player && !(e is UnitEntity { IsDead: true }) && activateTargets.Contains(e.CreatureId)
                        && !(questObjectJumps.TryGetValue(e.Guid, out DateTime at) && (DateTime.UtcNow - at).TotalSeconds < 60d))
                    .OrderBy(e => Vector3.Distance(me.Position, e.Position)).FirstOrDefault();
                if (use != null)
                {
                    // each object once a minute: one that cannot be used must not hold the bot
                    if (questObjectJumps.Count > 200)
                        questObjectJumps.Clear();
                    questObjectJumps[use.Guid] = DateTime.UtcNow;
                    Diagnostics.FailureLedger.Record("dungeon-quest-object", $"world {me.Map.Entry.Id}", $"creature {use.CreatureId}");
                    me.TeleportTo(me.Map.Entry, use.Position, (me.Map as MapInstance)?.InstanceId);
                    return true;
                }

                // grids spawn only when a player is near: a quest object in a grid nobody has loaded yet (2188 "Assess the situation" console)
                Vector3? objectSpawn = Game.Map.EntityCacheManager.Instance.GetEntityCache((ushort)me.Map.Entry.Id)?.All
                    .Where(m => activateTargets.Contains(m.Creature) && !me.Map.IsGridActive(new Vector3(m.X, m.Y, m.Z)))
                    .OrderBy(m => Vector3.Distance(me.Position, new Vector3(m.X, m.Y, m.Z)))
                    .Select(m => (Vector3?)new Vector3(m.X, m.Y, m.Z))
                    .FirstOrDefault();
                if (objectSpawn != null)
                {
                    Diagnostics.FailureLedger.Record("dungeon-quest-object", $"world {me.Map.Entry.Id}", "jump to an unloaded grid");
                    me.TeleportTo(me.Map.Entry, objectSpawn.Value, (me.Map as MapInstance)?.InstanceId);
                    return true;
                }
            }
            NonPlayer next = all.OfType<NonPlayer>()
                .Where(c => !c.IsDead && c.Health > 0u && me.GetDispositionTo(c.Faction1) == Disposition.Hostile)
                .OrderBy(c => Vector3.Distance(me.Position, c.Position))
                .FirstOrDefault();

            if (next == null)
            {
                // grids spawn only when a player is near: jump to the closest hostile spawn point in a grid that is not loaded yet
                Vector3? spawn = Game.Map.EntityCacheManager.Instance.GetEntityCache((ushort)me.Map.Entry.Id)?.All
                    .Where(m => !me.Map.IsGridActive(new Vector3(m.X, m.Y, m.Z)) && BotQuests.IsHostile(me, m.Creature))
                    .OrderBy(m => Vector3.Distance(me.Position, new Vector3(m.X, m.Y, m.Z)))
                    .Select(m => (Vector3?)new Vector3(m.X, m.Y, m.Z))
                    .FirstOrDefault();
                if (spawn != null)
                {
                    me.TeleportTo(me.Map.Entry, spawn.Value, (me.Map as MapInstance)?.InstanceId);
                    return true;
                }

                Diagnostics.FailureLedger.Record("dungeon", "nothing hostile left in reach", $"world {BotManager.DungeonWorld}");
                return true;
            }

            me.TeleportTo(me.Map.Entry, next.Position, (me.Map as MapInstance)?.InstanceId);
            return true;
        }

        /// <summary>
        /// Ambient bots that are healthy attack the nearest hostile creature within reach and rest when hurt.
        /// Returns true while the bot is busy fighting.
        /// </summary>
        /// <summary>
        /// Ambient bots take kill quests for creatures around them, hunt the targets (jumping to a spawn point of the
        /// targets when none are near), and turn finished quests in without visiting anyone. Runs every few seconds.
        /// </summary>
        private void ThinkQuests(Player me)
        {
            questTeleportTimer -= ThinkInterval;
            questTimer         -= ThinkInterval;
            questAssignTimer   -= ThinkInterval;
            if (questTimer > 0d)
                return;

            questTimer = 4d + BotManager.Rng.NextDouble() * 2d;

            // forced sweep (bot sweep start): a free quest bot takes the next swept quest; a swept quest that ran out of time is recorded and dropped
            if (ForceSweep.IsSweeping(this) && ForceSweep.Expired(this))
            {
                Quest swept = me.QuestManager.GetActiveQuests().FirstOrDefault(q => q.Id == forcedHeld);
                ForceSweep.Finished(this, me, "timeout", ForceSweep.Objectives(swept), lastTrace);
                if (swept != null)
                {
                    if (swept.CanAbandon())
                        try { me.QuestManager.QuestAbandon(swept.Id); } catch (Exception) { }
                    else
                        BotQuests.Park(me, swept.Id);
                    questStarted.Remove(swept.Id);
                }
                forcedHeld = 0;
                ForcedQuest = 0;
            }
            if (ForceSweep.Active && IsAmbient && Role == BotRole.Quest && ForcedQuest == 0 && forcedHeld == 0 && !ForceSweep.IsSweeping(this))
                ForcedQuest = ForceSweep.Next(this, me);

            // turn in finished quests and drop ones that have gone nowhere
            foreach (Quest quest in me.QuestManager.GetActiveQuests())
            {
                // a parked quest (cannot be abandoned, no progress) gets no stall clock again: it was re-parked every stall timeout (10518: 109x in 35 min)
                if (quest.State == QuestState.Accepted && BotQuests.IsParked(me, quest.Id))
                    continue;

                // quests loaded from an earlier run were never started by this bot: start their stall clock now, otherwise they never time out and block the idle check
                if (!questStarted.ContainsKey(quest.Id))
                    questStarted[quest.Id] = DateTime.UtcNow;

                // progress restarts the stall clock, so long kill counts are not abandoned while they advance
                uint sum = (uint)quest.Sum(o => (long)o.Progress);
                if (questProgressSum.TryGetValue(quest.Id, out uint lastSum) && sum > lastSum && questStarted.ContainsKey(quest.Id))
                    questStarted[quest.Id] = DateTime.UtcNow;
                questProgressSum[quest.Id] = sum;

                // objectives with no in-world path are credited a few seconds after the quest was taken
                if (quest.State == QuestState.Accepted && questStarted.TryGetValue(quest.Id, out DateTime taken) && DateTime.UtcNow - taken > TimeSpan.FromSeconds(10))
                    BotQuests.CreditSynthetic(me, quest);

                if (quest.State == QuestState.Achieved)
                {
                    try
                    {
                        // several item rewards are a choice, pick one at random like a player would
                        var itemRewards = quest.Info.Rewards.Values.Where(r => r.Quest2RewardTypeId == 1u).ToList();
                        ushort pick = itemRewards.Count > 1 ? (ushort)itemRewards[BotManager.Rng.Next(itemRewards.Count)].Id : (ushort)0;
                        me.QuestManager.QuestComplete(quest.Id, pick, false, true);
                        Diagnostics.FailureLedger.Record("bot-quest-progress", $"completed quest {quest.Id}");
                        Feed(me, $"completed quest {quest.Id}");
                        Diagnostics.QuestOutcomes.Completed(quest.Id, quest.Id == forcedHeld);
                        QuestCoverage.OnCompleted(me, quest.Info);
                        log.Info($"Bot {Name} completed quest {quest.Id}.");
                    }
                    catch (Exception exception)
                    {
                        log.Debug($"Bot {Name} could not complete quest {quest.Id}: {exception.Message}");
                    }
                    questStarted.Remove(quest.Id);
                }
                else if (quest.State == QuestState.Accepted
                    && questStarted.TryGetValue(quest.Id, out DateTime started) && DateTime.UtcNow - started > QuestTimeout)
                {
                    try
                    {
                        string stallDetail = string.Join("; ", quest.Select(o => $"objective {o.ObjectiveInfo.Id} type {(uint)o.ObjectiveInfo.Entry.Type} data {o.ObjectiveInfo.Entry.Data} {o.Progress}/{o.MaxProgress}"));
                        Diagnostics.FailureLedger.Record("quest-stalled", $"quest {quest.Id} in world {me.Map?.Entry.Id}", stallDetail);
                        Diagnostics.QuestOutcomes.Stalled(quest.Id, stallDetail);
                        Diagnostics.QuestTruth.NoteStall(me, quest.Id, stallDetail);
                        if (quest.Id == forcedHeld)
                            forcedStall = (ForceSweep.Objectives(quest), stallDetail);
                        if (!quest.CanAbandon())
                        {
                            BotQuests.Park(me, quest.Id);
                            Diagnostics.FailureLedger.Record("bot-quest-parked", $"quest {quest.Id}", "cannot be abandoned (Quest2 flag 0x4000)");
                            Feed(me, $"stuck: quest {quest.Id} no progress, cannot be abandoned, parked ({stallDetail})");
                        }
                        else
                        {
                            Feed(me, $"stuck: quest {quest.Id} no progress, abandoned ({stallDetail})");
                            me.QuestManager.QuestAbandon(quest.Id);
                        }
                    }
                    catch (Exception exception)
                    {
                        log.Debug($"Bot {Name} could not abandon quest {quest.Id}: {exception.Message}");
                    }
                    questStarted.Remove(quest.Id);
                }
            }

            // quests tied to one in the log (they need it accepted, or one of its objectives done) are taken while it is held
            // (a forced test quest, bot quest, stays the only one in the log)
            foreach (QuestInfo tied in forcedHeld != 0 ? BotQuests.CompleteQuestTies(me) : BotQuests.TiedQuests(me))
            {
                if (BotQuests.Held(me).Count >= MaxBotQuests)
                    break;

                try
                {
                    me.QuestManager.QuestAdd(tied);
                    questStarted[(ushort)tied.Entry.Id] = DateTime.UtcNow;
                    Diagnostics.FailureLedger.Record("bot-quest-progress", $"accepted tied quest {tied.Entry.Id}");
                    Feed(me, $"accepted quest {tied.Entry.Id} (tied to a quest in the log)");
                    BotQuests.NoteAttempt((ushort)tied.Entry.Id);
                }
                catch (Exception exception)
                {
                    log.Debug($"Bot {Name} could not accept tied quest {tied.Entry.Id}: {exception.Message}");
                }
            }

            // take a new quest for something standing nearby
            if (BotQuests.Held(me).Count < MaxBotQuests && me.Map != null && forcedHeld == 0 && ForcedQuest == 0)
            {
                me.Map.Search(me.Position, HuntRange, new SearchCheckRange(me.Position, HuntRange, me), out List<GridEntity> nearby);
                QuestInfo pick = BotQuests.Pick(me, nearby.OfType<NonPlayer>().Where(c => !c.IsDead).Select(c => c.CreatureId));
                if (pick != null)
                {
                    try
                    {
                        me.QuestManager.QuestAdd(pick);
                        Diagnostics.FailureLedger.Record("bot-quest-progress", $"accepted quest {pick.Entry.Id}");
                        Feed(me, $"accepted quest {pick.Entry.Id}");
                        BotQuests.NoteAttempt((ushort)pick.Entry.Id);
                        log.Info($"Bot {Name} accepted quest {pick.Entry.Id}.");
                        questStarted[(ushort)pick.Entry.Id] = DateTime.UtcNow;
                    }
                    catch (Exception exception)
                    {
                        log.Debug($"Bot {Name} could not accept quest {pick.Entry.Id}: {exception.Message}");
                    }
                }
            }

            if (ForcedQuest != 0 && ApplyForcedQuest(me))
                return;
            if (forcedHeld != 0)
            {
                if (me.QuestManager.GetActiveQuests().Any(q => q.Id == forcedHeld))
                    goto quests;
                QuestState? left = me.QuestManager.GetQuestState(forcedHeld);
                Feed(me, $"forced quest {forcedHeld} left the log ({left?.ToString() ?? "abandoned"})");
                ForceSweep.Finished(this, me, left == QuestState.Completed ? "completed" : "stalled", forcedStall.Objectives ?? "",
                    left == QuestState.Completed ? "" : $"{forcedStall.Detail ?? left?.ToString() ?? "abandoned"} | last trace: {lastTrace}");
                forcedStall = default;
                forcedHeld = 0;
            }

            // nothing to do: choose a quest first and go to where its targets are, whatever world that is
            // (or every active quest has had no progress for 90 s: local picks keep bots at 1-2 stuck quests and PickAnywhere never ran)
            var activeNow = BotQuests.Held(me);
            bool idle = activeNow.Count == 0
                || activeNow.All(q => questStarted.TryGetValue(q.Id, out DateTime s) && DateTime.UtcNow - s > TimeSpan.FromSeconds(90));
            if (idle && questAssignTimer <= 0d && me.Map != null)
            {
                questAssignTimer = 15d;
                QuestInfo far = BotQuests.PickAnywhere(me, out ushort farWorld, out Vector3 farSpawn);
                if (far != null)
                {
                    try
                    {
                        me.QuestManager.QuestAdd(far);
                        questStarted[(ushort)far.Entry.Id] = DateTime.UtcNow;
                        Diagnostics.FailureLedger.Record("bot-quest-progress", $"accepted quest {far.Entry.Id}");
                        Feed(me, $"accepted quest {far.Entry.Id}, travelling to world {farWorld}");
                        BotQuests.NoteAttempt((ushort)far.Entry.Id);
                        log.Info($"Bot {Name} accepted quest {far.Entry.Id} and travels to world {farWorld}.");

                        if (farWorld == 0)
                            return; // only synthetic objectives, nothing to walk to

                        var worldEntry = NexusForever.Shared.GameTable.GameTableManager.Instance.World.GetEntry(farWorld);
                        Diagnostics.FailureLedger.Record("bot-quest-world-type", $"{(worldEntry != null ? ((NexusForever.WorldServer.Game.Map.Static.MapType)worldEntry.Type).ToString() : "unknown")}", $"quest {far.Entry.Id} world {farWorld}");

                        questTeleportTimer = 45d + BotManager.Rng.NextDouble() * 45d;
                        home = farSpawn;
                        me.TeleportTo(farWorld, farSpawn.X, farSpawn.Y, farSpawn.Z);
                        return;
                    }
                    catch (Exception exception)
                    {
                        log.Debug($"Bot {Name} could not start quest {far.Entry.Id}: {exception.Message}");
                    }
                }
                else if (!IsClean && BotQuests.SpawnIndexReady && MarkExhausted((byte)me.Level) && BotQuests.SuggestLevel(me, exhaustedLevels) is byte suggested and > 0)
                {
                    Feed(me, $"no quests left at level {me.Level}, going to level {suggested}");
                    Diagnostics.FailureLedger.Record("bot-level-rotation", $"level {me.Level} to {suggested}");
                    me.XpManager.SetLevel(suggested);
                }

                // rotation lowers the level where the bot stands: an idle bot among creatures far above it leaves for a spot of its level
                if (Role != BotRole.Dungeon && me.Map is not DungeonMapInstance && OutLeveledHere(me, out uint aboveLevel)
                    && BotQuests.TryFindLevelSpot(me, out ushort spotWorld, out Vector3 spot))
                {
                    Feed(me, $"creatures here are level {aboveLevel}, too high for level {me.Level}: moving to world {spotWorld}");
                    Diagnostics.FailureLedger.Record("bot-level-zone-move", $"creatures {aboveLevel / 10 * 10}s, bot {me.Level / 10 * 10}s", $"world {me.Map.Entry.Id} to {spotWorld}");
                    home = spot;
                    me.TeleportTo(spotWorld, spot.X, spot.Y, spot.Z);
                    return;
                }
            }

        quests:
            questTargets     = BotQuests.ActiveTargets(me);
            activateTargets  = BotQuests.ActiveActivateTargets(me);
            talkTargets      = BotQuests.ActiveTalkTargets(me);
            killTargets      = new HashSet<uint>(questTargets);
            killTargets.ExceptWith(activateTargets);
            activating       = activateTargets.Count > 0 && killTargets.Count == 0;

            if (questTargets.Count == 0 || me.Map == null)
            {
                if (forcedHeld != 0)
                {
                    Quest held = me.QuestManager.GetActiveQuests().FirstOrDefault(q => q.Id == forcedHeld);
                    if (held != null)
                        Trace(me, $"no creature targets; open objectives: {string.Join("; ", held.Where(o => !o.IsComplete()).Select(o => $"{o.ObjectiveInfo.Id} type {(uint)o.ObjectiveInfo.Type} data {o.ObjectiveInfo.Entry.Data} {o.Progress}/{o.MaxProgress}{(held.CanUpdateObjective(o) ? "" : " waits")}"))}");
                }
                return;
            }

            Trace(me, $"targets kill [{string.Join(",", killTargets.Take(8))}] activate [{string.Join(",", activateTargets.Take(8))}] talk [{string.Join(",", talkTargets.Take(8))}] in world {me.Map.Entry.Id}");

            // objects to activate (flares, levers, quest items lying around): stand next to the nearest one and use it
            if (activateTargets.Count > 0 && ActivateNearby(me))
                return;

            if (questTeleportTimer > 0d)
                return;

            // nothing to kill or use in reach: go to where the targets spawn
            me.Map.Search(me.Position, QuestHuntRange, new SearchCheckRange(me.Position, QuestHuntRange, me), out List<GridEntity> around);
            var aroundIds = around.OfType<WorldEntity>().Where(c => c is not Player && !(c is UnitEntity { IsDead: true })).Select(c => c.CreatureId).ToHashSet();

            // a quest without progress for 2 minutes whose targets are not here gets the trip, even when another quest keeps the bot busy here
            // (79 of 103 stall events in a 60-bot run happened in a world where none of the quest's open targets spawn, e.g. quest 7869 in world 51,
            // its Climate Beacons only spawn in world 1421)
            Quest behind = BotQuests.Held(me)
                .Where(q => q.State == QuestState.Accepted && questStarted.TryGetValue(q.Id, out DateTime since) && DateTime.UtcNow - since > QuestBehind)
                .OrderBy(q => questStarted[q.Id])
                .FirstOrDefault();
            HashSet<uint> behindTargets = behind != null ? BotQuests.QuestTargets(me, behind) : null;
            if (behindTargets is { Count: > 0 } && !behindTargets.Overlaps(aroundIds))
            {
                questTeleportTimer = 45d + BotManager.Rng.NextDouble() * 45d;
                Trace(me, $"quest {behind.Id} behind, its targets [{string.Join(",", behindTargets.Take(8))}] are not within {QuestHuntRange} m");
                if (BotQuests.TryFindSpawn(me, behindTargets, out Vector3 behindSpawn))
                {
                    me.TeleportTo(me.Map.Entry, behindSpawn, (me.Map as MapInstance)?.InstanceId);
                    home = behindSpawn;
                    return;
                }

                if (me.Map is not MapInstance && BotQuests.TryFindSpawnAnywhere(behindTargets, out ushort behindWorld, out Vector3 behindWorldSpawn))
                {
                    Diagnostics.FailureLedger.Record("bot-quest-travel-behind", $"quest {behind.Id}", $"world {me.Map.Entry.Id} to {behindWorld}");
                    me.TeleportTo(behindWorld, behindWorldSpawn.X, behindWorldSpawn.Y, behindWorldSpawn.Z);
                    home = behindWorldSpawn;
                    return;
                }
            }

            if (questTargets.Overlaps(aroundIds))
                return;

            questTeleportTimer = 45d + BotManager.Rng.NextDouble() * 45d;
            if (BotQuests.TryFindSpawn(me, questTargets, out Vector3 spawn))
            {
                Trace(me, $"teleporting to a target spawn at {spawn.X:0} {spawn.Y:0} {spawn.Z:0}");
                me.TeleportTo(me.Map.Entry, spawn, (me.Map as MapInstance)?.InstanceId);
                home = spawn;
            }
            else if (me.Map is not MapInstance && BotQuests.TryFindSpawnAnywhere(questTargets, out ushort spawnWorld, out Vector3 worldSpawn))
            {
                me.TeleportTo(spawnWorld, worldSpawn.X, worldSpawn.Y, worldSpawn.Z);
                home = worldSpawn;
            }
        }

        /// <summary>
        /// Test command: drop every active quest, take the forced one at its level and travel to its targets. True when the think is used up.
        /// </summary>
        private bool ApplyForcedQuest(Player me)
        {
            ushort questId = ForcedQuest;
            ForcedQuest = 0;
            QuestInfo info = GlobalQuestManager.Instance.GetQuestInfo(questId);
            if (info == null || me.Map == null)
                return false;

            foreach (Quest quest in me.QuestManager.GetActiveQuests().ToList())
            {
                // quests that cannot be abandoned (Quest2 flag 0x4000) stay in the log: parked, so they do not steer the test
                if (quest.Id == questId)
                    continue;
                if (!quest.CanAbandon())
                    BotQuests.Park(me, quest.Id);
                else
                    try { me.QuestManager.QuestAbandon(quest.Id); } catch (Exception) { }
                questStarted.Remove(quest.Id);
            }

            long level = BotQuests.TestLevel(info);
            if (level != me.Level)
            {
                me.XpManager.SetLevel((byte)level);
                Diagnostics.QuestTruth.Grant(me, "level-set");
            }

            // what the normal picker would say to this quest for this bot at the test level (the forced path skips it)
            string verdict;
            try
            {
                verdict = me.QuestManager.CanAcceptWithoutGiver(info) ? "candidate" : Game.Entity.QuestManager.LastRefusal ?? "?";
            }
            catch (Exception exception)
            {
                verdict = "error " + exception.Message;
            }

            BotQuests.Unpark(me, questId);
            BotGear.MakeRoom(me, (uint)info.Entry.PushedItemIds.Count(i => i != 0u) + 1u);
            try
            {
                // a held quest that cannot be abandoned stays as it is
                if (!me.QuestManager.GetActiveQuests().Any(q => q.Id == questId))
                    me.QuestManager.QuestAdd(info);
            }
            catch (Exception exception)
            {
                Feed(me, $"forced quest {questId} could not be added: {exception.Message}");
                ForceSweep.Finished(this, me, "not-takeable", "", "QuestAdd threw: " + exception.Message);
                return false;
            }

            if (!me.QuestManager.GetActiveQuests().Any(q => q.Id == questId) && me.QuestManager.GetQuestState(questId) != QuestState.Completed)
            {
                ForceSweep.Finished(this, me, "not-takeable", "", $"QuestAdd left it out of the log (inventory slots {me.Inventory.GetInventorySlotsRemaining(Game.Entity.Static.InventoryLocation.Inventory)})");
                return false;
            }

            ForceSweep.Applied(this, questId, verdict);
            lastTrace = null;

            forcedHeld = questId;
            questStarted[questId] = DateTime.UtcNow;
            Diagnostics.FailureLedger.Record("bot-forced-quest", $"quest {questId}", Name);
            Feed(me, $"forced quest {questId} (test), level {me.Level}");
            if (BotQuests.TryFindSpawnAnywhere(BotQuests.IndexedTargets(questId), out ushort world, out Vector3 spawn))
            {
                Feed(me, $"forced quest {questId}: travelling to world {world} {spawn.X:0} {spawn.Y:0} {spawn.Z:0}");
                questTeleportTimer = 45d;
                home = spawn;
                me.TeleportTo(world, spawn.X, spawn.Y, spawn.Z);
            }

            return true;
        }

        /// <summary>
        /// Walk up to the nearest object a quest needs activated and use it. Returns true when there was one in reach.
        /// </summary>
        private bool ActivateNearby(Player me)
        {
            me.Map.Search(me.Position, QuestHuntRange, new SearchCheckRange(me.Position, QuestHuntRange, me), out List<GridEntity> around);
            WorldEntity target = around
                .OfType<WorldEntity>()
                .Where(c => c is not Player && !(c is UnitEntity { IsDead: true }) && activateTargets.Contains(c.CreatureId))
                .OrderBy(c => Vector3.Distance(me.Position, c.Position))
                .FirstOrDefault();

            if (target == null)
                return false;

            // reach is horizontal: a creature on a platform or a prop floor the bot's teleport does not land on stayed 6-8 m away in 3D forever
            // (forced quest 3901: creature 13717 at Y -876, "walking to activate creature 13717 (8 m)" for 6 minutes)
            // (a second try at the same creature within 15 m activates from there: forced quest 3901 stayed 8 m from creature 13717 after every teleport)
            float flat = Vector2.Distance(new Vector2(me.Position.X, me.Position.Z), new Vector2(target.Position.X, target.Position.Z));
            bool retry = target.Guid == lastWalkGuid && flat < 15f;
            lastWalkGuid = target.Guid;
            if (!retry && (flat > 2.5f || MathF.Abs(me.Position.Y - target.Position.Y) > 12f))
            {
                Trace(me, $"walking to activate creature {target.CreatureId} ({Vector3.Distance(me.Position, target.Position):0} m)");
                me.TeleportTo(me.Map.Entry, target.Position, (me.Map as MapInstance)?.InstanceId);
            }
            else if (talkTargets.Contains(target.CreatureId))
            {
                Trace(me, $"talking to creature {target.CreatureId}");
                me.QuestManager.OnCreatureTalked(target.CreatureId);
                Diagnostics.FailureLedger.Record("bot-quest-progress", "talked to an npc");
            }
            else
            {
                bool cast = EntityHandler.ActivateCast(me, target);
                Trace(me, $"activating creature {target.CreatureId}: {(cast ? "cast" : "not cast (cooldown or prerequisite)")}");
            }

            return true;
        }

        private double bossCheckTimer;

        private bool Hunt(Player me)
        {
            uint max = me.MaxHealth;

            // hurt: stop fighting and recover, health regenerates slowly while out of combat
            if (max > 0u && me.Health < max * 0.5d)
            {
                me.TargetGuid = 0u;
                huntTimer    -= ThinkInterval;
                if (huntTimer <= 0d)
                {
                    huntTimer = 3d;
                    me.Heal(max / 20u);
                }
                return false;
            }

            WorldEntity target = me.TargetGuid != 0u ? me.GetVisible<WorldEntity>(me.TargetGuid) : null;

            // dungeon teams focus the encounter boss: adds and hazard units a boss summons (Trogun's Fissures, 1.4 M health each on veteran)
            // kept the team busy while the boss stood at 70 %; every few seconds a bot on an add looks for a boss in reach
            bool switchToBoss = false;
            if (Role == BotRole.Dungeon && target is NonPlayer { IsDead: false } current && !Game.Encounters.EncounterManager.Instance.IsBoss(current))
            {
                bossCheckTimer -= ThinkInterval;
                if (bossCheckTimer <= 0d)
                {
                    bossCheckTimer = 6d;
                    me.Map.Search(me.Position, HuntRange, new SearchCheckRange(me.Position, HuntRange, me), out List<GridEntity> near);
                    switchToBoss = near.OfType<NonPlayer>().Any(c => !c.IsDead && c.Health > 0u && me.GetDispositionTo(c.Faction1) == Disposition.Hostile
                        && Game.Encounters.EncounterManager.Instance.IsBoss(c));
                    if (switchToBoss)
                        huntTimer = 0d;
                }
            }

            if (switchToBoss || target is not NonPlayer { IsDead: false } || Vector3.Distance(me.Position, target.Position) > 65f)
            {
                me.TargetGuid = 0u;

                // look for a new victim now and then, not every think
                huntTimer -= ThinkInterval;
                if (huntTimer > 0d)
                    return false;
                huntTimer = 2d + BotManager.Rng.NextDouble() * 4d;

                float range = killTargets.Count > 0 ? QuestHuntRange : HuntRange;
                me.Map.Search(me.Position, range, new SearchCheckRange(me.Position, range, me), out List<GridEntity> nearby);
                target = nearby
                    .OfType<NonPlayer>()
                    .Where(c => !c.IsDead && c.Health > 0u && (killTargets.Count > 0
                        ? killTargets.Contains(c.CreatureId)
                        : me.GetDispositionTo(c.Faction1) == Disposition.Hostile))
                    .OrderBy(c => Role == BotRole.Dungeon && Game.Encounters.EncounterManager.Instance.IsBoss(c) ? 0 : 1)
                    .ThenBy(c => Vector3.Distance(me.Position, c.Position))
                    .FirstOrDefault();

                if (target == null)
                {
                    // forced test bots only: say why no kill target was engaged (nearest one within 300 m and its state)
                    if (forcedHeld != 0 && killTargets.Count > 0)
                    {
                        me.Map.Search(me.Position, 300f, new SearchCheckRange(me.Position, 300f, me), out List<GridEntity> wide);
                        NonPlayer far = wide.OfType<NonPlayer>().Where(c => killTargets.Contains(c.CreatureId)).OrderBy(c => Vector3.Distance(me.Position, c.Position)).FirstOrDefault();
                        Trace(me, far == null ? $"no kill target [{string.Join(",", killTargets.Take(8))}] within 300 m"
                            : $"kill target {far.CreatureId} at {Vector3.Distance(me.Position, far.Position):0} m (dy {far.Position.Y - me.Position.Y:0}), dead {far.IsDead}, health {far.Health}/{far.MaxHealth}, faction {far.Faction1}");
                    }
                    return false;
                }

                Trace(me, $"attacking creature {target.CreatureId}");
                me.TargetGuid = target.Guid;
            }

            float distance = Vector3.Distance(me.Position, target.Position);

            // forced test bots: every 30 s of a fight say how it goes (a kill target that never dies stalled 14 quests in the forced sweep)
            if (forcedHeld != 0 && target is UnitEntity fought)
            {
                fightTraceTimer -= ThinkInterval;
                if (fightTraceTimer <= 0d)
                {
                    fightTraceTimer = 30d;
                    Trace(me, $"fighting creature {fought.CreatureId}: health {fought.Health}/{fought.MaxHealth}, {distance:0} m (dy {fought.Position.Y - me.Position.Y:0}), "
                        + $"faction {fought.Faction1} {me.GetDispositionTo(fought.Faction1)}, last refused cast {Game.Spell.Spell.LastFailure?.ToString() ?? "none"}");
                    Game.Spell.Spell.LastFailure = null;
                }
            }

            // a gathering node (quest item source): use the harvesting tool on it instead of fighting it
            if (target is HarvestUnit node)
            {
                if (distance > 6f)
                    me.TeleportTo(me.Map.Entry, node.Position + new Vector3(1.5f, 0f, 1.5f), (me.Map as MapInstance)?.InstanceId);
                else if (!BotTradeskills.Harvest(me, node))
                {
                    killTargets.Remove(node.CreatureId);
                    me.TargetGuid = 0u;
                }
                return true;
            }

            // a friendly kill target: cast the quest's FactionSet spell on it first (quest script, e.g. "Q7658 - Reading Vitals"), two tries per target
            if (target is NonPlayer quarry && me.GetDispositionTo(quarry.Faction1) != Game.Reputation.Static.Disposition.Hostile && TryFactionSpell(me, quarry, distance))
                return true;

            if (distance <= AttackRange && CastFirst(me, spell => !IsHeal(spell), target))
                return true;

            // out of range or everything on cooldown: close in
            if (distance > 8f && followTimer <= 0d)
            {
                me.MovementManager.Follow(target, 5f);
                followTimer = 1d;
            }

            return true;
        }

        private void ThinkCompanion(Player me)
        {
            Player owner = CharacterManager.Instance.GetPlayer(OwnerName);
            if (owner?.Map == null)
                return;

            KeepGroup(me, owner);

            if (!levelSynced && owner.Level > me.Level)
            {
                me.XpManager.SetLevel((byte)Math.Min(owner.Level, 50u));
                levelSynced = true;
            }
            else if (owner.Level <= me.Level)
                levelSynced = true;

            if (summonRequested)
            {
                summonRequested = false;
                teleportTimer   = 0d;
                if (owner.Map != me.Map || Vector3.Distance(me.Position, owner.Position) > 5f)
                    me.TeleportTo(owner.Map.Entry, owner.Position, (owner.Map as MapInstance)?.InstanceId);
                return;
            }

            // a bot told to stay holds its position (it still fights whatever comes close)
            if (Mode == BotMode.Stay)
            {
                if (owner.Map == me.Map)
                    Assist(me, owner);
                return;
            }

            // follow the owner into other maps, including instances (dungeons, housing)
            if (owner.Map != me.Map)
            {
                if (teleportTimer <= 0d)
                {
                    me.TeleportTo(owner.Map.Entry, owner.Position, (owner.Map as MapInstance)?.InstanceId);
                    teleportTimer = 5d;
                }
                return;
            }

            float distance = Vector3.Distance(me.Position, owner.Position);
            if (distance > TeleportRange)
            {
                if (teleportTimer <= 0d)
                {
                    me.TeleportTo(owner.Map.Entry, owner.Position, (owner.Map as MapInstance)?.InstanceId);
                    teleportTimer = 5d;
                }
                return;
            }

            if (distance > FollowStartRange && followTimer <= 0d)
            {
                me.MovementManager.Follow(owner, FollowDistance);
                followTimer = 1d;
            }

            if (Mode != BotMode.Passive)
                Assist(me, owner);
        }

        private void Assist(Player me, Player owner)
        {
            // heal the owner when below 60% health
            uint ownerMaxHealth = owner.MaxHealth;
            if (ownerMaxHealth > 0u && owner.Health > 0u && owner.Health < ownerMaxHealth * 0.6d && CastFirst(me, IsHeal, owner))
                return;

            uint targetGuid = owner.TargetGuid;

            // nothing targeted: defend against anything that is attacking the owner or the bot
            if (targetGuid == 0u && CombatConfig.Enabled)
            {
                me.Map.Search(me.Position, AttackRange, new SearchCheckRange(me.Position, AttackRange, me), out List<GridEntity> nearby);
                NonPlayer attacker = nearby
                    .OfType<NonPlayer>()
                    .Where(c => !c.IsDead && (c.Threat == owner || c.Threat == me))
                    .OrderBy(c => Vector3.Distance(me.Position, c.Position))
                    .FirstOrDefault();
                targetGuid = attacker?.Guid ?? 0u;
            }

            if (targetGuid == 0u)
                return;

            WorldEntity target = me.GetVisible<WorldEntity>(targetGuid);
            if (target == null || target is Player || target.Health == 0u || target is UnitEntity { IsDead: true })
                return;

            if (me.GetDispositionTo(target.Faction1) != Disposition.Hostile)
                return;

            float distance = Vector3.Distance(me.Position, target.Position);
            if (distance > AttackRange)
                return;

            if (CastFirst(me, spell => !IsHeal(spell), target))
                return;

            // nothing was in range, close in on the target (unless told to stay)
            if (Mode != BotMode.Stay && distance > 8f && followTimer <= 0d)
            {
                me.MovementManager.Follow(target, 5f);
                followTimer = 1d;
            }
        }

        /// <summary>The median level of the living hostile creatures within 60 m is more than 10 above the bot (at least 3 of them).</summary>
        private static bool OutLeveledHere(Player me, out uint level)
        {
            level = 0u;
            me.Map.Search(me.Position, 60f, new SearchCheckRange(me.Position, 60f, me), out List<GridEntity> near);
            List<uint> levels = near.OfType<NonPlayer>()
                .Where(c => !c.IsDead && c.Health > 0u && me.GetDispositionTo(c.Faction1) == Disposition.Hostile)
                .Select(c => c.Level)
                .OrderBy(l => l)
                .ToList();
            if (levels.Count < 3)
                return false;
            level = levels[levels.Count / 2];
            return level > me.Level + 10u;
        }

        /// <summary>
        /// Turn towards the target before a cast: player telegraphs are tested against the caster's rotation (the client sends no targets,
        /// only its movement), and bots never update it by moving. Telegraph forward for yaw r is (-sin r, -cos r) (Telegraph.RotatePoint).
        /// </summary>
        private static void FaceTarget(Player me, WorldEntity target)
        {
            if (target == me)
                return;
            float dx = target.Position.X - me.Position.X;
            float dz = target.Position.Z - me.Position.Z;
            if (dx * dx + dz * dz < 0.01f)
                return;
            me.Rotation = new Vector3(MathF.Atan2(-dx, -dz), me.Rotation.Y, me.Rotation.Z);
        }

        /// <summary>Every 30 s: where the class resource stands (ledger resource-sample: empty, partial or full), the resource economy check.</summary>
        private static void SampleResource(Player me)
        {
            uint slot = me.Class switch { Class.Stalker => 3u, Class.Spellslinger => 4u, _ => 1u };
            float value = me.GetResource(slot);
            float max = me.GetVitalMax(5u + slot);
            string where = value <= 0f ? "empty" : max > 0f && value >= max ? "full" : "partial";
            Diagnostics.FailureLedger.Record("resource-sample", $"{me.Class} Resource{slot} {where}{(me.InCombat ? " in combat" : "")}", $"max {max}");
        }

        private static bool IsHeal(CharacterSpell spell)
        {
            return spell.SpellInfo.Effects != null
                && spell.SpellInfo.Effects.Any(e => e.EffectType == (uint)SpellEffectType.Heal);
        }

        /// <summary>
        /// Cast the first equipped ability (in action set order) that matches the filter, is off cooldown and in range.
        /// </summary>
        private const uint ClassAbilitySpellClass = 40u;

        /// <summary>0 class spender, 1 other class ability, 2 anything else (CastFirst order).</summary>
        private static int AbilityPass(Player me, CharacterSpell spell)
        {
            if (spell.SpellInfo.BaseInfo?.Entry.SpellClass != ClassAbilitySpellClass)
                return 2;
            // spenders whose cost is not charged (Focus, Suit Power) would always be affordable and take every cast: they rank as builders
            return Game.Spell.InnateCost.IsResourceGated(me, spell.SpellInfo.Entry) ? 0 : 1;
        }

        /// <summary>
        /// A pet the owner has not set a stance for gets one through the client's stance request (ClientPetStanceChange), a flag picked by
        /// the pet's guid, so every flag is exercised over a run.
        /// </summary>
        private static void SetPetStance(Player me)
        {
            foreach (NonPlayer pet in Game.Spell.Spell.GetPets(me.Guid))
            {
                if (pet.PetStance != 0)
                    continue;
                byte stance = (byte)(1 << (int)(pet.Guid % 5u));
                Network.Message.Handler.PetHandler.HandleClientPetStanceChange(me.Session, BotPackets.Build<Network.Message.Model.ClientPetStanceChange>(w =>
                {
                    w.Write(pet.Guid);
                    w.Write(stance, 5u);
                }));
            }
        }

        private bool CastFirst(Player me, Func<CharacterSpell, bool> filter, WorldEntity target)
        {
            if (busyTimer > 0d || me.SpellManager.GetGlobalSpellCooldown() > 0d)
                return false;

            SetPetStance(me);

            float distance = Vector3.Distance(me.Position, target.Position);

            // Every learned ability (the Ability bag, not only its first 8 slots, which held no builder for Engineers and Espers), in three
            // passes: class spenders gated by a charged resource (Spell4 innate cost or caster innate requirement), the other class abilities (Spell4Base.spellClass 40,
            // builders), then the rest. Class auto attacks such as 26434 "Unarmed Auto Attack" and 960 "Psyblade Strike" have spellClass 0
            // and no cooldown, so in slot order they took every global cooldown and the class resources never moved.
            List<Item> abilities = me.Inventory.GetItems(InventoryLocation.Ability).ToList();
            for (int pass = 0; pass < 3; pass++)
            foreach (Item item in abilities)
            {
                uint slot = item.BagIndex;
                CharacterSpell spell = me.SpellManager.GetSpell(item.Id);
                if (spell == null || !filter(spell) || AbilityPass(me, spell) != pass)
                    continue;
                // non-class spells keep the old rule, first 8 slots only (the bag also holds recalls, mounts and quest spells)
                if (pass == 2 && slot >= 8u)
                    continue;

                Spell4Entry entry = spell.SpellInfo.Entry;
                // with its pet alive a summon ability casts the pet's ability, which has its own cooldown and range (the summon's own
                // cooldown runs from the summon and does not block the switch)
                if (spell.PetSwitch() is Game.Spell.SpellInfo petSwitch)
                    entry = petSwitch.Entry;
                if (me.SpellManager.GetSpellCooldown(entry.Id) > 0d)
                    continue;

                // a knocked down, stunned, disarmed or silenced bot waits like a player (Spell4CCConditions, the server refuses the cast)
                if (!Game.Spell.SpellCCConditions.Allows(me, spell.SpellInfo.CasterCCConditions))
                    continue;

                // abilities with charges fail every cast until one has recharged (83,000 wasted attempts in one run)
                if (spell.MaxAbilityCharges > 0u && spell.AbilityCharges == 0u)
                    continue;

                // spenders wait for the builders to fill the class resource (Spell4 innate cost, Game/Spell/InnateCost.cs)
                if (!Game.Spell.InnateCost.CanPay(me, entry))
                {
                    Diagnostics.FailureLedger.Record("innate-cost", "bot waits: class resource too low", $"class {me.Class}");
                    continue;
                }

                if (blockedSpells.TryGetValue(entry.Id, out double blockedUntil) && blockedUntil > clock)
                    continue;

                if (entry.TargetMaxRange > 0f && distance > entry.TargetMaxRange)
                    continue;

                // the cast would be refused by its caster prerequisite (campfire in a dungeon, a mount without the skill): check what the server checks, retry in 5 s
                if (entry.PrerequisiteIdCasterCast != 0u && !Game.Prerequisite.PrerequisiteManager.Instance.Meets(me, entry.PrerequisiteIdCasterCast, true))
                {
                    blockedSpells[entry.Id] = clock + 5d;
                    continue;
                }

                me.TargetGuid = target.Guid;
                FaceTarget(me, target);
                Diagnostics.FailureLedger.Record("bot-cast", $"{me.Class} slot {slot} spell {entry.Id}");
                try
                {
                    spell.Cast();
                }
                catch (Exception exception)
                {
                    log.Debug($"Bot {Name} could not cast slot {slot}: {exception.Message}");
                    continue;
                }

                // do not start another cast until this one is done (cast time is in milliseconds)
                busyTimer = entry.CastTime / 1000d + 0.25d;
                return true;
            }

            return false;
        }
    }
}
