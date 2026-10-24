using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared;
using NexusForever.Shared.Database;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Reputation.Static;
using NLog;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// NF_BOTS_DUNGEON=world with NF_BOTS_TEAM=n: ambient bots form teams of n (5 for dungeons, 20 for raids), make a server side group, enter
    /// the instance of that group, clear it (trash and bosses with their encounters), leave, wait for the instance to reset and go again.
    /// Entry, clear time, wipes, boss kills and resets are counted in the failure ledger as dungeon-*.
    /// </summary>
    public sealed class DungeonTeams : Singleton<DungeonTeams>
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public static int TeamSize { get; } = int.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_TEAM"), out int n) && n > 0 ? n : 5;
        /// <summary>NF_BOTS_DUNGEON_MINUTES: a run that is not clear after this long leaves (default 30).</summary>
        public static double RunMinutes { get; } = double.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_DUNGEON_MINUTES"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double rm) && rm > 0d ? rm : 30d;
        public static int RaidTeamSize { get; } = int.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_RAID_TEAM"), out int r) && r > 0 ? r : 20;
        public static int RaidTeams { get; } = int.TryParse(Environment.GetEnvironmentVariable("NF_BOTS_RAID_TEAMS"), out int rt) && rt >= 0 ? rt : 1;

        public enum TeamState { Forming, Entering, Clearing, Leaving, Cooldown }

        public sealed class Team
        {
            public int Id;
            public List<Bot> Members = new();
            public TeamState State = TeamState.Forming;
            public double Timer;
            public double StateTime;
            public DateTime Started;
            public ulong InstanceId;
            public int Attempt;
            public int BossKills;
            public int Kills;
            public int Run;
            public ushort World;
            public byte Level;
            public double CheckTimer;
            public double ProgressTimer;
            public double ClearFor;
            public double RetryTimer;
            public double AbsentTime;
            public bool Veteran;
            /// <summary>Entry path of the current run (<see cref="DungeonEntry"/>).</summary>
            public string Mode = DungeonEntry.Teleport;
            /// <summary>The worlds this team runs in turn (raid teams: NF_BOTS_RAID, dungeon teams: NF_BOTS_DUNGEON) and its size.</summary>
            public ushort[] Worlds;
            public int Size;
        }

        private readonly List<Team> teams = new();
        private DateTime mixedWait;

        public Team TeamOf(Bot bot)
        {
            lock (teams)
                return teams.FirstOrDefault(t => t.Members.Contains(bot));
        }

        /// <summary>A creature died in an instance: counted for the team that is inside.</summary>
        public void OnKill(MapInstance map)
        {
            lock (teams)
                foreach (Team team in teams.Where(t => t.State == TeamState.Clearing && t.Members.Any(m => m.Session.Player?.Map == map)))
                    team.Kills++;
        }

        /// <summary>A boss of an instance died: count it for the team that is inside.</summary>
        public void OnBossKilled(MapInstance map)
        {
            lock (teams)
                foreach (Team team in teams.Where(t => t.State == TeamState.Clearing && t.Members.Any(m => m.Session.Player?.Map == map)))
                    team.BossKills++;
        }

        public void Update(double lastTick, IEnumerable<Bot> bots)
        {
            if (BotManager.DungeonWorlds.Length == 0 && BotManager.RaidWorlds.Length == 0)
                return;

            lock (teams)
            {
                // new teams from the ambient bots that are in the world and not in a team
                List<Bot> free = bots.Where(b => b.IsAmbient && b.Role == BotRole.Dungeon && b.State == BotState.InWorld && b.Session.Player?.Map != null && !teams.Any(t => t.Members.Contains(b))).ToList();
                while (true)
                {
                    bool raid = BotManager.RaidWorlds.Length > 0 && teams.Count(t => t.Worlds == BotManager.RaidWorlds) < RaidTeams;
                    if (!raid && BotManager.DungeonWorlds.Length == 0)
                        break;
                    int size = raid ? RaidTeamSize : TeamSize;
                    if (free.Count < size)
                        break;
                    // same faction first: group invites need it (GroupInviteResult.WrongFaction); a mixed team (server members) only after
                    // 90 s without a same-faction one, while bots are still logging in
                    List<Bot> members = free.GroupBy(b => b.Session.Player.Faction1).Where(g => g.Count() >= size)
                        .Select(g => g.Take(size).ToList()).FirstOrDefault();
                    if (members == null)
                    {
                        if (mixedWait == default)
                            mixedWait = DateTime.UtcNow;
                        if ((DateTime.UtcNow - mixedWait).TotalSeconds < 90d)
                            break;
                        members = free.Take(size).ToList();
                    }
                    mixedWait = default;
                    ushort[] worlds = raid ? BotManager.RaidWorlds : BotManager.DungeonWorlds;
                    var team = new Team { Id = teams.Count + 1, Members = members, Worlds = worlds, Size = size };
                    team.World = worlds[teams.Count(t => t.Worlds == worlds) % worlds.Length];
                    free = free.Except(members).ToList();
                    teams.Add(team);
                    log.Info($"Dungeon team {team.Id} formed with {team.Members.Count} bots.");
                }

                foreach (Team team in teams)
                    UpdateTeam(team, lastTick, team.World);
            }
        }

        private static IEnumerable<Player> Players(Team team) => team.Members.Select(m => m.Session.Player).Where(p => p?.Map != null);

        private void UpdateTeam(Team team, double lastTick, ushort world)
        {
            team.StateTime += lastTick;
            List<Player> players = Players(team).ToList();
            if (players.Count < team.Members.Count)
            {
                // a member is logging in or out; one that stays away (failed bot, lost session) must not hold the team forever
                team.AbsentTime += lastTick;
                if (team.AbsentTime < 60d || players.Count == 0)
                    return;
                if (team.AbsentTime - lastTick < 60d)
                    Diagnostics.FailureLedger.Record("dungeon-member-missing", $"world {world}", $"{team.Members.Count - players.Count} of {team.Members.Count} not in a map for 60 s, state {team.State}; the team goes on without them");
            }
            else
                team.AbsentTime = 0d;

            switch (team.State)
            {
                case TeamState.Forming:
                {
                    // a world without a map file cannot be created, the teleport would throw inside the map update
                    var worldEntry = GameTableManager.Instance.World.GetEntry(world);
                    if (worldEntry == null)
                    {
                        Diagnostics.FailureLedger.Record("dungeon-no-map", $"world {world}", worldEntry?.AssetPath ?? "unknown world");
                        team.State = TeamState.Cooldown;
                        team.StateTime = 0d;
                        break;
                    }

                    team.Veteran = VeteranRun(world, team.Run + 1);
                    team.Level = DungeonLevel(world, out int medianLevel, out int bossLevel);
                    if (team.Veteran)
                        bossLevel = team.Level = VeteranLevel(world);
                    else if (Environment.GetEnvironmentVariable("NF_BOTS_TEAM_LEVEL") == "quest" && OpenQuestLevel(world, team.Run) is byte questLevel)
                        team.Level = questLevel;
                    Diagnostics.FailureLedger.Record("dungeon-level-gap", $"world {world} team level {team.Level} vs boss level {bossLevel}, median creature level {medianLevel}", $"gap {team.Level - bossLevel}");
                    foreach (Player player in players)
                        if (player.Level != team.Level)
                            player.XpManager.SetLevel(team.Level);

                    foreach (Player player in players)
                        if (GroupManager.Instance.GetGroup(player.CharacterId) != null)
                            GroupManager.Instance.Leave(player, GroupManager.Instance.GetGroup(player.CharacterId).Id, false);

                    PlayerGroup group = DungeonEntry.FormGroup(players, team.Size, world);
                    team.InstanceId = (1ul << 62) | group.Id;
                    // the leader picks the difficulty through the client message (ClientSetInstanceSettings), it applies to the new instance
                    Player leader = players.FirstOrDefault(p => p.CharacterId == group.LeaderId) ?? players[0];
                    Network.Message.Handler.InstanceHandler.HandleSetInstanceSettings(leader.Session, BotPackets.Build<Network.Message.Model.ClientSetInstanceSettings>(w =>
                    {
                        w.Write(0u);
                        w.Write(team.Veteran ? (byte)InstanceDifficulty.Veteran : (byte)InstanceDifficulty.Normal, 2u);
                        w.Write((byte)0);
                        w.Write((ushort)0, 11u);
                    }));
                    team.BossKills = 0;
                    team.Kills = 0;
                    team.Run++;
                    team.Mode = DungeonEntry.ChooseMode(world, players, team.Run);
                    if (team.Mode == DungeonEntry.Finder && !DungeonEntry.QueueAsGroup(players, leader, world))
                        team.Mode = DungeonEntry.PortalTo(world) != null ? DungeonEntry.Portal : DungeonEntry.Teleport;
                    team.State = TeamState.Entering;
                    team.StateTime = 0d;
                    team.RetryTimer = 0d;
                    break;
                }
                case TeamState.Entering:
                {
                    bool inside = players.All(p => p.Map is DungeonMapInstance m && m.Entry.Id == world);
                    if (inside)
                    {
                        team.State = TeamState.Clearing;
                        team.StateTime = 0d;
                        team.ClearFor = 0d;
                        if (team.Started == default)
                            team.Started = DateTime.UtcNow;
                        Diagnostics.FailureLedger.Record("dungeon-entered", $"world {world}{Mode(team)} team of {players.Count} by {team.Mode}", $"run {team.Run}, attempt {team.Attempt + 1}, level {team.Level}");
                        if (players[0].Map is DungeonMapInstance instance && (instance.Difficulty == InstanceDifficulty.Veteran) != team.Veteran)
                            Diagnostics.FailureLedger.Record("dungeon-difficulty-mismatch", $"world {world}", $"team chose {(team.Veteran ? "veteran" : "normal")}, instance is {instance.Difficulty}");
                        if (team.Attempt == 0 && players[0].Map is DungeonMapInstance map)
                            GiveInstanceQuests(players, map);
                        break;
                    }

                    // a matched game waits for every member's answer; members that are not inside (just resurrected, teleport pending, first try
                    // failed) take the next entry step every 5 s (portal: go there, then activate it)
                    if (team.Mode == DungeonEntry.Finder)
                        DungeonEntry.AcceptPending(players);
                    team.RetryTimer -= lastTick;
                    if (team.RetryTimer <= 0d)
                    {
                        team.RetryTimer = team.Mode == DungeonEntry.Teleport ? 15d : 5d;
                        Enter(team, players, world);
                    }

                    if (team.StateTime > 180d)
                    {
                        Diagnostics.FailureLedger.Record("dungeon-enter-failed", $"world {world} by {team.Mode}", $"{players.Count(p => p.Map is DungeonMapInstance)} of {players.Count} inside after 3 minutes");
                        team.State = TeamState.Cooldown;
                        team.StateTime = 0d;
                    }
                    break;
                }
                case TeamState.Clearing:
                    UpdateClearing(team, players, world, lastTick);
                    break;
                case TeamState.Leaving:
                {
                    if (players.All(p => p.Map is not DungeonMapInstance) || team.StateTime > 60d)
                    {
                        // the leave paths (group leave + pending removal, LeaveGame) should have taken everyone out
                        foreach (Player stuck in players.Where(p => p.Map is DungeonMapInstance))
                        {
                            Diagnostics.FailureLedger.Record("dungeon-leave-failed", $"world {world} by {team.Mode}", $"{stuck.Name} still inside after 60 s");
                            if (DungeonInstancedMap.GetReturn(stuck.CharacterId) is MapPosition back)
                                stuck.TeleportTo(back);
                        }
                        team.State = TeamState.Cooldown;
                        team.StateTime = 0d;
                    }
                    break;
                }
                case TeamState.Cooldown:
                    // the empty instance unloads after 120 s, a fresh one is created for the next run
                    if (team.StateTime > 150d)
                    {
                        // the next dungeon of the list
                        int at = Array.IndexOf(team.Worlds, team.World);
                        team.World = team.Worlds[(at + 1) % team.Worlds.Length];
                        team.State = TeamState.Forming;
                        team.StateTime = 0d;
                    }
                    break;
            }
        }

        private void Enter(Team team, List<Player> players, ushort world)
        {
            foreach (Player player in players)
                DungeonEntry.EnterStep(team.Mode, player, world, team.InstanceId);
        }

        private void UpdateClearing(Team team, List<Player> players, ushort world, double lastTick)
        {
            if (team.StateTime < 10d)
                return;

            // everyone dead: a wipe, the encounters reset and the team goes in again once the bots have resurrected
            if (players.All(p => p.IsDead))
            {
                Diagnostics.FailureLedger.Record("dungeon-wipe", $"world {world}", $"run {team.Run}, bosses killed {team.BossKills}, {team.StateTime:0} s in");
                team.Attempt++;
                team.State = TeamState.Entering;
                team.StateTime = 0d;
                team.RetryTimer = 0d;
                return;
            }

            // a member that left the instance (died and resurrected outside) walks back in
            if (players.Any(p => !p.IsDead && p.Map is not DungeonMapInstance))
                Enter(team, players, world);

            team.CheckTimer -= lastTick;
            if (team.CheckTimer > 0d)
                return;

            team.CheckTimer = 2d;
            team.ProgressTimer -= 2d;
            if (team.ProgressTimer <= 0d)
            {
                team.ProgressTimer = 30d;
                Diagnostics.FailureLedger.Record("dungeon-progress", $"world {world} alive {players.Count(p => !p.IsDead)} of {players.Count}", $"run {team.Run}, {team.StateTime:0} s in, bosses killed {team.BossKills}, health {string.Join("/", players.Select(p => p.MaxHealth == 0u ? 0 : (int)(p.Health * 100d / p.MaxHealth)))} percent");
            }

            Player any = players.FirstOrDefault(p => !p.IsDead && p.Map is DungeonMapInstance);
            if (any == null)
                return;

            // cleared: nothing hostile and alive left in the instance for 6 s
            any.Map.Search(any.Position, 4000f, new SearchCheckRange(any.Position, 4000f), out List<GridEntity> all);
            var alive = all.OfType<NonPlayer>().Where(c => !c.IsDead && c.VendorInfo == null && any.GetDispositionTo(c.Faction1) == Disposition.Hostile).ToList();
            int hostile = alive.Count;
            // cleared: no boss left after at least one fell (summoned creatures that the spawn data lists as standing around do not count),
            // or nothing hostile at all
            bool bossesDone = team.BossKills > 0 && !alive.Any(c => Game.Encounters.EncounterManager.Instance.IsBoss(c));
            // hostile spawns in grids nobody has been near yet are not spawned, so the search above does not see them (Skullcano Redmoon,
            // Thunderfoot, Tugga): the run is not clear until the bots have been there (ThinkDungeon jumps to them)
            int unloaded = any.Map.GetSpawnModels().Count(m => !any.Map.IsGridActive(new Vector3(m.X, m.Y, m.Z)) && BotQuests.IsHostile(any, m.Creature));
            team.ClearFor = ((hostile == 0 && team.Kills > 0) || bossesDone) && unloaded == 0 ? team.ClearFor + 2d : 0d;
            if (hostile == 0 && team.Kills == 0 && team.StateTime > 60d)
            {
                // nothing to fight: the spawn data of this world has no hostile creature, not a clear
                Diagnostics.FailureLedger.Record("dungeon-empty", $"world {world}", "no hostile creature alive and none killed after 60 s");
                Leave(team, players, world);
                return;
            }
            if (team.ProgressTimer > 27d)
            {
                var bossesAlive = alive.Where(c => Game.Encounters.EncounterManager.Instance.IsBoss(c)).Select(c => c.CreatureId).Distinct().Take(6);
                Diagnostics.FailureLedger.Record("dungeon-remaining", $"world {world} {hostile} hostile alive, {unloaded} in unvisited grids, {alive.Count(c => Game.Encounters.EncounterManager.Instance.IsBoss(c))} with encounters", $"creatures {string.Join(",", alive.GroupBy(c => c.CreatureId).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key + "x" + g.Count()))}; with encounter {string.Join(",", bossesAlive)}");
                // a long run with few creatures left: say why each one is still alive (reach, immunity, health)
                if (team.StateTime > 300d && hostile <= 30)
                    foreach (NonPlayer c in alive.Take(30))
                    {
                        Player nearest = players.Where(p => !p.IsDead && p.Map == c.Map).OrderBy(p => Vector3.Distance(p.Position, c.Position)).FirstOrDefault();
                        float d = nearest == null ? -1f : Vector3.Distance(nearest.Position, c.Position);
                        Diagnostics.FailureLedger.Record("dungeon-stalled", $"world {world} creature {c.CreatureId} health {(c.MaxHealth == 0u ? 0 : c.Health * 10ul / c.MaxHealth * 10ul)}% at {team.StateTime / 60d:0} min",
                            $"health {c.Health}/{c.MaxHealth}, immune {c.IsDamageImmune}, scripted-invulnerable {c.ScriptedInvulnerable}, flags 0x{NexusForever.Shared.GameTable.GameTableManager.Instance.Creature2.GetEntry(c.CreatureId)?.Flags ?? 0u:x}, boss {Game.Encounters.EncounterManager.Instance.IsBoss(c)}, attackers {c.Attackers.Count}, targeted by {players.Count(p => p.TargetGuid == c.Guid)}, nearest bot {d:0} m (dy {(nearest == null ? 0f : c.Position.Y - nearest.Position.Y):0}), at {c.Position.X:0},{c.Position.Y:0},{c.Position.Z:0}, faction {c.Faction1}");
                    }
            }
            if (team.ClearFor >= 6d)
            {
                Diagnostics.FailureLedger.Record("dungeon-cleared", $"world {world}{Mode(team)} team of {players.Count}", $"run {team.Run}, {team.StateTime:0} s, {team.Kills} kills, bosses killed {team.BossKills}, wipes {team.Attempt}");
                Leave(team, players, world);
            }
            else if (team.StateTime > RunMinutes * 60d)
            {
                Diagnostics.FailureLedger.Record("dungeon-timeout", $"world {world}", $"{hostile} hostile left after {RunMinutes:0} minutes, bosses killed {team.BossKills}");
                Leave(team, players, world);
            }
        }

        private static string Mode(Team team) => team.Veteran ? " veteran" : "";

        /// <summary>
        /// NF_BOTS_VETERAN: "alternate" (default) = every second run of a world that has veteran creature rows is veteran, "all" = every run there,
        /// "off" = never. NF_VETERAN_WORLDS still forces veteran instances for listed worlds.
        /// </summary>
        private static bool VeteranRun(ushort world, int run)
        {
            if (!VeteranVariants.HasVeteran(world))
                return false;
            string mode = Environment.GetEnvironmentVariable("NF_BOTS_VETERAN") ?? "alternate";
            return mode == "all" || (mode != "off" && run % 2 == 0);
        }

        /// <summary>Veteran runs are played at the highest level of the world's veteran creature rows (50 for the Prime dungeons).</summary>
        private static byte VeteranLevel(ushort world)
        {
            uint level = DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Where(s => s.World == world)
                .Select(s => VeteranVariants.Get(world, s.Creature, InstanceDifficulty.Veteran)).Where(c => c != 0u)
                .Select(c => GameTableManager.Instance.Creature2.GetEntry(c)?.MaxLevel ?? 0u).DefaultIfEmpty(50u).Max();
            return (byte)Math.Clamp(level, 1u, 50u);
        }

        private static Dictionary<uint, List<Game.Quest.QuestInfo>> questsByTarget;

        /// <summary>
        /// NF_BOTS_TEAM_LEVEL=quest: a level the instance's never completed quests ask for (their pick level, at most 50), so a run can take the
        /// quests the boss level refuses; runs take the distinct levels by turns, the most common first. Null when every instance quest of the
        /// world was completed once.
        /// </summary>
        private static byte? OpenQuestLevel(ushort world, int run)
        {
            EnsureQuestIndex();
            var creatures = DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Where(s => s.World == world).Select(s => s.Creature).ToHashSet();
            var levels = creatures.Where(questsByTarget.ContainsKey).SelectMany(c => questsByTarget[c]).Distinct()
                .Where(q => !Diagnostics.QuestOutcomes.IsCompleted((ushort)q.Entry.Id))
                .Select(q => (int)BotQuests.TestLevel(q)).Where(l => l > 1).ToList();
            if (levels.Count == 0)
                return null;
            // the open quests' levels by turns, the most common first: one run per level until the quests of each level are done
            var byCount = levels.GroupBy(l => l).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).Select(g => g.Key).ToList();
            int level = byCount[run % byCount.Count];
            Diagnostics.FailureLedger.Record("dungeon-quest-level", $"world {world} level {level}", $"{levels.Count} open quests");
            return (byte)Math.Clamp(level, 1, 50);
        }

        /// <summary>
        /// Quests with a kill, talk, activate or interaction objective on a creature this instance spawns (after the veteran swap): each member takes the ones the real
        /// acceptance rules allow (QuestManager.CanAcceptWithoutGiver, QuestAdd), as a player picks up the dungeon quests before going in.
        /// </summary>
        private static void GiveInstanceQuests(List<Player> players, DungeonMapInstance map)
        {
            EnsureQuestIndex();
            ushort world = (ushort)map.Entry.Id;
            var creatures = map.GetSpawnModels().Select(m => VeteranVariants.Get(world, m.Creature, map.Difficulty) is uint v && v != 0u ? v : m.Creature).ToHashSet();
            var quests = creatures.Where(questsByTarget.ContainsKey).SelectMany(c => questsByTarget[c]).Distinct().ToList();
            GiveQuests(players, map, quests);
        }

        /// <summary>
        /// A member completed <paramref name="completed"/> inside the instance: the instance quests that follow it (Quest2 prerequisite quests,
        /// e.g. 6836 "Help Request" -> 6837 "Expedition: Rage Logic") are taken now, as a player takes the next step of the chain.
        /// </summary>
        public static void GiveFollowUps(Player player, DungeonMapInstance map, ushort completed)
        {
            EnsureQuestIndex();
            ushort world = (ushort)map.Entry.Id;
            var creatures = map.GetSpawnModels().Select(m => VeteranVariants.Get(world, m.Creature, map.Difficulty) is uint v && v != 0u ? v : m.Creature).ToHashSet();
            var quests = creatures.Where(questsByTarget.ContainsKey).SelectMany(c => questsByTarget[c]).Distinct()
                .Where(q => q.PrerequisiteQuests.Any(p => p.Id == completed)).ToList();
            if (quests.Count > 0)
                GiveQuests(new List<Player> { player }, map, quests);
        }

        private static void EnsureQuestIndex()
        {
            if (questsByTarget == null)
            {
                var index = new Dictionary<uint, List<Game.Quest.QuestInfo>>();
                foreach (Game.Quest.QuestInfo info in Game.Quest.GlobalQuestManager.Instance.GetAllQuestInfo())
                    foreach (var objective in info.Objectives)
                    {
                        // kills, and the objects and people quest bots use (activate, talk, interactions): a player takes every quest of the instance
                        IEnumerable<uint> targets = objective.Type switch
                        {
                            Game.Quest.Static.QuestObjectiveType.KillCreature or Game.Quest.Static.QuestObjectiveType.TalkTo
                                or Game.Quest.Static.QuestObjectiveType.ActivateEntity or Game.Quest.Static.QuestObjectiveType.ActivateEntity2
                                or Game.Quest.Static.QuestObjectiveType.SucceedCSI => objective.Entry.Data != 0u ? new[] { objective.Entry.Data }
                                    // no creature in data: the one the text names or the quest bots' indicator / reward pane resolution
                                    : QuestCoverage.TextCreatures(objective.Entry),
                            Game.Quest.Static.QuestObjectiveType.KillTargetGroup or Game.Quest.Static.QuestObjectiveType.KillTargetGroups
                                or Game.Quest.Static.QuestObjectiveType.ActivateTargetGroup or Game.Quest.Static.QuestObjectiveType.ActivateTargetGroupChecklist
                                or Game.Quest.Static.QuestObjectiveType.TalkToTargetGroup
                                => Game.Quest.TargetGroupHelper.ResolveAll(objective.Entry.Data),
                            _ => Array.Empty<uint>()
                        };
                        foreach (uint creature in targets)
                        {
                            if (!index.TryGetValue(creature, out var list))
                                index[creature] = list = new List<Game.Quest.QuestInfo>();
                            if (!list.Contains(info))
                                list.Add(info);
                        }
                    }
                questsByTarget = index;
            }
        }

        private static void GiveQuests(List<Player> players, DungeonMapInstance map, List<Game.Quest.QuestInfo> quests)
        {
            ushort world = (ushort)map.Entry.Id;
            foreach (Player player in players)
            {
                int taken = 0;
                foreach (Game.Quest.QuestInfo info in quests)
                {
                    if (!player.QuestManager.CanAcceptWithoutGiver(info))
                    {
                        Diagnostics.FailureLedger.Record("dungeon-quest-refused", $"world {world}{(map.Difficulty == InstanceDifficulty.Veteran ? " veteran" : "")} quest {info.Entry.Id}", QuestManager.LastRefusal ?? "?");
                        continue;
                    }
                    player.QuestManager.QuestAdd(info);
                    taken++;
                    // taken after entering: no zone change will come for an EnterZone objective on the instance's zone, credit it the way the
                    // quest bots do when they are already inside (BotQuests.TryVisitZone)
                    if (info.Objectives.Any(o => o.Type == Game.Quest.Static.QuestObjectiveType.EnterZone))
                        foreach (uint zone in player.ZoneAndParents())
                            player.QuestManager.ObjectiveUpdate(Game.Quest.Static.QuestObjectiveType.EnterZone, zone, 1);
                }
                if (taken > 0)
                    Diagnostics.FailureLedger.Record("dungeon-quest-taken", $"world {world}{(map.Difficulty == InstanceDifficulty.Veteran ? " veteran" : "")}", $"{player.Name} took {taken} of {quests.Count}");
            }
        }

        private void Leave(Team team, List<Player> players, ushort world)
        {
            team.State = TeamState.Leaving;
            team.StateTime = 0d;
            team.Attempt = 0;
            team.Started = default;
            DungeonEntry.Leave(team.Mode, players);
            Diagnostics.FailureLedger.Record("dungeon-left", $"world {world} by {team.Mode}", $"{players.Count(p => p.Map is DungeonMapInstance)} of {players.Count} still in the instance right after leaving");
        }

        /// <summary>
        /// The level the team plays at. NF_BOTS_TEAM_LEVEL: "boss" (default) = the highest level among bosses with an encounter definition (so the bosses are tested at their
        /// own level), "median" = the median level of all creatures of the instance, or a number. At most 50.
        /// </summary>
        private static byte DungeonLevel(ushort world, out int median, out int boss)
        {
            var spawns = DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns().Where(s => s.World == world).ToList();
            var levels = spawns.Select(s => (int)(GameTableManager.Instance.Creature2.GetEntry(s.Creature)?.MaxLevel ?? 0u)).Where(l => l > 0).OrderBy(l => l).ToList();
            median = levels.Count == 0 ? 30 : Math.Clamp(levels[levels.Count / 2], 1, 50);

            boss = 0;
            foreach (uint creature in spawns.Select(s => s.Creature).Distinct())
            {
                var entry = GameTableManager.Instance.Creature2.GetEntry(creature);
                string name = entry == null ? null : GameTableManager.Instance.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English)?.GetEntry(entry.LocalizedTextIdName);
                if (entry != null && Game.Encounters.EncounterManager.Instance.HasDefinition(name, world))
                    boss = Math.Max(boss, (int)entry.MaxLevel);
            }
            boss = Math.Clamp(boss, 0, 50);

            string mode = Environment.GetEnvironmentVariable("NF_BOTS_TEAM_LEVEL") ?? "boss";
            if (int.TryParse(mode, out int fixedLevel))
                return (byte)Math.Clamp(fixedLevel, 1, 50);
            return (byte)(mode == "median" || boss == 0 ? median : boss);
        }
    }
}
