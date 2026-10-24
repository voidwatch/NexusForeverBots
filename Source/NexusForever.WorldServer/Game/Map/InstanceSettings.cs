using System.Collections.Concurrent;
using System.Linq;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Groups;

namespace NexusForever.WorldServer.Game.Map
{
    /// <summary>
    /// Instance settings chosen with ClientSetInstanceSettings (0x0163, layout from the retail client: portal unit, difficulty 2 bits, prime level,
    /// rally 11 bits). The choice belongs to the instance owner (group or lone character, <see cref="DungeonInstancedMap.InstanceIdFor"/>), only the
    /// group leader may change it, and it applies to instances created after it (an existing instance keeps its difficulty until it resets).
    /// The portal unit is not mapped to a world here, so the choice covers every dungeon world (docs/ASSUMPTIONS.md "Instance settings").
    /// </summary>
    public static class InstanceSettings
    {
        public sealed record Choice(InstanceDifficulty Difficulty, byte PrimeLevel, ushort Rally);

        private static readonly ConcurrentDictionary<ulong, Choice> choices = new();

        public static Choice For(ulong instanceId) => choices.TryGetValue(instanceId, out Choice choice) ? choice : null;

        public static bool Set(Player player, byte difficulty, byte primeLevel, ushort rally)
        {
            PlayerGroup group = GroupManager.Instance.GetGroup(player.CharacterId);
            if (group != null && group.LeaderId != player.CharacterId)
            {
                FailureLedger.Record("instance-settings-refused", "not the group leader");
                return false;
            }

            if (difficulty > (byte)InstanceDifficulty.Veteran)
            {
                FailureLedger.Record("instance-settings-refused", $"difficulty {difficulty}");
                return false;
            }

            choices[DungeonInstancedMap.InstanceIdFor(player)] = new Choice((InstanceDifficulty)difficulty, primeLevel, rally);
            FailureLedger.Record("instance-settings-set", ((InstanceDifficulty)difficulty).ToString());
            return true;
        }

        /// <summary>Unloads the owner's dungeon instances nobody is in (ClientResetInstances); returns how many were reset.</summary>
        public static int Reset(Player player)
        {
            PlayerGroup group = GroupManager.Instance.GetGroup(player.CharacterId);
            if (group != null && group.LeaderId != player.CharacterId)
            {
                FailureLedger.Record("instance-reset-refused", "not the group leader");
                return 0;
            }

            ulong id = DungeonInstancedMap.InstanceIdFor(player);
            int reset = 0;
            foreach (DungeonInstancedMap map in MapManager.Instance.LoadedMaps.OfType<DungeonInstancedMap>())
                foreach (MapInstance instance in map.GetInstances())
                {
                    if (instance.InstanceId != id || instance.UnloadStatus.HasValue)
                        continue;
                    if (instance.GetPlayerCount() > 0)
                    {
                        FailureLedger.Record("instance-reset-refused", "players inside");
                        continue;
                    }

                    try
                    {
                        instance.Unload();
                        reset++;
                    }
                    catch (System.InvalidOperationException)
                    {
                        FailureLedger.Record("instance-reset-refused", "already unloading");
                    }
                }

            FailureLedger.Record("instance-reset", reset > 0 ? "unloaded" : "nothing to reset");
            return reset;
        }
    }
}
