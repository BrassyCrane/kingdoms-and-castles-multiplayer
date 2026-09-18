using Riptide;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// A message that can cross the wire. Implementations are plain data: they carry
    /// fields and know how to write and read themselves, nothing more. They do not send
    /// themselves and they do not act on the game, <see cref="NetRouter"/> sends, and
    /// registered handlers act.
    ///
    /// The codec is written by hand rather than derived from field metadata. That keeps
    /// field order explicit and provably identical on both ends, which is the whole
    /// ballgame for a binary format, and it turns a mistake into a compile error instead
    /// of a mid-session desync.
    ///
    /// Rule for implementers: Serialize and Deserialize must touch the same fields in
    /// the same order. Read the two methods top to bottom and they should mirror.
    /// </summary>
    public interface INetMessage : IMessageSerializable
    {
        /// <summary>Wire id. Constant for the type, and must match its registration.</summary>
        NetMessageId Id { get; }
    }

    /// <summary>
    /// Implemented by messages the host relays on to other clients, where the receiving
    /// client needs to know who originally did the thing.
    ///
    /// This is needed because <see cref="NetContext.SenderId"/> does not survive a relay.
    /// On the host it is the true originator, taken from the connection the bytes arrived
    /// on. But a client receives everything down its single connection to the host, so on
    /// the client side that value only ever identifies the host. Anything the client must
    /// attribute to a particular player therefore has to travel in the payload.
    ///
    /// <see cref="NetRouter.Relay"/> stamps this from the connection id before
    /// rebroadcasting, so the value clients see is the one the host observed rather than
    /// one the sender asserted about itself.
    /// </summary>
    public interface IOriginated : INetMessage
    {
        /// <summary>Client id of the player whose action this was. Set by the host.</summary>
        ushort Origin { get; set; }
    }

    /// <summary>
    /// Who a message came from, and which side is handling it. Passed alongside the
    /// message rather than stored on it, so one instance stays safe to broadcast to
    /// several peers without per-recipient mutation.
    /// </summary>
    public struct NetContext
    {
        /// <summary>Riptide client id of the sender. 0 when the local host originated it.</summary>
        public readonly ushort SenderId;

        /// <summary>True when the authoritative host is handling this.</summary>
        public readonly bool IsServer;

        public NetContext(ushort senderId, bool isServer)
        {
            SenderId = senderId;
            IsServer = isServer;
        }
    }
}
