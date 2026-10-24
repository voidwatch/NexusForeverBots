using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    public static class GroupHandler
    {
        [MessageHandler(GameMessageOpcode.ClientGroupInvite)]
        public static void HandleGroupInvite(WorldSession session, ClientGroupInvite groupInvite)
        {
            GroupManager.Instance.Invite(session.Player, groupInvite.Name);
        }

        [MessageHandler(GameMessageOpcode.ClientGroupInviteResponse)]
        public static void HandleGroupInviteResponse(WorldSession session, ClientGroupInviteResponse response)
        {
            GroupManager.Instance.InviteResponse(session.Player, response.GroupId, response.Response);
        }

        [MessageHandler(GameMessageOpcode.ClientGroupLeave)]
        public static void HandleGroupLeave(WorldSession session, ClientGroupLeave groupLeave)
        {
            GroupManager.Instance.Leave(session.Player, groupLeave.GroupId, groupLeave.ShouldDisband);
        }

        [MessageHandler(GameMessageOpcode.ClientGroupKick)]
        public static void HandleGroupKick(WorldSession session, ClientGroupKick groupKick)
        {
            GroupManager.Instance.Kick(session.Player, groupKick.GroupId, groupKick.TargetedPlayer);
        }

        [MessageHandler(GameMessageOpcode.ClientGroupPromote)]
        public static void HandleGroupPromote(WorldSession session, ClientGroupPromote groupPromote)
        {
            GroupManager.Instance.Promote(session.Player, groupPromote.GroupId, groupPromote.TargetedPlayer);
        }

        [MessageHandler(GameMessageOpcode.ClientGroupLootRulesChange)]
        public static void HandleGroupLootRulesChange(WorldSession session, ClientGroupLootRulesChange change)
        {
            GroupManager.Instance.SetLootRules(session.Player, change.GroupId, change.NormalRule, change.ThresholdRule, change.ThresholdQuality, change.HarvestRule);
        }
    }
}
