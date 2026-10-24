using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NexusForever.Shared;
using NexusForever.Shared.Network;
using NexusForever.WorldServer.Network;
using NexusForever.WorldServer.Network.Message.Model;
using NexusForever.WorldServer.Diagnostics;
using NexusForever.WorldServer.Game.Entity;

namespace NexusForever.WorldServer.Game.Social
{
    /// <summary>
    /// Friend, ignore and rival lists plus private notes by character id, kept per character as JSON in the cache volume (was in memory
    /// only). Filled by FriendshipLib.AddByName/RemoveByName (0x03B4/0x03C2), the by-identity messages (0x03B3 add, 0x03C1 remove, 0x03C7 note) and
    /// read by the whisper path, which drops whispers from ignored players. The owner's client gets the list at login (0x03BE) and every change
    /// (0x03B5 add, 0x03C3 remove, 0x03C9 note, 0x03C4 result), layouts from the client readers (docs/ASSUMPTIONS.md "Friendship list messages").
    /// </summary>
    public sealed class FriendManager : Singleton<FriendManager>
    {
        public const int MaxEntries = 100;

        public sealed class State
        {
            public List<ulong> Friends { get; set; } = new();
            public List<ulong> Ignores { get; set; } = new();
            public List<ulong> Rivals { get; set; } = new();
            public Dictionary<ulong, string> Notes { get; set; } = new();
        }

        private readonly ConcurrentDictionary<ulong, CharacterJson<State>> states = new();

        private CharacterJson<State> Store(ulong owner) => states.GetOrAdd(owner, id => new CharacterJson<State>("friends", id));

        private T Read<T>(ulong owner, System.Func<State, T> read)
        {
            CharacterJson<State> store = Store(owner);
            lock (store)
                return read(store.Value);
        }

        private bool Change(ulong owner, System.Func<State, bool> change)
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

        public IReadOnlyCollection<ulong> Friends(ulong owner) => Read(owner, s => s.Friends.ToList());

        public bool IsFriend(ulong owner, ulong other) => Read(owner, s => s.Friends.Contains(other));

        public bool IsIgnoring(ulong owner, ulong other) => Read(owner, s => s.Ignores.Contains(other));

        public bool IsRival(ulong owner, ulong other) => Read(owner, s => s.Rivals.Contains(other));

        public string Note(ulong owner, ulong other) => Read(owner, s => s.Notes.TryGetValue(other, out string note) ? note : null);

        private static bool Add(List<ulong> list, ulong owner, ulong other, string what)
        {
            if (owner == other)
            {
                FailureLedger.Record("social-refused", $"{what} self");
                return false;
            }

            if (list.Count >= MaxEntries || list.Contains(other))
            {
                FailureLedger.Record("social-refused", $"{what} full or duplicate");
                return false;
            }

            list.Add(other);
            return true;
        }

        /// <summary>A friend cannot also be ignored.</summary>
        public bool AddFriend(ulong owner, ulong other) => Change(owner, s =>
        {
            s.Ignores.Remove(other);
            return Add(s.Friends, owner, other, "friend");
        });

        public bool Ignore(ulong owner, ulong other) => Change(owner, s =>
        {
            s.Friends.Remove(other);
            return Add(s.Ignores, owner, other, "ignore");
        });

        public bool AddRival(ulong owner, ulong other) => Change(owner, s => Add(s.Rivals, owner, other, "rival"));

        public bool RemoveFriend(ulong owner, ulong other) => Change(owner, s => s.Friends.Remove(other));

        public bool Unignore(ulong owner, ulong other) => Change(owner, s => s.Ignores.Remove(other));

        public bool RemoveRival(ulong owner, ulong other) => Change(owner, s => s.Rivals.Remove(other));

        /// <summary>Private note on a listed character (friend, ignore or rival), at most 64 characters (assumption, no client limit found).</summary>
        public bool SetNote(ulong owner, ulong other, string note) => Change(owner, s =>
        {
            if (!s.Friends.Contains(other) && !s.Ignores.Contains(other) && !s.Rivals.Contains(other))
                return false;
            if (string.IsNullOrEmpty(note))
                return s.Notes.Remove(other);
            s.Notes[other] = note.Length > 64 ? note.Substring(0, 64) : note;
            return true;
        });

        /// <summary>Add by friendship type (CharacterFriendshipType Friend 0, Ignore 1, Rival 3).</summary>
        public bool AddByType(ulong owner, ulong other, byte type)
        {
            bool wasIgnored = type == 0 && IsIgnoring(owner, other);
            bool wasFriend = type == 1 && IsFriend(owner, other);
            bool done = type switch
            {
                0 => AddFriend(owner, other),
                1 => Ignore(owner, other),
                3 => AddRival(owner, other),
                _ => false
            };
            if (done)
            {
                if (wasIgnored)
                    Send(owner, new ServerFriendshipRemove { FriendshipId = FriendshipId(1, other) });
                if (wasFriend)
                    Send(owner, new ServerFriendshipRemove { FriendshipId = FriendshipId(0, other) });
                Send(owner, new ServerFriendshipAdd { Entry = Entry(type, other) });
            }
            return done;
        }

        public bool RemoveByType(ulong owner, ulong other, byte type)
        {
            bool done = type switch
            {
                0 => RemoveFriend(owner, other),
                1 => Unignore(owner, other),
                3 => RemoveRival(owner, other),
                _ => false
            };
            if (done)
                Send(owner, new ServerFriendshipRemove { FriendshipId = FriendshipId(type, other) });
            return done;
        }

        /// <summary>Note through the client message path: the owner's client gets 0x03C9 for every list the character is on.</summary>
        public bool SetNoteAndNotify(ulong owner, ulong other, string note)
        {
            if (!SetNote(owner, other, note))
                return false;
            string stored = Note(owner, other) ?? "";
            foreach (byte type in Types(owner, other))
                Send(owner, new ServerFriendshipSetNote { FriendshipId = FriendshipId(type, other), Note = stored });
            return true;
        }

        /// <summary>FriendshipResult for a refused request (client enum FriendshipResult, values from the client's Lua enum table).</summary>
        public byte RefusalReason(ulong owner, ulong other, byte type)
        {
            if (owner == other)
                return ServerFriendshipResult.CannotInviteSelf;
            return type switch
            {
                0 => IsFriend(owner, other) ? ServerFriendshipResult.PlayerAlreadyFriend : ServerFriendshipResult.MaxFriends,
                1 => IsIgnoring(owner, other) ? ServerFriendshipResult.PlayerAlreadyIgnored : ServerFriendshipResult.MaxIgnored,
                3 => IsRival(owner, other) ? ServerFriendshipResult.PlayerAlreadyRival : ServerFriendshipResult.MaxRivals,
                _ => ServerFriendshipResult.InvalidType
            };
        }

        public void SendResult(ulong owner, string name, byte result) => Send(owner, new ServerFriendshipResult { Name = name ?? "", Result = result });

        /// <summary>Full list at login (0x03BE), then the notes (0x03C9).</summary>
        public void SendList(Player player)
        {
            ulong owner = player.CharacterId;
            var entries = Read(owner, s => s.Friends.Select(c => (Type: (byte)0, Other: c))
                .Concat(s.Ignores.Select(c => ((byte)1, c)))
                .Concat(s.Rivals.Select(c => ((byte)3, c)))
                .ToList());
            var list = new ServerFriendshipList();
            foreach ((byte type, ulong other) in entries)
                list.Entries.Add(Entry(type, other));
            player.Session.EnqueueMessageEncrypted(list);
            foreach ((byte type, ulong other) in entries)
            {
                string note = Note(owner, other);
                if (!string.IsNullOrEmpty(note))
                    player.Session.EnqueueMessageEncrypted(new ServerFriendshipSetNote { FriendshipId = FriendshipId(type, other), Note = note });
            }
            FailureLedger.Record("friendship-list", entries.Count == 0 ? "sent empty" : "sent");
        }

        /// <summary>
        /// Friendship ids are the server's to choose (the client only echoes them in 0x03C3/0x03C9): type in the top 4 bits, character id below,
        /// so one character on two lists has two entries, as the client's entry carries one type.
        /// </summary>
        public static ulong FriendshipId(byte type, ulong other) => ((ulong)type << 60) | (other & 0x0FFFFFFFFFFFFFFFul);

        private IEnumerable<byte> Types(ulong owner, ulong other) => Read(owner, s =>
        {
            var types = new List<byte>();
            if (s.Friends.Contains(other)) types.Add(0);
            if (s.Ignores.Contains(other)) types.Add(1);
            if (s.Rivals.Contains(other)) types.Add(3);
            return types;
        });

        private static FriendshipEntry Entry(byte type, ulong other) => new()
        {
            FriendshipId = FriendshipId(type, other),
            RealmId      = WorldServer.RealmId,
            CharacterId  = other,
            Name         = CharacterCache.CharacterManager.Instance.GetCharacterInfo(other)?.Name ?? "",
            Type         = type
        };

        private static void Send(ulong owner, Shared.Network.Message.IWritable message)
        {
            WorldSession session = NetworkManager<WorldSession>.Instance.GetSession(s => s.Player?.CharacterId == owner);
            session?.EnqueueMessageEncrypted(message);
        }
    }
}
