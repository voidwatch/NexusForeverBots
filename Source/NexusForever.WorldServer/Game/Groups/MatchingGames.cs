using System;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.CharacterCache;
using NexusForever.WorldServer.Game.Entity;
using NexusForever.WorldServer.Game.Map;

namespace NexusForever.WorldServer.Game.Groups
{
    /// <summary>
    /// Matched games of the group finder: a match (solo queue, filled premade or a full premade) becomes a pending game that every member
    /// answers with ClientMatchingMatchReadyResponse (0x05C8). When all accepted, the members are moved into the group's instance of the
    /// matched world; TransferIntoMatch (0x0606) brings a member back in, LeaveGame (0x05DA) takes a member out and out of the group.
    /// Certain: the three client messages and their layouts. Defaults (docs/ASSUMPTIONS.md "Instances and matching"): 60 s to answer,
    /// a decline or a timeout cancels the game (the group stays, nobody is queued again), the server message that shows the popup on a
    /// real client is not decoded and not sent (matching-pending-popup-unsent).
    /// </summary>
    public sealed class MatchingGames : Singleton<MatchingGames>
    {
        public const double AnswerSeconds = 60d;

        public sealed class MatchedGame
        {
            public PlayerGroup Group;
            public ushort World;
            public readonly HashSet<ulong> Accepted = new();
            public DateTime Expires;
            public bool Started;
        }

        private readonly Dictionary<ulong, MatchedGame> games = new();

        private MatchingGames()
        {
        }

        public MatchedGame For(Player player)
        {
            PlayerGroup group = GroupManager.Instance.GetGroup(player.CharacterId);
            return group != null && games.TryGetValue(group.Id, out MatchedGame game) ? game : null;
        }

        /// <summary>A match formed for the world: every member must answer.</summary>
        public void Register(PlayerGroup group, ushort world)
        {
            games[group.Id] = new MatchedGame { Group = group, World = world, Expires = DateTime.UtcNow.AddSeconds(AnswerSeconds) };
            FailureLedger.Record("matching-game", "pending", $"world {world}, {group.Members.Count} members");
            FailureLedger.Record("matching-pending-popup-unsent", "server message for the pending-game popup not decoded", $"world {world}");
        }

        public void Respond(Player player, bool accept)
        {
            MatchedGame game = For(player);
            if (game == null || game.Started)
            {
                FailureLedger.Record("matching-game", game == null ? "answer refused: no pending game" : "answer refused: game already started");
                return;
            }

            if (!accept)
            {
                games.Remove(game.Group.Id);
                FailureLedger.Record("matching-game", "cancelled: declined", $"world {game.World}");
                return;
            }

            game.Accepted.Add(player.CharacterId);
            if (game.Group.Members.Any(m => !game.Accepted.Contains(m.CharacterId)))
            {
                FailureLedger.Record("matching-game", "accepted", $"{game.Accepted.Count} of {game.Group.Members.Count}");
                return;
            }

            game.Started = true;
            FailureLedger.Record("matching-game", "started: all accepted", $"world {game.World}, {game.Group.Members.Count} members");
            foreach (GroupMember member in game.Group.Members)
                Transfer(CharacterManager.Instance.GetPlayer(member.CharacterId), game);
        }

        /// <summary>TransferIntoMatch: a member of a started game that is not inside goes to the instance entrance of the group's instance.</summary>
        public void Transfer(Player player)
        {
            MatchedGame game = For(player);
            if (game == null || !game.Started)
            {
                FailureLedger.Record("matching-game", "transfer refused: no started game");
                return;
            }
            Transfer(player, game);
        }

        private static void Transfer(Player player, MatchedGame game)
        {
            if (player == null || (player.Map is DungeonMapInstance inside && inside.Entry.Id == game.World))
                return;
            System.Numerics.Vector3 entrance = InstancePortals.Entrance(game.World);
            player.TeleportTo(game.World, entrance.X, entrance.Y, entrance.Z, (1ul << 62) | game.Group.Id);
            FailureLedger.Record("matching-game", "transferred into the match", $"world {game.World}");
        }

        /// <summary>LeaveGame: the member leaves the instance (back where they came from) and the group.</summary>
        public void Leave(Player player)
        {
            MatchedGame game = For(player);
            if (game == null)
            {
                FailureLedger.Record("matching-game", "leave refused: not in a matched game");
                return;
            }

            if (player.Map is DungeonMapInstance && DungeonInstancedMap.GetReturn(player.CharacterId) is MapPosition back)
                player.TeleportTo(back);
            GroupManager.Instance.Leave(player, game.Group.Id, false);
            FailureLedger.Record("matching-game", "left the game", $"world {game.World}");
        }

        /// <summary>Pending games time out; games of groups that no longer exist are dropped.</summary>
        public void Update(double lastTick)
        {
            if (games.Count == 0)
                return;

            DateTime now = DateTime.UtcNow;
            foreach (MatchedGame game in games.Values.ToList())
            {
                if (GroupManager.Instance.GetGroupById(game.Group.Id) == null)
                    games.Remove(game.Group.Id);
                else if (!game.Started && now > game.Expires)
                {
                    games.Remove(game.Group.Id);
                    FailureLedger.Record("matching-game", "cancelled: not every member answered in time", $"world {game.World}, {game.Accepted.Count} of {game.Group.Members.Count}");
                }
            }
        }
    }
}
