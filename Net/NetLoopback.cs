using System;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Development aid: makes a solo session behave as though a second player were
    /// present, so client-side handlers can be exercised without a second machine.
    ///
    /// The gap it fills. <see cref="NetRouter.Relay"/> deliberately excludes the sender,
    /// because the sender already applied the action locally. Alone, you are the only
    /// client, so the relay reaches nobody, and the client handler never runs. The send
    /// path and the codec get tested; the receive-and-apply half does not. That half is
    /// where a bad handler corrupts a save rather than merely annoying someone.
    ///
    /// What this does. When a relay would have gone nowhere, it re-delivers the message
    /// locally, attributed to a fictional peer. Two details make it a real test rather
    /// than a comfortable one:
    ///
    /// * It goes through <see cref="NetCodec"/>, so the handler receives a genuinely
    ///   deserialized instance, not the object the sender was holding. A codec bug shows
    ///   up here exactly as it would across a network.
    /// * The sender id is <see cref="SyntheticPeerId"/>, not yours. Handlers that check
    ///   "was this me?" take the same branch they would for a real peer, instead of the
    ///   self-echo branch.
    ///
    /// What it still cannot prove: that two machines agree. Both ends run the same build
    /// here, so a wire contract that is self-consistent but wrong stays invisible. It
    /// closes the handler gap, not the interop one.
    /// </summary>
    public static class NetLoopback
    {
        /// <summary>
        /// DEV ONLY, leave false in anything you upload.
        ///
        /// Set from Main during startup. When on, every excluded relay is also applied
        /// locally as though a peer had sent it, which means your own actions visibly
        /// happen twice. That is the intended behaviour, and it makes for a strange game.
        /// </summary>
        public static bool Enabled = false;

        /// <summary>
        /// Sender id stamped on looped-back messages. Deliberately far outside the range
        /// Riptide hands out (ids start at 1 and climb), so anything treating it as a real
        /// peer is obvious in the log rather than silently plausible.
        /// </summary>
        public const ushort SyntheticPeerId = 61000;

        private static bool announced;

        /// <summary>
        /// True when loopback should stand in for a peer: switched on, and nobody else is
        /// actually connected. Auto-stands-down the moment a real player joins, so a
        /// forgotten flag cannot double-apply everyone's actions in a live game.
        /// </summary>
        public static bool ShouldSubstitute(INetTransport transport)
        {
            if (!Enabled || transport == null || !transport.IsServer) return false;

            // The host's own client counts, so 1 means "only me".
            bool alone = transport.ConnectedClientCount <= 1;

            if (!alone)
            {
                if (announced)
                {
                    NetLog.Info("loopback standing down, a real peer is connected");
                    announced = false;
                }
                return false;
            }

            if (!announced)
            {
                NetLog.Warn("LOOPBACK ACTIVE, relays are being applied locally as though " +
                            "from peer " + SyntheticPeerId + ". Development only; your own " +
                            "actions will apply twice.");
                announced = true;
            }

            return true;
        }

        /// <summary>
        /// Re-delivers a relayed message to this machine as though a peer had sent it.
        /// Failures are logged and swallowed, this is a diagnostic, and it must never be
        /// the reason a session falls over.
        /// </summary>
        public static void DeliverAsPeer(IOriginated message)
        {
            if (message == null) return;

            ushort restore = message.Origin;
            try
            {
                message.Origin = SyntheticPeerId;

                // Full round trip through the wire format: the handler must receive a
                // decoded instance, not the sender's own object.
                byte[] bytes = NetCodec.Encode(message, message.Id);
                INetMessage decoded = NetCodec.Decode(bytes, message.Id);

                NetRegistry.Handler handler = NetRegistry.HandlerFor(message.Id, false);
                if (handler == null)
                {
                    NetLog.Info("loopback: " + message.Id + " has no client handler; nothing to exercise");
                    return;
                }

                handler(decoded, new NetContext(SyntheticPeerId, false));
            }
            catch (Exception ex)
            {
                NetLog.Error("loopback delivering " + message.Id, ex);
            }
            finally
            {
                message.Origin = restore;
            }
        }
    }
}
