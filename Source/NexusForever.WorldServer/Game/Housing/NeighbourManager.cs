using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;

namespace NexusForever.WorldServer.Game.Housing
{
    /// <summary>
    /// Residence neighbours: HousingLib.NeighborInvite / NeighborInviteByName (0x0512), NeighborInviteAccept / Decline (0x0513),
    /// NeighborEvict (0x0515), NeighborSetPermission (0x0518). Neighbours are mutual; the owner can make a neighbour a roommate (permission bit 0).
    /// Lists are kept per character in the cache volume. Used by the visit privacy check and by <see cref="Residence.CanModifyResidence"/>
    /// (roommates may decorate). docs/ASSUMPTIONS.md "Neighbours".
    /// </summary>
    public sealed class NeighbourManager : Singleton<NeighbourManager>
    {
        public const int MaxNeighbours = 100;
        public const uint Roommate = 1u;
        private static readonly TimeSpan InviteLifetime = TimeSpan.FromMinutes(1);

        public sealed class State
        {
            public Dictionary<ulong, uint> Neighbours { get; set; } = new();
        }

        private readonly ConcurrentDictionary<ulong, CharacterJson<State>> states = new();
        private readonly ConcurrentDictionary<ulong, (ulong Inviter, DateTime Expires)> invites = new();

        private CharacterJson<State> Store(ulong owner) => states.GetOrAdd(owner, id => new CharacterJson<State>("neighbours", id));

        public IReadOnlyDictionary<ulong, uint> Neighbours(ulong owner)
        {
            CharacterJson<State> store = Store(owner);
            lock (store)
                return new Dictionary<ulong, uint>(store.Value.Neighbours);
        }

        public bool IsNeighbour(ulong owner, ulong other) => Neighbours(owner).ContainsKey(other);

        public bool IsRoommate(ulong owner, ulong other) => Neighbours(owner).TryGetValue(other, out uint permission) && (permission & Roommate) != 0u;

        private bool Change(ulong owner, Func<State, bool> change)
        {
            CharacterJson<State> store = Store(owner);
            lock (store)
            {
                if (!change(store.Value))
                    return false;
                store.Save();
                return true;
            }
        }

        public string Invite(Player inviter, ulong invitee)
        {
            if (invitee == inviter.CharacterId)
                return "refused: self";
            if (IsNeighbour(inviter.CharacterId, invitee))
                return "refused: already a neighbour";
            if (Neighbours(inviter.CharacterId).Count >= MaxNeighbours)
                return "refused: list full";

            invites[invitee] = (inviter.CharacterId, DateTime.UtcNow + InviteLifetime);
            return "invited";
        }

        public string Respond(Player invitee, bool accept)
        {
            if (!invites.TryRemove(invitee.CharacterId, out var invite) || invite.Expires < DateTime.UtcNow)
                return "refused: no pending invite";
            if (!accept)
                return "declined";

            Change(invite.Inviter, s => s.Neighbours.TryAdd(invitee.CharacterId, 0u));
            Change(invitee.CharacterId, s => s.Neighbours.TryAdd(invite.Inviter, 0u));
            return "accepted";
        }

        public bool HasInvite(ulong invitee) => invites.TryGetValue(invitee, out var invite) && invite.Expires >= DateTime.UtcNow;

        public string Evict(Player owner, ulong other)
        {
            bool removed = Change(owner.CharacterId, s => s.Neighbours.Remove(other));
            Change(other, s => s.Neighbours.Remove(owner.CharacterId));
            return removed ? "evicted" : "refused: not a neighbour";
        }

        public string SetPermission(Player owner, ulong other, uint permission)
        {
            return Change(owner.CharacterId, s =>
            {
                if (!s.Neighbours.ContainsKey(other))
                    return false;
                s.Neighbours[other] = permission;
                return true;
            }) ? "set" : "refused: not a neighbour";
        }
    }
}
