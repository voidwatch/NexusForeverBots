using System.Collections.Generic;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Network.Message.Model
{
    // Server friendship messages, layouts from the retail client's readers (client analysis, local),
    // Lua events from the handlers (client analysis, local). docs/ASSUMPTIONS.md "Friendship list messages".

    /// <summary>One list entry, client reader: friendship id u64, realm 14 bits, character id u64, name, type 4 bits.</summary>
    public class FriendshipEntry : IWritable
    {
        public ulong FriendshipId { get; set; }
        public ushort RealmId { get; set; }
        public ulong CharacterId { get; set; }
        public string Name { get; set; }
        public byte Type { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(FriendshipId);
            writer.Write(RealmId, 14u);
            writer.Write(CharacterId);
            writer.WriteStringWide(Name);
            writer.Write(Type, 4u);
        }
    }

    /// <summary>0x03BE, Lua event FriendshipLoaded: u16 count, entries.</summary>
    [Message(GameMessageOpcode.ServerFriendshipList)]
    public class ServerFriendshipList : IWritable
    {
        public List<FriendshipEntry> Entries { get; } = new();

        public void Write(GamePacketWriter writer)
        {
            writer.Write((ushort)Entries.Count);
            foreach (FriendshipEntry entry in Entries)
                entry.Write(writer);
        }
    }

    /// <summary>
    /// 0x03B5, Lua event FriendshipAdd: entry, then realm 14 bits, character id u64 and a float (the same tail as 0x03CA, a per-character
    /// value whose meaning is not read from the client; 0 is sent).
    /// </summary>
    [Message(GameMessageOpcode.ServerFriendshipAdd)]
    public class ServerFriendshipAdd : IWritable
    {
        public FriendshipEntry Entry { get; set; }
        public float Unknown { get; set; }

        public void Write(GamePacketWriter writer)
        {
            Entry.Write(writer);
            writer.Write(Entry.RealmId, 14u);
            writer.Write(Entry.CharacterId);
            writer.Write(Unknown);
        }
    }

    /// <summary>0x03C3, Lua events FriendshipRemove/FriendshipPostRemove: friendship id u64.</summary>
    [Message(GameMessageOpcode.ServerFriendshipRemove)]
    public class ServerFriendshipRemove : IWritable
    {
        public ulong FriendshipId { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(FriendshipId);
        }
    }

    /// <summary>0x03C4, Lua event FriendshipResult: character name, result 6 bits (client enum FriendshipResult).</summary>
    [Message(GameMessageOpcode.ServerFriendshipResult)]
    public class ServerFriendshipResult : IWritable
    {
        public const byte PlayerNotFound = 0, PlayerAlreadyFriend = 3, FriendshipNotFound = 5, InvalidType = 6, MaxFriends = 11,
            MaxRivals = 12, MaxIgnored = 13, PlayerAlreadyRival = 18, PlayerAlreadyIgnored = 20, CannotInviteSelf = 29;

        public string Name { get; set; }
        public byte Result { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.WriteStringWide(Name);
            writer.Write(Result, 6u);
        }
    }

    /// <summary>0x03C9, note of an entry: friendship id u64, note.</summary>
    [Message(GameMessageOpcode.ServerFriendshipSetNote)]
    public class ServerFriendshipSetNote : IWritable
    {
        public ulong FriendshipId { get; set; }
        public string Note { get; set; }

        public void Write(GamePacketWriter writer)
        {
            writer.Write(FriendshipId);
            writer.WriteStringWide(Note);
        }
    }
}
