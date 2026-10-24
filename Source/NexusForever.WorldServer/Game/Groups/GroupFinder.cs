using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.Shared.GameTable;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Entity.Static;

namespace NexusForever.WorldServer.Game.Groups
{
    /// <summary>
    /// Group finder queue for 5 player dungeons: one tank, one healer and three damage dealers of the same faction per dungeon world.
    /// The protocol of this repository has no matching messages (see docs/SYSTEMS.md), so nothing calls this from a client yet; bots drive it.
    /// ASSUMPTIONS (docs/ASSUMPTIONS.md "Group finder"): roles by class (Warrior, Engineer, Stalker can tank; Esper, Medic, Spellslinger can heal; every class can
    /// deal damage), 1/1/3 composition, same faction, entries older than two minutes are dropped. Must be called from the world update thread.
    /// </summary>
    public sealed class GroupFinder : Singleton<GroupFinder>
    {
        public enum Role { Tank, Healer, Damage }

        private sealed class Entry
        {
            public Player Player;
            public Role Role;
            public ushort World;
            public DateTime Since = DateTime.UtcNow;
        }

        private readonly List<Entry> queue = new();

        /// <summary>A premade group queued after its role check: filled from the solo queue up to one tank, one healer, three damage.</summary>
        private sealed class GroupEntry
        {
            public PlayerGroup Group;
            public Dictionary<ulong, Role> Roles;
            public ushort World;
            public DateTime Since = DateTime.UtcNow;
        }

        private readonly List<GroupEntry> groupQueue = new();

        public bool IsGroupQueued(PlayerGroup group) => groupQueue.Any(g => g.Group == group);

        public void EnqueueGroup(PlayerGroup group, Dictionary<ulong, Role> roles, ushort world)
        {
            groupQueue.RemoveAll(g => g.Group == group);
            groupQueue.Add(new GroupEntry { Group = group, Roles = new Dictionary<ulong, Role>(roles), World = world });
            FailureLedger.Record("group-finder", "premade queued", $"world {world}, {group.Members.Count} members");
        }
        private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

        private GroupFinder()
        {
        }

        public static bool CanFill(Class playerClass, Role role)
        {
            return role switch
            {
                Role.Tank   => playerClass is Class.Warrior or Class.Engineer or Class.Stalker,
                Role.Healer => playerClass is Class.Esper or Class.Medic or Class.Spellslinger,
                _           => true
            };
        }

        /// <summary>
        /// Dungeon worlds the player may queue for: MatchingGameMap rows whose MatchingGameType has a team of 5 and a level range that holds the player's level,
        /// in a dungeon world, with the map's prerequisite met.
        /// </summary>
        public List<ushort> AvailableDungeons(Player player)
        {
            var result = new List<ushort>();
            foreach (var map in GameTableManager.Instance.MatchingGameMap.Entries.Where(m => m != null && m.Id != 0u).OrderBy(m => m.Id))
            {
                var type = GameTableManager.Instance.MatchingGameType.GetEntry(map.MatchingGameTypeId);
                var world = GameTableManager.Instance.World.GetEntry(map.WorldId);
                if (type == null || world == null || type.TeamSize != 5u || (Map.Static.MapType)world.Type != Map.Static.MapType.Dungeon)
                    continue;

                if (player.Level < type.MinLevel || player.Level > type.MaxLevel)
                    continue;

                if (map.PrerequisiteId != 0u && !Prerequisite.PrerequisiteManager.Instance.Meets(player, map.PrerequisiteId))
                    continue;

                if (!result.Contains((ushort)map.WorldId))
                    result.Add((ushort)map.WorldId);
            }

            return result;
        }

        public int Queued(ushort world) => queue.Count(e => e.World == world);

        public bool IsQueued(Player player) => queue.Any(e => e.Player == player);

        /// <summary>
        /// Put the player in the queue for a dungeon world. Refused when the player is grouped, already queued or cannot fill the role.
        /// </summary>
        public bool Enqueue(Player player, Role role, ushort world)
        {
            Expire();
            if (GroupManager.Instance.GetGroup(player.CharacterId) != null)
            {
                FailureLedger.Record("group-finder", "refused: already in a group");
                return false;
            }

            if (IsQueued(player))
            {
                FailureLedger.Record("group-finder", "refused: already queued");
                return false;
            }

            if (!CanFill(player.Class, role))
            {
                FailureLedger.Record("group-finder", "refused: class cannot fill the role", $"{player.Class} as {role}");
                return false;
            }

            queue.Add(new Entry { Player = player, Role = role, World = world });
            FailureLedger.Record("group-finder", "queued", $"{role} for world {world}");
            return true;
        }

        public void Dequeue(Player player)
        {
            queue.RemoveAll(e => e.Player == player);
        }

        private void Expire()
        {
            groupQueue.RemoveAll(g => DateTime.UtcNow - g.Since > MaxWait || GroupManager.Instance.GetGroupById(g.Group.Id) == null
                || g.Group.Members.Count == 0 || g.Group.Members.Count >= 5);
            int dropped = queue.RemoveAll(e => DateTime.UtcNow - e.Since > MaxWait || e.Player.Map == null);
            if (dropped > 0)
                FailureLedger.Record("group-finder", "dropped stale entries", $"{dropped}");
        }

        private static Player GroupMemberPlayer(ulong characterId) =>
            NexusForever.Shared.Network.NetworkManager<NexusForever.WorldServer.Network.WorldSession>.Instance.GetSession(s => s.Player?.CharacterId == characterId)?.Player;

        /// <summary>The last group a match formed (bot probes disband the groups they cause).</summary>
        public PlayerGroup LastMatch { get; private set; }

        /// <summary>
        /// Try to form a group for the world from the queue (oldest entries first). Returns the new group, or null when no full composition exists.
        /// </summary>
        public PlayerGroup TryMatch(ushort world)
        {
            Expire();
            foreach (GroupEntry premade in groupQueue.Where(g => g.World == world).OrderBy(g => g.Since).ToList())
            {
                Player leader = premade.Group.Members.Select(m => GroupMemberPlayer(m.CharacterId)).FirstOrDefault(p => p != null);
                if (leader == null)
                    continue;
                int Need(Role role, int wanted) => Math.Max(0, wanted - premade.Roles.Values.Count(r => r == role));
                int needTank = Need(Role.Tank, 1), needHealer = Need(Role.Healer, 1), needDamage = Need(Role.Damage, 3);
                if (premade.Group.Members.Count + needTank + needHealer + needDamage > 5)
                {
                    FailureLedger.Record("group-finder", "premade cannot be filled: its roles exceed a team", $"{premade.Group.Members.Count} members");
                    groupQueue.Remove(premade);
                    continue;
                }

                var pool = queue.Where(e => e.World == world && e.Player.Faction1 == leader.Faction1).OrderBy(e => e.Since).ToList();
                var picked = pool.Where(e => e.Role == Role.Tank).Take(needTank)
                    .Concat(pool.Where(e => e.Role == Role.Healer).Take(needHealer))
                    .Concat(pool.Where(e => e.Role == Role.Damage).Take(needDamage)).ToList();
                if (picked.Count < needTank + needHealer + needDamage)
                    continue;

                foreach (Entry entry in picked)
                    GroupManager.Instance.AddServerMember(premade.Group, entry.Player);
                queue.RemoveAll(e => picked.Contains(e));
                groupQueue.Remove(premade);
                LastMatch = premade.Group;
                MatchingGames.Instance.Register(premade.Group, world);
                FailureLedger.Record("group-finder", "premade filled", $"world {world}, +{picked.Count} from the solo queue, {premade.Group.Members.Count} members");
                return premade.Group;
            }

            foreach (var faction in queue.Where(e => e.World == world).GroupBy(e => e.Player.Faction1))
            {
                var pool = faction.OrderBy(e => e.Since).ToList();
                var tank   = pool.FirstOrDefault(e => e.Role == Role.Tank);
                var healer = pool.FirstOrDefault(e => e.Role == Role.Healer);
                var damage = pool.Where(e => e.Role == Role.Damage).Take(3).ToList();
                if (tank == null || healer == null || damage.Count < 3)
                    continue;

                var picked = new List<Entry> { tank, healer };
                picked.AddRange(damage);
                var players = picked.Select(e => e.Player).ToList();
                PlayerGroup group = GroupManager.Instance.CreateServerGroup(players, 5);
                if (group.Members.Count != 5)
                {
                    FailureLedger.Record("group-finder", "match formed a short group", $"{group.Members.Count} members");
                    continue;
                }

                queue.RemoveAll(e => picked.Contains(e));
                LastMatch = group;
                MatchingGames.Instance.Register(group, world);
                FailureLedger.Record("group-finder", "match", $"world {world}, faction {faction.Key}");
                return group;
            }

            return null;
        }
    }
}
