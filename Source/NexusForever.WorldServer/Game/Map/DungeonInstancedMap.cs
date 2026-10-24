using System;
using System.Collections.Concurrent;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Game.Map.Static;
using NexusForever.WorldServer.Game.Static;
using NLog;

namespace NexusForever.WorldServer.Game.Map
{
    /// <summary>
    /// Instanced worlds for dungeons, raids, expeditions and adventures: one instance per group (or per character without a group), created
    /// when the first member enters and unloaded two minutes after the last one left, which resets the creatures and the encounters.
    /// </summary>
    public class DungeonInstancedMap : InstancedMap<DungeonMapInstance>
    {
        private static readonly ILogger log = LogManager.GetCurrentClassLogger();

        /// <summary>Where a player came from, to put them back when the instance unloads or they leave.</summary>
        private static readonly ConcurrentDictionary<ulong, MapPosition> returnPositions = new();

        public static void RememberReturn(ulong characterId, MapPosition position)
        {
            returnPositions[characterId] = position;
        }

        public static MapPosition GetReturn(ulong characterId)
        {
            return returnPositions.TryGetValue(characterId, out MapPosition position) ? position : null;
        }

        /// <summary>
        /// Instance id of a group (high bit set) or of a lone character, the same for every member, stable while the group exists.
        /// </summary>
        public static ulong InstanceIdFor(Player player)
        {
            PlayerGroup group = GroupManager.Instance.GetGroup(player.CharacterId);
            return group != null ? (1ul << 62) | group.Id : player.CharacterId;
        }

        /// <summary>
        /// A player left or lost the group whose instance they are in: removed after the pending removal timer (GameFormula 1123, 30 s), with
        /// ServerPendingWorldRemoval (reason GroupMembership) so the client shows the countdown; LeavePendingRemovalInstance (0x057C) leaves at once.
        /// </summary>
        public static void OnGroupMembershipLost(Player player, ulong groupId)
        {
            if (player?.Map is DungeonMapInstance instance && instance.InstanceId == ((1ul << 62) | groupId) && instance.UnloadStatus == null)
            {
                instance.EnqueuePendingRemoval(player, WorldRemovalReason.GroupMembership);
                Diagnostics.FailureLedger.Record("instance-pending-removal", $"world {instance.Entry.Id}", "left the group inside its instance");
            }
        }

        /// <summary>A player joined the group that owns the instance they are in: a pending removal is cancelled.</summary>
        public static void OnGroupJoined(Player player, ulong groupId)
        {
            if (player?.Map is DungeonMapInstance instance && instance.InstanceId == ((1ul << 62) | groupId) && instance.HasPendingRemoval(player))
            {
                instance.CancelPendingRemoval(player);
                Diagnostics.FailureLedger.Record("instance-pending-removal", $"world {instance.Entry.Id}", "cancelled: back in the group");
            }
        }

        public override GenericError? CanEnter(Player player, MapPosition position)
        {
            // remember where the player came from (open world position) unless they are moving between instances
            if (player.Map is not MapInstance && player.Map != null && !returnPositions.ContainsKey(player.CharacterId))
            {
                RememberReturn(player.CharacterId, new MapPosition
                {
                    Info     = new MapInfo { Entry = player.Map.Entry, InstanceId = null },
                    Position = player.Position
                });
            }

            return null;
        }

        protected override ulong? ResolveInstanceId(Player player, MapInfo info)
        {
            return info.InstanceId ?? InstanceIdFor(player);
        }

        protected override DungeonMapInstance CreateInstance(Player player, MapInfo info)
        {
            PlayerGroup owner = GroupManager.Instance.GetGroup(player.CharacterId);
            var instance = new DungeonMapInstance
            {
                InstanceId       = info.InstanceId ?? InstanceIdFor(player),
                OwnerCharacterId = owner?.LeaderId ?? player.CharacterId
            };
            instance.Initialise(Entry);
            log.Trace($"Dungeon instance {instance.InstanceId} created for world {Entry.Id}.");
            Diagnostics.FailureLedger.Record("dungeon-instance", $"created for world {Entry.Id} ({(instance.InstanceId >> 62 == 1 ? "group" : "character")} instance)", $"{player.Name} requested {(info.InstanceId.HasValue ? info.InstanceId.Value.ToString() : "none")}, group {(GroupManager.Instance.GetGroup(player.CharacterId)?.Id.ToString() ?? "none")}, came from {player.Map?.Entry.Id}");
            return instance;
        }
    }
}
