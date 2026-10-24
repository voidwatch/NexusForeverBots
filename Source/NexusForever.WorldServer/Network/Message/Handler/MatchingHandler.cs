using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared.GameTable;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    /// <summary>Client messages reachable from the Lua API that had no handler (docs/OPCODE-COVERAGE.md, docs/ASSUMPTIONS.md "Remaining partial systems").</summary>
    public static class MatchingHandler
    {
        /// <summary>MatchMakingLib.MatchType from the client's Lua enum table: Dungeon 1, PrimeLevelDungeon 10, ScaledPrimeLevelDungeon 14.</summary>
        public const byte MatchTypeDungeon = 1;
        private static readonly byte[] dungeonTypes = { 1, 10, 14 };

        [MessageHandler(GameMessageOpcode.ClientTaxiPurchase)]
        public static void HandleTaxiPurchase(WorldSession session, ClientTaxiPurchase purchase)
        {
            session.Player.TaxiManager.TravelPath(purchase.Nodes);
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingQueue)]
        public static void HandleMatchingQueue(WorldSession session, ClientMatchingQueue queue)
        {
            Queue(session.Player, queue.MatchType, queue.Roles, queue.Maps);
        }

        /// <summary>QueueAsGroup: the leader queues the group; every member confirms a role (0x05B2) before the group counts as queued.</summary>
        [MessageHandler(GameMessageOpcode.ClientMatchingQueueParty)]
        public static void HandleMatchingQueueParty(WorldSession session, ClientMatchingQueueParty queue)
        {
            Player leader = session.Player;
            PlayerGroup group = GroupManager.Instance.GetGroup(leader.CharacterId);
            if (group == null)
            {
                Queue(leader, queue.MatchType, queue.Roles, queue.Maps);
                return;
            }

            ushort? world = Resolve(leader, queue.MatchType, queue.Maps, out string refusal);
            if (world == null)
            {
                Diagnostics.FailureLedger.Record("group-finder", $"group queue refused: {refusal}", $"type {queue.MatchType}");
                return;
            }

            lock (roleChecks)
                roleChecks[group] = new RoleCheck { World = world.Value, MatchType = queue.MatchType };
            Diagnostics.FailureLedger.Record("group-finder", "group role check started", $"world {world}, {group.Members.Count} members");
        }

        /// <summary>The random queue (0x05F8) carries no map list: the first dungeon the player may queue for is taken.</summary>
        [MessageHandler(GameMessageOpcode.ClientMatchingQueueRandom)]
        public static void HandleMatchingQueueRandom(WorldSession session, ClientMatchingQueueRandom queue)
        {
            Queue(session.Player, queue.MatchType, queue.Value1, null);
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingMatchReadyResponse)]
        public static void HandleMatchingMatchReadyResponse(WorldSession session, ClientMatchingMatchReadyResponse response)
        {
            MatchingGames.Instance.Respond(session.Player, response.Accept);
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingTransferIntoMatch)]
        public static void HandleMatchingTransferIntoMatch(WorldSession session, ClientMatchingTransferIntoMatch transfer)
        {
            MatchingGames.Instance.Transfer(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingMatchLeave)]
        public static void HandleMatchingMatchLeave(WorldSession session, ClientMatchingMatchLeave leave)
        {
            MatchingGames.Instance.Leave(session.Player);
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingRoleCheckResponse)]
        public static void HandleMatchingRoleCheckResponse(WorldSession session, ClientMatchingRoleCheckResponse response)
        {
            Player player = session.Player;
            PlayerGroup group = GroupManager.Instance.GetGroup(player.CharacterId);
            RoleCheck check;
            lock (roleChecks)
            {
                if (group == null || !roleChecks.TryGetValue(group, out check))
                {
                    Diagnostics.FailureLedger.Record("group-finder", "role check response without a role check");
                    return;
                }

                if (!response.Accept || !TryRole(player, response.Roles, out GroupFinder.Role role))
                {
                    roleChecks.Remove(group);
                    Diagnostics.FailureLedger.Record("group-finder", response.Accept ? "role check failed: no role the class can fill" : "role check declined");
                    return;
                }

                check.Roles[player.CharacterId] = role;
                if (!group.Members.All(m => check.Roles.ContainsKey(m.CharacterId)))
                {
                    Diagnostics.FailureLedger.Record("group-finder", "role confirmed", role.ToString());
                    return;
                }
                roleChecks.Remove(group);
            }

            // a full five with one tank and one healer is a match; smaller premades queue and are filled from the solo queue
            int tanks = check.Roles.Values.Count(r => r == GroupFinder.Role.Tank);
            int healers = check.Roles.Values.Count(r => r == GroupFinder.Role.Healer);
            bool full = group.Members.Count == 5 && tanks >= 1 && healers >= 1;
            if (full)
            {
                Diagnostics.FailureLedger.Record("group-finder", "group queue match (premade)", $"world {check.World}, 5 members");
                MatchingGames.Instance.Register(group, check.World);
                return;
            }
            GroupFinder.Instance.EnqueueGroup(group, check.Roles, check.World);
            GroupFinder.Instance.TryMatch(check.World);
        }

        private sealed class RoleCheck
        {
            public ushort World;
            public byte MatchType;
            public readonly Dictionary<ulong, GroupFinder.Role> Roles = new();
        }

        private static readonly Dictionary<PlayerGroup, RoleCheck> roleChecks = new();

        public static bool RoleCheckActive(PlayerGroup group)
        {
            lock (roleChecks)
                return roleChecks.ContainsKey(group);
        }

        private static void Queue(Player player, byte matchType, uint roles, IReadOnlyList<uint> maps)
        {
            ushort? world = Resolve(player, matchType, maps, out string refusal);
            if (world == null)
            {
                Diagnostics.FailureLedger.Record("group-finder", $"queue refused: {refusal}", $"type {matchType}");
                return;
            }

            if (!TryRole(player, roles, out GroupFinder.Role role))
            {
                Diagnostics.FailureLedger.Record("group-finder", "queue refused: no role the class can fill", $"{player.Class} roles 0x{roles:x}");
                return;
            }

            if (GroupFinder.Instance.Enqueue(player, role, world.Value))
                GroupFinder.Instance.TryMatch(world.Value);
        }

        /// <summary>Roles bit field: bit (1 &lt;&lt; role), MatchMakingLib.Roles Tank 0, Healer 1, DPS 2 (client Lua enum). The first role the class can fill wins.</summary>
        private static bool TryRole(Player player, uint roles, out GroupFinder.Role role)
        {
            foreach (GroupFinder.Role candidate in new[] { GroupFinder.Role.Tank, GroupFinder.Role.Healer, GroupFinder.Role.Damage })
                if ((roles & (1u << (int)candidate)) != 0u && GroupFinder.CanFill(player.Class, candidate))
                {
                    role = candidate;
                    return true;
                }

            role = GroupFinder.Role.Damage;
            return false;
        }

        /// <summary>Dungeon world for a queue request: the first MatchingGameMap of the request (any, when none listed) the player may queue for.</summary>
        private static ushort? Resolve(Player player, byte matchType, IReadOnlyList<uint> maps, out string refusal)
        {
            refusal = null;
            if (!dungeonTypes.Contains(matchType))
            {
                refusal = "match type has no queue on the server (only dungeons)";
                return null;
            }

            List<ushort> available = GroupFinder.Instance.AvailableDungeons(player);
            if (maps == null || maps.Count == 0)
            {
                if (available.Count == 0)
                    refusal = "no dungeon available at this level";
                return available.Count > 0 ? available[0] : null;
            }

            foreach (uint mapId in maps)
            {
                var map = GameTableManager.Instance.MatchingGameMap.GetEntry(mapId);
                if (map != null && available.Contains((ushort)map.WorldId))
                    return (ushort)map.WorldId;
            }

            refusal = "none of the requested maps is available";
            return null;
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class GroupOptionsHandler
    {
        [MessageHandler(GameMessageOpcode.ClientGroupMemberFlagsUpdate)]
        public static void HandleGroupMemberFlags(WorldSession session, ClientGroupMemberFlagsUpdate update)
        {
            string result = GroupManager.Instance.SetMemberFlags(session.Player, update.GroupId, update.Target.Id,
                (Game.Groups.Static.GroupMemberInfoFlags)update.Flags, (Game.Groups.Static.GroupMemberInfoFlags)update.Changed);
            Diagnostics.FailureLedger.Record("group-member-flags", result, $"changed 0x{update.Changed:x}");
        }

        [MessageHandler(GameMessageOpcode.ClientGroupFlagsUpdate)]
        public static void HandleGroupFlags(WorldSession session, ClientGroupFlagsUpdate update)
        {
            string result = GroupManager.Instance.SetGroupFlags(session.Player, update.GroupId, (Game.Groups.Static.GroupFlags)update.Flags);
            Diagnostics.FailureLedger.Record("group-flags", result, $"flags 0x{update.Flags:x}");
        }

        /// <summary>SetInstanceDifficulty: the leader picks normal or veteran for the group's next instance (same store as ClientSetInstanceSettings 0x0163).</summary>
        [MessageHandler(GameMessageOpcode.ClientGroupInstanceDifficulty)]
        public static void HandleGroupInstanceDifficulty(WorldSession session, ClientGroupInstanceDifficulty difficulty)
        {
            PlayerGroup group = GroupManager.Instance.GetGroup(session.Player.CharacterId);
            if (group == null || group.Id != difficulty.GroupId)
            {
                Diagnostics.FailureLedger.Record("group-instance-difficulty", "refused: not in that group");
                return;
            }
            bool done = Game.Map.InstanceSettings.Set(session.Player, difficulty.Difficulty, 0, 0);
            Diagnostics.FailureLedger.Record("group-instance-difficulty", done ? $"set {difficulty.Difficulty}" : "refused");
        }

        /// <summary>
        /// GotoGroupInstance: join the instance a group member is in. Dungeon instances are keyed by the group (DungeonInstancedMap.InstanceIdFor), so
        /// teleporting into the member's world puts the player into the same instance, at the instance entrance.
        /// </summary>
        [MessageHandler(GameMessageOpcode.ClientGroupGotoInstance)]
        public static void HandleGroupGotoInstance(WorldSession session, ClientGroupGotoInstance gotoInstance)
        {
            Player player = session.Player;
            PlayerGroup group = GroupManager.Instance.GetGroup(player.CharacterId);
            if (group == null || group.Id != gotoInstance.GroupId)
            {
                Diagnostics.FailureLedger.Record("group-goto-instance", "refused: not in that group");
                return;
            }

            Player inside = group.Members.Where(m => m.CharacterId != player.CharacterId)
                .Select(m => Game.CharacterCache.CharacterManager.Instance.GetPlayer(m.CharacterId))
                .FirstOrDefault(p => p?.Map is Game.Map.DungeonMapInstance);
            if (inside == null)
            {
                Diagnostics.FailureLedger.Record("group-goto-instance", "refused: no member in an instance");
                return;
            }
            if (player.Map == inside.Map)
            {
                Diagnostics.FailureLedger.Record("group-goto-instance", "refused: already there");
                return;
            }

            ushort world = (ushort)inside.Map.Entry.Id;
            System.Numerics.Vector3 entrance = Game.Map.InstancePortals.Entrance(world);
            player.TeleportTo(world, entrance.X, entrance.Y, entrance.Z);
            Diagnostics.FailureLedger.Record("group-goto-instance", "teleported", $"world {world}");
        }

        [MessageHandler(GameMessageOpcode.ClientGroupMarkUnit)]
        public static void HandleGroupMarkUnit(WorldSession session, ClientGroupMarkUnit mark)
        {
            Diagnostics.FailureLedger.Record("group-marker", GroupManager.Instance.SetMarker(session.Player, mark.Marker, mark.UnitId));
        }

        [MessageHandler(GameMessageOpcode.ClientGroupReadyCheck)]
        public static void HandleGroupReadyCheck(WorldSession session, ClientGroupReadyCheck check)
        {
            Diagnostics.FailureLedger.Record("group-ready-check", GroupManager.Instance.ReadyCheck(session.Player, check.GroupId));
        }

        [MessageHandler(GameMessageOpcode.ClientLeavePendingRemoval)]
        public static void HandleLeavePendingRemoval(WorldSession session, ClientLeavePendingRemoval leave)
        {
            bool done = session.Player.Map is Game.Map.MapInstance instance && instance.LeavePendingRemovalNow(session.Player);
            Diagnostics.FailureLedger.Record("instance-leave-pending", done ? "leaving now" : "refused: no pending removal");
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class FriendshipListHandler
    {
        [MessageHandler(GameMessageOpcode.ClientFriendshipAdd)]
        public static void HandleFriendshipAdd(WorldSession session, ClientFriendshipAdd add)
        {
            ulong other = add.Target.CharacterId;
            bool known = Game.CharacterCache.CharacterManager.Instance.GetCharacterInfo(other) != null;
            var friends = Game.Social.FriendManager.Instance;
            bool done = known && friends.AddByType(session.Player.CharacterId, other, add.Type);
            if (done && !string.IsNullOrEmpty(add.Note))
                friends.SetNoteAndNotify(session.Player.CharacterId, other, add.Note);
            Diagnostics.FailureLedger.Record("friendship", $"add by identity type {add.Type} {(done ? "done" : known ? "refused" : "refused: unknown character")}");
        }

        [MessageHandler(GameMessageOpcode.ClientFriendshipRemove)]
        public static void HandleFriendshipRemove(WorldSession session, ClientFriendshipRemove remove)
        {
            bool done = Game.Social.FriendManager.Instance.RemoveByType(session.Player.CharacterId, remove.Target.CharacterId, remove.Type);
            Diagnostics.FailureLedger.Record("friendship", $"remove by identity type {remove.Type} {(done ? "done" : "refused")}");
        }

        [MessageHandler(GameMessageOpcode.ClientFriendshipSetNote)]
        public static void HandleFriendshipSetNote(WorldSession session, ClientFriendshipSetNote note)
        {
            bool done = Game.Social.FriendManager.Instance.SetNoteAndNotify(session.Player.CharacterId, note.Target.CharacterId, note.Note);
            Diagnostics.FailureLedger.Record("friendship", $"note {(done ? "set" : "refused: not on a list")}");
        }
    }
}

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class NeighbourHandler
    {
        private static ulong Resolve(Model.Shared.TargetPlayerIdentity target, string name)
        {
            if (target.CharacterId != 0ul)
                return target.CharacterId;
            return string.IsNullOrEmpty(name) ? 0ul : Game.CharacterCache.CharacterManager.Instance.GetCharacterInfo(name)?.CharacterId ?? 0ul;
        }

        [MessageHandler(GameMessageOpcode.ClientHousingNeighborInvite)]
        public static void HandleNeighborInvite(WorldSession session, ClientHousingNeighborInvite invite)
        {
            ulong other = Resolve(invite.Target, invite.Name);
            string result = other == 0ul ? "refused: unknown character" : Game.Housing.NeighbourManager.Instance.Invite(session.Player, other);
            Diagnostics.FailureLedger.Record("housing-neighbour", $"invite {result}");
        }

        [MessageHandler(GameMessageOpcode.ClientHousingNeighborResponse)]
        public static void HandleNeighborResponse(WorldSession session, ClientHousingNeighborResponse response)
        {
            Diagnostics.FailureLedger.Record("housing-neighbour", $"response {Game.Housing.NeighbourManager.Instance.Respond(session.Player, response.Accept)}");
        }

        [MessageHandler(GameMessageOpcode.ClientHousingNeighborEvict)]
        public static void HandleNeighborEvict(WorldSession session, ClientHousingNeighborEvict evict)
        {
            ulong other = Resolve(evict.Target, evict.Name);
            Diagnostics.FailureLedger.Record("housing-neighbour", $"evict {Game.Housing.NeighbourManager.Instance.Evict(session.Player, other)}");
        }

        [MessageHandler(GameMessageOpcode.ClientHousingNeighborPermission)]
        public static void HandleNeighborPermission(WorldSession session, ClientHousingNeighborPermission permission)
        {
            ulong other = Resolve(permission.Target, permission.Name);
            Diagnostics.FailureLedger.Record("housing-neighbour", $"permission {Game.Housing.NeighbourManager.Instance.SetPermission(session.Player, other, permission.Permission)}");
        }
    }
}
