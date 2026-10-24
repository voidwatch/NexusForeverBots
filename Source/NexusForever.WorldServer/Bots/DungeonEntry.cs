using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NexusForever.Shared.Database;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.GameTable.Model;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Game.Map;
using NexusForever.WorldServer.Network.Message.Handler;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// How a dungeon team gets its group, into the instance and out again, through the client message handlers a player's group uses:
    /// invites (ClientGroupInvite / ClientGroupInviteResponse, raid conversion with ClientGroupFlagsUpdate), entry by the instance portal unit in
    /// the open world (ClientActivateUnit) or the group finder (ClientMatchingQueueParty, role check 0x05B2, pending game 0x05C8, TransferIntoMatch
    /// 0x0606), and leaving by ClientGroupLeave inside the instance (pending removal) with LeavePendingRemovalInstance (0x057C), or LeaveGame
    /// (0x05DA) after a matched game. NF_BOTS_DUNGEON_ENTRY: auto (default: portal and finder by turns where the world has them, else teleport),
    /// portal, finder, teleport. Getting to the portal is a teleport (the harness does not walk).
    /// </summary>
    public static class DungeonEntry
    {
        public const string Teleport = "teleport", Portal = "portal", Finder = "finder";

        private static readonly string configured = (Environment.GetEnvironmentVariable("NF_BOTS_DUNGEON_ENTRY") ?? "auto").Trim().ToLowerInvariant();

        /// <summary>The spawned open world portal unit whose destination is the world (<see cref="InstancePortals.OpenWorldPortalTo"/>).</summary>
        public static (ushort World, Vector3 Position, uint Creature)? PortalTo(ushort world) => InstancePortals.OpenWorldPortalTo(world);

        public static List<uint> MatchingMaps(ushort world) => GameTableManager.Instance.MatchingGameMap.Entries
            .Where(m => m != null && m.WorldId == world).Select(m => m.Id).ToList();

        /// <summary>Role bits for the role check: one tank and one healer when the classes allow it, damage for the rest; null when no full team exists.</summary>
        public static Dictionary<ulong, uint> FinderRoles(List<Player> players)
        {
            if (players.Count != 5)
                return null;
            Player tank = players.FirstOrDefault(p => GroupFinder.CanFill(p.Class, GroupFinder.Role.Tank));
            Player healer = players.FirstOrDefault(p => p != tank && GroupFinder.CanFill(p.Class, GroupFinder.Role.Healer));
            if (tank == null || healer == null)
                return null;
            return players.ToDictionary(p => p.CharacterId, p => p == tank ? 1u << 0 : p == healer ? 1u << 1 : 1u << 2);
        }

        /// <summary>The entry path of this run.</summary>
        public static string ChooseMode(ushort world, List<Player> players, int run)
        {
            bool portal = PortalTo(world) is var p && p != null
                && GameTableManager.Instance.Creature2.GetEntry(p.Value.Creature) is Creature2Entry c
                && GameTableManager.Instance.InstancePortal.GetEntry(c.InstancePortalId) is InstancePortalEntry ip
                && (ip.MinLevel == 0u || players.All(m => m.Level >= ip.MinLevel));
            string why = MatchingMaps(world).Count == 0 ? "no MatchingGameMap row" : FinderRoles(players) == null ? "no tank or no healer class"
                : !players.All(m => GroupFinder.Instance.AvailableDungeons(m).Contains(world)) ? "level or world type outside the queue" : null;
            bool finder = why == null;
            string mode = configured switch
            {
                Portal   => portal ? Portal : Teleport,
                Finder   => finder ? Finder : Teleport,
                Teleport => Teleport,
                _        => portal && finder ? (run % 2 == 1 ? Portal : Finder) : portal ? Portal : finder ? Finder : Teleport
            };
            Diagnostics.FailureLedger.Record("dungeon-entry-mode", $"world {world} {mode}", $"portal {(portal ? "yes" : "no")}, finder {(finder ? "yes" : "no: " + why)}, setting {configured}");
            return mode;
        }

        /// <summary>
        /// The team's group: the leader invites every member (same faction only, as the invite rule requires), a team above 5 is converted to a
        /// raid (up to 20) first; members the invites cannot add (mixed factions, above 20) join as server members, counted.
        /// </summary>
        public static PlayerGroup FormGroup(List<Player> players, int teamSize, ushort world)
        {
            Player leader = players[0];
            foreach (Player member in players.Skip(1))
            {
                if (teamSize > 5 && GroupManager.Instance.GetGroup(leader.CharacterId) is PlayerGroup open && !open.Flags.HasFlag(GroupFlags.Raid) && open.Members.Count >= 2)
                {
                    ulong id = open.Id;
                    GroupOptionsHandler.HandleGroupFlags(leader.Session, BotPackets.Build<ClientGroupFlagsUpdate>(w => { w.Write(id); w.Write((uint)(open.Flags | GroupFlags.Raid)); }));
                }
                string name = member.Name;
                GroupHandler.HandleGroupInvite(leader.Session, BotPackets.Build<ClientGroupInvite>(w => { w.WriteStringWide(name); w.WriteStringWide(""); }));
                PlayerGroup invited = GroupManager.Instance.GetGroup(leader.CharacterId);
                if (invited == null)
                    continue;
                ulong groupId = invited.Id;
                GroupHandler.HandleGroupInviteResponse(member.Session, BotPackets.Build<ClientGroupInviteResponse>(w => { w.Write(groupId); w.Write(true); w.Write(0u); }));
            }

            PlayerGroup group = GroupManager.Instance.GetGroup(leader.CharacterId);
            int invitedCount = group?.Members.Count ?? 0;
            if (group == null)
                group = GroupManager.Instance.CreateServerGroup(players, teamSize);
            else
            {
                group.MaxSize = Math.Max(group.MaxSize, teamSize);
                foreach (Player member in players.Where(p => GroupManager.Instance.GetGroup(p.CharacterId) == null))
                    GroupManager.Instance.AddServerMember(group, member);
            }

            Diagnostics.FailureLedger.Record("dungeon-group", invitedCount == players.Count ? "formed by invites" : "server members added",
                $"world {world}: {invitedCount} of {players.Count} by invite{(group.Flags.HasFlag(GroupFlags.Raid) ? ", raid" : "")}");
            return group;
        }

        /// <summary>Queue the group with the finder: QueueAsGroup by the leader, each member confirms its role; a full five becomes a pending game.</summary>
        public static bool QueueAsGroup(List<Player> players, Player leader, ushort world)
        {
            Dictionary<ulong, uint> roles = FinderRoles(players);
            List<uint> maps = MatchingMaps(world);
            if (roles == null || maps.Count == 0)
                return false;
            MatchingHandler.HandleMatchingQueueParty(leader.Session, BotPackets.Build<ClientMatchingQueueParty>(w =>
            {
                w.Write(MatchingHandler.MatchTypeDungeon, 5u);
                w.Write((uint)maps.Count);
                foreach (uint map in maps)
                    w.Write(map);
                w.Write((ushort)0, 14u);
                w.Write(0u);
                w.Write(roles[leader.CharacterId]);
                w.Write(0u);
            }));
            foreach (Player member in players)
            {
                uint bits = roles[member.CharacterId];
                MatchingHandler.HandleMatchingRoleCheckResponse(member.Session, BotPackets.Build<ClientMatchingRoleCheckResponse>(w =>
                {
                    w.Write(MatchingHandler.MatchTypeDungeon, 5u);
                    w.Write(bits);
                    w.Write(true);
                }));
            }
            bool pending = MatchingGames.Instance.For(leader) != null;
            Diagnostics.FailureLedger.Record("dungeon-finder", pending ? "pending game after the role check" : "no pending game after the role check", $"world {world}");
            return pending;
        }

        /// <summary>Every member answers the pending game with accept; the last answer moves the group in.</summary>
        public static void AcceptPending(List<Player> players)
        {
            foreach (Player member in players)
                if (MatchingGames.Instance.For(member) is MatchingGames.MatchedGame game && !game.Started && !game.Accepted.Contains(member.CharacterId))
                    MatchingHandler.HandleMatchingMatchReadyResponse(member.Session, BotPackets.Build<ClientMatchingMatchReadyResponse>(w => w.Write(true)));
        }

        /// <summary>One entry step for a member outside: activate the portal when it is in sight, else go to it; finder members transfer in.</summary>
        public static void EnterStep(string mode, Player player, ushort world, ulong instanceId)
        {
            // a teleport on its way (portal activation, transfer, travel to the portal) is not started again
            if (player.IsDead || !player.CanTeleport() || (player.Map is DungeonMapInstance inside && inside.Entry.Id == world))
                return;

            if (mode == Finder && MatchingGames.Instance.For(player) is MatchingGames.MatchedGame game && game.Started)
            {
                MatchingHandler.HandleMatchingTransferIntoMatch(player.Session, BotPackets.Build<ClientMatchingTransferIntoMatch>(w => { }));
                return;
            }

            if (mode == Portal && PortalTo(world) is var target && target != null)
            {
                (ushort portalWorld, Vector3 position, uint creature) = target.Value;
                if (player.Map == null || player.Map.Entry.Id != portalWorld || Vector3.Distance(player.Position, position) > 30f)
                {
                    player.TeleportTo(portalWorld, position.X, position.Y, position.Z);
                    return;
                }
                WorldEntity unit = player.GetVisibleCreature<WorldEntity>(creature).FirstOrDefault();
                if (unit == null)
                {
                    Diagnostics.FailureLedger.Record("dungeon-portal-missing", $"world {world}", $"portal creature {creature} not visible at its spawn in world {portalWorld}");
                    return;
                }
                uint guid = unit.Guid;
                EntityHandler.HandleActivateUnit(player.Session, BotPackets.Build<ClientActivateUnit>(w => w.Write(guid)));
                return;
            }

            Vector3 entrance = InstancePortals.Entrance(world);
            player.TeleportTo(world, entrance.X, entrance.Y, entrance.Z, instanceId);
        }

        /// <summary>
        /// Leave: after a matched game LeaveGame (0x05DA); otherwise each member leaves the group inside the instance (ClientGroupLeave), which
        /// starts the pending removal, and asks to leave at once (0x057C). A member the instance does not hold any more is only taken out of the group.
        /// </summary>
        public static void Leave(string mode, List<Player> players)
        {
            foreach (Player player in players)
            {
                if (mode == Finder && MatchingGames.Instance.For(player) != null)
                {
                    MatchingHandler.HandleMatchingMatchLeave(player.Session, BotPackets.Build<ClientMatchingMatchLeave>(w => { }));
                    continue;
                }

                if (GroupManager.Instance.GetGroup(player.CharacterId) is PlayerGroup group)
                {
                    ulong id = group.Id;
                    GroupHandler.HandleGroupLeave(player.Session, BotPackets.Build<ClientGroupLeave>(w => { w.Write(id); w.Write(false); }));
                }
                if (player.Map is MapInstance instance && instance.HasPendingRemoval(player))
                    GroupOptionsHandler.HandleLeavePendingRemoval(player.Session, BotPackets.Build<ClientLeavePendingRemoval>(w => { }));
            }
        }
    }
}
