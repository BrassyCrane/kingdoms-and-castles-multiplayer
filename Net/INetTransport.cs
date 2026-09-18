using Riptide;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// The seam between our messages and whatever is actually moving bytes.
    ///
    /// Everything above this interface talks in terms of "send to the host" and
    /// "broadcast to everyone", never in terms of Riptide objects. That keeps the
    /// session and gameplay layers testable without a live socket, and means the
    /// transport can be swapped without touching a single message.
    /// </summary>
    public interface INetTransport
    {
        /// <summary>True when this process is the authoritative host.</summary>
        bool IsServer { get; }

        /// <summary>True when this process has a live connection to a host.</summary>
        bool IsClientConnected { get; }

        /// <summary>
        /// Our own client id as the host knows it. 0 when not connected. Note the host
        /// is also a client of itself, so this is meaningful on both sides.
        /// </summary>
        ushort LocalClientId { get; }

        /// <summary>
        /// Host-only: how many clients are connected, the host's own local client included, so
        /// a host sitting alone reports 1 rather than 0. Compare against 1, not 0, to ask
        /// whether anyone else is in the session.
        /// </summary>
        int ConnectedClientCount { get; }

        /// <summary>Sends upstream to the host. No-op when not connected.</summary>
        void SendToServer(Message message);

        /// <summary>Host-only: sends to one client.</summary>
        void SendTo(Message message, ushort clientId);

        /// <summary>
        /// Host-only: sends to every connected client.
        /// <paramref name="exceptClientId"/> of 0 means "no exclusion", client ids
        /// start at 1, so 0 is never a real recipient.
        /// </summary>
        void Broadcast(Message message, ushort exceptClientId);
    }
}
