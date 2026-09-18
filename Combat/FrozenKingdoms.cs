using System;
using System.Collections.Generic;
using KaCMultiplayer.Net;

namespace KaCMultiplayer.Combat
{
    /// <summary>
    /// Which teams belong to players who have left, cached so a per-tick check is a set lookup
    /// rather than a walk of the player roster.
    ///
    /// The caching is the whole point. A departed kingdom is meant to stop dead until its owner
    /// returns, and the honest way to enforce that is to ask "is this thing's owner absent?" on
    /// every tick path. Asking it by walking <c>Main.kCPlayers</c> each time, for every army and
    /// every ship, every frame, is precisely the shape of the per-tick work that once starved this
    /// mod's simulation into a freeze. So the answer is computed occasionally and read constantly.
    ///
    /// Refreshed on a timer rather than on a roster event: leaving and rejoining are rare, a second
    /// of staleness costs nothing (a departed kingdom ticking for one more second is invisible), and
    /// a timer cannot be forgotten the way a new call site can.
    /// </summary>
    public static class FrozenKingdoms
    {
        /// <summary>Fixed ticks between rebuilds. Ghost status changes on the scale of minutes.</summary>
        private const int TicksBetweenRefresh = 50;

        private static int tickCounter;

        /// <summary>
        /// Teams whose owner is absent. Empty is the overwhelmingly common case, and it is checked
        /// first everywhere, so a session where nobody has left pays a single count comparison.
        /// </summary>
        private static readonly HashSet<int> frozen = new HashSet<int>();

        /// <summary>True when nobody has left, so callers can skip the lookup entirely.</summary>
        public static bool None { get { return frozen.Count == 0; } }

        /// <summary>Whether this team's kingdom should be standing still.</summary>
        public static bool IsFrozen(int teamId)
        {
            return frozen.Count != 0 && frozen.Contains(teamId);
        }

        /// <summary>Clears the cache, for a new session or a load.</summary>
        public static void Reset()
        {
            frozen.Clear();
            tickCounter = 0;
        }

        /// <summary>Called every fixed tick; rebuilds the set occasionally.</summary>
        public static void Tick()
        {
            if (!Main.FreezeGhostKingdomsFully) { if (frozen.Count != 0) frozen.Clear(); return; }
            if (!NetClient.client.IsConnected) { if (frozen.Count != 0) frozen.Clear(); return; }

            if (++tickCounter < TicksBetweenRefresh) return;
            tickCounter = 0;

            Refresh();
        }

        /// <summary>Rebuilds the set from the current roster.</summary>
        private static void Refresh()
        {
            try
            {
                frozen.Clear();

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || !kp.isGhost) continue;
                    if (kp.inst == null || kp.inst.PlayerLandmassOwner == null) continue;

                    // Never freeze ourselves. A local player is present by definition, and a stale
                    // ghost flag on our own record would otherwise stop our own kingdom dead.
                    int team = kp.inst.PlayerLandmassOwner.teamId;
                    if (Player.inst != null && Player.inst.PlayerLandmassOwner != null
                        && Player.inst.PlayerLandmassOwner.teamId == team) continue;

                    frozen.Add(team);
                }
            }
            catch (Exception ex)
            {
                // On any doubt, freeze nothing. A departed kingdom that keeps ticking is the bug we
                // already had; a live kingdom frozen by mistake is a player unable to play.
                NetLog.Error("refreshing frozen kingdoms", ex);
                frozen.Clear();
            }
        }
    }
}
