using System;
using System.IO;
using NexusForever.Shared.Network;
using NexusForever.Shared.Network.Message;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// Builds client messages the same way the real client would send them, so the existing message handlers can be reused unchanged.
    /// </summary>
    public static class BotPackets
    {
        public static T Build<T>(Action<GamePacketWriter> write) where T : IReadable, new()
        {
            byte[] data;
            using (var stream = new MemoryStream())
            using (var writer = new GamePacketWriter(stream))
            {
                write(writer);
                writer.FlushBits();
                data = stream.ToArray();
            }

            var message = new T();
            using (var stream = new MemoryStream(data))
            using (var reader = new GamePacketReader(stream))
                message.Read(reader);

            return message;
        }
    }
}
