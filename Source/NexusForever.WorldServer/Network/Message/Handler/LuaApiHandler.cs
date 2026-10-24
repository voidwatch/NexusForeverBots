using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Game.Groups;
using NexusForever.WorldServer.Network.Message.Model;

namespace NexusForever.WorldServer.Network.Message.Handler
{
    /// <summary>Client messages reachable from the Lua API that had no handler (docs/OPCODE-COVERAGE.md).</summary>
    public static class LuaApiHandler
    {
        [MessageHandler(GameMessageOpcode.ClientChallengeChoice)]
        public static void HandleChallengeChoice(WorldSession session, ClientChallengeChoice choice)
        {
            var challenges = session.Player.ChallengeManager;
            bool done;
            switch (choice.Choice)
            {
                case ClientChallengeChoice.Activate:
                    done = challenges.Start(choice.ChallengeId);
                    break;
                case ClientChallengeChoice.Abandon:
                    done = challenges.Abandon(choice.ChallengeId);
                    break;
                case ClientChallengeChoice.AcceptShared:
                    // nothing on the server shares challenges yet (ServerChallengeShared 0x00A9 is never sent): accepting starts it for this player
                    done = challenges.Start(choice.ChallengeId);
                    break;
                case ClientChallengeChoice.RejectShared:
                    done = true;
                    break;
                default:
                    Diagnostics.FailureLedger.Record("challenge-choice-unknown", $"choice {choice.Choice}", $"challenge {choice.ChallengeId} value {choice.Value}");
                    return;
            }

            Diagnostics.FailureLedger.Record("challenge-choice", $"choice {choice.Choice} {(done ? "done" : "refused")}", $"challenge {choice.ChallengeId}");
        }

        /// <summary>Attribute points were removed with Drop 6: build 16042 has no attribute point tables, there is nothing to reset.</summary>
        [MessageHandler(GameMessageOpcode.ClientResetAttributePoints)]
        public static void HandleResetAttributePoints(WorldSession session, ClientResetAttributePoints reset)
        {
            Diagnostics.FailureLedger.Record("attribute-reset", "no attribute points in this build");
        }

        /// <summary>Cinematics run on the client; the server has no cinematic state to track (no quest objective waits on one).</summary>
        [MessageHandler(GameMessageOpcode.ClientCinematicState)]
        public static void HandleCinematicState(WorldSession session, ClientCinematicState state)
        {
            Diagnostics.FailureLedger.Record("cinematic-state", $"state {state.State}", $"flag {state.Flag}");
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingQueueLeaveAll)]
        public static void HandleMatchingQueueLeaveAll(WorldSession session, ClientMatchingQueueLeaveAll leave)
        {
            Leave(session, "all");
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingQueueLeave)]
        public static void HandleMatchingQueueLeave(WorldSession session, ClientMatchingQueueLeave leave)
        {
            Leave(session, $"type {leave.MatchType}");
        }

        [MessageHandler(GameMessageOpcode.ClientMatchingQueueLeaveAsGroup)]
        public static void HandleMatchingQueueLeaveAsGroup(WorldSession session, ClientMatchingQueueLeaveAsGroup leave)
        {
            // the whole group leaves the queue (GroupFinder queues players one by one)
            PlayerGroup group = GroupManager.Instance.GetGroup(session.Player.CharacterId);
            if (group != null)
                foreach (GroupMember member in group.Members)
                {
                    var player = Game.CharacterCache.CharacterManager.Instance.GetPlayer(member.CharacterId);
                    if (player != null)
                        GroupFinder.Instance.Dequeue(player);
                }
            Leave(session, $"group, type {leave.MatchType}");
        }

        /// <summary>GroupFinder has one queue (dungeons); every leave message removes the player from it.</summary>
        private static void Leave(WorldSession session, string what)
        {
            bool queued = GroupFinder.Instance.IsQueued(session.Player);
            GroupFinder.Instance.Dequeue(session.Player);
            Diagnostics.FailureLedger.Record("group-finder", queued ? "left the queue" : "leave while not queued", what);
        }

        [MessageHandler(GameMessageOpcode.ClientGroupRequestJoin)]
        public static void HandleGroupRequestJoin(WorldSession session, ClientGroupRequestJoin request)
        {
            GroupManager.Instance.RequestJoin(session.Player, request.Name);
        }

        [MessageHandler(GameMessageOpcode.ClientGroupRequestJoinResponse)]
        public static void HandleGroupRequestJoinResponse(WorldSession session, ClientGroupRequestJoinResponse response)
        {
            GroupManager.Instance.RequestJoinResponse(session.Player, response.GroupId, response.Name, response.Accept);
        }

        /// <summary>
        /// FriendshipLib.AddByName: friend or ignore entry on the server's list (Game/Social/FriendManager). 
        /// server (rivals and notes are kept too, the owner's client gets the ServerFriendship* list messages).
        /// </summary>
        [MessageHandler(GameMessageOpcode.ClientFriendshipAddByName)]
        public static void HandleFriendshipAddByName(WorldSession session, ClientFriendshipAddByName add)
        {
            var other = Game.CharacterCache.CharacterManager.Instance.GetCharacterInfo(add.Name);
            bool done = false;
            if (other == null)
            {
                Diagnostics.FailureLedger.Record("friendship-refused", "no character of that name", $"type {add.Type}");
                Game.Social.FriendManager.Instance.SendResult(session.Player.CharacterId, add.Name, ServerFriendshipResult.PlayerNotFound);
            }
            else if (add.Type is ClientFriendshipAddByName.Friend or ClientFriendshipAddByName.Ignore or ClientFriendshipAddByName.Rival)
            {
                var friends = Game.Social.FriendManager.Instance;
                byte reason = friends.RefusalReason(session.Player.CharacterId, other.CharacterId, add.Type);
                done = friends.AddByType(session.Player.CharacterId, other.CharacterId, add.Type);
                if (done && !string.IsNullOrEmpty(add.Note))
                    friends.SetNoteAndNotify(session.Player.CharacterId, other.CharacterId, add.Note);
                if (!done)
                    friends.SendResult(session.Player.CharacterId, add.Name, reason);
            }
            else
            {
                Diagnostics.FailureLedger.Record("friendship-refused", $"type {add.Type} has no list on the server (account friends need the auth server)");
                Game.Social.FriendManager.Instance.SendResult(session.Player.CharacterId, add.Name, ServerFriendshipResult.InvalidType);
            }
            Diagnostics.FailureLedger.Record("friendship", $"add type {add.Type} {(done ? "done" : "refused")}");
        }

        [MessageHandler(GameMessageOpcode.ClientFriendshipRemoveByName)]
        public static void HandleFriendshipRemoveByName(WorldSession session, ClientFriendshipRemoveByName remove)
        {
            var other = Game.CharacterCache.CharacterManager.Instance.GetCharacterInfo(remove.Name);
            bool done = other != null && Game.Social.FriendManager.Instance.RemoveByType(session.Player.CharacterId, other.CharacterId, remove.Type);
            if (!done)
                Game.Social.FriendManager.Instance.SendResult(session.Player.CharacterId, remove.Name,
                    other == null ? ServerFriendshipResult.PlayerNotFound : ServerFriendshipResult.FriendshipNotFound);
            Diagnostics.FailureLedger.Record("friendship", $"remove type {remove.Type} {(done ? "done" : "refused")}");
        }
    }
}
