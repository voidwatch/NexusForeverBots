using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;
using Kind = NexusForever.WorldServer.Game.Entity.PathMissionManager.MissionKind;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Path bots (NF_BOTS_PATH_BOTS = n quest bots): play the missions of their own path through the same entry points a client uses: units are used
    /// through ClientActivateUnitCast (EntityHandler), scans and digs are spell casts (Scientist Scanbot Scan 45403, explorer dig 27712), kills are
    /// fights, areas are visits. One mission at a time, given up after 4 minutes without completion (path-bot-gave-up with the step it was on).
    /// </summary>
    public partial class Bot
    {
        public bool PlaysPaths { get; set; }

        private const uint DigSpell = 27712u;
        private uint pathMission;
        private double pathTimer, pathMissionTime;
        private string pathStep = "";
        private readonly HashSet<uint> pathGivenUp = new();
        private readonly HashSet<uint> exploreTried = new();
        private uint exploreLast;
        private uint pathWalkGuid;

        private static readonly Kind[] PlayedKinds = { Kind.Kill, Kind.Activate, Kind.Spell, Kind.Scan, Kind.Area, Kind.Holdout, Kind.Scavenger, Kind.Door, Kind.Hub, Kind.Infrastructure, Kind.Explore, Kind.PowerMap };
        /// <summary>NF_BOTS_PATH_FOCUS=Explore,PowerMap: path bots only pick missions of these kinds (testing one mission type).</summary>
        private static readonly Kind[] FocusKinds = (Environment.GetEnvironmentVariable("NF_BOTS_PATH_FOCUS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => Enum.TryParse(k, true, out Kind kind) ? kind : (Kind?)null).Where(k => k.HasValue).Select(k => k.Value).ToArray();

        private bool ThinkPath(Player me)
        {
            if (me.Map == null || me.IsDead || me.Map is MapInstance)
                return false;

            PathMissionManager missions = me.PathMissionManager;
            if (missions.HoldoutRunning)
            {
                killTargets = missions.HoldoutUnits.Select(u => u.CreatureId).ToHashSet();
                Hunt(me);
                return true;
            }

            pathTimer -= ThinkInterval;
            if (pathTimer > 0d)
                return pathMission != 0u;
            pathTimer = 2d;

            PathMissionManager.MissionDef def = pathMission != 0u ? PathMissionManager.GetMission(pathMission) : null;
            if (def != null && missions.IsCompleted(def.Entry.Id))
            {
                Diagnostics.FailureLedger.Record("path-bot-completed", $"path {def.Entry.PathTypeEnum} type {def.Entry.PathMissionTypeEnum} {def.Kind}", $"mission {def.Entry.Id} in {pathMissionTime:0} s");
                Feed(me, $"path mission {def.Entry.Id} ({def.Kind}) completed");
                def = null;
            }
            else if (def != null && pathMissionTime > 240d + (def.Kind == Kind.Explore ? 3d * def.Count : 0d) && !missions.PowerMapLeader.HasValue)
            {
                Diagnostics.FailureLedger.Record("path-bot-gave-up", $"path {def.Entry.PathTypeEnum} type {def.Entry.PathMissionTypeEnum} {def.Kind}: {pathStep}",
                    $"mission {def.Entry.Id}, progress {missions.GetProgress(def.Entry.Id)}/{def.Count}");
                pathGivenUp.Add(def.Entry.Id);
                def = null;
            }

            if (def == null)
            {
                // every third pick (and whenever no mission is open) a challenge of the zone is played instead
                if (challengeId != 0u || (FocusKinds.Length == 0 && BotManager.Rng.Next(3) == 0))
                {
                    pathMission = 0u;
                    if (ThinkChallenge(me))
                        return true;
                }
                def = PickPathMission(me);
                pathMission = def?.Entry.Id ?? 0u;
                pathMissionTime = 0d;
                pathStep = "";
                if (def == null)
                    return ThinkChallenge(me);
            }

            pathMissionTime += 2d;
            try
            {
                return DrivePath(me, def);
            }
            catch (Exception exception)
            {
                Diagnostics.FailureLedger.Record("path-bot-error", $"{def.Kind}: {exception.GetType().Name}", exception.Message);
                pathGivenUp.Add(def.Entry.Id);
                pathMission = 0u;
                return false;
            }
        }

        /// <summary>A mission of the bot's path it may take (faction, prerequisite), in its zone first, then in its world, then anywhere.</summary>
        private PathMissionManager.MissionDef PickPathMission(Player me)
        {
            PathMissionManager missions = me.PathMissionManager;
            bool Open(PathMissionManager.MissionDef d) => PlayedKinds.Contains(d.Kind) && (FocusKinds.Length == 0 || FocusKinds.Contains(d.Kind)) && !pathGivenUp.Contains(d.Entry.Id) && !missions.IsCompleted(d.Entry.Id);

            var here = missions.GetAvailable().Where(Open).ToList();
            if (here.Count > 0)
                return here[BotManager.Rng.Next(here.Count)];

            var all = PathMissionManager.AllMissions().Where(d => d.Entry.PathTypeEnum == (uint)me.Path && Open(d) && missions.IsActive(d.Entry.Id)).ToList();
            if (all.Count == 0)
                return null;
            var sameWorld = all.Where(d => GameTableManager.Instance.PathEpisode.GetEntry(d.Entry.PathEpisodeId)?.WorldId == me.Map.Entry.Id).ToList();
            var pick = (sameWorld.Count > 0 ? sameWorld : all)[BotManager.Rng.Next(sameWorld.Count > 0 ? sameWorld.Count : all.Count)];

            // go to the mission's zone: its first world location, else a spawn of one of its targets
            if (!GoToMission(me, pick))
            {
                pathGivenUp.Add(pick.Entry.Id);
                Diagnostics.FailureLedger.Record("path-bot-no-place", $"path {pick.Entry.PathTypeEnum} type {pick.Entry.PathMissionTypeEnum} {pick.Kind}", $"mission {pick.Entry.Id}");
                return null;
            }
            return pick;
        }

        private bool GoToMission(Player me, PathMissionManager.MissionDef def)
        {
            foreach (uint locationId in new[] { def.Entry.WorldLocation2Id00, def.Entry.WorldLocation2Id01 }.Concat(def.Locations))
            {
                WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(locationId);
                if (location != null && location.WorldId != 0u)
                {
                    me.TeleportTo((ushort)location.WorldId, location.Position0, location.Position1, location.Position2);
                    return true;
                }
            }

            if (def.Kind == Kind.Explore && ExploreTarget(me, def, out ushort exploreWorld, out Vector3 hex))
            {
                me.TeleportTo(exploreWorld, hex.X, hex.Y, hex.Z);
                return true;
            }

            HashSet<uint> targets = Targets(def);
            targets.UnionWith(def.StartCreatures);
            if (targets.Count > 0 && BotQuests.TryFindSpawnAnywhere(targets, out ushort world, out Vector3 position))
            {
                me.TeleportTo(world, position.X, position.Y, position.Z);
                return true;
            }
            return false;
        }

        private static HashSet<uint> Targets(PathMissionManager.MissionDef def)
        {
            var set = new HashSet<uint>(def.Targets);
            if (def.Creature != 0u)
                set.Add(def.Creature);
            if (def.Group != 0u)
                set.UnionWith(TargetGroupHelper.ResolveAll(def.Group));
            return set;
        }

        private bool DrivePath(Player me, PathMissionManager.MissionDef def)
        {
            PathMissionManager.State state = me.PathMissionManager.PeekState(def.Entry.Id);
            switch (def.Kind)
            {
                case Kind.Kill:
                    return Fight(me, Targets(def));
                case Kind.Activate:
                    return Use(me, def.Nodes == PathMissionManager.NodeMode.Name && state != null
                        ? Targets(def).Where(c => !def.NameOf.TryGetValue(c, out uint key) || !state.Nodes.Contains(key)).ToHashSet() : Targets(def));
                case Kind.Spell:
                case Kind.Scan:
                    return Scan(me, def.Nodes == PathMissionManager.NodeMode.Name && state != null
                        ? Targets(def).Where(c => !def.NameOf.TryGetValue(c, out uint key) || !state.Nodes.Contains(key)).ToHashSet() : Targets(def), state);
                case Kind.Area:
                {
                    uint next = def.Locations.FirstOrDefault(l => state == null || !state.Nodes.Contains(l));
                    return Visit(me, next);
                }
                case Kind.Holdout:
                    return !PathMissionManager.HoldoutWithoutEnemies(def.Entry.Id) && Use(me, def.StartCreatures);
                case Kind.Scavenger:
                {
                    var clue = def.Clues.FirstOrDefault(c => state == null || !state.Nodes.Contains(c.Id));
                    if (clue == null)
                        return false;
                    var creatures = new HashSet<uint>();
                    if (clue.Creature != 0u) creatures.Add(clue.Creature);
                    if (clue.Group != 0u) creatures.UnionWith(TargetGroupHelper.ResolveAll(clue.Group));
                    return clue.Type switch
                    {
                        0u => Fight(me, creatures),
                        1u => Use(me, creatures),
                        _  => Dig(me, clue.Location)
                    };
                }
                case Kind.Door:
                {
                    if (state == null || !state.Nodes.Contains(0u))
                    {
                        // inside the micro zone: the micro side entrance stands there; otherwise use the surface entrance
                        if (def.StartCreatures.Count > 0 && BotQuests.TryFindSpawn(me, def.StartCreatures, out Vector3 inside))
                        {
                            pathStep = "walking into the micro zone";
                            me.TeleportTo(me.Map.Entry, inside);
                            return true;
                        }
                        return Use(me, def.Targets);
                    }
                    if (def.DoorActivateGroup != 0u && !state.Nodes.Contains(1u))
                        return Use(me, TargetGroupHelper.ResolveAll(def.DoorActivateGroup));
                    return Fight(me, TargetGroupHelper.ResolveAll(def.DoorKillGroup));
                }
                case Kind.Hub:
                {
                    if (def.Entry.PathMissionTypeEnum == 25u)
                    {
                        // mayor: an improvement at a depot near each listed location
                        int index = Enumerable.Range(0, def.Locations.Length).FirstOrDefault(i => state == null || !state.Nodes.Contains((uint)i));
                        WorldLocation2Entry place = GameTableManager.Instance.WorldLocation2.GetEntry(def.Locations[index]);
                        if (place == null)
                            return false;
                        var near = new Vector3(place.Position0, place.Position1, place.Position2);
                        if (me.Map.Entry.Id != place.WorldId || Vector3.Distance(me.Position, near) > 80f)
                            return Visit(me, def.Locations[index]);
                        return Use(me, def.Targets, 80f);
                    }
                    return Use(me, def.Targets);
                }
                case Kind.Explore:
                {
                    if (!ExploreTarget(me, def, out ushort world, out Vector3 hex))
                    {
                        pathStep = "explore: no undiscovered hex group";
                        return false;
                    }
                    pathStep = $"exploring map zone {def.MapZone} ({me.ZoneMapManager.DiscoveredGroups(def.MapZone)}/{def.Count} hex groups)";
                    me.TeleportTo(world, hex.X, hex.Y, hex.Z);
                    return true;
                }
                case Kind.PowerMap:
                {
                    // stand at the start until the map starts, then keep next to the leader
                    Vector3? leader = me.PathMissionManager.PowerMapLeader;
                    if (!leader.HasValue)
                    {
                        // still not started after a while at the start: say why (zone, distance, availability)
                        if (pathMissionTime > 20d && pathMissionTime < 23d)
                        {
                            WorldLocation2Entry start = GameTableManager.Instance.WorldLocation2.GetEntry(def.StartLocation);
                            float d = start == null ? -1f : Vector2.Distance(new Vector2(me.Position.X, me.Position.Z), new Vector2(start.Position0, start.Position2));
                            Diagnostics.FailureLedger.Record("path-powermap-not-started", $"mission {def.Entry.Id}",
                                $"zone {me.Zone?.Id.ToString() ?? "none"}, world {me.Map?.Entry.Id} (start world {start?.WorldId}), {d:0} m from the start, available {me.PathMissionManager.GetAvailable().Contains(def)}, state done {me.PathMissionManager.IsCompleted(def.Entry.Id)}");
                        }
                        return Visit(me, def.StartLocation);
                    }
                    pathStep = "following the power map leader";
                    if (Vector3.Distance(me.Position, leader.Value) > 4f)
                        me.TeleportTo(me.Map.Entry, leader.Value);
                    return true;
                }
                case Kind.Infrastructure:
                {
                    uint held = state != null && state.Values.TryGetValue(0u, out uint h) ? h : 0u;
                    if (held < def.MaxStack && def.Resources.Count > 0)
                        return Use(me, def.Resources);
                    return Use(me, def.Targets);
                }
            }
            return false;
        }

        private WorldEntity Nearest(Player me, HashSet<uint> creatures, float range, HashSet<uint> skipGuids = null)
        {
            me.Map.Search(me.Position, range, new SearchCheckRange(me.Position, range, me), out List<GridEntity> around);
            return around.OfType<WorldEntity>().Where(c => c is not Player && creatures.Contains(c.CreatureId) && !(c is UnitEntity { IsDead: true }) && (skipGuids == null || !skipGuids.Contains(c.Guid)))
                .OrderBy(c => Vector3.Distance(me.Position, c.Position)).FirstOrDefault();
        }

        private uint challengeId;
        private double challengeTime;
        private readonly HashSet<uint> challengesTried = new();

        /// <summary>
        /// Challenges for path bots: pick a playable challenge (ChallengeManager.Playable) of the zone or anywhere, go to its start, activate it through
        /// ClientChallengeChoice (0x00C5) and do what its type counts: kills, unit uses, casts of its spell, loot of its item, reaching its goal.
        /// </summary>
        private bool ThinkChallenge(Player me)
        {
            var manager = me.ChallengeManager;
            var running = challengeId != 0u ? manager.ActiveChallenges.FirstOrDefault(a => a.Entry.Id == challengeId) : null;
            if (challengeId != 0u && running == null)
            {
                bool started = challengeTime > 0d;
                var played = GameTableManager.Instance.Challenge.GetEntry(challengeId);
                Diagnostics.FailureLedger.Record("challenge-bot", (started ? (manager.BestMedal(challengeId) > 0 ? "medal" : "no medal") : "not started")
                    + $" ({(played == null ? "?" : Game.Challenge.ChallengeManager.Playable(played) ?? "unplayable")})",
                    $"challenge {challengeId} type {GameTableManager.Instance.Challenge.GetEntry(challengeId)?.ChallengeTypeEnum} after {challengeTime:0} s{(started && manager.BestMedal(challengeId) == 0 ? $", last step: {pathStep}" : "")}");
                challengeId = 0u;
                return false;
            }

            if (running == null)
            {
                var zones = me.ZoneAndParents().ToHashSet();
                var open = GameTableManager.Instance.Challenge.Entries.Where(c => c != null && c.Id != 0u && c.Id < (1u << 14) && !challengesTried.Contains(c.Id)
                    && manager.BestMedal(c.Id) == 0 && Game.Challenge.ChallengeManager.Playable(c) != null).ToList();
                var here = open.Where(c => zones.Contains(c.WorldZoneId) || zones.Contains(c.WorldZoneIdRestriction)).ToList();
                var pick = here.Count > 0 ? here[BotManager.Rng.Next(here.Count)] : open.Count > 0 ? open[BotManager.Rng.Next(open.Count)] : null;
                if (pick == null)
                    return false;
                challengesTried.Add(pick.Id);

                // to the start: the start location, the indicator for non-races, else a spawn of what it counts
                uint place = pick.WorldLocation2IdStartLocation != 0u ? pick.WorldLocation2IdStartLocation
                    : pick.ChallengeTypeEnum != Game.Challenge.ChallengeManager.GeneralType ? pick.WorldLocation2IdIndicator : 0u;
                var location = GameTableManager.Instance.WorldLocation2.GetEntry(place);
                if (location != null)
                    me.TeleportTo((ushort)location.WorldId, location.Position0, location.Position1, location.Position2);
                else if (!Travel(me, ChallengeCreatures(pick), "challenge start"))
                {
                    Diagnostics.FailureLedger.Record("challenge-bot", "no place to start", $"challenge {pick.Id} type {pick.ChallengeTypeEnum}");
                    return false;
                }

                uint id = pick.Id;
                LuaApiHandler.HandleChallengeChoice(me.Session, BotPackets.Build<ClientChallengeChoice>(w =>
                {
                    w.Write(id, 14u);
                    w.Write(ClientChallengeChoice.Activate, 5u);
                    w.Write(0u);
                }));
                challengeId = pick.Id;
                challengeTime = 0d;
                return true;
            }

            challengeTime += 2d;
            var entry = running.Entry;
            switch (entry.ChallengeTypeEnum)
            {
                case Game.Challenge.ChallengeManager.CombatType:
                    return Fight(me, ChallengeCreatures(entry));
                case Game.Challenge.ChallengeManager.ChecklistType:
                {
                    var creatures = ChallengeCreatures(entry);
                    WorldEntity next = Nearest(me, creatures, 80f, running.Used);
                    if (next == null)
                        return Travel(me, creatures, "challenge checklist");
                    if (Vector3.Distance(me.Position, next.Position) > 3f)
                        me.TeleportTo(me.Map.Entry, next.Position);
                    else
                        EntityHandler.HandleActivateUnitCast(me.Session, BotPackets.Build<ClientActivateUnitCast>(w => { w.Write(0u); w.Write(next.Guid); }));
                    return true;
                }
                case Game.Challenge.ChallengeManager.VirtualItemType:
                case Game.Challenge.ChallengeManager.ItemType:
                {
                    var creatures = ChallengeCreatures(entry);
                    var hostile = creatures.Where(c => BotQuests.IsHostile(me, c)).ToHashSet();
                    return hostile.Count > 0 ? Fight(me, hostile) : Use(me, creatures);
                }
                case Game.Challenge.ChallengeManager.AbilityType:
                {
                    if (me.SpellManager.GetGlobalSpellCooldown() > 0d || me.IsCasting() || me.SpellManager.GetSpellCooldown(entry.Target) > 0d)
                        return true;
                    me.Map.Search(me.Position, 30f, new SearchCheckRange(me.Position, 30f, me), out List<GridEntity> around);
                    NonPlayer target = around.OfType<NonPlayer>().Where(c => !c.IsDead).OrderBy(c => Vector3.Distance(me.Position, c.Position)).FirstOrDefault();
                    me.CastSpell(entry.Target, new Game.Spell.SpellParameters { UserInitiatedSpellCast = true, PrimaryTargetId = target?.Guid ?? 0u });
                    return true;
                }
                case Game.Challenge.ChallengeManager.GeneralType when Game.Challenge.ChallengeManager.Playable(entry) == "sources":
                {
                    var creatures = ChallengeCreatures(entry);
                    var hostile = creatures.Where(c => BotQuests.IsHostile(me, c)).ToHashSet();
                    return hostile.Count > 0 ? Fight(me, hostile) : Use(me, creatures);
                }
                case Game.Challenge.ChallengeManager.GeneralType:
                {
                    var goal = GameTableManager.Instance.WorldLocation2.GetEntry(entry.WorldLocation2IdIndicator);
                    if (goal != null)
                        me.TeleportTo((ushort)goal.WorldId, goal.Position0, goal.Position1, goal.Position2);
                    return true;
                }
            }
            return false;
        }

        private static HashSet<uint> ChallengeCreatures(ChallengeEntry entry)
        {
            return entry.ChallengeTypeEnum switch
            {
                Game.Challenge.ChallengeManager.CombatType or Game.Challenge.ChallengeManager.ChecklistType => TargetGroupHelper.ResolveAll(entry.Target),
                Game.Challenge.ChallengeManager.VirtualItemType or Game.Challenge.ChallengeManager.ItemType or Game.Challenge.ChallengeManager.GeneralType
                    => Game.Challenge.ChallengeManager.Sources(entry.Id),
                _ => new HashSet<uint>()
            };
        }

        /// <summary>Jump to a spawn of one of the creatures (this world, else anywhere); false when none is spawned.</summary>
        private bool Travel(Player me, HashSet<uint> creatures, string what)
        {
            if (creatures.Count == 0)
            {
                pathStep = $"{what}: no target creature";
                return false;
            }
            if (BotQuests.TryFindSpawn(me, creatures, out Vector3 position))
            {
                pathStep = $"{what}: travelling to a spawn";
                me.TeleportTo(me.Map.Entry, position);
                return true;
            }
            if (BotQuests.TryFindSpawnAnywhere(creatures, out ushort world, out position))
            {
                pathStep = $"{what}: travelling to world {world}";
                me.TeleportTo(world, position.X, position.Y, position.Z);
                return true;
            }
            pathStep = $"{what}: no spawn of [{string.Join(",", creatures.Take(5))}]";
            return false;
        }

        private bool Fight(Player me, HashSet<uint> creatures)
        {
            WorldEntity target = Nearest(me, creatures, 60f);
            if (target == null)
                return Travel(me, creatures, "kill");
            pathStep = $"fighting creature {target.CreatureId}";
            killTargets = creatures;
            Hunt(me);
            return true;
        }

        /// <summary>Use a unit through the client message (ClientActivateUnitCast), standing next to it.</summary>
        private bool Use(Player me, HashSet<uint> creatures, float range = 60f)
        {
            WorldEntity target = Nearest(me, creatures, range);
            if (target == null)
                return Travel(me, creatures, "use");
            // a second try at the same unit within 15 m uses it from there (props on platforms keep a teleported bot a few metres away)
            float flat = Vector2.Distance(new Vector2(me.Position.X, me.Position.Z), new Vector2(target.Position.X, target.Position.Z));
            bool retry = target.Guid == pathWalkGuid && flat < 15f;
            pathWalkGuid = target.Guid;
            if (flat > 3f && !retry)
            {
                pathStep = $"walking to use creature {target.CreatureId}";
                me.TeleportTo(me.Map.Entry, target.Position);
                return true;
            }
            pathStep = $"using creature {target.CreatureId}";
            EntityHandler.HandleActivateUnitCast(me.Session, BotPackets.Build<ClientActivateUnitCast>(w =>
            {
                w.Write(0u);
                w.Write(target.Guid);
            }));
            return true;
        }

        /// <summary>Scan a unit: the Scientist Scanbot Scan spell (2 s cast, 15 m) on it; scanned units are skipped for checklist missions.</summary>
        private bool Scan(Player me, HashSet<uint> creatures, PathMissionManager.State state)
        {
            me.Map.Search(me.Position, 60f, new SearchCheckRange(me.Position, 60f, me), out List<GridEntity> around);
            WorldEntity target = around.OfType<WorldEntity>().Where(c => c is not Player && creatures.Contains(c.CreatureId) && (state == null || !state.Nodes.Contains(c.Guid)))
                .OrderBy(c => Vector3.Distance(me.Position, c.Position)).FirstOrDefault();
            if (target == null)
                return Travel(me, creatures, "scan");
            if (Vector3.Distance(me.Position, target.Position) > 10f)
            {
                pathStep = $"walking to scan creature {target.CreatureId}";
                me.TeleportTo(me.Map.Entry, target.Position);
                return true;
            }
            if (me.SpellManager.GetGlobalSpellCooldown() > 0d || me.IsCasting())
                return true;
            pathStep = $"scanning creature {target.CreatureId}";
            me.CastSpell(PathMissionManager.ScanSpells[0], new Game.Spell.SpellParameters { UserInitiatedSpellCast = true, PrimaryTargetId = target.Guid });
            return true;
        }

        /// <summary>
        /// The centre of the first undiscovered hex of an ExploreZone mission's map zone, in the mission's world: ground height from the terrain
        /// when the bot is on that map, else from a world location of the zone (the bot snaps to the ground after the teleport).
        /// </summary>
        private bool ExploreTarget(Player me, PathMissionManager.MissionDef def, out ushort world, out Vector3 position)
        {
            world = (ushort)def.World;
            position = default;
            // a hex group that stays undiscovered after a visit (another map zone's hexes there, water) is skipped
            if (exploreLast != 0u && me.Map?.Entry.Id == world && me.ZoneMapManager.NextUndiscovered(def.MapZone) == exploreLast)
            {
                exploreTried.Add(exploreLast);
                var hexAt = Game.Map.ZoneMapIndex.Hexes(exploreLast);
                Diagnostics.FailureLedger.Record("path-explore-miss", $"map zone {def.MapZone}",
                    $"group {exploreLast} hex {(hexAt.Count > 0 ? $"{hexAt[0].X},{hexAt[0].Y}" : "-")}, bot zone {me.Zone?.Id.ToString() ?? "none"}, {me.ZoneMapManager.Where}, at {me.Position.X:0},{me.Position.Z:0}");
            }
            uint group = me.ZoneMapManager.NextUndiscovered(def.MapZone, exploreTried);
            exploreLast = group;
            if (group == 0u || world == 0)
                return false;
            var hexes = Game.Map.ZoneMapIndex.Hexes(group);
            if (hexes.Count == 0)
                return false;
            (float x, float z) = Game.Map.ZoneMapIndex.HexCenter(hexes[0].X, hexes[0].Y);
            float y = me.Position.Y;
            if (me.Map != null && me.Map.Entry.Id == world)
                y = me.Map.GetTerrainHeight(x, z, y);
            else
            {
                uint zone = GameTableManager.Instance.MapZone.GetEntry(def.MapZone)?.WorldZoneId ?? 0u;
                uint worldId = world;
                WorldLocation2Entry any = GameTableManager.Instance.WorldLocation2.Entries.FirstOrDefault(l => l.WorldId == worldId && l.WorldZoneId == zone);
                if (any != null)
                    y = any.Position1;
            }
            position = new Vector3(x, y, z);
            return true;
        }

        private bool Visit(Player me, uint locationId)
        {
            WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(locationId);
            if (location == null)
            {
                pathStep = $"location {locationId} unknown";
                return false;
            }
            pathStep = $"visiting location {locationId}";
            me.TeleportTo((ushort)location.WorldId, location.Position0, location.Position1, location.Position2);
            return true;
        }

        private bool Dig(Player me, uint locationId)
        {
            WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(locationId);
            if (location == null)
            {
                pathStep = $"dig location {locationId} unknown";
                return false;
            }
            var place = new Vector3(location.Position0, location.Position1, location.Position2);
            if (me.Map.Entry.Id != location.WorldId || Vector2.Distance(new Vector2(me.Position.X, me.Position.Z), new Vector2(place.X, place.Z)) > 5f)
                return Visit(me, locationId);
            if (me.SpellManager.GetGlobalSpellCooldown() > 0d || me.IsCasting())
                return true;
            pathStep = $"digging at location {locationId}";
            me.CastSpell(DigSpell, new Game.Spell.SpellParameters { UserInitiatedSpellCast = true });
            return true;
        }
    }
}
