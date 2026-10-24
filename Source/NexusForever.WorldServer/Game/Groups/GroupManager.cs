using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.Shared.Game;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.CharacterCache;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups.Static;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Network.Message.Model.Shared;
using NLog;

namespace NexusForever.WorldServer.Game.Groups
{
    public class GroupMember
    {
        public ulong CharacterId { get; init; }
        public uint Index { get; init; }
        public GroupMemberInfoFlags Flags { get; set; }

        // last values sent to the other members, used to only send stat updates when something changed
        public uint LastHealth { get; set; }
        public uint LastMaxHealth { get; set; }
        public uint LastLevel { get; set; }
    }

    public class GroupInvite
    {
        public ulong GroupId { get; init; }
        public ulong InviterId { get; init; }
        public ulong InviteeId { get; init; }
        public DateTime Expires { get; init; }
    }

    public class PlayerGroup
    {
        public ulong Id { get; init; }
        public ulong LeaderId { get; set; }
        public GroupFlags Flags { get; set; } = GroupFlags.OpenWorld;
        public List<GroupMember> Members { get; } = new();
        /// <summary>Largest size of this group (5 for normal groups, raid groups made by the server are larger).</summary>
        public int MaxSize { get; set; } = 5;
        /// <summary>Loot rules set by the leader (ClientGroupLootRulesChange). Items below <see cref="ThresholdQuality"/> use <see cref="NormalRule"/>, the rest <see cref="ThresholdRule"/>.</summary>
        public LootRule NormalRule { get; set; } = LootRule.NeedBeforeGreed;
        public LootRule ThresholdRule { get; set; } = LootRule.NeedBeforeGreed;
        public LootThreshold ThresholdQuality { get; set; } = LootThreshold.Good;
        public HarvestLootRule HarvestRule { get; set; } = HarvestLootRule.FirstTagger;
        /// <summary>Target markers: marker index to unit id (SetTargetMarker 0x0432).</summary>
        public Dictionary<uint, uint> Markers { get; } = new();

        public GroupMember GetMember(ulong characterId)
        {
            return Members.FirstOrDefault(m => m.CharacterId == characterId);
        }
    }

    /// <summary>
    /// In-memory party system for a single world server. Rules and packets follow the NexusForever group implementation,
    /// without the separate group server: open world groups of up to 5, invites that expire after 30 seconds,
    /// the leader can kick and promote, and a group with two members disbands when one leaves.
    /// Groups are not saved, they end when everyone logs out or the server restarts.
    /// </summary>
    public sealed class GroupManager : Singleton<GroupManager>, IUpdate
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        public const int MaxGroupSize = 5;
        private const double InviteSeconds   = 30d;
        private const double StatInterval    = 1d;
        private const double PositionInterval = 3d;

        private ulong nextGroupId = 1ul;
        private readonly Dictionary<ulong, PlayerGroup> groups = new();
        private readonly Dictionary<ulong, PlayerGroup> memberGroups = new();  // character id -> group
        private readonly Dictionary<ulong, GroupInvite> invites = new();       // invitee character id -> invite
        private readonly Dictionary<ulong, GroupInvite> joinRequests = new();  // requester character id -> request to a group's leader
        private readonly List<ulong> expired = new();

        private double statTimer;
        private double positionTimer;

        private GroupManager()
        {
        }

        public PlayerGroup GetGroup(ulong characterId)
        {
            return memberGroups.TryGetValue(characterId, out PlayerGroup group) ? group : null;
        }

        /// <summary>
        /// Make a group of the supplied players without invites (server side bot teams for dungeon and raid runs). Players that are already
        /// grouped are skipped. Must be called from the world update thread.
        /// </summary>
        public PlayerGroup CreateServerGroup(IReadOnlyList<Player> players, int maxSize)
        {
            var group = new PlayerGroup { Id = nextGroupId++, MaxSize = Math.Max(MaxGroupSize, maxSize) };
            groups.Add(group.Id, group);
            foreach (Player player in players)
                if (!memberGroups.ContainsKey(player.CharacterId))
                    AddMember(group, player);

            return group;
        }

        /// <summary>Add a player to an existing group without an invite (group finder fills a premade). Must be called from the world update thread.</summary>
        public bool AddServerMember(PlayerGroup group, Player player)
        {
            if (memberGroups.ContainsKey(player.CharacterId) || group.Members.Count >= group.MaxSize || !groups.ContainsKey(group.Id))
                return false;
            AddMember(group, player);
            return true;
        }

        public PlayerGroup GetGroupById(ulong groupId)
        {
            return groups.TryGetValue(groupId, out PlayerGroup group) ? group : null;
        }

        /// <summary>
        /// Invite the player with the supplied name to the inviter's group, creating the group if the inviter has none.
        /// </summary>
        public GroupInviteResult Invite(Player inviter, string inviteeName)
        {
            Player invitee = CharacterManager.Instance.GetPlayer(inviteeName);
            GroupInviteResult result = InviteInternal(inviter, invitee);

            Send(inviter, new ServerGroupInviteResult
            {
                Name   = inviteeName,
                Result = result
            });

            return result;
        }

        private GroupInviteResult InviteInternal(Player inviter, Player invitee)
        {
            if (invitee == null)
                return GroupInviteResult.PlayerNotFound;

            if (invitee.CharacterId == inviter.CharacterId)
                return GroupInviteResult.NotInvitingSelf;

            if (memberGroups.ContainsKey(invitee.CharacterId))
                return GroupInviteResult.Grouped;

            if (invites.ContainsKey(invitee.CharacterId))
                return GroupInviteResult.Pending;

            if (invitee.Faction != inviter.Faction)
                return GroupInviteResult.WrongFaction;

            PlayerGroup group = GetGroup(inviter.CharacterId);
            if (group == null)
            {
                group = new PlayerGroup { Id = nextGroupId++ };
                groups.Add(group.Id, group);
                AddMember(group, inviter);
            }

            GroupMember inviterMember = group.GetMember(inviter.CharacterId);
            if (group.LeaderId != inviter.CharacterId && !inviterMember.Flags.HasFlag(GroupMemberInfoFlags.CanInvite))
                return GroupInviteResult.NoPermissions;

            if (group.Members.Count >= group.MaxSize)
                return GroupInviteResult.Full;

            invites.Add(invitee.CharacterId, new GroupInvite
            {
                GroupId   = group.Id,
                InviterId = inviter.CharacterId,
                InviteeId = invitee.CharacterId,
                Expires   = DateTime.UtcNow.AddSeconds(InviteSeconds)
            });

            GroupMember leader = group.GetMember(group.LeaderId);
            var received = new ServerGroupInviteReceived
            {
                GroupId      = group.Id,
                LeaderIndex  = leader.Index - 1u,
                InviterIndex = inviterMember.Index - 1u
            };

            foreach (GroupMember member in group.Members)
            {
                Player player = CharacterManager.Instance.GetPlayer(member.CharacterId);
                if (player != null)
                    received.Members.Add(ToCharacter(player, member.Index));
            }

            Send(invitee, received);
            return GroupInviteResult.Sent;
        }

        /// <summary>
        /// GroupLib.Join (0x0422): ask to join the group of the named player. The leader gets the request (0x0424, "Group_JoinRequest"), the
        /// requester the result (0x0456, GroupLib.Result: SentToLeader or the refusal).
        /// </summary>
        public GroupInviteResult RequestJoin(Player requester, string name)
        {
            Player target = CharacterManager.Instance.GetPlayer(name);
            PlayerGroup group = target == null ? null : GetGroup(target.CharacterId);
            Player leader = group == null ? null : CharacterManager.Instance.GetPlayer(group.LeaderId);
            GroupInviteResult result;
            if (target == null)
                result = GroupInviteResult.PlayerNotFound;
            else if (target.CharacterId == requester.CharacterId)
                result = GroupInviteResult.NotInvitingSelf;
            else if (memberGroups.ContainsKey(requester.CharacterId))
                result = GroupInviteResult.Grouped;
            else if (group == null)
                result = GroupInviteResult.GroupNotFound;
            else if (target.Faction != requester.Faction)
                result = GroupInviteResult.WrongFaction;
            else if (group.Members.Count >= group.MaxSize)
                result = GroupInviteResult.Full;
            else if (joinRequests.ContainsKey(requester.CharacterId) || invites.ContainsKey(requester.CharacterId))
                result = GroupInviteResult.Pending;
            else if (leader == null)
                result = GroupInviteResult.LeaderOffline;
            else
            {
                joinRequests[requester.CharacterId] = new GroupInvite
                {
                    GroupId   = group.Id,
                    InviterId = group.LeaderId,
                    InviteeId = requester.CharacterId,
                    Expires   = DateTime.UtcNow.AddSeconds(InviteSeconds)
                };
                Send(leader, new ServerGroupRequestJoinResponse
                {
                    GroupId   = group.Id,
                    Requester = ToMemberInfo(requester, new GroupMember { CharacterId = requester.CharacterId, Index = 0u })
                });
                result = GroupInviteResult.SentToLeader;
            }

            Send(requester, new ServerGroupRequestJoinResult { GroupId = group?.Id ?? 0ul, Name = name, Result = result });
            Diagnostics.FailureLedger.Record("group-join-request", result.ToString(), $"group {group?.Id ?? 0ul}");
            return result;
        }

        /// <summary>GroupLib.AcceptRequest/DenyRequest (0x0453) from the leader for the pending request of the named player.</summary>
        public GroupInviteResult RequestJoinResponse(Player leader, ulong groupId, string name, bool accept)
        {
            Player requester = CharacterManager.Instance.GetPlayer(name);
            PlayerGroup group = GetGroupById(groupId);
            GroupInviteResult result;
            if (requester == null || !joinRequests.TryGetValue(requester.CharacterId, out GroupInvite request) || request.GroupId != groupId)
                result = GroupInviteResult.PlayerNotFound;
            else if (group == null)
                result = GroupInviteResult.GroupNotFound;
            else if (group.LeaderId != leader.CharacterId)
                result = GroupInviteResult.NoPermissions;
            else
            {
                joinRequests.Remove(requester.CharacterId);
                if (!accept)
                    result = GroupInviteResult.Declined;
                else if (memberGroups.ContainsKey(requester.CharacterId))
                    result = GroupInviteResult.Grouped;
                else if (group.Members.Count >= group.MaxSize)
                    result = GroupInviteResult.Full;
                else
                {
                    AddMember(group, requester);
                    result = GroupInviteResult.Accepted;
                }
                Send(requester, new ServerGroupRequestJoinResult { GroupId = groupId, Name = leader.Name, Result = result });
            }

            Diagnostics.FailureLedger.Record("group-join-response", result.ToString(), $"group {groupId}");
            return result;
        }

        public void InviteResponse(Player invitee, ulong groupId, bool accept)
        {
            if (!invites.TryGetValue(invitee.CharacterId, out GroupInvite invite) || invite.GroupId != groupId)
                return;

            invites.Remove(invitee.CharacterId);

            Player inviter = CharacterManager.Instance.GetPlayer(invite.InviterId);
            GroupInviteResult result = GroupInviteResult.Declined;

            if (accept)
            {
                PlayerGroup group = GetGroupById(groupId);
                if (group == null)
                    result = GroupInviteResult.GroupNotFound;
                else if (group.Members.Count >= group.MaxSize)
                    result = GroupInviteResult.Full;
                else
                {
                    AddMember(group, invitee);
                    result = GroupInviteResult.Accepted;
                }
            }

            if (inviter != null)
            {
                Send(inviter, new ServerGroupInviteResult
                {
                    Name   = invitee.Name,
                    Result = result
                });
            }
        }

        public void Leave(Player player, ulong groupId, bool disband)
        {
            PlayerGroup group = GetGroupById(groupId);
            GroupMember member = group?.GetMember(player.CharacterId);
            if (member == null)
            {
                SendActionResult(player, groupId, player, GroupActionResult.InvalidGroup);
                return;
            }

            if (disband)
            {
                if (group.LeaderId != player.CharacterId)
                {
                    SendActionResult(player, groupId, player, GroupActionResult.DisbandFailed);
                    return;
                }

                Disband(group);
                SendActionResult(player, 0ul, player, GroupActionResult.DisbandSuccess);
                return;
            }

            RemoveMember(group, member, RemoveReason.Left);
            SendActionResult(player, groupId, player, GroupActionResult.LeaveSuccess);
        }

        public void Kick(Player kicker, ulong groupId, Identity target)
        {
            PlayerGroup group = GetGroupById(groupId);
            GroupMember kickerMember = group?.GetMember(kicker.CharacterId);
            if (kickerMember == null)
            {
                SendActionResult(kicker, groupId, target, GroupActionResult.InvalidGroup);
                return;
            }

            if (!kickerMember.Flags.HasFlag(GroupMemberInfoFlags.CanKick))
            {
                SendActionResult(kicker, groupId, target, GroupActionResult.KickFailed);
                return;
            }

            GroupMember kicked = group.GetMember(target.Id);
            if (kicked == null)
            {
                SendActionResult(kicker, groupId, target, GroupActionResult.InvalidGroup);
                return;
            }

            RemoveMember(group, kicked, RemoveReason.Kicked);
            SendActionResult(kicker, groupId, target, GroupActionResult.KickSuccess);
        }

        public void Promote(Player promoter, ulong groupId, Identity target)
        {
            PlayerGroup group = GetGroupById(groupId);
            GroupMember promotee = group?.GetMember(target.Id);
            if (group == null || promotee == null || group.LeaderId != promoter.CharacterId)
            {
                SendActionResult(promoter, groupId, target, GroupActionResult.PromoteFailed);
                return;
            }

            Promote(group, promotee);
            SendActionResult(promoter, groupId, target, GroupActionResult.PromoteSuccess);
        }

        /// <summary>
        /// Remove the player from any group and cancel their invites, invoked when a player logs out.
        /// </summary>
        /// <summary>
        /// Leader changes the group loot rules; every member gets the new rules (same rules as the NexusForever group server).
        /// </summary>
        public GroupActionResult SetLootRules(Player player, ulong groupId, LootRule normalRule, LootRule thresholdRule, LootThreshold thresholdQuality, HarvestLootRule harvestRule)
        {
            PlayerGroup group = GetGroupById(groupId);
            GroupActionResult result;
            if (group == null || group.GetMember(player.CharacterId) == null)
                result = GroupActionResult.InvalidGroup;
            else if (group.LeaderId != player.CharacterId)
                result = GroupActionResult.ChangeSettingsFailed;
            else
            {
                group.NormalRule       = normalRule;
                group.ThresholdRule    = thresholdRule;
                group.ThresholdQuality = thresholdQuality;
                group.HarvestRule      = harvestRule;

                var changed = new ServerGroupLootRulesChange
                {
                    GroupId          = group.Id,
                    NormalRule       = normalRule,
                    ThresholdRule    = thresholdRule,
                    ThresholdQuality = thresholdQuality,
                    HarvestRule      = harvestRule
                };
                foreach (GroupMember member in group.Members)
                    Send(member.CharacterId, changed);
                result = GroupActionResult.ChangeSettingsSuccess;
            }

            FailureLedger.Record(result == GroupActionResult.ChangeSettingsSuccess ? "group-loot-rules-changed" : "group-loot-rules-refused",
                result.ToString(), $"{normalRule}/{thresholdRule}/{thresholdQuality}/{harvestRule}");
            SendActionResult(player, groupId, player, result);
            return result;
        }

        public void OnLogout(Player player)
        {
            invites.Remove(player.CharacterId);
            foreach (ulong inviteeId in invites.Where(i => i.Value.InviterId == player.CharacterId).Select(i => i.Key).ToList())
                invites.Remove(inviteeId);

            PlayerGroup group = GetGroup(player.CharacterId);
            GroupMember member = group?.GetMember(player.CharacterId);
            if (member != null)
                RemoveMember(group, member, RemoveReason.Disconnected);
        }

        private void AddMember(PlayerGroup group, Player player)
        {
            if (group.Members.Count == 0)
                group.LeaderId = player.CharacterId;

            uint index = 1u;
            while (group.Members.Any(m => m.Index == index))
                index++;

            var member = new GroupMember
            {
                CharacterId = player.CharacterId,
                Index       = index,
                Flags       = group.LeaderId == player.CharacterId
                    ? GroupMemberInfoFlags.GroupAdminFlags
                    : GroupMemberInfoFlags.GroupMemberFlags
            };

            group.Members.Add(member);
            memberGroups[player.CharacterId] = group;
            Game.Map.DungeonInstancedMap.OnGroupJoined(player, group.Id);

            player.EnqueueToVisible(new ServerEntityGroupAssociation
            {
                UnitId  = player.Guid,
                GroupId = group.Id
            }, true);

            // the new member gets the whole group and the stats of everyone already in it
            Send(player, new ServerGroupJoin
            {
                TargetPlayer = ToIdentity(player),
                Group        = ToNetworkGroup(group)
            });

            var added = new ServerGroupMemberAdd
            {
                GroupId     = group.Id,
                AddedMember = ToMemberInfo(player, member)
            };

            foreach (GroupMember other in group.Members)
            {
                if (other.CharacterId == player.CharacterId)
                    continue;

                Player otherPlayer = CharacterManager.Instance.GetPlayer(other.CharacterId);
                if (otherPlayer == null)
                    continue;

                Send(player, ToStatUpdate(group, otherPlayer));
                Send(otherPlayer, added);
                Send(otherPlayer, ToStatUpdate(group, player));
            }

            member.LastHealth    = player.Health;
            member.LastMaxHealth = player.MaxHealth;
            member.LastLevel     = player.Level;
        }

        private void RemoveMember(PlayerGroup group, GroupMember member, RemoveReason reason)
        {
            // open world groups end when one of the last two members leaves
            if (group.Members.Count <= 2)
            {
                Disband(group, reason);
                return;
            }

            if (group.LeaderId == member.CharacterId)
                Promote(group, group.Members.First(m => m.CharacterId != member.CharacterId));

            var remove = new ServerGroupRemove
            {
                GroupId      = group.Id,
                TargetPlayer = new Identity { RealmId = WorldServer.RealmId, Id = member.CharacterId },
                Reason       = reason
            };

            foreach (GroupMember other in group.Members)
                Send(other.CharacterId, remove);

            Player removed = CharacterManager.Instance.GetPlayer(member.CharacterId);
            if (removed != null)
            {
                Send(removed, new ServerGroupLeave { GroupId = group.Id, Reason = reason });
                ClearAssociation(removed);
            }

            group.Members.Remove(member);
            memberGroups.Remove(member.CharacterId);
            Game.Map.DungeonInstancedMap.OnGroupMembershipLost(removed, group.Id);
        }

        private void Disband(PlayerGroup group, RemoveReason reason = RemoveReason.Disband)
        {
            foreach (GroupMember member in group.Members)
            {
                Player player = CharacterManager.Instance.GetPlayer(member.CharacterId);
                if (player != null)
                {
                    Send(player, new ServerGroupLeave { GroupId = group.Id, Reason = reason });
                    ClearAssociation(player);
                }

                memberGroups.Remove(member.CharacterId);
                Game.Map.DungeonInstancedMap.OnGroupMembershipLost(player, group.Id);
            }

            foreach (ulong inviteeId in invites.Where(i => i.Value.GroupId == group.Id).Select(i => i.Key).ToList())
                invites.Remove(inviteeId);

            groups.Remove(group.Id);
        }

        private void Promote(PlayerGroup group, GroupMember newLeader)
        {
            GroupMember oldLeader = group.GetMember(group.LeaderId);
            if (oldLeader != null)
            {
                oldLeader.Flags &= ~GroupMemberInfoFlags.GroupAdminFlags;
                Broadcast(group, FlagsChanged(group, oldLeader));
            }

            group.LeaderId = newLeader.CharacterId;
            newLeader.Flags |= GroupMemberInfoFlags.GroupAdminFlags;
            Broadcast(group, FlagsChanged(group, newLeader));

            Broadcast(group, new ServerGroupPromote
            {
                GroupId     = group.Id,
                LeaderIndex = newLeader.Index,
                NewLeader   = new Identity { RealmId = WorldServer.RealmId, Id = newLeader.CharacterId }
            });
        }

        /// <summary>Flags a member may change on themselves (roles unless role locked, ready state).</summary>
        private const GroupMemberInfoFlags SelfFlags = GroupMemberInfoFlags.Tank | GroupMemberInfoFlags.Healer | GroupMemberInfoFlags.DPS
            | GroupMemberInfoFlags.Ready | GroupMemberInfoFlags.HasSetReady;

        /// <summary>Flags only the leader may change (on anyone, roles of others included).</summary>
        private const GroupMemberInfoFlags LeaderFlags = SelfFlags | GroupMemberInfoFlags.CanInvite | GroupMemberInfoFlags.CanKick | GroupMemberInfoFlags.CanMark
            | GroupMemberInfoFlags.MainTank | GroupMemberInfoFlags.MainAssist | GroupMemberInfoFlags.RaidAssistant | GroupMemberInfoFlags.RoleLocked;

        /// <summary>
        /// Client 0x0408: change member flags. A member may set their own roles (unless role locked) and ready state; the leader may set
        /// everything in <see cref="LeaderFlags"/> on anyone. The new flags go to every member. Returns a short result for the ledger.
        /// </summary>
        public string SetMemberFlags(Player actor, ulong groupId, ulong targetId, GroupMemberInfoFlags flags, GroupMemberInfoFlags changed)
        {
            PlayerGroup group = GetGroupById(groupId);
            GroupMember target = group?.GetMember(targetId);
            if (group == null || target == null || group.GetMember(actor.CharacterId) == null)
                return "refused: not in that group";

            bool leader = group.LeaderId == actor.CharacterId;
            GroupMemberInfoFlags allowed = leader ? LeaderFlags : targetId == actor.CharacterId ? SelfFlags : GroupMemberInfoFlags.None;
            if (!leader && target.Flags.HasFlag(GroupMemberInfoFlags.RoleLocked))
                allowed &= ~(GroupMemberInfoFlags.Tank | GroupMemberInfoFlags.Healer | GroupMemberInfoFlags.DPS);
            if ((changed & ~allowed) != 0)
                return $"refused: flags 0x{(uint)(changed & ~allowed):x} not allowed";

            target.Flags = (target.Flags & ~changed) | (flags & changed);
            Broadcast(group, new ServerGroupMemberFlagsChanged
            {
                GroupId        = group.Id,
                MemberIndex    = target.Index,
                TargetedPlayer = new Identity { RealmId = WorldServer.RealmId, Id = target.CharacterId },
                ChangedFlags   = target.Flags
            });
            return "changed";
        }

        /// <summary>
        /// Client 0x0406: the leader changes the group flags. The Raid bit converts the group to a raid of up to 20 (docs/ASSUMPTIONS.md
        /// "Group flags"); a raid is not converted back while it holds more than 5.
        /// </summary>
        public string SetGroupFlags(Player actor, ulong groupId, GroupFlags flags)
        {
            PlayerGroup group = GetGroupById(groupId);
            if (group == null || group.LeaderId != actor.CharacterId)
                return "refused: not the leader";

            bool raid = flags.HasFlag(GroupFlags.Raid);
            if (!raid && group.Members.Count > 5)
                return "refused: more than 5 members";

            group.Flags   = flags | GroupFlags.OpenWorld;
            group.MaxSize = raid ? System.Math.Max(group.MaxSize, 20) : 5;
            return raid ? "raid" : "party";
        }

        /// <summary>
        /// Client 0x0432: a member with CanMark (or the leader) puts a marker on a unit (unit 0 clears the marker); a unit carries one
        /// marker at most. The whole list goes to every member (0x0431).
        /// </summary>
        public string SetMarker(Player actor, uint marker, uint unitId)
        {
            PlayerGroup group = GetGroup(actor.CharacterId);
            GroupMember member = group?.GetMember(actor.CharacterId);
            if (group == null || member == null)
                return "refused: not grouped";
            if (group.LeaderId != actor.CharacterId && !member.Flags.HasFlag(GroupMemberInfoFlags.CanMark))
                return "refused: no mark permission";
            if (marker == 0u)
                return "refused: marker 0";

            lock (group.Markers)
            {
                foreach (uint other in group.Markers.Where(m => m.Value == unitId).Select(m => m.Key).ToList())
                    group.Markers.Remove(other);
                if (unitId == 0u)
                    group.Markers.Remove(marker);
                else
                    group.Markers[marker] = unitId;
            }

            var list = new ServerGroupMarkers { GroupId = group.Id };
            lock (group.Markers)
                list.Markers.AddRange(group.Markers.Select(m => (m.Key, m.Value)));
            Broadcast(group, list);
            return unitId == 0u ? "cleared" : "marked";
        }

        /// <summary>Client 0x044A: the leader starts a ready check; every member's ready state is cleared and sent.</summary>
        public string ReadyCheck(Player actor, ulong groupId)
        {
            PlayerGroup group = GetGroupById(groupId);
            if (group == null || (group.LeaderId != actor.CharacterId && !(group.GetMember(actor.CharacterId)?.Flags.HasFlag(GroupMemberInfoFlags.RaidAssistant) ?? false)))
                return "refused: not the leader or an assistant";

            foreach (GroupMember member in group.Members)
            {
                member.Flags &= ~(GroupMemberInfoFlags.Ready | GroupMemberInfoFlags.HasSetReady);
                Broadcast(group, new ServerGroupMemberFlagsChanged
                {
                    GroupId        = group.Id,
                    MemberIndex    = member.Index,
                    TargetedPlayer = new Identity { RealmId = WorldServer.RealmId, Id = member.CharacterId },
                    ChangedFlags   = member.Flags
                });
            }
            return "started";
        }

        private static ServerGroupMemberFlagsChanged FlagsChanged(PlayerGroup group, GroupMember member)
        {
            return new ServerGroupMemberFlagsChanged
            {
                GroupId         = group.Id,
                MemberIndex     = member.Index,
                TargetedPlayer  = new Identity { RealmId = WorldServer.RealmId, Id = member.CharacterId },
                ChangedFlags    = GroupMemberInfoFlags.GroupAdminFlags,
                IsFromPromotion = true
            };
        }

        private static void ClearAssociation(Player player)
        {
            player.EnqueueToVisible(new ServerEntityGroupAssociation
            {
                UnitId  = player.Guid,
                GroupId = 0ul
            }, true);
        }

        /// <summary>
        /// Invoked each world tick with the delta since the previous tick occurred.
        /// </summary>
        public void Update(double lastTick)
        {
            LootWindow.Update();
            DateTime now = DateTime.UtcNow;
            foreach (GroupInvite invite in invites.Values.Where(i => i.Expires <= now))
                expired.Add(invite.InviteeId);

            foreach (ulong inviteeId in expired)
            {
                GroupInvite invite = invites[inviteeId];
                invites.Remove(inviteeId);

                Player inviter = CharacterManager.Instance.GetPlayer(invite.InviterId);
                Player invitee = CharacterManager.Instance.GetPlayer(invite.InviteeId);
                if (inviter != null && invitee != null)
                {
                    Send(inviter, new ServerGroupInviteResult { Name = invitee.Name, Result = GroupInviteResult.ExpiredInviter });
                    Send(invitee, new ServerGroupInviteResult { Name = inviter.Name, Result = GroupInviteResult.ExpiredInvitee });
                }
            }

            expired.Clear();

            foreach (GroupInvite request in joinRequests.Values.Where(i => i.Expires <= now).ToList())
            {
                joinRequests.Remove(request.InviteeId);
                Player requester = CharacterManager.Instance.GetPlayer(request.InviteeId);
                if (requester != null)
                    Send(requester, new ServerGroupRequestJoinResult { GroupId = request.GroupId, Name = requester.Name, Result = GroupInviteResult.ExpiredInvitee });
            }

            statTimer -= lastTick;
            if (statTimer <= 0d)
            {
                statTimer = StatInterval;
                SendStatUpdates();
            }

            positionTimer -= lastTick;
            if (positionTimer <= 0d)
            {
                positionTimer = PositionInterval;
                SendPositionUpdates();
            }
        }

        private void SendStatUpdates()
        {
            foreach (PlayerGroup group in groups.Values)
            {
                foreach (GroupMember member in group.Members)
                {
                    Player player = CharacterManager.Instance.GetPlayer(member.CharacterId);
                    if (player == null)
                        continue;

                    if (member.LastHealth == player.Health
                        && member.LastMaxHealth == player.MaxHealth
                        && member.LastLevel == player.Level)
                        continue;

                    member.LastHealth    = player.Health;
                    member.LastMaxHealth = player.MaxHealth;
                    member.LastLevel     = player.Level;

                    ServerGroupMemberStatUpdate update = ToStatUpdate(group, player);
                    foreach (GroupMember other in group.Members)
                    {
                        if (other.CharacterId != member.CharacterId)
                            Send(other.CharacterId, update);
                    }
                }
            }
        }

        private void SendPositionUpdates()
        {
            foreach (PlayerGroup group in groups.Values)
            {
                List<Player> players = group.Members
                    .Select(m => CharacterManager.Instance.GetPlayer(m.CharacterId))
                    .Where(p => p?.Map != null)
                    .ToList();

                foreach (IGrouping<uint, Player> map in players.GroupBy(p => p.Map.Entry.Id))
                {
                    var update = new ServerGroupPositionUpdate
                    {
                        GroupId = group.Id,
                        WorldId = map.Key
                    };

                    foreach (Player player in map)
                    {
                        update.Updates.Add(new ServerGroupPositionUpdate.Update
                        {
                            Identity    = ToIdentity(player),
                            Position    = player.Position,
                            WorldZoneId = player.Zone != null ? player.Zone.Id : 0u
                        });
                    }

                    Broadcast(group, update);
                }
            }
        }

        private void Broadcast(PlayerGroup group, IWritable message)
        {
            foreach (GroupMember member in group.Members)
                Send(member.CharacterId, message);
        }

        private static void Send(ulong characterId, IWritable message)
        {
            Send(CharacterManager.Instance.GetPlayer(characterId), message);
        }

        private static void Send(Player player, IWritable message)
        {
            player?.Session?.EnqueueMessageEncrypted(message);
        }

        private void SendActionResult(Player recipient, ulong groupId, Player target, GroupActionResult result)
        {
            SendActionResult(recipient, groupId, ToIdentity(target), result);
        }

        private void SendActionResult(Player recipient, ulong groupId, Identity target, GroupActionResult result)
        {
            Send(recipient, new ServerGroupActionResult
            {
                GroupId  = groupId,
                Identity = target,
                Result   = result
            });
        }

        private static Identity ToIdentity(Player player)
        {
            return new Identity
            {
                RealmId = WorldServer.RealmId,
                Id      = player.CharacterId
            };
        }

        private static GroupCharacter ToCharacter(Player player, uint memberIndex)
        {
            return new GroupCharacter
            {
                Name           = player.Name,
                Faction        = player.Faction,
                Race           = player.Race,
                Class          = player.Class,
                Sex            = player.Sex,
                Level          = (byte)player.Level,
                EffectiveLevel = (byte)player.Level,
                Path           = player.Path,
                Realm          = WorldServer.RealmId,
                WorldZoneId    = player.Zone != null ? (ushort)player.Zone.Id : (ushort)0,
                MapId          = player.Map != null ? player.Map.Entry.Id : 0u
            };
        }

        private static GroupMemberInfo ToMemberInfo(Player player, GroupMember member)
        {
            return new GroupMemberInfo
            {
                MemberIdentity = ToIdentity(player),
                Flags          = member.Flags,
                Member         = ToCharacter(player, member.Index),
                GroupIndex     = member.Index
            };
        }

        private static ServerGroupMemberStatUpdate ToStatUpdate(PlayerGroup group, Player player)
        {
            return new ServerGroupMemberStatUpdate
            {
                GroupId        = group.Id,
                TargetPlayer   = ToIdentity(player),
                Level          = (byte)player.Level,
                EffectiveLevel = (byte)player.Level,
                Health         = player.Health,
                HealthMax      = player.MaxHealth,
                Shield         = player.Shield,
                Path           = player.Path
            };
        }

        private GroupInfo ToNetworkGroup(PlayerGroup group)
        {
            var info = new GroupInfo
            {
                GroupId        = group.Id,
                Flags          = group.Flags,
                MaxGroupSize   = (uint)group.MaxSize,
                LeaderIdentity = new Identity { RealmId = WorldServer.RealmId, Id = group.LeaderId },
                RealmId        = WorldServer.RealmId,
                LootRule          = group.NormalRule,
                LootRuleThreshold = group.ThresholdRule,
                LootThreshold     = group.ThresholdQuality,
                LootRuleHarvest   = group.HarvestRule
            };

            foreach (GroupMember member in group.Members)
            {
                Player player = CharacterManager.Instance.GetPlayer(member.CharacterId);
                if (player != null)
                    info.MemberInfos.Add(ToMemberInfo(player, member));
            }

            return info;
        }
    }
}
