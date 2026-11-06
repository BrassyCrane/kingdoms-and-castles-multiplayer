// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;

using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Lets a companion mod (Multiplayer-Addon Modes, for one) talk between machines over this
    /// mod's connection. A companion is compiled separately and cannot define a message type of
    /// ours, so it names a channel, sends a string, and listens for strings on that channel. What
    /// the strings mean is entirely the companion's business; this carries them and says who
    /// sent them.
    ///
    /// Everything goes to everyone: up to the host, which relays it to every other machine and
    /// hands it to its own listeners too. A message meant for one kingdom says so in its own
    /// payload and the others ignore it. The sender never gets its own message back.
    ///
    /// Reached by reflection through <see cref="Main.AddonSend"/> and <see cref="Main.AddonListen"/>,
    /// which is why the handler is a plain Action of BCL types.
    /// </summary>
    public static class AddonChannel
    {
        private static readonly Dictionary<string, List<Action<int, bool, string>>> listeners =
            new Dictionary<string, List<Action<int, bool, string>>>();

        /// <summary>
        /// Calls <paramref name="handler"/>(senderTeam, fromHost, payload) for every message on
        /// <paramref name="channel"/> from another machine. senderTeam is -1 when the sender has
        /// no kingdom yet (still in the lobby). Listeners last for the life of the game, across
        /// sessions, so a companion registers once.
        /// </summary>
        public static void Listen(string channel, Action<int, bool, string> handler)
        {
            if (string.IsNullOrEmpty(channel) || handler == null) return;

            List<Action<int, bool, string>> list;
            if (!listeners.TryGetValue(channel, out list))
                listeners[channel] = list = new List<Action<int, bool, string>>();

            if (!list.Contains(handler)) list.Add(handler);
        }

        /// <summary>Sends to every other machine. False when there is no session to send on.</summary>
        public static bool Send(string channel, string payload)
        {
            if (string.IsNullOrEmpty(channel) || !NetRouter.IsConnected) return false;

            NetRouter.Send(new AddonMessage { Channel = channel, Payload = payload ?? string.Empty });
            return true;
        }

        /// <summary>
        /// The host's half of a relay: marks whether the host itself sent it, before it goes out,
        /// so every receiver can tell a host decision from a guest's say-so.
        /// </summary>
        internal static void StampOnHost(AddonMessage m, NetContext context)
        {
            m.FromHost = context.SenderId == 0 || context.SenderId == NetRouter.LocalClientId;
        }

        /// <summary>
        /// Hands a companion message to everyone listening on its channel, with who sent it. Our own
        /// message coming back is dropped, and one listener throwing cannot stop the others.
        /// </summary>
        internal static void Dispatch(AddonMessage m)
        {
            if (m == null) return;

            // Our own, sent back. Origin 0 is the host's own loopback (see NetContext.SenderId),
            // which is only "ours" on the host itself.
            if (m.Origin == NetRouter.LocalClientId || (m.Origin == 0 && NetRouter.IsServer)) return;

            List<Action<int, bool, string>> list;
            if (string.IsNullOrEmpty(m.Channel) || !listeners.TryGetValue(m.Channel, out list)) return;

            int senderTeam = -1;
            SessionPlayer sender = NetPlayers.ById(m.Origin);
            if (sender != null && sender.inst != null && sender.inst.PlayerLandmassOwner != null)
                senderTeam = sender.inst.PlayerLandmassOwner.teamId;

            // A copy: a handler may register another listener while this runs.
            foreach (Action<int, bool, string> handler in list.ToArray())
            {
                try { handler(senderTeam, m.FromHost, m.Payload ?? string.Empty); }
                catch (Exception ex) { NetLog.Error("companion channel '" + m.Channel + "'", ex); }
            }
        }
    }
}
