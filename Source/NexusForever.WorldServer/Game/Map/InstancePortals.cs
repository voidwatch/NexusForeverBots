using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using NexusForever.Shared.Database;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map.Static;

namespace NexusForever.WorldServer.Game.Map
{
    /// <summary>
    /// Instance portal units (Creature2.InstancePortalId, spawned as entity type 14). The tables do not hold a portal's destination:
    /// InstancePortal has name, level range, completion time and type only. Destinations are resolved by name (docs/ASSUMPTIONS.md
    /// "Instance portals", confidence likely): the portal name without "Enter/Portal to/Entrance to/Travel to" or the first part of the
    /// Creature2 description must equal exactly one instanced World's name. Leave/exit portals (types 2 and 3) return the player to where they
    /// entered the instance. Anything else is recorded in the ledger (instance-portal-unmapped) and nothing happens.
    /// </summary>
    public static class InstancePortals
    {
        private static readonly ConcurrentDictionary<uint, ushort> destinations = new();
        private static Dictionary<string, List<WorldEntry>> worldsByName;
        private static readonly Regex prefix = new(@"^(enter|instance portal to|portal to|entrance to|travel to|leave|exit)\s+", RegexOptions.IgnoreCase);

        private static string Normalise(string name) => Regex.Replace(Regex.Replace(name.ToLowerInvariant(), @"\bthe\b", ""), "[^a-z0-9]", "");

        private static string Text(uint id) => id == 0u ? null : GameTableManager.Instance.TextEnglish.GetEntry(id);

        /// <summary>Instanced destination world of a portal unit, 0 when the name does not resolve to exactly one instanced world.</summary>
        public static ushort Destination(Creature2Entry creature, InstancePortalEntry portal)
        {
            if (destinations.TryGetValue(creature.Id, out ushort cached))
                return cached;

            if (worldsByName == null)
            {
                var index = new Dictionary<string, List<WorldEntry>>();
                foreach (WorldEntry world in GameTableManager.Instance.World.Entries.Where(w => w != null && w.LocalizedTextIdName != 0u))
                {
                    string name = Text(world.LocalizedTextIdName);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    if (!index.TryGetValue(Normalise(name), out List<WorldEntry> list))
                        index[Normalise(name)] = list = new List<WorldEntry>();
                    list.Add(world);
                }
                worldsByName = index;
            }

            ushort found = 0;
            foreach (string candidate in new[] { prefix.Replace(Text(portal.LocalizedTextIdName) ?? "", ""), creature.Description?.Split(" - ")[0] })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !worldsByName.TryGetValue(Normalise(candidate), out List<WorldEntry> worlds))
                    continue;
                var instanced = worlds.Where(w => (MapType)w.Type is MapType.Dungeon or MapType.Adventure or MapType.Raid or MapType.Expedition).ToList();
                if (instanced.Count == 1)
                {
                    found = (ushort)instanced[0].Id;
                    break;
                }
            }

            if (found == 0)
                found = ByAssetPath(Normalise(prefix.Replace(Text(portal.LocalizedTextIdName) ?? "", "")));

            destinations[creature.Id] = found;
            return found;
        }

        /// <summary>
        /// Adventure portals carry the adventure's title, the worlds only an asset path (AdventureGaleras ...): the six adventures by their titles
        /// (the adventure list of the game; certain).
        /// </summary>
        private static readonly Dictionary<string, string> adventureAssets = new()
        {
            ["siegeoftempestrefuge"] = "adventuregaleras",
            ["crimelordsofwhitevale"] = "adventurewhitevale",
            ["warofwilds"] = "adventurenorthernwilds",
            ["malgravetrail"] = "adventuremalgrave",
            ["hycrestinsurrection"] = "adventurehycrest",
            ["riotinvoid"] = "adventureastrovoidprison"
        };

        /// <summary>
        /// Second pass: most instanced worlds have no name, only an asset path (Map\Housing1x1DungeonMeltingPot, Map\ShiphandRageLogic).
        /// Housing expedition and shiphand portals match when the portal name and the asset name without its Housing1x1Dungeon/Shiphand prefix
        /// contain one another and exactly one instanced world matches (likely); adventures by their titles. Zone portals ("Portal to Whitevale") never
        /// match an adventure world.
        /// </summary>
        private static ushort ByAssetPath(string name)
        {
            if (name.Length < 6)
                return 0;
            bool adventureTitle = adventureAssets.TryGetValue(name, out string adventure);
            if (adventureTitle)
                name = adventure;
            var hits = GameTableManager.Instance.World.Entries
                .Where(w => w != null && (MapType)w.Type is MapType.Dungeon or MapType.Adventure or MapType.Raid or MapType.Expedition && !string.IsNullOrEmpty(w.AssetPath))
                .Where(w =>
                {
                    string asset = Normalise(w.AssetPath.Substring(w.AssetPath.LastIndexOf('\\') + 1));
                    if (adventureTitle || asset == name)
                        return asset == name;
                    // only housing expeditions and shiphand missions: "Portal to Whitevale" is a zone portal, not AdventureWhitevale
                    string tail = Regex.Replace(asset, "^(housing1x1dungeon|shiphand)", "");
                    return tail != asset && tail.Length >= 6 && (tail.Contains(name) || name.Contains(tail));
                })
                .Select(w => (ushort)w.Id).Distinct().ToList();
            return hits.Count == 1 ? hits[0] : (ushort)0;
        }

        private static readonly Regex travelPrefix = new(@"^(ship|portal|transport|journey|fly|shuttle|travel)\s+to\s+", RegexOptions.IgnoreCase);
        private static readonly ConcurrentDictionary<uint, (ushort World, Vector3 Position)> travel = new();
        private static readonly object travelLock = new();
        private static List<(uint Creature, ushort World, Vector3 Position, HashSet<uint> Zones)> portalSpawns;

        /// <summary>Zone of a spawn: WorldZoneId of the nearest WorldLocation2 of the same world within 400 m, with its parent zones.</summary>
        private static HashSet<uint> ZonesAt(ushort world, Vector3 position)
        {
            WorldLocation2Entry nearest = null;
            float best = 400f * 400f;
            foreach (WorldLocation2Entry l in GameTableManager.Instance.WorldLocation2.Entries)
            {
                if (l == null || l.WorldId != world || l.WorldZoneId == 0u)
                    continue;
                float d = (l.Position0 - position.X) * (l.Position0 - position.X) + (l.Position2 - position.Z) * (l.Position2 - position.Z);
                if (d < best)
                {
                    best = d;
                    nearest = l;
                }
            }

            var zones = new HashSet<uint>();
            for (uint zone = nearest?.WorldZoneId ?? 0u; zone != 0u && zones.Add(zone);)
                zone = GameTableManager.Instance.WorldZone.GetEntry(zone)?.ParentZoneId ?? 0u;
            return zones;
        }

        /// <summary>
        /// Open-world transports ("Ship to Crimson Isle", "Portal to Algoroc", "Transport to Levian Bay"; docs/ASSUMPTIONS.md "Instance
        /// portals", likely): the target is a WorldZone of that name and the arrival is a portal unit spawned in that zone (the dock or portal hub
        /// there), preferring open worlds. Checked offline with scripts/research/portal_travel.py: 57 of 75 open-world portal spawns resolve.
        /// </summary>
        public static (ushort World, Vector3 Position) TravelDestination(Creature2Entry creature, InstancePortalEntry portal, ushort fromWorld, Vector3 from)
        {
            if (travel.TryGetValue(creature.Id, out var cached))
                return cached;

            string name = Text(portal.LocalizedTextIdName) ?? "";
            if (!travelPrefix.IsMatch(name))
                return travel[creature.Id] = (0, Vector3.Zero);
            string target = Normalise(travelPrefix.Replace(name, ""));
            var targetZones = GameTableManager.Instance.WorldZone.Entries
                .Where(z => z != null && Normalise(Text(z.LocalizedTextIdName) ?? "") == target).Select(z => z.Id).ToHashSet();
            if (targetZones.Count == 0)
                return travel[creature.Id] = (0, Vector3.Zero);

            lock (travelLock)
            {
                portalSpawns ??= DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns(false)
                    .Where(sp => GameTableManager.Instance.Creature2.GetEntry(sp.Creature) is Creature2Entry c && c.InstancePortalId != 0u
                        && GameTableManager.Instance.InstancePortal.GetEntry(c.InstancePortalId) is InstancePortalEntry p && p.InstancePortalTypeEnum < 2u)
                    .Select(sp => (sp.Creature, sp.World, new Vector3(sp.X, sp.Y, sp.Z), ZonesAt(sp.World, new Vector3(sp.X, sp.Y, sp.Z))))
                    .ToList();
            }

            HashSet<uint> here = ZonesAt(fromWorld, from);
            if (here.Overlaps(targetZones))
                return travel[creature.Id] = (0, Vector3.Zero);
            var docks = portalSpawns.Where(sp => sp.Zones.Overlaps(targetZones)).ToList();
            if (docks.Count == 0)
                return travel[creature.Id] = (0, Vector3.Zero);
            var dock = docks.OrderBy(d => GameTableManager.Instance.World.GetEntry(d.World)?.Type == 0u ? 0 : 1).First();
            return travel[creature.Id] = (dock.World, dock.Position);
        }

        private static readonly ConcurrentDictionary<uint, WorldLocation2Entry> activateTeleports = new();

        /// <summary>The WorldLocation2 of the first Teleport effect among the unit's activate spells, null when none teleports.</summary>
        public static WorldLocation2Entry ActivateTeleport(Creature2Entry creature)
        {
            return activateTeleports.GetOrAdd(creature.Id, _ =>
            {
                foreach (uint spell in new[] { creature.Spell4IdActivate00, creature.Spell4IdActivate01, creature.Spell4IdActivate02, creature.Spell4IdActivate03 })
                {
                    if (spell == 0u)
                        continue;
                    Spell4EffectsEntry teleport = GameTableManager.Instance.Spell4Effects.Entries
                        .FirstOrDefault(e => e != null && e.SpellId == spell && e.EffectType == (uint)Spell.Static.SpellEffectType.Teleport);
                    if (teleport != null && GameTableManager.Instance.WorldLocation2.GetEntry(teleport.DataBits00) is WorldLocation2Entry location)
                        return location;
                }
                return null;
            });
        }

        /// <summary>A player activated a portal unit; returns true when the portal took the player somewhere.</summary>
        public static bool Activate(Player player, Creature2Entry creature)
        {
            InstancePortalEntry portal = GameTableManager.Instance.InstancePortal.GetEntry(creature.InstancePortalId);
            if (portal == null)
            {
                FailureLedger.Record("instance-portal-unmapped", "no InstancePortal row", $"creature {creature.Id}");
                return false;
            }

            if (portal.InstancePortalTypeEnum is 2u or 3u)
            {
                if (player.Map is not MapInstance)
                {
                    FailureLedger.Record("instance-portal", "exit used outside an instance", $"portal {portal.Id}");
                    return false;
                }

                MapPosition back = DungeonInstancedMap.GetReturn(player.CharacterId);
                if (back == null)
                {
                    FailureLedger.Record("instance-portal-unmapped", "exit without a remembered entry position", $"portal {portal.Id}");
                    return false;
                }

                player.TeleportTo(back);
                FailureLedger.Record("instance-portal", "left the instance", $"portal {portal.Id}");
                return true;
            }

            if (portal.MinLevel != 0u && player.Level < portal.MinLevel)
            {
                FailureLedger.Record("instance-portal", "refused: below the portal's minimum level", $"portal {portal.Id} level {player.Level} < {portal.MinLevel}");
                return false;
            }

            // the unit's own activate spell names the arrival: Creature2.spell4IdActivate with a Teleport effect (DataBits00 = WorldLocation2), e.g.
            // "Ship to Walker's Landing" 43681 -> Farside Walker's Landing, "Ship to Thayd" 44078 -> Spaceport Horizon (certain: table link)
            if (ActivateTeleport(creature) is WorldLocation2Entry landing)
            {
                player.TeleportTo((ushort)landing.WorldId, landing.Position0, landing.Position1, landing.Position2);
                FailureLedger.Record("instance-portal", "travelled (activate spell teleport)", $"portal {portal.Id} world {landing.WorldId}");
                return true;
            }

            ushort world = Destination(creature, portal);
            if (world == 0 && player.Map != null)
            {
                (ushort travelWorld, Vector3 arrival) = TravelDestination(creature, portal, (ushort)player.Map.Entry.Id, player.Position);
                if (travelWorld != 0)
                {
                    player.TeleportTo(travelWorld, arrival.X, arrival.Y, arrival.Z);
                    FailureLedger.Record("instance-portal", "travelled", $"portal {portal.Id} world {travelWorld}");
                    return true;
                }
            }
            if (world == 0)
            {
                FailureLedger.Record("instance-portal-unmapped", $"type {portal.InstancePortalTypeEnum}: name does not resolve to one instanced world", $"creature {creature.Id} portal {portal.Id}");
                return false;
            }

            Vector3 entrance = Entrance(world);
            player.TeleportTo(world, entrance.X, entrance.Y, entrance.Z);
            FailureLedger.Record("instance-portal", "entered", $"portal {portal.Id} world {world}");
            return true;
        }

        /// <summary>Entrance of an instance: the world location closest to the origin when one is within 300 m, else the first creature spawn.</summary>
        public static Vector3 Entrance(ushort world)
        {
            var near = GameTableManager.Instance.WorldLocation2.Entries.Where(l => l != null && l.WorldId == world)
                .OrderBy(l => l.Position0 * l.Position0 + l.Position2 * l.Position2).FirstOrDefault();
            if (near != null && near.Position0 * near.Position0 + near.Position2 * near.Position2 < 300f * 300f)
                return new Vector3(near.Position0, near.Position1, near.Position2);

            return Bots.BotQuests.TryGetWorldEntrance(world, out Vector3 spawn) ? spawn : Vector3.Zero;
        }

        private static Dictionary<ushort, (ushort World, Vector3 Position, uint Creature)> portals;
        private static readonly object portalGate = new();

        /// <summary>The spawned open world portal unit whose destination (as <see cref="Activate"/> resolves it) is the world: entry point and the way out of an instance.</summary>
        public static (ushort World, Vector3 Position, uint Creature)? OpenWorldPortalTo(ushort world)
        {
            lock (portalGate)
            {
                if (portals == null)
                {
                    portals = new Dictionary<ushort, (ushort, Vector3, uint)>();
                    foreach (var spawn in DatabaseManager.Instance.WorldDatabase.GetCreatureSpawns(false))
                    {
                        Creature2Entry creature = GameTableManager.Instance.Creature2.GetEntry(spawn.Creature);
                        if (creature == null || creature.InstancePortalId == 0u || Bots.BotQuests.IsInstancedWorld(spawn.World))
                            continue;
                        InstancePortalEntry portal = GameTableManager.Instance.InstancePortal.GetEntry(creature.InstancePortalId);
                        if (portal == null || portal.InstancePortalTypeEnum is 2u or 3u || ActivateTeleport(creature) != null)
                            continue;
                        ushort destination = Destination(creature, portal);
                        if (destination != 0 && !portals.ContainsKey(destination))
                            portals[destination] = (spawn.World, new Vector3(spawn.X, spawn.Y, spawn.Z), spawn.Creature);
                    }
                    FailureLedger.Record("dungeon-portals", $"{portals.Count} instanced worlds have a spawned entrance portal", string.Join(",", portals.Keys.OrderBy(k => k)));
                }
                return portals.TryGetValue(world, out var found) ? found : null;
            }
        }
    }
}
