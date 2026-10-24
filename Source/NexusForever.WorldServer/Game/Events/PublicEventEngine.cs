using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Game.Map.Search;
using NexusForever.WorldServer.Game.Quest;
using NexusForever.WorldServer.Game.Quest.Static;
using NexusForever.WorldServer.Game.Research;
using NLog;

namespace NexusForever.WorldServer.Game.Events
{
    /// <summary>
    /// Runs public events from the PublicEvent and PublicEventObjective tables. The tables hold the objectives (type, count, target group, timers,
    /// parent chain) but no script, so the engine plays the generic part: an event starts when a player comes near its location, objectives become
    /// active in parent order, kills and activations in the event area count, units the script would have spawned are spawned from the target group
    /// or from the creatures researched for the event, timed objectives run out, and script driven objectives complete once everything else is done.
    /// Every guess is counted in the failure ledger (event-*) and listed in docs/ASSUMPTIONS.md.
    /// </summary>
    public sealed class PublicEventEngine : Singleton<PublicEventEngine>, IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();
        private static readonly Random random = new();
        private readonly object gate = new();

        public enum ObjectiveStatus { Inactive, Active, Succeeded, Failed }

        // PublicEventObjectiveType values of the client enum
        private const uint KillTargetGroup = 0, KillEventUnit = 1, CollectItem = 2, ActivateTargetGroup = 3, ActivateTargetGroupChecklist = 4, Script = 5,
            ParticipantsInTriggerVolume = 6, KillEventObjectiveUnit = 8, ScriptWithoutCount = 9, DefendObjectiveUnit = 12, TimedWin = 13,
            KillClusterTargetGroup = 14, KillClusterEventUnit = 15, KillClusterEventObjectiveUnit = 16, Exterminate = 20, ScriptWithoutMax = 25;

        // PublicEventType values: everything except pvp (warplots, battlegrounds, arenas); dungeons and raids run only when a player is in their world
        private static bool Startable(uint type) => type is not (2u or 3u or 4u or 5u or 6u or 10u);

        public sealed class Template
        {
            public PublicEventEntry Entry;
            public List<PublicEventObjectiveEntry> Objectives = new();
            public ushort World;
            public Vector3 Center;
            public float Radius;
            public string Name;
            /// <summary>
            /// World boss events (all 7 "[GROUP 20+]" boss events share it): an initial TimedWin "resetting" objective of 3600 s, a type 7
            /// objective "place 75 Progenitor Access Particles" (PublicEventVirtualItemDepot 36), a 120 s TimedWin "will break out" countdown
            /// and a KillTargetGroup of the boss. 0 for other events.
            /// </summary>
            public double WorldBossReset;
            public double WorldBossCountdown;
            public uint WorldBossKill;
        }

        public sealed class Objective
        {
            public PublicEventObjectiveEntry Entry;
            public ObjectiveStatus Status;
            public uint Count;
            public double Active;
            public bool Unsupported;
            // ASSUMPTION (docs/ASSUMPTIONS.md): objectives with no data behind them are played, not credited: Counted = every kill by a participant in the event area counts,
            // Presence = a participant stays in the event area for a while
            public bool Counted, Presence;
            public List<NonPlayer> Units = new();
            public uint Needed => Entry.Count > 0u ? Entry.Count : 6u;
        }

        public sealed class Run
        {
            public Template Template;
            public BaseMap Map;
            public Dictionary<uint, Objective> Objectives = new();
            public HashSet<ulong> Participants = new();
            public double Elapsed;
            public double EmptyFor;
            public bool Finished;
            public double Cleanup;
        }

        private readonly Dictionary<ushort, List<Template>> byWorld = new();
        private readonly Dictionary<uint, Template> byId = new();
        private readonly Dictionary<uint, Template> byObjective = new();
        private readonly List<Run> runs = new();
        private readonly Dictionary<(ushort, uint), double> cooldowns = new();
        private readonly Dictionary<uint, (Template Template, double Seconds)> worldBossRespawn = new();

        /// <summary>
        /// Respawn time of a world boss creature: the event's reset timer plus its countdown (3600 + 120 s for every world boss), null for
        /// other creatures (docs/ASSUMPTIONS.md "World bosses", certain from PublicEventObjective).
        /// </summary>
        public double? WorldBossRespawnSeconds(uint creatureId)
        {
            lock (gate)
            {
                Build();
                return worldBossRespawn.TryGetValue(creatureId, out var boss) ? boss.Seconds : null;
            }
        }

        /// <summary>World boss events found in the tables: event id, reset s, countdown s.</summary>
        public List<(uint Event, double Reset, double Countdown)> WorldBossEvents()
        {
            lock (gate)
            {
                Build();
                return byId.Values.Where(t => t.WorldBossReset > 0d).Select(t => (t.Entry.Id, t.WorldBossReset, t.WorldBossCountdown)).ToList();
            }
        }
        private bool built;

        public int ActiveRuns { get { lock (gate) return runs.Count(r => !r.Finished); } }

        private void Build()
        {
            if (built)
                return;

            built = true;
            var gtm = GameTableManager.Instance;
            var text = gtm.GetTextTable(NexusForever.Shared.GameTable.Static.Language.English);
            // CompleteEvent quest objectives name a PublicEventObjective (data = objective id). Events without a location of their own use
            // the indicator of the quest objective that asks for one of their objectives
            var questLocation = new Dictionary<uint, uint>();
            foreach (QuestInfo info in GlobalQuestManager.Instance.GetAllQuestInfo())
                foreach (var objective in info.Objectives.Where(o => o.Type == QuestObjectiveType.CompleteEvent))
                {
                    uint indicator = new[] { objective.Entry.WorldLocationsIdIndicator00, objective.Entry.WorldLocationsIdIndicator01, objective.Entry.WorldLocationsIdIndicator02, objective.Entry.WorldLocationsIdIndicator03 }.FirstOrDefault(i => i != 0u);
                    if (indicator != 0u)
                        questLocation.TryAdd(objective.Entry.Data, indicator);
                }

            foreach (PublicEventEntry entry in gtm.PublicEvent.Entries)
            {
                if (!Startable(entry.PublicEventTypeEnum))
                    continue;

                var template = new Template { Entry = entry, World = (ushort)entry.WorldId, Name = text?.GetEntry(entry.LocalizedTextIdName) ?? "" };
                template.Objectives = gtm.PublicEventObjective.Entries.Where(o => o.PublicEventId == entry.Id && o.PublicEventTeamId <= 1u).OrderBy(o => o.DisplayOrder).ThenBy(o => o.Id).ToList();
                if (template.Objectives.Count == 0)
                    continue;

                // location: the event's own, else the first objective that has one
                uint locationId = entry.WorldLocation2Id;
                if (locationId == 0u)
                    locationId = template.Objectives.Select(o => o.WorldLocation2Id).FirstOrDefault(l => l != 0u);
                if (locationId == 0u)
                    locationId = template.Objectives.Select(o => questLocation.GetValueOrDefault(o.Id)).FirstOrDefault(l => l != 0u);
                if (locationId == 0u)
                    locationId = questLocation.GetValueOrDefault(entry.Id);
                WorldLocation2Entry location = locationId != 0u ? gtm.WorldLocation2.GetEntry(locationId) : null;
                if (location == null)
                    continue;

                PublicEventObjectiveEntry reset = template.Objectives.FirstOrDefault(o => o.PublicEventObjectiveTypeEnum == TimedWin && (o.PublicEventObjectiveFlags & 1u) != 0u && o.FailureTimeMs >= 600000u);
                PublicEventObjectiveEntry depot = template.Objectives.FirstOrDefault(o => o.PublicEventObjectiveTypeEnum == 7u && o.Count > 0u);
                PublicEventObjectiveEntry kill = template.Objectives.FirstOrDefault(o => o.PublicEventObjectiveTypeEnum == KillTargetGroup && o.ObjectId != 0u);
                if (reset != null && depot != null && kill != null)
                {
                    template.WorldBossReset = reset.FailureTimeMs / 1000d;
                    template.WorldBossCountdown = template.Objectives.Where(o => o.PublicEventObjectiveTypeEnum == TimedWin && o != reset).Select(o => o.FailureTimeMs / 1000d).DefaultIfEmpty(0d).Max();
                    template.WorldBossKill = kill.Id;
                    foreach (uint creatureId in TargetGroupHelper.GetCreatures(kill.ObjectId))
                        worldBossRespawn[creatureId] = (template, template.WorldBossReset + template.WorldBossCountdown);
                }

                template.Center = new Vector3(location.Position0, location.Position1, location.Position2);
                template.Radius = Math.Clamp(location.Radius, 40f, 90f);
                if (template.World == 0 && location.WorldId != 0u)
                    template.World = (ushort)location.WorldId;

                if (!byWorld.TryGetValue(template.World, out var list))
                    byWorld[template.World] = list = new List<Template>();
                list.Add(template);
                byId[entry.Id] = template;
                foreach (var o in template.Objectives)
                    byObjective[o.Id] = template;
            }

            log.Info($"Public events: {byId.Count} startable events with a location in {byWorld.Count} worlds.");
        }

        /// <summary>
        /// Called every couple of seconds for each player: join a running event nearby, or start one whose location the player reached.
        /// </summary>
        public void PlayerTick(Player player)
        {
            // bots never finish the client loading handshake
            if (player.Map == null || player.IsDead || (player.IsLoading && player.Session is not Bots.BotSession))
                return;

            lock (gate)
            {
                Build();
                if (!byWorld.TryGetValue((ushort)player.Map.Entry.Id, out var templates))
                    return;

                foreach (Template template in templates)
                {
                    if (Dist2D(player.Position, template.Center) > template.Radius)
                        continue;

                    Run run = runs.FirstOrDefault(r => !r.Finished && r.Template == template && r.Map == player.Map);
                    if (run != null)
                    {
                        run.Participants.Add(player.CharacterId);
                        run.EmptyFor = 0d;
                        continue;
                    }

                    if (cooldowns.TryGetValue((template.World, template.Entry.Id), out double until) && until > Now)
                        continue;

                    if (player.Level < template.Entry.MinPlayerLevel)
                    {
                        Diagnostics.FailureLedger.Record("event-skipped", "player level below the event minimum", $"event {template.Entry.Id} min {template.Entry.MinPlayerLevel}");
                        continue;
                    }

                    Start(template, player);
                }
            }
        }

        // the tables' height is not the terrain height, events are circles on the ground
        private static float Dist2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

        private double clock;
        private double Now => clock;

        private void Start(Template template, Player first)
        {
            var run = new Run { Template = template, Map = first.Map };
            run.Participants.Add(first.CharacterId);
            foreach (PublicEventObjectiveEntry entry in template.Objectives)
                run.Objectives[entry.Id] = new Objective { Entry = entry, Status = ObjectiveStatus.Inactive };

            if (template.WorldBossReset > 0d)
            {
                // no kill within the reset time (the cooldown check before Start): the reset timer and the countdown have run out, the boss is up
                foreach (Objective objective in run.Objectives.Values)
                {
                    if (objective.Entry.Id == template.WorldBossKill)
                        Activate(run, objective);
                    else
                        objective.Status = ObjectiveStatus.Succeeded;
                }
                Diagnostics.FailureLedger.Record("world-boss", "event started with the boss up (reset and countdown elapsed)", $"event {template.Entry.Id}");
            }
            else
            {
                bool anyInitial = template.Objectives.Any(o => (o.PublicEventObjectiveFlags & 1u) != 0u);
                foreach (Objective objective in run.Objectives.Values)
                    if ((anyInitial && (objective.Entry.PublicEventObjectiveFlags & 1u) != 0u) || (!anyInitial && objective.Entry.PublicEventObjectiveIdParent == 0u))
                        Activate(run, objective);
            }

            runs.Add(run);
            Diagnostics.FailureLedger.Record("event-started", $"event {template.Entry.Id} {template.Name}");
            log.Trace($"Public event {template.Entry.Id} ({template.Name}) started by character {first.CharacterId}.");
        }

        private void Activate(Run run, Objective objective)
        {
            objective.Status = ObjectiveStatus.Active;
            objective.Active = 0d;
            uint type = objective.Entry.PublicEventObjectiveTypeEnum;
            switch (type)
            {
                case Exterminate:
                    // kill everything of the group in the area; spawn some when the area has none of them
                    SpawnUnits(run, objective, GroupCreatures(objective.Entry.ObjectId), false, tolerateNone: true);
                    break;
                case KillTargetGroup:
                case KillClusterTargetGroup:
                case ActivateTargetGroup:
                case ActivateTargetGroupChecklist:
                    SpawnUnits(run, objective, GroupCreatures(objective.Entry.ObjectId), type is ActivateTargetGroup or ActivateTargetGroupChecklist);
                    break;
                case KillEventUnit:
                case KillClusterEventUnit:
                case KillEventObjectiveUnit:
                case KillClusterEventObjectiveUnit:
                case DefendObjectiveUnit:
                    SpawnUnits(run, objective, EventCreatures(run, objective), false);
                    break;
                case TimedWin:
                case ParticipantsInTriggerVolume:
                    break;
                default:
                    objective.Unsupported = true;
                    Diagnostics.FailureLedger.Record("event-objective-script", $"type {type} completes when the rest of the event is done", $"event {run.Template.Entry.Id} objective {objective.Entry.Id}");
                    break;
            }

            if (objective.Unsupported)
            {
                objective.Unsupported = false;
                if (objective.Entry.Count >= 2u)
                    objective.Counted = true;
                else
                    objective.Presence = true;

                Diagnostics.FailureLedger.Record("event-objective-played", $"type {type} {(objective.Counted ? "counts kills in the area" : "needs a participant in the area")}", $"event {run.Template.Entry.Id} objective {objective.Entry.Id}");
            }
        }

        private static List<uint> GroupCreatures(uint groupId)
        {
            if (groupId == 0u)
                return new List<uint>();

            var explicitIds = TargetGroupHelper.GetCreatures(groupId);
            if (explicitIds.Count > 0)
                return explicitIds.ToList();

            // a group built from race, faction or similar filters: only creatures the world has a spawn for
            var resolved = TargetGroupHelper.ResolveSpawned(groupId);
            return resolved.Count <= 40 ? resolved.ToList() : new List<uint>();
        }

        private static List<uint> EventCreatures(Run run, Objective objective)
        {
            // a creature id in the objective, else the creatures researched for the event, else the target group creatures of its other objectives
            if (objective.Entry.ObjectId != 0u && GameTableManager.Instance.Creature2.GetEntry(objective.Entry.ObjectId) != null)
                return new List<uint> { objective.Entry.ObjectId };

            var researched = EventResearch.GetCreatures(run.Template.Name, run.Template.Entry.WorldZoneId);
            if (researched.Count > 0)
                return researched;

            var sibling = run.Template.Objectives.Where(o => o.PublicEventObjectiveTypeEnum is KillTargetGroup or KillClusterTargetGroup && o.ObjectId != 0u)
                .SelectMany(o => GroupCreatures(o.ObjectId)).Distinct().ToList();
            return sibling;
        }

        /// <summary>
        /// Make sure the objective has enough targets: count the living matching creatures near the event, spawn the missing ones.
        /// </summary>
        private void SpawnUnits(Run run, Objective objective, List<uint> creatureIds, bool activatable, bool tolerateNone = false)
        {
            if (creatureIds.Count == 0)
            {
                if (tolerateNone)
                    return; // anything killed in the area counts

                objective.Unsupported = true;
                Diagnostics.FailureLedger.Record("event-no-creature-source", $"objective type {objective.Entry.PublicEventObjectiveTypeEnum} has no creature to spawn, completes when the rest is done", $"event {run.Template.Entry.Id} objective {objective.Entry.Id}");
                return;
            }

            // instances (dungeons, raids, adventures) are populated from their spawn data and run their own encounters; events there only
            // count what the players do, they never add creatures
            if (run.Map is MapInstance)
                return;

            run.Map.Search(run.Template.Center, run.Template.Radius, new SearchCheckRange(run.Template.Center, run.Template.Radius), out List<GridEntity> nearby);
            int present = nearby.OfType<NonPlayer>().Count(n => !n.IsDead && creatureIds.Contains(n.CreatureId));
            uint missing = (uint)Math.Max(0, (int)objective.Needed - present);
            // events want a few more than the count so it can be reached while some die elsewhere
            missing = Math.Min(missing + (missing > 0u ? 2u : 0u), 40u);
            for (uint i = 0; i < missing; i++)
            {
                uint creatureId = creatureIds[random.Next(creatureIds.Count)];
                NonPlayer unit = SpawnUnit(run, creatureId);
                if (unit != null)
                    objective.Units.Add(unit);
            }

            if (missing > 0u)
                Diagnostics.FailureLedger.Record("event-units-spawned", $"{missing} units for objective type {objective.Entry.PublicEventObjectiveTypeEnum}", $"event {run.Template.Entry.Id}");
        }

        private NonPlayer SpawnUnit(Run run, uint creatureId)
        {
            Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(creatureId);
            if (creature == null)
                return null;

            double angle = random.NextDouble() * Math.PI * 2d;
            float distance = 8f + (float)random.NextDouble() * Math.Min(20f, run.Template.Radius - 10f);
            float x = run.Template.Center.X + (float)Math.Cos(angle) * distance;
            float z = run.Template.Center.Z + (float)Math.Sin(angle) * distance;
            float y = run.Map.GetTerrainHeight(x, z, run.Template.Center.Y);
            if (float.IsNaN(y) || y < -5000f)
                y = run.Template.Center.Y;

            Creature2DisplayGroupEntryEntry displayGroup = GameTableManager.Instance.Creature2DisplayGroupEntry.Entries.FirstOrDefault(d => d.Creature2DisplayGroupId == creature.Creature2DisplayGroupId);
            var unit = new NonPlayer { EventTag = run };
            unit.Initialise(new NexusForever.Database.World.Model.EntityModel
            {
                Type        = (byte)EntityType.NonPlayer,
                Creature    = creature.Id,
                World       = (ushort)run.Map.Entry.Id,
                X           = x,
                Y           = y,
                Z           = z,
                DisplayInfo = displayGroup?.Creature2DisplayInfoId ?? 0u,
                Faction1    = (ushort)creature.FactionId,
                Faction2    = (ushort)creature.FactionId
            });
            unit.SummonLifetime = 1800d;

            var position = new MapPosition { Position = new Vector3(x, y, z) };
            if (!run.Map.CanEnter(unit, position))
                return null;

            run.Map.EnqueueAdd(unit, position);
            return unit;
        }

        /// <summary>
        /// A player killed a creature: it counts for kill objectives of events running on that map when it is an event unit or stands in the event area.
        /// </summary>
        public void OnCreatureKilled(NonPlayer creature, IReadOnlyCollection<Player> credited)
        {
            lock (gate)
            {
                // a world boss kill (in or outside a run) starts the reset: the event and the boss come back after reset + countdown
                if (worldBossRespawn.TryGetValue(creature.CreatureId, out var boss) && creature.Map != null)
                {
                    var key = ((ushort)creature.Map.Entry.Id, boss.Template.Entry.Id);
                    cooldowns[key] = Math.Max(cooldowns.GetValueOrDefault(key), Now + boss.Seconds);
                    Diagnostics.FailureLedger.Record("world-boss", "boss killed, reset started", $"event {boss.Template.Entry.Id} creature {creature.CreatureId} by {credited.Count} players");
                }

                foreach (Run run in runs.Where(r => !r.Finished && r.Map == creature.Map))
                {
                    bool tagged = creature.EventTag == run;
                    bool inArea = Dist2D(creature.Position, run.Template.Center) <= run.Template.Radius + 30f;
                    if (!tagged && !inArea)
                        continue;

                    foreach (Player player in credited)
                        run.Participants.Add(player.CharacterId);

                    foreach (Objective objective in run.Objectives.Values.Where(o => o.Status == ObjectiveStatus.Active && !o.Unsupported))
                    {
                        uint type = objective.Entry.PublicEventObjectiveTypeEnum;
                        bool counts = objective.Counted || type switch
                        {
                            KillTargetGroup or KillClusterTargetGroup => TargetGroupHelper.Contains(objective.Entry.ObjectId, creature.CreatureId),
                            KillEventUnit or KillClusterEventUnit or KillEventObjectiveUnit or KillClusterEventObjectiveUnit => objective.Units.Contains(creature) || tagged,
                            Exterminate => objective.Entry.ObjectId == 0u || GroupCreatures(objective.Entry.ObjectId).Count == 0 || TargetGroupHelper.Contains(objective.Entry.ObjectId, creature.CreatureId),
                            _ => false
                        };

                        if (counts)
                            Progress(run, objective, 1u);
                    }
                }
            }
        }

        /// <summary>
        /// A player activated a creature (target group objectives).
        /// </summary>
        public void OnCreatureActivated(Player player, WorldEntity creature)
        {
            lock (gate)
            {
                foreach (Run run in runs.Where(r => !r.Finished && r.Map == creature.Map))
                {
                    if (Dist2D(creature.Position, run.Template.Center) > run.Template.Radius + 30f)
                        continue;

                    run.Participants.Add(player.CharacterId);
                    foreach (Objective objective in run.Objectives.Values.Where(o => o.Status == ObjectiveStatus.Active && !o.Unsupported
                        && o.Entry.PublicEventObjectiveTypeEnum is ActivateTargetGroup or ActivateTargetGroupChecklist))
                    {
                        if (TargetGroupHelper.Contains(objective.Entry.ObjectId, creature.CreatureId))
                            Progress(run, objective, 1u);
                    }
                }
            }
        }

        private void Progress(Run run, Objective objective, uint amount)
        {
            objective.Count += amount;
            if (objective.Count >= objective.Needed)
                Succeed(run, objective);
        }

        private void Succeed(Run run, Objective objective)
        {
            if (objective.Status != ObjectiveStatus.Active)
                return;

            objective.Status = ObjectiveStatus.Succeeded;

            // quests ask for single objectives (CompleteEvent data = objective id)
            foreach (ulong characterId in run.Participants)
            {
                Player player = CharacterCache.CharacterManager.Instance.GetPlayer(characterId);
                if (player != null && player.Map == run.Map)
                    player.QuestManager.ObjectiveUpdate(QuestObjectiveType.CompleteEvent, objective.Entry.Id, 1u);
            }

            foreach (Objective child in run.Objectives.Values.Where(o => o.Entry.PublicEventObjectiveIdParent == objective.Entry.Id && o.Status == ObjectiveStatus.Inactive))
                Activate(run, child);
        }

        public void Update(double lastTick)
        {
            lock (gate)
            {
                clock += lastTick;
                if (runs.Count == 0)
                    return;

                foreach (Run run in runs.ToList())
                {
                    if (run.Finished)
                    {
                        run.Cleanup -= lastTick;
                        if (run.Cleanup <= 0d)
                        {
                            foreach (NonPlayer unit in run.Objectives.Values.SelectMany(o => o.Units).Distinct())
                                if (unit.Map != null && !unit.IsDead)
                                    unit.RemoveFromMap();
                            runs.Remove(run);
                        }
                        continue;
                    }

                    UpdateRun(run, lastTick);
                }
            }
        }

        private void UpdateRun(Run run, double lastTick)
        {
            run.Elapsed += lastTick;

            // nobody around for two minutes: the event is abandoned
            run.EmptyFor += lastTick;
            if (run.EmptyFor > 120d)
            {
                Finish(run, false, "abandoned");
                return;
            }

            if (run.Elapsed > 45d || run.Objectives.Values.Any(o => o.Status == ObjectiveStatus.Succeeded))
                foreach (Objective late in run.Objectives.Values.Where(o => o.Status == ObjectiveStatus.Inactive && o.Entry.PublicEventObjectiveIdParent == 0u).ToList())
                    Activate(run, late);

            foreach (Objective objective in run.Objectives.Values.Where(o => o.Status == ObjectiveStatus.Active).ToList())
            {
                objective.Active += lastTick;
                uint type = objective.Entry.PublicEventObjectiveTypeEnum;
                double limit = objective.Entry.FailureTimeMs / 1000d;

                if (objective.Presence)
                {
                    run.Map.Search(run.Template.Center, run.Template.Radius, new SearchCheckRange(run.Template.Center, run.Template.Radius), out List<GridEntity> present);
                    if (objective.Active >= 15d && present.OfType<Player>().Any(p => !p.IsDead))
                        Succeed(run, objective);
                    else if (limit > 0d && objective.Active >= limit)
                    {
                        Fail(run, objective, "nobody was in the event area");
                        return;
                    }
                    continue;
                }

                if (type == TimedWin)
                {
                    if (limit <= 0d || objective.Active >= limit)
                        Succeed(run, objective);
                    continue;
                }

                if (type == DefendObjectiveUnit && !objective.Unsupported)
                {
                    if (objective.Units.Count > 0 && objective.Units.All(u => u.IsDead))
                    {
                        Fail(run, objective, "defended unit died");
                        return;
                    }

                    if (limit > 0d && objective.Active >= limit)
                        Succeed(run, objective);
                    continue;
                }

                if (type == ParticipantsInTriggerVolume)
                {
                    run.Map.Search(run.Template.Center, run.Template.Radius, new SearchCheckRange(run.Template.Center, run.Template.Radius), out List<GridEntity> found);
                    uint inside = (uint)found.OfType<Player>().Count(p => !p.IsDead);
                    objective.Count = inside;
                    if (objective.Count >= Math.Min(objective.Needed, Math.Max(1u, (uint)run.Participants.Count)))
                        Succeed(run, objective);
                    continue;
                }

                if (objective.Counted && objective.Active >= 300d && (limit <= 0d || limit > 300d))
                {
                    Diagnostics.FailureLedger.Record("event-objective-synthetic", $"type {type} counted objective credited after 300 s", $"event {run.Template.Entry.Id} objective {objective.Entry.Id}");
                    Succeed(run, objective);
                    continue;
                }

                if (limit > 0d && objective.Active >= limit && !objective.Unsupported)
                {
                    Fail(run, objective, "objective timer ran out");
                    return;
                }
            }

            // script driven objectives and objectives without creatures complete once all the others are done (or after 60 s)
            var unsupported = run.Objectives.Values.Where(o => o.Status == ObjectiveStatus.Active && o.Unsupported).ToList();
            if (unsupported.Count > 0)
            {
                bool othersDone = run.Objectives.Values.All(o => o.Unsupported || o.Status is ObjectiveStatus.Succeeded or ObjectiveStatus.Failed || o.Status == ObjectiveStatus.Inactive && o.Entry.PublicEventObjectiveIdParent != 0u);
                foreach (Objective objective in unsupported)
                    if (othersDone || objective.Active >= 60d)
                    {
                        Diagnostics.FailureLedger.Record("event-objective-synthetic", $"type {objective.Entry.PublicEventObjectiveTypeEnum} credited without play", $"event {run.Template.Entry.Id} objective {objective.Entry.Id}");
                        Succeed(run, objective);
                    }
            }

            if (run.Objectives.Values.All(o => o.Status == ObjectiveStatus.Succeeded))
                Finish(run, true, "all objectives done");
            else if (run.Template.Entry.FailureTimeMs > 0u && run.Elapsed > run.Template.Entry.FailureTimeMs / 1000d)
                Finish(run, false, "event timer ran out");
            else if (run.Elapsed > 1800d)
                Finish(run, false, "30 minute cap");
        }

        private void Fail(Run run, Objective objective, string reason)
        {
            objective.Status = ObjectiveStatus.Failed;
            Finish(run, false, reason);
        }

        private void Finish(Run run, bool success, string reason)
        {
            run.Finished = true;
            run.Cleanup = 30d;
            var cooldownKey = (run.Template.World, run.Template.Entry.Id);
            cooldowns[cooldownKey] = Math.Max(cooldowns.GetValueOrDefault(cooldownKey), Now + (success ? 300d : 120d));
            lastResult[run.Template.Entry.Id] = success ? "succeeded" : "failed: " + reason;
            Diagnostics.FailureLedger.Record(success ? "event-succeeded" : "event-failed", $"event {run.Template.Entry.Id} {run.Template.Name}", reason);
            if (!success)
                return;

            foreach (ulong characterId in run.Participants)
            {
                Player player = CharacterCache.CharacterManager.Instance.GetPlayer(characterId);
                if (player == null || player.Map != run.Map)
                    continue;

                player.QuestManager.ObjectiveUpdate(QuestObjectiveType.CompleteEvent, run.Template.Entry.Id, 1u);
                player.XpManager.GrantXp(Combat.CombatConfig.KillXp(player.Level) * 5u, ExpReason.PublicEvent);
            }
        }

        /// <summary>
        /// True when the engine can run the event (it has a location in a world), so a quest asking for it is played for real.
        /// </summary>
        // CompleteEvent data is a PublicEventObjective id in most quests and a PublicEvent id in the rest (the ids overlap, objective first)
        private Template Resolve(uint data, bool isEvent = false) => isEvent ? byId.GetValueOrDefault(data) : byObjective.TryGetValue(data, out Template t) ? t : byId.TryGetValue(data, out t) ? t : null;

        /// <summary>
        /// Status of a PublicEventObjective in the event run on the supplied map (a live run before a finished one), Inactive when no run has it.
        /// The numbers equal the client's PublicEventStatus Lua enum (Inactive 0, Active 1, Succeeded 2, Failed 3, read from the client code), which prerequisite type 118 compares against.
        /// </summary>
        public ObjectiveStatus StatusOf(uint objectiveId, BaseMap map)
        {
            lock (gate)
            {
                Run run = runs.Where(r => r.Map == map && r.Objectives.ContainsKey(objectiveId)).OrderBy(r => r.Finished).FirstOrDefault();
                return run?.Objectives[objectiveId].Status ?? ObjectiveStatus.Inactive;
            }
        }

        /// <summary>True while an unfinished run of the public event is on the map (prerequisite type 117).</summary>
        public bool IsRunning(uint eventId, BaseMap map)
        {
            lock (gate)
                return runs.Any(r => !r.Finished && r.Map == map && r.Template.Entry.Id == eventId);
        }

        public bool CanRun(uint objectiveId)
        {
            lock (gate)
            {
                Build();
                return Resolve(objectiveId) != null;
            }
        }

        /// <summary>Events a bot test can send players to (one entry per startable event with a location in a world the filter accepts).</summary>
        public List<uint> TestableEvents(Func<ushort, bool> worldOk)
        {
            lock (gate)
            {
                Build();
                return byId.Values.Where(t => worldOk(t.World)).Select(t => t.Entry.Id).OrderBy(i => i).ToList();
            }
        }

        private readonly Dictionary<uint, string> lastResult = new();

        /// <summary>"running", the result of the last run (succeeded, failed: reason) or "never".</summary>
        public string State(uint eventId)
        {
            lock (gate)
            {
                if (runs.Any(r => !r.Finished && r.Template.Entry.Id == eventId))
                    return "running";
                return lastResult.GetValueOrDefault(eventId, "never");
            }
        }

        public bool TryGetLocation(uint objectiveId, out ushort world, out Vector3 center, out float radius, bool isEvent = false)
        {
            lock (gate)
            {
                Build();
                if (Resolve(objectiveId, isEvent) is { } t)
                {
                    world = t.World;
                    center = t.Center;
                    radius = t.Radius;
                    return true;
                }
            }

            world = 0;
            center = default;
            radius = 0f;
            return false;
        }

        /// <summary>
        /// Creature ids a player has to kill and to activate for the event (bots use this to find targets in the event area).
        /// </summary>
        public (HashSet<uint> Kill, HashSet<uint> Activate) TargetCreatures(uint objectiveId, bool isEvent = false)
        {
            var kill = new HashSet<uint>();
            var activate = new HashSet<uint>();
            lock (gate)
            {
                Build();
                if (Resolve(objectiveId, isEvent) is not { } t)
                    return (kill, activate);

                var run = new Run { Template = t };
                foreach (var o in t.Objectives)
                {
                    uint type = o.PublicEventObjectiveTypeEnum;
                    if (type is KillTargetGroup or KillClusterTargetGroup)
                        kill.UnionWith(GroupCreatures(o.ObjectId));
                    else if (type is ActivateTargetGroup or ActivateTargetGroupChecklist)
                        activate.UnionWith(GroupCreatures(o.ObjectId));
                    else if (type is KillEventUnit or KillClusterEventUnit or KillEventObjectiveUnit or KillClusterEventObjectiveUnit or Exterminate)
                        kill.UnionWith(EventCreatures(run, new Objective { Entry = o }));
                }
            }

            return (kill, activate);
        }

        /// <summary>
        /// Table of what the engine would do with each event, for gt eventplan.
        /// </summary>
        public IEnumerable<string> Plan(uint eventId)
        {
            lock (gate)
            {
                Build();
                foreach (Template t in byId.Values.Where(t => eventId == 0u || t.Entry.Id == eventId).OrderBy(t => t.Entry.Id))
                {
                    yield return $"event {t.Entry.Id} {t.Name}: world {t.World} zone {t.Entry.WorldZoneId} type {t.Entry.PublicEventTypeEnum} at {t.Center.X:0},{t.Center.Y:0},{t.Center.Z:0} radius {t.Radius:0} timer {t.Entry.FailureTimeMs / 1000}s";
                    foreach (var o in t.Objectives)
                        yield return $"  objective {o.Id}: type {o.PublicEventObjectiveTypeEnum} count {o.Count} object {o.ObjectId} flags {o.PublicEventObjectiveFlags} parent {o.PublicEventObjectiveIdParent} timer {o.FailureTimeMs / 1000}s";
                }
            }
        }
    }
}
