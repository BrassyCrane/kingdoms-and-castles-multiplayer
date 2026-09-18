using System;
using System.Collections.Generic;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Turns Riptide's disconnect and rejection reasons into something a player can act on.
    ///
    /// Every reason both enums define is spelled out. Covering only the common few and falling
    /// back to the enum name puts "ServerFull", "Kicked" or "ServerStopped" in a dialog box in
    /// front of a player, so the fallback here says something plain instead of leaking an
    /// identifier.
    /// </summary>
    public static class DisconnectMessages
    {
        // Keyed by enum name because the two enums overlap in meaning but not in type, and
        // several names appear in both. One table keeps the wording consistent between a
        // failed connection and a mid-game drop.
        private static readonly Dictionary<string, string> ByReason = new Dictionary<string, string>
        {
            // ---- couldn't connect in the first place ----
            { "NoConnection",       "Couldn't reach the host. They may have closed the game." },
            { "AlreadyConnected",   "You're already connected to this server." },
            { "ServerFull",         "The server is full." },
            { "Rejected",           "The host turned down the connection." },
            { "NeverConnected",     "Never managed to connect to the host." },
            { "ConnectionRejected", "The host turned down the connection." },

            // ---- lost an established connection ----
            { "TimedOut",           "Connection timed out." },
            { "Disconnected",       "Lost connection to the host." },
            { "Kicked",             "You were removed from the server by the host." },
            { "ServerStopped",      "The host ended the game." },
            { "PoorConnection",     "Dropped: the connection was too unstable to keep up." },
            { "TransportError",     "A network error ended the connection." },
        };

        /// <summary>
        /// Player-facing text for a reason. Never returns the raw enum name, an unmapped
        /// reason gets a generic sentence, with the identifier left to the log instead.
        /// </summary>
        public static string For(Enum reason)
        {
            if (reason == null) return "Disconnected from the server.";

            string name = reason.ToString();

            string text;
            if (ByReason.TryGetValue(name, out text)) return text;

            // A reason we have no wording for. Worth knowing about, but the player gets a
            // sentence rather than an enum member.
            NetLog.Warn("no player-facing message for disconnect reason '" + name + "'");
            return "Disconnected from the server.";
        }
    }
}
