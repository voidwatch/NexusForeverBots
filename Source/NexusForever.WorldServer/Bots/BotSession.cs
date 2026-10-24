using System;
using NexusForever.Shared.Network.Message;
using NexusForever.WorldServer.Network;

namespace NexusForever.WorldServer.Bots
{
    /// <summary>
    /// A <see cref="WorldSession"/> with no socket. Everything the server would send to a client is handed to
    /// <see cref="ServerMessage"/> instead, so the bot can react to it (character list, create result and so on).
    /// </summary>
    public class BotSession : WorldSession
    {
        public event Action<IWritable> ServerMessage;

        public override void EnqueueMessage(IWritable message)
        {
            ServerMessage?.Invoke(message);
        }

        public override void EnqueueMessageEncrypted(IWritable message)
        {
            ServerMessage?.Invoke(message);
        }

        public override void Update(double lastTick)
        {
            // there is no client sending packets, keep the heartbeat alive so the session is not timed out
            Heartbeat.OnHeartbeat();
            base.Update(lastTick);
        }
    }
}
