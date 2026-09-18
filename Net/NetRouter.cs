using System;
using Riptide;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Owns sending and receiving. Messages are data; this is the only thing that puts
    /// them on a wire or pulls them off one.
    ///
    /// Keeping send off the message type matters more than it looks. When a message can
    /// send itself it also has to know whether it is currently a server or a client
    /// thing, and it ends up carrying a mutable sender id that gets rewritten per
    /// recipient, which quietly makes broadcasting one instance to several peers unsafe.
    /// Here the message stays inert and the router supplies the context.
    /// </summary>
    public static class NetRouter
    {
        private static INetTransport transport;

        /// <summary>Wired up once during startup, before any message is sent.</summary>
        public static void Bind(INetTransport t)
        {
            if (t == null) throw new ArgumentNullException("t");
            transport = t;
        }

        public static bool IsServer
        {
            get { return transport != null && transport.IsServer; }
        }

        public static bool IsConnected
        {
            get { return transport != null && transport.IsClientConnected; }
        }

        /// <summary>Our client id as the host knows it, or 0 when not connected.</summary>
        public static ushort LocalClientId
        {
            get { return transport == null ? (ushort)0 : transport.LocalClientId; }
        }

        // ---- sending ----------------------------------------------------------

        /// <summary>
        /// Sends upstream to the host. Safe to call in single-player: with no connection
        /// this is a no-op, which is what lets gameplay patches call it unconditionally.
        /// </summary>
        public static void Send(INetMessage message)
        {
            if (transport == null || !transport.IsClientConnected) return;

            Message encoded;
            if (!TryEncode(message, out encoded)) return;

            try
            {
                transport.SendToServer(encoded);
            }
            catch (Exception ex)
            {
                NetLog.Error("sending " + message.Id + " to host", ex);
            }
        }

        /// <summary>Host-only: sends to a single client. No-op on a client.</summary>
        public static void SendTo(INetMessage message, ushort clientId)
        {
            if (transport == null || !transport.IsServer) return;
            if (clientId == 0)
            {
                NetLog.Warn("SendTo called with client id 0 for " + message.Id + "; ignored");
                return;
            }

            Message encoded;
            if (!TryEncode(message, out encoded)) return;

            try
            {
                transport.SendTo(encoded, clientId);
            }
            catch (Exception ex)
            {
                NetLog.Error("sending " + message.Id + " to client " + clientId, ex);
            }
        }

        /// <summary>
        /// Host-only: sends to every client. Pass the originator's id as
        /// <paramref name="exceptClientId"/> when relaying, so the sender does not
        /// receive an echo of its own action and apply it twice.
        /// </summary>
        public static void Broadcast(INetMessage message, ushort exceptClientId = 0)
        {
            if (transport == null || !transport.IsServer) return;

            Message encoded;
            if (!TryEncode(message, out encoded)) return;

            try
            {
                transport.Broadcast(encoded, exceptClientId);
            }
            catch (Exception ex)
            {
                NetLog.Error("broadcasting " + message.Id, ex);
            }
        }

        /// <summary>
        /// Host-only: stamps the true originator onto a message and passes it on to the
        /// other clients. This is the standard shape of a server handler for anything a
        /// player does that everyone else needs to see.
        ///
        /// The originator is taken from <paramref name="context"/>, which the router
        /// filled in from the connection the bytes actually arrived on, so a client
        /// cannot claim to be someone else by presetting the field before sending.
        /// </summary>
        public static void Relay(IOriginated message, NetContext context)
        {
            if (transport == null || !transport.IsServer) return;

            message.Origin = context.SenderId;
            Broadcast(message, context.SenderId);

            // Solo development: with no other clients this relay reached nobody, so the
            // client handler would never run. Stand in for the missing peer. Inert unless
            // explicitly switched on, and stands down as soon as a real player connects.
            if (NetLoopback.ShouldSubstitute(transport))
                NetLoopback.DeliverAsPeer(message);
        }

        /// <summary>
        /// Host-only: like <see cref="Relay"/>, but the originator gets a copy back too.
        ///
        /// Use this when the sender does not apply the action locally and depends on the
        /// echo to see its own effect, lobby chat is the standard example, where the
        /// send site only clears the input box and waits for the message to come back
        /// around. Using <see cref="Relay"/> there would make your own messages vanish
        /// from your own window.
        ///
        /// Prefer <see cref="Relay"/> when the sender already applied the change locally,
        /// otherwise it has to recognise and discard its own echo.
        /// </summary>
        public static void RelayIncludingSender(IOriginated message, NetContext context)
        {
            if (transport == null || !transport.IsServer) return;

            message.Origin = context.SenderId;
            Broadcast(message, 0);
        }

        /// <summary>
        /// Relays a client's message to the other clients, and reports whether the HOST should
        /// now apply it itself.
        ///
        /// Use this, not <see cref="RelayIncludingSender"/>, for anything the host must also act
        /// on. Applying only in the <c>OnClient</c> handler and relaying to everyone including
        /// the host's own local client makes the host depend on a loopback echo reaching it. When
        /// that echo does not arrive the failure is silent and exactly one-directional: clients
        /// see everything the host does, the host sees nothing any client does. No error, no
        /// warning, a client's keep and buildings simply never appear for the host, which is
        /// then free to build on top of them.
        ///
        /// The sender is still included in the relay. Its own <c>OnClient</c> handler discards its
        /// echo via <c>IsOwnEcho</c>, so excluding it here would change nothing for it while
        /// making the contract harder to reason about.
        ///
        /// Returns false, "don't apply, the echo will", when the local client id is unknown. We
        /// cannot exclude ourselves from the broadcast in that case, so applying directly as well
        /// would place everything twice.
        /// </summary>
        public static bool RelayAndApply(IOriginated message, NetContext context)
        {
            if (transport == null || !transport.IsServer) return false;

            message.Origin = context.SenderId;

            ushort local = LocalClientId;
            Broadcast(message, local);

            return local != 0;
        }

        private static bool TryEncode(INetMessage message, out Message encoded)
        {
            encoded = null;
            if (message == null)
            {
                NetLog.Warn("refusing to send a null message");
                return false;
            }

            try
            {
                MessageSendMode mode =
                    NetRegistry.DeliveryFor(message.Id) == NetDelivery.Unreliable
                        ? MessageSendMode.Unreliable
                        : MessageSendMode.Reliable;

                encoded = Message.Create(mode, (ushort)message.Id);
                message.Serialize(encoded);
                return true;
            }
            catch (Exception ex)
            {
                // A codec fault here would otherwise put a half-written message on the
                // wire and desync the far end, so drop it and say so loudly.
                NetLog.Error("encoding " + message.Id + " (" + message.GetType().Name + ")", ex);
                encoded = null;
                return false;
            }
        }

        // ---- receiving --------------------------------------------------------

        /// <summary>
        /// Decodes and dispatches one inbound message. Hook this to the transport's
        /// received event on both sides, with <paramref name="asServer"/> set to match.
        /// </summary>
        public static void Receive(MessageReceivedEventArgs e, bool asServer)
        {
            NetMessageId id = (NetMessageId)e.MessageId;

            // Clears a leaked apply scope from an earlier frame. Without this, one
            // exception escaping a using block would leave the session permanently
            // convinced it is applying remote state, and it would stop broadcasting.
            NetApply.Reset();

            if (!NetRegistry.IsKnown(id))
            {
                // Almost always a version mismatch between host and client. Worth a line
                // in the log, but not worth tearing the session down over.
                NetLog.Warn("ignoring unknown wire id " + e.MessageId +
                            " (band: " + id.Band() + "), version mismatch?");
                return;
            }

            ushort senderId = e.FromConnection == null ? (ushort)0 : e.FromConnection.Id;

            INetMessage message;
            try
            {
                message = NetRegistry.Create(id);
                message.Deserialize(e.Message);
            }
            catch (Exception ex)
            {
                NetLog.Error("decoding " + id + " from client " + senderId, ex);
                return;
            }

            NetRegistry.Handler handler = NetRegistry.HandlerFor(id, asServer);
            if (handler == null) return;   // legitimately one-sided; nothing to do here

            try
            {
                // Every handler runs inside an apply scope, so the Harmony patches that
                // broadcast a player's action stay quiet while this machine is reproducing
                // someone else's. Scoping here rather than in each handler is not tidiness: a
                // single handler that forgets creates a feedback loop, because applying the
                // message trips the patch that rebroadcasts it. Villager add reached tens of
                // thousands of villagers inside one game-year that way, until the reliable
                // channel gave up and dropped the client on "Poor connection".
                //
                // Suppressing at dispatch is the right level: it is not something each of ~40
                // handlers should be able to get wrong. NetApply.Bypass() still lets the one
                // case that must rebroadcast (starting-keep placement) step back out.
                using (NetApply.Scope())
                    handler(message, new NetContext(senderId, asServer));
            }
            catch (Exception ex)
            {
                // Contained deliberately: one bad message must not take down the receive
                // loop and with it the whole session.
                NetLog.Error("handling " + id + " from client " + senderId +
                             (asServer ? " [server]" : " [client]"), ex);
            }
        }
    }
}
