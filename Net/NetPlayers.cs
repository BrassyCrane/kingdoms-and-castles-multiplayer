using KaCMultiplayer;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Resolves a network client id to the player it belongs to.
    ///
    /// Kept off the message types themselves. Resolving the sender implicitly on every message
    /// ties every message type to the player registry whether it cares or not; messages are
    /// plain data, so the lookup lives here and handlers call it when they need a player.
    ///
    /// Returning null is normal, not exceptional. A message can outlive its sender by a
    /// few frames, someone disconnects, their record is torn down, and a message they
    /// sent just beforehand is still in flight. Handlers must cope with null rather than
    /// assume a player is there.
    /// </summary>
    public static class NetPlayers
    {
        /// <summary>
        /// The player behind a client id, or null if there isn't one.
        ///
        /// Checks both registries rather than indexing them: a disconnect race is an ordinary
        /// event and shouldn't cost an exception. This replaced <c>Main.GetPlayerByClientID</c>,
        /// which indexed both directly and threw on a missing key; that method is now gone and
        /// its last caller (the server's client-left handler) uses this instead.
        /// </summary>
        public static SessionPlayer ById(ushort clientId)
        {
            string steamId;
            if (!Main.clientSteamIds.TryGetValue(clientId, out steamId))
                return null;

            SessionPlayer player;
            if (!Main.kCPlayers.TryGetValue(steamId, out player))
            {
                // Mapped to a steam id but no player record: the two registries have
                // drifted apart, which is worth knowing about.
                NetLog.Warn("client " + clientId + " maps to steam id " + steamId +
                            " but has no player record");
                return null;
            }

            return player;
        }

        /// <summary>
        /// The player who owns a landmass team, or null if nobody does.
        ///
        /// An explicit walk rather than <c>FirstOrDefault(...).inst</c>. That form returns null
        /// for a miss and then immediately dereferences it, so "no one owns this team" arrived as
        /// a NullReferenceException, which the caller then used as control flow. On a per-frame
        /// path that costs an exception per miss, and a disconnected player's orphaned buildings
        /// miss on every frame.
        /// </summary>
        public static SessionPlayer ByTeam(int teamId)
        {
            foreach (SessionPlayer candidate in Main.kCPlayers.Values)
            {
                if (candidate == null || candidate.inst == null) continue;

                var owner = candidate.inst.PlayerLandmassOwner;
                if (owner != null && owner.teamId == teamId) return candidate;
            }
            return null;
        }

        /// <summary>Team ids currently claimed by a player. Diagnostics only.</summary>
        public static string KnownTeams()
        {
            var ids = new System.Collections.Generic.List<string>();
            foreach (SessionPlayer candidate in Main.kCPlayers.Values)
            {
                if (candidate == null || candidate.inst == null) continue;
                var owner = candidate.inst.PlayerLandmassOwner;
                ids.Add(owner == null ? "?" : owner.teamId.ToString());
            }
            return ids.Count == 0 ? "(none)" : string.Join(", ", ids.ToArray());
        }

        /// <summary>
        /// Convenience for handlers that need the acting player. Logs and returns false
        /// when the player has gone, so the caller can bail with one line.
        /// </summary>
        public static bool TryGet(ushort clientId, string context, out SessionPlayer player)
        {
            player = ById(clientId);
            if (player == null)
            {
                NetLog.Info(context + ": no player for client " + clientId + ", ignoring");
                return false;
            }
            return true;
        }
    }
}
