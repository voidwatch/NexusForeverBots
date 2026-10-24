using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.Shared.GameTable.Static;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Static;

namespace NexusForever.WorldServer.Game.Entity
{
    /// <summary>
    /// Path missions of the active path in the zone the player stands in (the zone or one of its parents is the episode's zone). State is kept as JSON
    /// in the cache volume (CharacterJson, no database table yet); the client gets ServerPathMissionActivate when a mission gets a state and at login,
    /// ServerPathMissionUpdate on progress (user data = count or checklist bits by type, read from the client); see docs/ASSUMPTIONS.md "Path missions".
    /// Mission type numbers are the client's PathMissionType enum (certain, client registration code). What a mission needs comes from the table its
    /// ObjectId points at (ObjectId ranges match exactly one table per type):
    /// Soldier 0 Holdout = PathSoldierEvent (start creature: Creature2.PathMissionIdSoldier, enemies from Jabbithole, one wave per PathSoldierEventWave),
    /// 4 Assassinate = PathSoldierAssassinate (kill), 5 Demolition / 6 Rescue = PathSoldierActivate (use), 7 SWAT = PathSoldierSWAT (spell hits);
    /// Settler 19 Hub = PathSettlerHub (build improvements at its depots until the avenue contributions reach MissionCount), 21 Infrastructure =
    /// PathSettlerInfrastructure (gather resources, deposit at the depot), 25 Mayor = PathSettlerMayor (build an improvement at each listed location),
    /// 26 Sheriff = PathSettlerSheriff (quests), 27 Scout = PathExplorerArea (visit the nodes);
    /// Scientist 2 Scan / 14 ScanChecklist / 23 SpecimenSurvey = scans ("Scientist Scanbot Scan" 45403/31469) of the creatures Jabbithole lists for
    /// the mission, 20 FieldStudy = PathScientistFieldStudy, 22 Experimentation = creatures with Creature2.PathScientistExperimentationId,
    /// 24 DatacubeDiscovery = the zone's datacubes (Datacube type 0, creatures with Creature2.DatacubeId);
    /// Explorer 3 Area / 15 Vista = PathExplorerArea nodes, 12 Door = PathExplorerDoor (enter the micro zone), 13 ScavengerHunt = the clues of
    /// PathExplorerScavengerHunt, 17 ActivateChecklist = PathExplorerActivate.
    /// Not played: 16 ExploreZone (ObjectId = MapZone, map hex exploration is client side) and 18 PowerMap (no target positions in the data).
    /// </summary>
    public class PathMissionManager
    {
        public enum MissionKind { Kill, Activate, Spell, Scan, Area, Quests, Holdout, Scavenger, Door, Hub, Infrastructure, Explore, PowerMap, Unsupported }

        /// <summary>What makes progress distinct: every event counts, each creature id once, each entity once, or each named target once.</summary>
        public enum NodeMode { Count, Creature, Entity, Name }

        public sealed class Clue
        {
            public uint Id, Type, Creature, Group, Location;
            public float Radius;
        }

        public sealed class MissionDef
        {
            public PathMissionEntry Entry;
            public MissionKind Kind;
            public NodeMode Nodes = NodeMode.Count;
            public uint Creature, Group, Count = 1u;
            public HashSet<uint> Targets = new();
            public Dictionary<uint, uint> NameOf = new();
            public uint[] Locations = Array.Empty<uint>();
            public ushort[] Quests = Array.Empty<ushort>();
            public uint Zone, World;
            // holdout
            public HashSet<uint> StartCreatures = new();
            public uint Waves;
            public double MaxEventTime;
            /// <summary>PathSoldierEvent.initialSpawnTime and maxTimeBetweenWaves in seconds (0 = none).</summary>
            public double InitialSpawn, MaxBetweenWaves;
            // scavenger hunt
            public List<Clue> Clues = new();
            // door
            public uint MicroZone, DoorActivateGroup, DoorKillGroup;
            // settler hub / infrastructure
            public uint Hub;
            public Dictionary<uint, (uint Avenue, uint Value)> DepotGroups = new();
            public uint[] AvenueMax = new uint[3];
            public HashSet<uint> Resources = new();
            public HashSet<uint> ResourceSpells = new();
            public uint MaxStack = 1u;
            // explorer ExploreZone (16): the MapZone whose hex groups are discovered; PowerMap (18): the trail and the range to stay in
            public ushort MapZone;
            public Vector3[] Trail = Array.Empty<Vector3>();
            public float Threshold;
            public uint StartLocation;
        }

        public sealed class State
        {
            public uint Progress { get; set; }
            public bool Done { get; set; }
            public HashSet<uint> Nodes { get; set; } = new();
            public Dictionary<uint, uint> Values { get; set; } = new();
        }

        public sealed class Saved
        {
            public Dictionary<uint, State> Missions { get; set; } = new();
        }

        /// <summary>A running holdout: the waves the server spawned for the player and the event clock.</summary>
        /// <summary>A running explorer power map: a leader moves along the trail while the player stays within range.</summary>
        private sealed class PowerMapRun
        {
            public MissionDef Def;
            public int Segment;
            public float Along, Travelled, Length;
            public double Waiting;
            public uint Collected;
        }

        /// <summary>Leader speed along a power map trail (guess: run speed of a walking creature, the tables hold none).</summary>
        public const float PowerMapSpeed = 4f;
        /// <summary>How long the leader waits for a player out of range before the map fails (guess: the client shows a despawn countdown).</summary>
        public const double PowerMapWaitLimit = 30d;

        private sealed class Holdout
        {
            public MissionDef Def;
            public Vector3 Origin;
            public uint Wave;
            /// <summary>Waves spawned so far; the units of a wave that is not beaten when the next one comes stay in <see cref="Units"/>.</summary>
            public uint Spawned;
            public double Elapsed, WaveTime;
            public List<NonPlayer> Units = new();
            public uint[] LocalTargets;
        }

        public static readonly uint[] ScanSpells = { 45403u, 31469u };
        public const uint HoldoutUnitsPerWave = 3u;

        // zone id -> path type -> missions, built once
        private static readonly object indexLock = new();
        private static Dictionary<(uint Zone, uint Path), List<MissionDef>> index;
        private static Dictionary<(uint World, uint Path), List<MissionDef>> byWorld;
        private static Dictionary<uint, uint> xpPerMission;
        private static readonly Dictionary<uint, MissionDef> byId = new();

        private readonly Player player;
        private readonly CharacterJson<Saved> store;
        private Dictionary<uint, State> states => store.Value.Missions;
        private double timer;
        private Holdout holdout;
        private PowerMapRun powerMap;

        public PathMissionManager(Player owner)
        {
            player = owner;
            store = new CharacterJson<Saved>("pathmissions", owner.CharacterId);
        }

        public int CompletedCount => states.Values.Count(s => s.Done);

        public bool IsCompleted(uint missionId) => states.TryGetValue(missionId, out State s) && s.Done;

        /// <summary>The mission belongs to the player's path and faction, its prerequisite is met and it is not done.</summary>
        public bool IsActive(uint missionId)
        {
            PathMissionEntry entry = GameTableManager.Instance.PathMission.GetEntry(missionId);
            if (entry == null || IsCompleted(missionId) || entry.PathTypeEnum != (uint)player.Path)
                return false;

            return Available(new MissionDef { Entry = entry });
        }

        public uint GetProgress(uint missionId) => states.TryGetValue(missionId, out State s) ? s.Progress : 0u;

        public bool HoldoutRunning => holdout != null;

        /// <summary>
        /// The mission state number the client keeps (PathMission GetMissionState, compared by prerequisite 155; client mission class, certain):
        /// 0 = the client has no record of the mission (the server sent none), 4 = complete, otherwise by mission type from the user data the
        /// server sends: holdout 3 while it runs else 2; scan / scan checklist 3 when bit 0 is set else 1; assassinate, demolition, rescue,
        /// SWAT 2 | bit 0; scavenger hunt 3 when bit 30 is set else 2; power map 3 while it runs else 2; every other type 3 (started).
        /// </summary>
        public uint ClientState(uint missionId)
        {
            if (!states.TryGetValue(missionId, out State state) || GetMission(missionId) is not MissionDef def)
                return 0u;
            if (state.Done)
                return 4u;
            uint data = UserData(def, state);
            switch (def.Entry.PathMissionTypeEnum)
            {
                case 0u:
                    return holdout != null && holdout.Def.Entry.Id == missionId ? 3u : 2u;
                case 2u:
                case 14u:
                    return (data & 1u) != 0u ? 3u : 1u;
                case 4u:
                case 5u:
                case 6u:
                case 7u:
                    return (data & 1u) | 2u;
                case 13u:
                    return (data & 0x40000000u) != 0u ? 3u : 2u;
                case 18u:
                    return powerMap != null && powerMap.Def.Entry.Id == missionId ? 3u : 2u;
                default:
                    return 3u;
            }
        }

        /// <summary>Where the leader of the running power map is, null when none runs (bot driver).</summary>
        public Vector3? PowerMapLeader => powerMap == null ? null : LeaderPosition(powerMap);

        /// <summary>Creatures named for each mission on the Jabbithole NPC pages (data/research/path-mission-targets.tsv, scripts/research/jh_path_missions.py).</summary>
        private static Dictionary<uint, List<(string Name, uint[] Creatures)>> LoadJabbithole()
        {
            var result = new Dictionary<uint, List<(string, uint[])>>();
            // Jabbithole's path mission tabs, then title matches for scans Jabbithole lists nothing for (scripts/research/path_name_targets.py)
            string folder = Environment.GetEnvironmentVariable("NF_RESEARCH_PATH") ?? "/data/research";
            var lines = new[] { "path-mission-targets.tsv", "path-mission-name-targets.tsv" }.Select(f => Path.Combine(folder, f)).Where(File.Exists).SelectMany(File.ReadLines);

            foreach (string line in lines)
            {
                string[] f = line.Split('\t');
                if (line.StartsWith('#') || f.Length < 4 || !uint.TryParse(f[0], out uint mission))
                    continue;
                uint[] creatures = f[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => uint.TryParse(s, out uint c) ? c : 0u).Where(c => c != 0u).ToArray();
                if (creatures.Length == 0)
                    continue;
                if (!result.TryGetValue(mission, out var list))
                    result[mission] = list = new List<(string, uint[])>();
                list.Add((f[3], creatures));
            }
            return result;
        }

        private static void AddTargets(MissionDef def, List<(string Name, uint[] Creatures)> named)
        {
            uint key = 0u;
            foreach ((string _, uint[] creatures) in named)
            {
                key++;
                foreach (uint creature in creatures)
                {
                    def.Targets.Add(creature);
                    def.NameOf.TryAdd(creature, key);
                }
            }
        }

        private static void EnsureIndex()
        {
            if (index != null)
                return;

            lock (indexLock)
            {
                if (index != null)
                    return;

                var tables = GameTableManager.Instance;
                var built = new Dictionary<(uint, uint), List<MissionDef>>();
                var builtWorld = new Dictionary<(uint, uint), List<MissionDef>>();
                var perPath = tables.PathMission.Entries.GroupBy(m => m.PathTypeEnum).ToDictionary(g => g.Key, g => (uint)g.Count());
                xpPerMission = new Dictionary<uint, uint>();
                foreach (var path in perPath)
                {
                    uint maxXp = tables.PathLevel.Entries.Where(l => l.PathTypeEnum == path.Key).Select(l => l.PathXP).DefaultIfEmpty(0u).Max();
                    xpPerMission[path.Key] = Math.Max(1u, maxXp / Math.Max(1u, path.Value));
                }

                var nodesByArea = tables.PathExplorerNode.Entries.GroupBy(n => n.PathExplorerAreaId).ToDictionary(g => g.Key, g => g.Select(n => n.WorldLocation2Id).Where(w => w != 0u).ToArray());
                var jabbithole = LoadJabbithole();
                var startCreatures = tables.Creature2.Entries.Where(c => c.PathMissionIdSoldier != 0u).GroupBy(c => c.PathMissionIdSoldier).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToHashSet());
                var wavesByEvent = tables.PathSoldierEventWave.Entries.GroupBy(w => w.PathSoldierEventId).ToDictionary(g => g.Key, g => (uint)g.Count());
                var experimentCreatures = tables.Creature2.Entries.Where(c => c.PathScientistExperimentationId != 0u).GroupBy(c => c.PathScientistExperimentationId).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToHashSet());
                var datacubeCreatures = tables.Creature2.Entries.Where(c => c.DatacubeId != 0u).GroupBy(c => c.DatacubeId).ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());
                var groupsByHub = tables.PathSettlerImprovementGroup.Entries.GroupBy(g => g.PathSettlerHubId).ToDictionary(g => g.Key, g => g.ToList());
                var hubAreas = tables.PathExplorerArea.Entries.ToDictionary(a => a.Id, a => a.PathSettlerHubId);
                // power map trails: every Spline2 by world with its first node (each of the 25 power map missions has a spline starting within 4 m of its location)
                var splineWorld = tables.Spline2.Entries.ToDictionary(s => s.Id, s => s.WorldId);
                var splineNodes = tables.Spline2Node.Entries.GroupBy(n => n.SplineId).ToDictionary(g => g.Key, g => g.OrderBy(n => n.Ordinal).Select(n => new Vector3(n.Position0, n.Position1, n.Position2)).ToArray());

                foreach (PathMissionEntry mission in tables.PathMission.Entries)
                {
                    PathEpisodeEntry episode = tables.PathEpisode.GetEntry(mission.PathEpisodeId);
                    if (episode == null)
                        continue;

                    var def = new MissionDef { Entry = mission, Kind = MissionKind.Unsupported, Zone = episode.WorldZoneId, World = episode.WorldId };
                    jabbithole.TryGetValue(mission.Id, out var named);
                    switch ((mission.PathTypeEnum, mission.PathMissionTypeEnum))
                    {
                        case (0u, 0u):
                        {
                            var row = tables.PathSoldierEvent.GetEntry(mission.ObjectId);
                            if (row != null && startCreatures.TryGetValue(mission.Id, out HashSet<uint> starts))
                            {
                                def.Kind = MissionKind.Holdout;
                                if (named != null)
                                    AddTargets(def, named);
                                def.Targets.ExceptWith(starts);
                                def.StartCreatures = starts;
                                def.Waves = Math.Max(1u, wavesByEvent.TryGetValue(row.Id, out uint waves) ? waves : 1u);
                                def.MaxEventTime = row.MaxEventTime > 0u ? row.MaxEventTime / 1000d : 300d;
                                def.InitialSpawn = row.InitialSpawnTime / 1000d;
                                def.MaxBetweenWaves = row.MaxTimeBetweenWaves / 1000d;
                                def.Count = def.Waves;
                                // 66 holdouts list only their control point (Jabbithole and the tables): their waves are the hostile creatures spawned
                                // around the start (StartHoldout, guess)
                            }
                            break;
                        }
                        case (0u, 4u):
                        {
                            var row = tables.PathSoldierAssassinate.GetEntry(mission.ObjectId);
                            if (row != null) { def.Kind = MissionKind.Kill; def.Creature = row.Creature2Id; def.Group = row.TargetGroupId; def.Count = Math.Max(1u, row.Count); }
                            break;
                        }
                        case (0u, 5u):
                        case (0u, 6u):
                        {
                            var row = tables.PathSoldierActivate.GetEntry(mission.ObjectId);
                            if (row != null) { def.Kind = MissionKind.Activate; def.Creature = row.Creature2Id; def.Group = row.TargetGroupId; def.Count = Math.Max(1u, row.Count); }
                            break;
                        }
                        case (0u, 7u):
                        {
                            var row = tables.PathSoldierSWAT.GetEntry(mission.ObjectId);
                            if (row != null) { def.Kind = MissionKind.Spell; def.Creature = row.Creature2Id; def.Group = row.TargetGroupId; def.Count = Math.Max(1u, row.Count); }
                            break;
                        }
                        case (1u, 19u):
                        {
                            var hub = tables.PathSettlerHub.GetEntry(mission.ObjectId);
                            if (hub != null && groupsByHub.TryGetValue(hub.Id, out var groups))
                            {
                                def.Kind = MissionKind.Hub;
                                def.Hub = hub.Id;
                                def.AvenueMax = new[] { hub.MaxAvenueEconomy, hub.MaxAvenueSecurity, hub.MaxAvenueQualityOfLife };
                                // MissionCount equals the sum of the three avenue caps on 74 of 96 hubs (53 of the 68 hub missions); hubs without caps
                                // only have the count (avenue order Economy 0, Security 1, Quality of Life 2 as the hub columns, likely)
                                def.Count = Math.Max(1u, hub.MissionCount);
                                foreach (var g in groups.Where(g => g.Creature2IdDepot != 0u))
                                {
                                    def.Targets.Add(g.Creature2IdDepot);
                                    def.DepotGroups[g.Id] = (g.SettlerAvenueTypeEnum, g.ContributionValue);
                                }
                            }
                            break;
                        }
                        case (1u, 21u):
                        {
                            var row = tables.PathSettlerInfrastructure.GetEntry(mission.ObjectId);
                            if (row != null && row.Creature2IdDepot != 0u)
                            {
                                def.Kind = MissionKind.Infrastructure;
                                def.Targets.Add(row.Creature2IdDepot);
                                foreach (uint c in new[] { row.Creature2IdResource00, row.Creature2IdResource01, row.Creature2IdResource02 }.Where(c => c != 0u))
                                    def.Resources.Add(c);
                                foreach (uint s in new[] { row.Spell4IdResource00, row.Spell4IdResource01, row.Spell4IdResource02 }.Where(s => s != 0u))
                                    def.ResourceSpells.Add(s);
                                def.MaxStack = Math.Max(1u, row.MaxStackCountResource00);
                                def.Count = Math.Max(1u, row.Count);
                            }
                            break;
                        }
                        case (1u, 25u):
                        {
                            var row = tables.PathSettlerMayor.GetEntry(mission.ObjectId);
                            if (row != null)
                            {
                                uint[] texts = { row.LocalizedTextId00, row.LocalizedTextId01, row.LocalizedTextId02, row.LocalizedTextId03, row.LocalizedTextId04, row.LocalizedTextId05, row.LocalizedTextId06, row.LocalizedTextId07 };
                                uint[] places = { row.WorldLocation2Id00, row.WorldLocation2Id01, row.WorldLocation2Id02, row.WorldLocation2Id03, row.WorldLocation2Id04, row.WorldLocation2Id05, row.WorldLocation2Id06, row.WorldLocation2Id07 };
                                def.Locations = places.Where((p, i) => p != 0u && texts[i] != 0u).ToArray();
                                if (def.Locations.Length > 0)
                                {
                                    // "Create an improvement in Hub 1!": an improvement built at a depot near each listed location
                                    def.Kind = MissionKind.Hub;
                                    def.Count = (uint)def.Locations.Length;
                                    foreach (var g in tables.PathSettlerImprovementGroup.Entries.Where(g => g.Creature2IdDepot != 0u))
                                        def.Targets.Add(g.Creature2IdDepot);
                                }
                            }
                            break;
                        }
                        case (1u, 26u):
                        {
                            var row = tables.PathSettlerSheriff.GetEntry(mission.ObjectId);
                            if (row != null)
                            {
                                ushort[] quests = { (ushort)row.Quest2IdSheriff00, (ushort)row.Quest2IdSheriff01, (ushort)row.Quest2IdSheriff02, (ushort)row.Quest2IdSheriff03,
                                    (ushort)row.Quest2IdSheriff04, (ushort)row.Quest2IdSheriff05, (ushort)row.Quest2IdSheriff06, (ushort)row.Quest2IdSheriff07 };
                                quests = quests.Where(q => q != 0).ToArray();
                                if (quests.Length > 0) { def.Kind = MissionKind.Quests; def.Quests = quests; def.Count = (uint)quests.Length; }
                            }
                            break;
                        }
                        case (2u, 2u):
                        case (2u, 14u):
                        case (2u, 23u):
                        {
                            if (named == null)
                                break;
                            def.Kind = MissionKind.Scan;
                            AddTargets(def, named);
                            if (mission.PathMissionTypeEnum == 14u)
                            {
                                // ScanChecklist: ChecklistCount different units of the listed kind
                                var info = tables.PathScientistCreatureInfo.GetEntry(mission.ObjectId);
                                def.Nodes = NodeMode.Entity;
                                def.Count = Math.Max(1u, info?.ChecklistCount ?? 1u);
                            }
                            else if (mission.PathMissionTypeEnum == 23u)
                            {
                                var row = tables.PathScientistSpecimenSurvey.GetEntry(mission.ObjectId);
                                uint[] objectives = row == null ? Array.Empty<uint>() : new[] { row.LocalizedTextIdObjective00, row.LocalizedTextIdObjective01, row.LocalizedTextIdObjective02,
                                    row.LocalizedTextIdObjective03, row.LocalizedTextIdObjective04, row.LocalizedTextIdObjective05, row.LocalizedTextIdObjective06,
                                    row.LocalizedTextIdObjective07, row.LocalizedTextIdObjective08, row.LocalizedTextIdObjective09 };
                                def.Nodes = NodeMode.Entity;
                                def.Count = Math.Max(1u, (uint)objectives.Count(o => o != 0u));
                            }
                            else
                            {
                                // Scan: each creature Jabbithole lists for the mission scanned once (docs/ASSUMPTIONS.md "Path missions", guess)
                                def.Nodes = NodeMode.Name;
                                def.Count = (uint)Math.Max(1, named.Count);
                            }
                            break;
                        }
                        case (2u, 20u):
                        {
                            var row = tables.PathScientistFieldStudy.GetEntry(mission.ObjectId);
                            if (row != null)
                            {
                                uint[] checklist = { row.LocalizedTextIdChecklist00, row.LocalizedTextIdChecklist01, row.LocalizedTextIdChecklist02, row.LocalizedTextIdChecklist03,
                                    row.LocalizedTextIdChecklist04, row.LocalizedTextIdChecklist05, row.LocalizedTextIdChecklist06, row.LocalizedTextIdChecklist07 };
                                def.Kind = MissionKind.Spell;
                                def.Creature = row.Creature2Id;
                                def.Group = row.TargetGroupId;
                                def.Count = Math.Max(1u, (uint)checklist.Count(c => c != 0u));
                            }
                            break;
                        }
                        case (2u, 22u):
                        {
                            if (experimentCreatures.TryGetValue(mission.ObjectId, out HashSet<uint> creatures))
                            {
                                def.Kind = MissionKind.Activate;
                                def.Targets = creatures;
                                def.Count = 1u;
                            }
                            break;
                        }
                        case (2u, 24u):
                        {
                            var row = tables.PathScientistDatacubeDiscovery.GetEntry(mission.ObjectId);
                            if (row == null)
                                break;
                            // the zone's datacubes of type 0 (client enum DatacubeType_Datacube; 1 Chronicle, 2 Journal) that have a creature to activate
                            var cubes = tables.Datacube.Entries.Where(d => d.DatacubeTypeEnum == 0u && InZone(d.WorldZoneId, row.WorldZoneId) && datacubeCreatures.ContainsKey(d.Id)).ToList();
                            if (cubes.Count == 0)
                                break;
                            def.Kind = MissionKind.Activate;
                            def.Nodes = NodeMode.Name;
                            uint key = 0u;
                            foreach (var cube in cubes)
                            {
                                key++;
                                foreach (uint c in datacubeCreatures[cube.Id])
                                {
                                    def.Targets.Add(c);
                                    def.NameOf.TryAdd(c, key);
                                }
                            }
                            def.Count = (uint)cubes.Count;
                            break;
                        }
                        case (3u, 17u):
                        {
                            var row = tables.PathExplorerActivate.GetEntry(mission.ObjectId);
                            if (row != null) { def.Kind = MissionKind.Activate; def.Creature = row.Creature2Id; def.Group = row.TargetGroupId; def.Count = Math.Max(1u, row.Count); }
                            break;
                        }
                        case (3u, 3u):
                        case (3u, 15u):
                        case (1u, 27u):
                        {
                            if (nodesByArea.TryGetValue(mission.ObjectId, out uint[] nodes) && nodes.Length > 0)
                            {
                                def.Kind = MissionKind.Area;
                                def.Locations = nodes;
                                def.Count = (uint)nodes.Length;
                            }
                            break;
                        }
                        case (3u, 16u):
                        {
                            // ExploreZone "CARTOGRAPHY: Auroria": ObjectId is the MapZone (names match); all of its hex groups discovered
                            int groups = Game.Map.ZoneMapIndex.Groups(mission.ObjectId).Count;
                            if (tables.MapZone.GetEntry(mission.ObjectId) != null && groups > 0)
                            {
                                def.Kind = MissionKind.Explore;
                                def.MapZone = (ushort)mission.ObjectId;
                                def.Count = (uint)groups;
                            }
                            break;
                        }
                        case (3u, 18u):
                        {
                            // PowerMap "TRACKING: follow the ...": the trail is the Spline2 that starts at the mission location (nearest start within 10 m)
                            var row = tables.PathExplorerPowerMap.GetEntry(mission.ObjectId);
                            if (row == null)
                                break;
                            foreach (uint locationId in new[] { mission.WorldLocation2Id00, row.WorldLocation2IdVisual })
                            {
                                WorldLocation2Entry start = tables.WorldLocation2.GetEntry(locationId);
                                if (start == null)
                                    continue;
                                var at = new Vector2(start.Position0, start.Position2);
                                var trail = splineNodes.Where(kv => splineWorld.TryGetValue(kv.Key, out uint w) && w == start.WorldId && kv.Value.Length > 1)
                                    .Select(kv => (Nodes: kv.Value, Distance: Vector2.Distance(at, new Vector2(kv.Value[0].X, kv.Value[0].Z))))
                                    .Where(t => t.Distance <= 10f).OrderBy(t => t.Distance).Select(t => t.Nodes).FirstOrDefault();
                                if (trail == null)
                                    continue;
                                def.Kind = MissionKind.PowerMap;
                                def.Trail = trail;
                                def.StartLocation = locationId;
                                def.Locations = new[] { locationId };
                                def.Threshold = Math.Max(10f, row.DistanceThreshold);
                                def.Count = Math.Max(1u, row.CollectQuantity);
                                break;
                            }
                            break;
                        }
                        case (3u, 12u):
                        {
                            var row = tables.PathExplorerDoor.GetEntry(mission.ObjectId);
                            if (row != null && row.WorldZoneIdInsideMicro != 0u)
                            {
                                def.Kind = MissionKind.Door;
                                def.MicroZone = row.WorldZoneIdInsideMicro;
                                def.DoorActivateGroup = row.TargetGroupIdActivate;
                                def.DoorKillGroup = row.TargetGroupIdKill;
                                def.Count = 1u + (row.TargetGroupIdActivate != 0u ? 1u : 0u) + (row.TargetGroupIdKill != 0u ? 1u : 0u);
                                foreach (var entrance in tables.PathExplorerDoorEntrance.Entries.Where(e => e.PathExplorerDoorId == row.Id))
                                {
                                    if (entrance.Creature2IdSurface != 0u) def.Targets.Add(entrance.Creature2IdSurface);
                                    if (entrance.Creature2IdMicro != 0u) def.StartCreatures.Add(entrance.Creature2IdMicro);
                                }
                            }
                            break;
                        }
                        case (3u, 13u):
                        {
                            var row = tables.PathExplorerScavengerHunt.GetEntry(mission.ObjectId);
                            if (row == null)
                                break;
                            uint[] clueIds = { row.PathExplorerScavengerClueId00, row.PathExplorerScavengerClueId01, row.PathExplorerScavengerClueId02, row.PathExplorerScavengerClueId03,
                                row.PathExplorerScavengerClueId04, row.PathExplorerScavengerClueId05, row.PathExplorerScavengerClueId06 };
                            foreach (uint id in clueIds.Where(c => c != 0u))
                            {
                                var clue = tables.PathExplorerScavengerClue.GetEntry(id);
                                if (clue == null)
                                    continue;
                                def.Clues.Add(new Clue { Id = clue.Id, Type = clue.ExplorerScavengerClueTypeEnum, Creature = clue.Creature2Id, Group = clue.TargetGroupId,
                                    Location = clue.WorldLocation2IdMiniMap, Radius = clue.ActiveRadius });
                            }
                            if (def.Clues.Count > 0)
                            {
                                def.Kind = MissionKind.Scavenger;
                                def.Count = (uint)def.Clues.Count;
                                if (row.Creature2IdStart != 0u)
                                    def.StartCreatures.Add(row.Creature2IdStart);
                            }
                            break;
                        }
                    }

                    if (def.Kind == MissionKind.Kill || def.Kind == MissionKind.Activate || def.Kind == MissionKind.Spell)
                        if (def.Creature == 0u && def.Group == 0u && def.Targets.Count == 0)
                            def.Kind = MissionKind.Unsupported;

                    byId[mission.Id] = def;
                    if (!built.TryGetValue((def.Zone, mission.PathTypeEnum), out List<MissionDef> list))
                        built[(def.Zone, mission.PathTypeEnum)] = list = new List<MissionDef>();
                    list.Add(def);
                    if (!builtWorld.TryGetValue((def.World, mission.PathTypeEnum), out List<MissionDef> worldList))
                        builtWorld[(def.World, mission.PathTypeEnum)] = worldList = new List<MissionDef>();
                    worldList.Add(def);
                }

                byWorld = builtWorld;
                index = built;
            }
        }

        private static bool InZone(uint zone, uint outer)
        {
            for (int depth = 0; zone != 0u && depth < 8; depth++)
            {
                if (zone == outer)
                    return true;
                zone = GameTableManager.Instance.WorldZone.GetEntry(zone)?.ParentZoneId ?? 0u;
            }
            return false;
        }

        /// <summary>
        /// All missions of the player's path in the world the player is in (PathEpisode.WorldId) that the player could take (faction, prerequisite).
        /// The episode's zone is where the mission is listed; its targets and locations are often in neighbouring zones (mission 1129, 1320: area
        /// nodes outside the episode zone), so progress counts anywhere in the episode's world.
        /// </summary>
        public List<MissionDef> GetAvailable()
        {
            EnsureIndex();
            if (player.Zone == null || player.Map == null)
                return new List<MissionDef>();

            // every spell success and kill asks: the list is kept for 5 s per world and path (prerequisites are not re-evaluated more often)
            if (cached != null && cachedWorld == player.Map.Entry.Id && cachedPath == (uint)player.Path && cacheAge < 5d)
                return cached;

            var available = byWorld.TryGetValue((player.Map.Entry.Id, (uint)player.Path), out List<MissionDef> list)
                ? list.Where(Available).ToList() : new List<MissionDef>();
            cached = available;
            cachedWorld = player.Map.Entry.Id;
            cachedPath = (uint)player.Path;
            cacheAge = 0d;

            if (loggedZones.Add(player.Zone.Id))
                foreach (var group in available.Where(d => d.Kind == MissionKind.Unsupported).GroupBy(d => (d.Entry.PathTypeEnum, d.Entry.PathMissionTypeEnum)))
                    FailureLedger.Record("path-mission-unsupported", $"path {group.Key.PathTypeEnum} type {group.Key.PathMissionTypeEnum}", $"{group.Count()} missions in zone {player.Zone.Id}");

            return available;
        }

        private readonly HashSet<uint> loggedZones = new();
        private List<MissionDef> cached;
        private uint cachedWorld, cachedPath;
        private double cacheAge;

        private bool Available(MissionDef def)
        {
            uint faction = def.Entry.PathMissionFactionEnum;
            if (faction == 1u && player.Faction1 != Reputation.Static.Faction.Exile)
                return false;
            if (faction == 2u && player.Faction1 != Reputation.Static.Faction.Dominion)
                return false;

            // ASSUMPTION: the unlock creature (talk to the contact) is skipped like quest givers are for bots
            return def.Entry.PrerequisiteId == 0u || Prerequisite.PrerequisiteManager.Instance.Meets(player, def.Entry.PrerequisiteId);
        }

        public static bool Matches(MissionDef def, uint creatureId)
        {
            return (def.Creature != 0u && def.Creature == creatureId) || (def.Group != 0u && TargetGroupHelper.Contains(def.Group, creatureId)) || def.Targets.Contains(creatureId);
        }

        public void OnCreatureKilled(uint creatureId, uint guid = 0u)
        {
            Advance(MissionKind.Kill, creatureId, guid);
            foreach (MissionDef def in GetAvailable())
            {
                if (def.Kind == MissionKind.Scavenger)
                    FindClue(def, c => c.Type == 0u && ClueMatches(c, creatureId));
                else if (def.Kind == MissionKind.Door && def.DoorKillGroup != 0u && TargetGroupHelper.Contains(def.DoorKillGroup, creatureId))
                    Node(def, 2u);
            }
        }

        public void OnCreatureActivated(uint creatureId) => OnCreatureActivated(creatureId, 0u, null);

        public void OnCreatureActivated(uint creatureId, uint guid, WorldEntity entity)
        {
            Advance(MissionKind.Activate, creatureId, guid);
            foreach (MissionDef def in GetAvailable())
            {
                switch (def.Kind)
                {
                    case MissionKind.Holdout when def.StartCreatures.Contains(creatureId):
                        StartHoldout(def, entity?.Position ?? player.Position);
                        break;
                    case MissionKind.Scavenger:
                        FindClue(def, c => c.Type == 1u && ClueMatches(c, creatureId));
                        break;
                    case MissionKind.Door when def.Targets.Contains(creatureId) || def.StartCreatures.Contains(creatureId):
                        GetState(def).Values[0u] = 1u; // the entrance is revealed
                        store.Dirty = true;
                        break;
                    case MissionKind.Door when def.DoorActivateGroup != 0u && TargetGroupHelper.Contains(def.DoorActivateGroup, creatureId):
                        Node(def, 1u);
                        break;
                    case MissionKind.Hub when def.Targets.Contains(creatureId):
                        Build(def, creatureId, entity?.Position ?? player.Position);
                        break;
                    case MissionKind.Infrastructure:
                        Infrastructure(def, creatureId, 0u);
                        break;
                }
            }
        }

        /// <summary>A spell cast by the player succeeded (target may be 0): scans, field studies, SWAT hits, settler resource spells.</summary>
        public void OnSpellSuccess(uint spell4Id, uint targetCreatureId, uint targetGuid)
        {
            if (targetCreatureId != 0u)
                Advance(MissionKind.Spell, targetCreatureId, targetGuid);
            if (targetCreatureId != 0u && ScanSpells.Contains(spell4Id))
                Advance(MissionKind.Scan, targetCreatureId, targetGuid);
            foreach (MissionDef def in GetAvailable())
                if (def.Kind == MissionKind.Infrastructure && def.ResourceSpells.Contains(spell4Id))
                    Infrastructure(def, 0u, spell4Id);
        }

        public void OnSpellSuccess(uint targetCreatureId) => OnSpellSuccess(0u, targetCreatureId, 0u);

        /// <summary>The explorer's dig (spell effect PathActionExplorerDig) at a position: buried scavenger hunt clues within their radius are found.</summary>
        public void OnDig(Vector3 position)
        {
            foreach (MissionDef def in GetAvailable())
                if (def.Kind == MissionKind.Scavenger)
                    FindClue(def, c => c.Type == 2u && NearLocation(c.Location, position, Math.Max(c.Radius, 20f)));
        }

        /// <summary>The player discovered a hex group of a map zone (ZoneMapManager): ExploreZone missions of that map zone count discovered groups.</summary>
        public void OnZoneMapExplored(ushort mapZoneId, int discovered)
        {
            foreach (MissionDef def in GetAvailable())
                if (def.Kind == MissionKind.Explore && def.MapZone == mapZoneId)
                    Progress(def, GetState(def), (uint)discovered);
        }

        private bool NearLocation(uint locationId, Vector3 position, float range)
        {
            WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(locationId);
            if (location == null || player.Map == null || location.WorldId != player.Map.Entry.Id)
                return false;
            float dx = position.X - location.Position0, dz = position.Z - location.Position2;
            return dx * dx + dz * dz <= range * range;
        }

        private static bool ClueMatches(Clue clue, uint creatureId)
        {
            return (clue.Creature != 0u && clue.Creature == creatureId) || (clue.Group != 0u && TargetGroupHelper.Contains(clue.Group, creatureId));
        }

        private void FindClue(MissionDef def, Func<Clue, bool> found)
        {
            State state = GetState(def);
            if (state.Done)
                return;
            foreach (Clue clue in def.Clues)
                if (!state.Nodes.Contains(clue.Id) && found(clue))
                {
                    state.Nodes.Add(clue.Id);
                    store.Dirty = true;
                    FailureLedger.Record("path-scavenger-clue", $"clue type {clue.Type}", $"mission {def.Entry.Id} clue {clue.Id}");
                }
            Progress(def, state, (uint)state.Nodes.Count);
        }

        private void Node(MissionDef def, uint node)
        {
            State state = GetState(def);
            if (state.Done || !state.Nodes.Add(node))
                return;
            store.Dirty = true;
            Progress(def, state, (uint)state.Nodes.Count);
        }

        private void Progress(MissionDef def, State state, uint progress)
        {
            if (state.Done)
                return;
            state.Progress = progress;
            store.Dirty = true;
            if (state.Progress >= def.Count)
                Complete(def, state);
            else
                SendUpdate(def, state);
        }

        /// <summary>
        /// A settler builds an improvement at a depot: hub missions add the group's contribution to its avenue (capped by the hub's avenue maximum),
        /// mayor missions count a build near each of their locations.
        /// </summary>
        private void Build(MissionDef def, uint depot, Vector3 at)
        {
            State state = GetState(def);
            if (state.Done)
                return;

            if (def.Entry.PathMissionTypeEnum == 25u)
            {
                for (uint i = 0u; i < def.Locations.Length; i++)
                    if (NearLocation(def.Locations[i], at, 80f))
                        Node(def, i);
                return;
            }

            // the first group of this depot whose avenue still has room and that has a tier left: tier t adds ContributionValue + t * PerTierBonus
            // (state.Values: avenue 0-2 -> contribution, 100 + group -> tiers built)
            foreach (var (groupId, (avenue, value)) in def.DepotGroups)
            {
                var group = GameTableManager.Instance.PathSettlerImprovementGroup.GetEntry(groupId);
                if (group?.Creature2IdDepot != depot || avenue > 2u)
                    continue;
                uint tiers = (uint)new[] { group.PathSettlerImprovementIdTier00, group.PathSettlerImprovementIdTier01, group.PathSettlerImprovementIdTier02, group.PathSettlerImprovementIdTier03 }.Count(t => t != 0u);
                state.Values.TryGetValue(100u + groupId, out uint built);
                state.Values.TryGetValue(avenue, out uint have);
                if (built >= Math.Max(1u, tiers) || (def.AvenueMax[avenue] > 0u && have >= def.AvenueMax[avenue]))
                    continue;
                uint add = Math.Max(1u, value + built * group.PerTierBonusContributionValue);
                state.Values[100u + groupId] = built + 1u;
                state.Values[avenue] = def.AvenueMax[avenue] > 0u ? Math.Min(def.AvenueMax[avenue], have + add) : have + add;
                FailureLedger.Record("path-settler-build", $"hub {def.Hub} avenue {avenue}", $"group {groupId} tier {built} +{add}");
                Progress(def, state, (uint)state.Values.Where(v => v.Key < 3u).Sum(v => (long)v.Value));
                return;
            }
        }

        private void Infrastructure(MissionDef def, uint creatureId, uint spell4Id)
        {
            State state = GetState(def);
            if (state.Done)
                return;

            state.Values.TryGetValue(0u, out uint held);
            if (def.Resources.Contains(creatureId) || def.ResourceSpells.Contains(spell4Id))
            {
                state.Values[0u] = Math.Min(def.MaxStack, held + 1u);
                store.Dirty = true;
            }
            else if (def.Targets.Contains(creatureId) && held > 0u)
            {
                state.Values[0u] = 0u;
                FailureLedger.Record("path-settler-deposit", $"infrastructure {def.Entry.ObjectId}", $"{held} resources");
                Progress(def, state, state.Progress + held);
            }
        }

        private void Advance(MissionKind kind, uint creatureId, uint guid)
        {
            foreach (MissionDef def in GetAvailable())
            {
                if (def.Kind != kind || !Matches(def, creatureId))
                    continue;

                State state = GetState(def);
                if (state.Done)
                    continue;

                switch (def.Nodes)
                {
                    case NodeMode.Count:
                        Progress(def, state, state.Progress + 1u);
                        break;
                    case NodeMode.Creature:
                        Node(def, creatureId);
                        break;
                    case NodeMode.Entity:
                        if (guid != 0u)
                            Node(def, guid);
                        break;
                    case NodeMode.Name:
                        Node(def, def.NameOf.TryGetValue(creatureId, out uint key) ? key : creatureId);
                        break;
                }
            }
        }

        /// <summary>
        /// A quest was completed: sheriff missions finish when all of their quests are.
        /// </summary>
        public void OnQuestCompleted(ushort questId)
        {
            foreach (MissionDef def in GetAvailable())
            {
                if (def.Kind != MissionKind.Quests || !def.Quests.Contains(questId))
                    continue;

                State state = GetState(def);
                if (state.Done)
                    continue;

                Progress(def, state, (uint)def.Quests.Count(q => player.QuestManager.GetQuestState(q) == Game.Quest.Static.QuestState.Completed));
            }
        }

        public void Update(double lastTick)
        {
            cacheAge += lastTick;
            if (holdout != null)
                UpdateHoldout(lastTick);
            if (powerMap != null)
                UpdatePowerMap(lastTick);

            timer -= lastTick;
            if (timer > 0d)
                return;

            timer = 1d;
            CheckAreas();

            saveTimer += 1d;
            if (store.Dirty && saveTimer >= 30d)
            {
                saveTimer = 0d;
                store.Save();
            }
        }

        private double saveTimer;

        /// <summary>
        /// A holdout starts when its start creature is used: the server spawns the waves at the start point (enemies from the mission's
        /// Jabbithole targets, <see cref="HoldoutUnitsPerWave"/> per wave), a wave counts when all its units are dead, the event fails after
        /// PathSoldierEvent.MaxEventTime (docs/ASSUMPTIONS.md "Path missions").
        /// </summary>
        private void StartHoldout(MissionDef def, Vector3 origin)
        {
            State state = GetState(def);
            if (state.Done || holdout != null || player.Map == null)
                return;

            if (noSpawnMissions.ContainsKey(def.Entry.Id))
                return;

            holdout = new Holdout { Def = def, Origin = origin };
            state.Progress = 0u;
            FailureLedger.Record("path-holdout-started", $"event type {GameTableManager.Instance.PathSoldierEvent.GetEntry(def.Entry.ObjectId)?.PathSoldierEventType}", $"mission {def.Entry.Id}, {def.Waves} waves, {def.MaxEventTime:0} s");
            // the first wave comes PathSoldierEvent.initialSpawnTime after the start (2-5 s on 203 of 356 rows, 0 on 56)
            if (def.InitialSpawn <= 0d)
                SpawnWave();
        }

        /// <summary>Holdouts that found no enemy to spawn (no listed target, no hostile creature around the start): not started again.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> noSpawnMissions = new();

        public static bool HoldoutWithoutEnemies(uint missionId) => noSpawnMissions.ContainsKey(missionId);

        /// <summary>
        /// Holdouts whose enemies no source lists: the hostile creatures spawned within 120 m of the control point, most frequent first (at most 4),
        /// the local faction a holdout defends against (guess, docs/ASSUMPTIONS.md "Path missions").
        /// </summary>
        private uint[] LocalHostiles(Vector3 origin)
        {
            var cache = Map.EntityCacheManager.Instance.GetEntityCache((ushort)player.Map.Entry.Id);
            if (cache == null)
                return Array.Empty<uint>();
            return cache.All
                .Where(m => Vector2.Distance(new Vector2(m.X, m.Z), new Vector2(origin.X, origin.Z)) <= 120f)
                .Where(m => GameTableManager.Instance.Creature2.GetEntry(m.Creature) is { } c
                    && player.GetDispositionTo((Reputation.Static.Faction)c.FactionId) == Reputation.Static.Disposition.Hostile)
                .GroupBy(m => m.Creature).OrderByDescending(g => g.Count()).Take(4).Select(g => g.Key).ToArray();
        }

        private void SpawnWave()
        {
            uint[] targets = holdout.Def.Targets.Where(c => GameTableManager.Instance.Creature2.GetEntry(c) != null).ToArray();
            if (targets.Length == 0)
                targets = holdout.LocalTargets ??= LocalHostiles(holdout.Origin);
            holdout.Units.RemoveAll(u => u.IsDead || u.Map == null);
            holdout.WaveTime = 0d;
            int spawned = 0;
            for (uint i = 0u; i < HoldoutUnitsPerWave && targets.Length > 0; i++)
            {
                uint creature = targets[(holdout.Spawned * HoldoutUnitsPerWave + i) % targets.Length];
                double angle = (holdout.Spawned * 3 + i) * 2.1d;
                var at = new Vector3(holdout.Origin.X + 12f * (float)Math.Cos(angle), holdout.Origin.Y, holdout.Origin.Z + 12f * (float)Math.Sin(angle));
                NonPlayer unit = CreatureSpawner.Spawn(player.Map, creature, at, holdout.Def.MaxEventTime + 30d, holdout);
                if (unit != null)
                {
                    holdout.Units.Add(unit);
                    spawned++;
                }
            }
            holdout.Spawned++;
            if (spawned == 0 && holdout.Units.Count == 0)
            {
                FailureLedger.Record("path-holdout-no-spawn", $"mission {holdout.Def.Entry.Id}", string.Join(",", targets.Take(4)));
                noSpawnMissions.TryAdd(holdout.Def.Entry.Id, true);
                holdout = null;
            }
        }

        private void UpdateHoldout(double lastTick)
        {
            holdout.Elapsed += lastTick;
            holdout.WaveTime += lastTick;
            State state = GetState(holdout.Def);

            if (player.IsDead || holdout.Elapsed > holdout.Def.MaxEventTime || player.Map == null || holdout.Units.Any(u => u.Map != null && u.Map != player.Map))
            {
                FailureLedger.Record("path-holdout-failed", player.IsDead ? "player died" : holdout.Elapsed > holdout.Def.MaxEventTime ? "time ran out" : "left the map",
                    $"mission {holdout.Def.Entry.Id}, wave {holdout.Wave + 1} of {holdout.Def.Waves}");
                foreach (NonPlayer unit in holdout.Units.Where(u => u.Map != null && !u.IsDead))
                    unit.RemoveFromMap();
                state.Progress = 0u;
                holdout = null;
                return;
            }

            // the first wave waits for PathSoldierEvent.initialSpawnTime
            if (holdout.Spawned == 0u)
            {
                if (holdout.WaveTime >= holdout.Def.InitialSpawn)
                    SpawnWave();
                return;
            }

            // a wave is beaten when its units are dead (or never entered the map within 5 s); all waves spawned so far count then
            if (holdout.Units.All(u => u.IsDead || (u.Map == null && holdout.WaveTime > 5d)))
            {
                holdout.Wave = holdout.Spawned;
                Progress(holdout.Def, state, holdout.Wave);
                if (state.Done || holdout.Wave >= holdout.Def.Waves)
                {
                    holdout = null;
                    return;
                }
                SpawnWave();
            }
            // PathSoldierEvent.maxTimeBetweenWaves (30-60 s on most rows): the next wave comes at the latest after that time, the rest of the
            // current one stays (field name certain, meaning likely)
            else if (holdout.Def.MaxBetweenWaves > 0d && holdout.WaveTime >= holdout.Def.MaxBetweenWaves && holdout.Spawned < holdout.Def.Waves)
            {
                FailureLedger.Record("path-holdout-wave-overlap", "next wave after maxTimeBetweenWaves", $"mission {holdout.Def.Entry.Id}");
                SpawnWave();
            }
        }

        private static Vector3 LeaderPosition(PowerMapRun run)
        {
            Vector3[] trail = run.Def.Trail;
            if (run.Segment >= trail.Length - 1)
                return trail[^1];
            Vector3 a = trail[run.Segment], b = trail[run.Segment + 1];
            float length = Vector3.Distance(a, b);
            return length <= 0.01f ? b : Vector3.Lerp(a, b, Math.Min(1f, run.Along / length));
        }

        /// <summary>
        /// Power map (explorer TRACKING missions): the player stands at the start location, the leader follows the trail (Spline2) at
        /// <see cref="PowerMapSpeed"/> while the player is within PathExplorerPowerMap.DistanceThreshold of it and waits otherwise; waiting longer
        /// than <see cref="PowerMapWaitLimit"/> fails the map. CollectQuantity drops lie evenly along the trail and are collected when the leader
        /// passes them with the player in range; maps without drops complete at the end of the trail (docs/ASSUMPTIONS.md "Path missions").
        /// </summary>
        private void StartPowerMap(MissionDef def)
        {
            if (powerMap != null || holdout != null || def.Trail.Length < 2)
                return;
            float length = 0f;
            for (int i = 0; i < def.Trail.Length - 1; i++)
                length += Vector3.Distance(def.Trail[i], def.Trail[i + 1]);
            powerMap = new PowerMapRun { Def = def, Length = Math.Max(1f, length) };
            GetState(def).Progress = 0u;
            FailureLedger.Record("path-powermap-started", $"mission {def.Entry.Id}", $"{def.Trail.Length} nodes, {length:0} m, range {def.Threshold:0} m, drops {def.Count}");
        }

        private void UpdatePowerMap(double lastTick)
        {
            PowerMapRun run = powerMap;
            State state = GetState(run.Def);
            WorldLocation2Entry start = GameTableManager.Instance.WorldLocation2.GetEntry(run.Def.StartLocation);
            if (player.IsDead || player.Map == null || start == null || player.Map.Entry.Id != start.WorldId || state.Done)
            {
                if (!state.Done)
                    FailureLedger.Record("path-powermap-failed", player.IsDead ? "player died" : "left the map", $"mission {run.Def.Entry.Id}");
                powerMap = null;
                return;
            }

            Vector3 leader = LeaderPosition(run);
            if (Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(leader.X, leader.Z)) > run.Def.Threshold)
            {
                run.Waiting += lastTick;
                if (run.Waiting > PowerMapWaitLimit)
                {
                    FailureLedger.Record("path-powermap-failed", "player out of range", $"mission {run.Def.Entry.Id} at {run.Travelled:0} of {run.Length:0} m");
                    state.Progress = 0u;
                    store.Dirty = true;
                    powerMap = null;
                }
                return;
            }

            run.Waiting = 0d;
            float step = PowerMapSpeed * (float)lastTick;
            run.Travelled += step;
            run.Along += step;
            while (run.Segment < run.Def.Trail.Length - 1)
            {
                float length = Vector3.Distance(run.Def.Trail[run.Segment], run.Def.Trail[run.Segment + 1]);
                if (run.Along < length)
                    break;
                run.Along -= length;
                run.Segment++;
            }

            // drops at (i + 1) / (n + 1) of the trail; maps without drops count the end of the trail
            bool collectsDrops = GameTableManager.Instance.PathExplorerPowerMap.GetEntry(run.Def.Entry.ObjectId)?.CollectQuantity > 0u;
            if (collectsDrops)
            {
                while (run.Collected < run.Def.Count && run.Travelled >= run.Length * (run.Collected + 1) / (run.Def.Count + 1))
                    run.Collected++;
                if (run.Collected != state.Progress)
                    Progress(run.Def, state, run.Collected);
            }

            if (run.Segment >= run.Def.Trail.Length - 1)
            {
                Progress(run.Def, state, run.Def.Count);
                FailureLedger.Record("path-powermap-end", $"mission {run.Def.Entry.Id}", $"{run.Length:0} m");
                powerMap = null;
            }
        }

        /// <summary>Units of the running holdout wave that are alive (bot driver).</summary>
        public IEnumerable<NonPlayer> HoldoutUnits => holdout == null ? Enumerable.Empty<NonPlayer>() : holdout.Units.Where(u => !u.IsDead && u.Map != null);

        /// <summary>
        /// Area missions: every node (a world location with a radius) of the area has to be visited. Door missions: the micro zone is entered.
        /// </summary>
        public void CheckAreas()
        {
            if (player.Map == null || player.Zone == null || (player.IsLoading && player.Session is not Bots.BotSession))
                return;

            foreach (MissionDef def in GetAvailable())
            {
                if (def.Kind == MissionKind.PowerMap)
                {
                    // the player at the start location: the map starts (the client shows a start button in range, its message is not known)
                    if (powerMap == null && !GetState(def).Done && NearLocation(def.StartLocation, player.Position, 15f))
                        StartPowerMap(def);
                    continue;
                }

                if (def.Kind == MissionKind.Explore)
                {
                    int discovered = player.ZoneMapManager.DiscoveredGroups(def.MapZone);
                    State explored = GetState(def);
                    if (!explored.Done && discovered > explored.Progress)
                        Progress(def, explored, (uint)discovered);
                    continue;
                }

                if (def.Kind == MissionKind.Door)
                {
                    if (player.ZoneAndParents().Contains(def.MicroZone))
                        Node(def, 0u);
                    continue;
                }

                if (def.Kind != MissionKind.Area)
                    continue;

                State state = GetState(def);
                if (state.Done)
                    continue;

                foreach (uint locationId in def.Locations)
                {
                    if (state.Nodes.Contains(locationId))
                        continue;

                    WorldLocation2Entry location = GameTableManager.Instance.WorldLocation2.GetEntry(locationId);
                    if (location == null || location.WorldId != player.Map.Entry.Id)
                        continue;

                    float dx = player.Position.X - location.Position0;
                    float dz = player.Position.Z - location.Position2;
                    float range = Math.Max(location.Radius, 8f);
                    if (dx * dx + dz * dz <= range * range
                        && (location.MaxVerticalDistance <= 0f || Math.Abs(player.Position.Y - location.Position1) <= Math.Max(location.MaxVerticalDistance, 10f)))
                    {
                        state.Nodes.Add(locationId);
                        store.Dirty = true;
                    }
                }

                Progress(def, state, (uint)state.Nodes.Count);
            }
        }

        private State GetState(MissionDef def)
        {
            if (!states.TryGetValue(def.Entry.Id, out State state))
            {
                states[def.Entry.Id] = state = new State();
                player.Session?.EnqueueMessageEncrypted(new NexusForever.WorldServer.Network.Message.Model.ServerPathMissionActivate
                {
                    Missions = { MissionPacket(def, state) }
                });
            }

            return state;
        }

        /// <summary>
        /// Mission types whose client progress (PathMission:GetNumCompleted, client function) is the number of set bits of the 32-bit user
        /// data: Area 3, Demolition 5, Rescue 6, ScavengerHunt 13, ScanChecklist 14, Vista 15, ActivateChecklist 17, FieldStudy 20, SpecimenSurvey 23,
        /// Mayor 25, Sheriff 26, Scout 27. The others read it as a plain count (Scan 2, Assassinate 4, SWAT 7, Hub 19, Infrastructure 21,
        /// DatacubeDiscovery 24). docs/ASSUMPTIONS.md "Path mission packets".
        /// </summary>
        private static readonly HashSet<uint> bitmaskTypes = new() { 3u, 5u, 6u, 13u, 14u, 15u, 17u, 20u, 23u, 25u, 26u, 27u };

        private static uint UserData(MissionDef def, State state)
        {
            if (!bitmaskTypes.Contains(def.Entry.PathMissionTypeEnum))
                return state.Progress;
            // which checklist entry each bit stands for is not tracked: the low bits give the client the right count
            return state.Progress >= 32u ? uint.MaxValue : (1u << (int)state.Progress) - 1u;
        }

        private static NexusForever.WorldServer.Network.Message.Model.ServerPathMissionActivate.Mission MissionPacket(MissionDef def, State state) => new()
        {
            MissionId = def.Entry.Id,
            Completed = state.Done,
            Userdata  = UserData(def, state)
        };

        private void SendUpdate(MissionDef def, State state)
        {
            player.Session?.EnqueueMessageEncrypted(new NexusForever.WorldServer.Network.Message.Model.ServerPathMissionUpdate
            {
                MissionId = (ushort)def.Entry.Id,
                Completed = state.Done,
                Userdata  = UserData(def, state)
            });
        }

        /// <summary>Every mission with a state at login (ServerPathMissionActivate, layout from the client reader).</summary>
        public void SendMissions()
        {
            var packet = new NexusForever.WorldServer.Network.Message.Model.ServerPathMissionActivate();
            foreach ((uint id, State state) in states)
                if (GetMission(id) is MissionDef def)
                    packet.Missions.Add(MissionPacket(def, state));
            if (packet.Missions.Count > 0)
                player.Session?.EnqueueMessageEncrypted(packet);
        }

        public State PeekState(uint missionId) => states.TryGetValue(missionId, out State s) ? s : null;

        private void Complete(MissionDef def, State state)
        {
            state.Done = true;
            store.Save();
            SendUpdate(def, state);
            string kind = $"path {def.Entry.PathTypeEnum} type {def.Entry.PathMissionTypeEnum} {def.Kind}";
            FailureLedger.Record("path-mission-completed", kind, $"mission {def.Entry.Id}");

            xpPerMission.TryGetValue(def.Entry.PathTypeEnum, out uint xp);
            if (xp > 0u)
            {
                try
                {
                    player.PathManager.AddXp(xp);
                }
                catch (Exception exception)
                {
                    // path level rewards (spells, items, titles) must not throw into the map update
                    FailureLedger.Record("path-reward-failed", $"{exception.GetType().Name}", $"mission {def.Entry.Id}: {exception.Message}");
                }
            }

            player.XpManager.GrantXp(Combat.CombatConfig.KillXp(player.Level) * 2u, Entity.Static.ExpReason.PathMission);

            // episode: all missions of the episode in this zone for the path are done
            EnsureIndex();
            if (index.TryGetValue((def.Zone, def.Entry.PathTypeEnum), out List<MissionDef> list)
                && list.Where(m => m.Entry.PathEpisodeId == def.Entry.PathEpisodeId && Available(m)).All(m => IsCompleted(m.Entry.Id)))
                FailureLedger.Record("path-episode-completed", $"path {def.Entry.PathTypeEnum} episode {def.Entry.PathEpisodeId}");
        }

        /// <summary>Number of missions in the tables by support, for reports and tests.</summary>
        public static (int Supported, int Unsupported) CountSupport()
        {
            EnsureIndex();
            return (byId.Values.Count(d => d.Kind != MissionKind.Unsupported), byId.Values.Count(d => d.Kind == MissionKind.Unsupported));
        }

        public static IEnumerable<MissionDef> AllMissions()
        {
            EnsureIndex();
            return byId.Values;
        }

        public static MissionDef GetMission(uint missionId)
        {
            EnsureIndex();
            return byId.TryGetValue(missionId, out MissionDef def) ? def : null;
        }

        /// <summary>Test hook: credit a mission directly (gt commands); bots play missions through the real handlers (Bots/BotPaths.cs).</summary>
        public void DriveMission(MissionDef def)
        {
            for (int i = 0; i < 1000 && !IsCompleted(def.Entry.Id); i++)
            {
                uint creature = def.Creature != 0u ? def.Creature : def.Group != 0u ? TargetGroupHelper.GetCreatures(def.Group).FirstOrDefault() : def.Targets.FirstOrDefault();
                switch (def.Kind)
                {
                    case MissionKind.Kill:
                        Advance(MissionKind.Kill, creature, (uint)i + 1u);
                        break;
                    case MissionKind.Activate:
                        Advance(MissionKind.Activate, def.Targets.Count > 0 ? def.Targets.ElementAt(i % def.Targets.Count) : creature, (uint)i + 1u);
                        break;
                    case MissionKind.Spell:
                        Advance(MissionKind.Spell, creature, (uint)i + 1u);
                        break;
                    case MissionKind.Scan:
                        Advance(MissionKind.Scan, def.Targets.ElementAt(i % def.Targets.Count), (uint)i + 1u);
                        break;
                    default:
                        return;
                }
            }
        }
    }
}
