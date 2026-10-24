using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using NexusForever.Shared.Cryptography;
using NexusForever.Shared.Network.Message;
using NexusForever.Shared.Network.Message.Model;
using NexusForever.Shared.Network.Packet;
using NexusForever.Shared.Network.Static;

namespace NexusForever.Shared.Network
{
    public abstract class GameSession : NetworkSession
    {
        /// <summary>
        /// Determines if queued incoming packets can be processed during a world update.
        /// </summary>
        public bool CanProcessPackets { get; set; } = true;

        protected PacketCrypt encryption;

        private FragmentedBuffer onDeck;
        private readonly ConcurrentQueue<ClientGamePacket> incomingPackets = new();
        private readonly ConcurrentQueue<ServerGamePacket> outgoingPackets = new();

        /// <summary>
        /// Enqueue <see cref="IWritable"/> to be sent to the client.
        /// </summary>
        public virtual void EnqueueMessage(IWritable message)
        {
            if (!MessageManager.Instance.GetOpcode(message, out GameMessageOpcode opcode))
            {
                log.Warn("Failed to send message with no attribute!");
                return;
            }

            if (opcode != GameMessageOpcode.ServerAuthEncrypted
                && opcode != GameMessageOpcode.ServerRealmEncrypted)
                log.Trace($"Sent packet {opcode}(0x{opcode:X}).");

            var packet = new ServerGamePacket(opcode, message);
            outgoingPackets.Enqueue(packet);
        }

        /// <summary>
        /// Enqueue <see cref="IWritable"/> to be sent encrypted to the client.
        /// </summary>
        public virtual void EnqueueMessageEncrypted(IWritable message)
        {
            if (!MessageManager.Instance.GetOpcode(message, out GameMessageOpcode opcode))
            {
                log.Warn("Failed to send message with no attribute!");
                return;
            }

            using (var stream = new MemoryStream())
            using (var writer = new GamePacketWriter(stream))
            {
                writer.Write(opcode, 16);
                message.Write(writer);
                writer.FlushBits();

                byte[] data      = stream.ToArray();
                byte[] encrypted = encryption.Encrypt(data, data.Length);
                EnqueueMessage(BuildEncryptedMessage(encrypted));
            }

            log.Trace($"Sent packet {opcode}(0x{opcode:X}).");
        }

        [Conditional("DEBUG")]
        public void EnqueueMessageEncrypted(uint opcode, string hex)
        {
            using (var stream = new MemoryStream())
            using (var writer = new GamePacketWriter(stream))
            {
                writer.Write(opcode, 16);
                
                byte[] body = Enumerable.Range(0, hex.Length)
                    .Where(x => x % 2 == 0)
                    .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                    .ToArray();
                writer.WriteBytes(body);
                
                writer.FlushBits();

                byte[] data      = stream.ToArray();
                byte[] encrypted = encryption.Encrypt(data, data.Length);
                EnqueueMessage(BuildEncryptedMessage(encrypted));
            }
        }

        protected abstract IWritable BuildEncryptedMessage(byte[] data);

        public override void OnAccept(Socket newSocket)
        {
            base.OnAccept(newSocket);

            ulong key = PacketCrypt.GetKeyFromAuthBuildAndMessage();
            encryption = new PacketCrypt(key);
        }

        protected override uint OnData(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            using (var reader = new GamePacketReader(stream))
            {
                while (stream.Remaining() != 0)
                {
                    // no packet on deck waiting for additional information, new data will be part of a new packet
                    if (onDeck == null)
                    {
                        if (stream.Remaining() < sizeof(uint))
                        {
                            // we don't have enough data to know the length of the next packet
                            // return the remaining buffer so new data can be appended
                            return stream.Remaining();
                        }

                        uint size = reader.ReadUInt();
                        onDeck = new FragmentedBuffer(size - sizeof(uint));
                    }

                    onDeck.Populate(reader);
                    if (onDeck.IsComplete)
                    {
                        var packet = new ClientGamePacket(onDeck.Data);

                        incomingPackets.Enqueue(packet);
                        onDeck = null;
                    }
                }
            }

            return 0u;
        }

        protected override void OnDisconnect()
        {
            base.OnDisconnect();

            incomingPackets.Clear();
            outgoingPackets.Clear();
        }

        public override void Update(double lastTick)
        {
            // process pending packet queue
            while (CanProcessPackets && incomingPackets.TryDequeue(out ClientGamePacket packet))
                HandlePacket(packet);

            // flush pending packet queue
            while (outgoingPackets.TryDequeue(out ServerGamePacket packet))
                FlushPacket(packet);

            base.Update(lastTick);
        }

        /// <summary>
        /// Called with the opcode name when a client packet has a message class but no handler.
        /// </summary>
        public static Action<string> UnhandledPacketHook { get; set; }

        /// <summary>
        /// Called with the opcode (hex) when a client packet has no message class at all.
        /// </summary>
        public static Action<string> UnknownPacketHook { get; set; }

        /// <summary>
        /// Client packets that carry nothing the server acts on: ClientStatisticsWatchdog/WindowOpen/Gfx/Connection/Framerate (0x023C-0x0240),
        /// ClientRewardRotationsRequestUpdate (0x07CC), ClientCreddExchangeInfoRequest (0x0269), ClientFortuneNotifyStorefront (0x03D3) and
        /// ClientFortuneNotifyGame (0x0793). Names from the upstream emulator's opcode list.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<ushort> IgnoredClientOpcodes = new() { 0x023C, 0x023D, 0x023E, 0x023F, 0x0240, 0x07CC, 0x0269, 0x03D3, 0x0793 };

        private static readonly ConcurrentDictionary<ushort, byte> warnedUnknown = new();

        protected void HandlePacket(ClientGamePacket packet)
        {
            IReadable message = MessageManager.Instance.GetMessage(packet.Opcode);
            if (message == null)
            {
                // client telemetry and store notices (statistics, framerate, CREDD and storefront pings): nothing to do, keep the log readable
                if (IgnoredClientOpcodes.Contains((ushort)packet.Opcode))
                    return;

                UnknownPacketHook?.Invoke($"0x{(ushort)packet.Opcode:X4}");
                if (warnedUnknown.TryAdd((ushort)packet.Opcode, 0))
                    log.Warn($"Received unknown packet {packet.Opcode:X} (further ones of this opcode are only counted)");
                return;
            }

            MessageHandlerDelegate handlerInfo = MessageManager.Instance.GetMessageHandler(packet.Opcode);
            if (handlerInfo == null)
            {
                log.Warn($"Received unhandled packet {packet.Opcode}(0x{packet.Opcode:X}).");
                UnhandledPacketHook?.Invoke(packet.Opcode.ToString());
                return;
            }

            if (packet.Opcode != GameMessageOpcode.ClientEncrypted
                && packet.Opcode != GameMessageOpcode.ClientPacked
                && packet.Opcode != GameMessageOpcode.ClientPackedWorld
                && packet.Opcode != GameMessageOpcode.ClientEntityCommand)
                log.Trace($"Received packet {packet.Opcode}(0x{packet.Opcode:X}).");

            // FIXME workaround for now. possible performance impact. 
            // ClientPing does not currently work and the session times out after 300s -> this keeps the session alive if -any- client packet is received
            Heartbeat.OnHeartbeat();

            using (var stream = new MemoryStream(packet.Data))
            using (var reader = new GamePacketReader(stream))
            {
                message.Read(reader);
                if (reader.BytesRemaining > 0)
                    log.Warn($"Failed to read entire contents of packet {packet.Opcode}");

                try
                {
                    handlerInfo.Invoke(this, message);
                }
                catch (InvalidPacketValueException exception)
                {
                    log.Error(exception);
                    ForceDisconnect();
                }
                catch (Exception exception)
                {
                    log.Error(exception);
                }
            }
        }

        [MessageHandler(GameMessageOpcode.ClientEncrypted)]
        public void HandleEncryptedPacket(ClientEncrypted encrypted)
        {
            byte[] data = encryption.Decrypt(encrypted.Data, encrypted.Data.Length);

            var packet = new ClientGamePacket(data);
            HandlePacket(packet);
        }

        [MessageHandler(GameMessageOpcode.ClientPacked)]
        public void HandlePacked(ClientPacked packed)
        {
            var packet = new ClientGamePacket(packed.Data);
            HandlePacket(packet);
        }

        private void FlushPacket(ServerGamePacket packet)
        {
            using (var stream = new MemoryStream())
            using (var writer = new GamePacketWriter(stream))
            {
                writer.Write(packet.Size);
                writer.Write(packet.Opcode, 16);
                writer.WriteBytes(packet.Data);

                SendRaw(stream.ToArray());
            }
        }
    }
}
