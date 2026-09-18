using System;
using UnityEngine;
using KaCMultiplayer.Net;

namespace KaCMultiplayer.Combat
{
    /// <summary>
    /// Applies <see cref="CombatRule"/> to the live world: looks up who owns the ground under a
    /// position and answers whether this machine is the one that should resolve damage there.
    ///
    /// All the lookups live here and all the logic lives in CombatRule, so the rule stays testable
    /// without a running game. Everything is wrapped: this is called from damage paths, which run
    /// per hit and sometimes per frame, and an exception escaping one of those would stall the
    /// simulation. On any doubt it answers "yes, resolve it here", because a fight that resolves on
    /// two machines diverges, while a fight that resolves on none simply never happens, which is
    /// the more obvious failure and the safer one to leave visible.
    /// </summary>
    public static class CombatAuthority
    {
        /// <summary>The team owning the ground at a position, or CombatRule.NoTeam.</summary>
        public static int LandOwnerTeamAt(Vector3 pos)
        {
            if (World.inst == null) return CombatRule.NoTeam;

            // Clamped so a projectile that strays off the edge of the map still resolves to a cell
            // rather than a null, which on a damage path would be an exception per hit.
            Cell cell = World.inst.GetCellDataClamped(pos);
            if (cell == null) return CombatRule.NoTeam;

            LandmassOwner owner = World.GetLandmassOwner(cell.landMassIdx);
            return owner != null ? owner.teamId : CombatRule.NoTeam;
        }

        /// <summary>This machine's own team, or CombatRule.NoTeam before one is assigned.</summary>
        public static int LocalTeam()
        {
            if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return CombatRule.NoTeam;
            return Player.inst.PlayerLandmassOwner.teamId;
        }

        /// <summary>
        /// Whether the kingdom on this team is actually being simulated by somebody.
        ///
        /// A ghost is a player who left; their kingdom is frozen and their machine is not resolving
        /// anything, so leaving them as the arbiter would mean nobody ever settles a fight on their
        /// island. The local team is always present, by definition.
        /// </summary>
        public static bool TeamPresent(int teamId)
        {
            if (teamId == CombatRule.NoTeam) return false;
            if (teamId == LocalTeam()) return true;

            foreach (SessionPlayer kp in Main.kCPlayers.Values)
            {
                if (kp == null || kp.inst == null || kp.inst.PlayerLandmassOwner == null) continue;
                if (kp.inst.PlayerLandmassOwner.teamId != teamId) continue;
                return !kp.isGhost;
            }

            // Nobody in the roster owns this team. Treat it as absent rather than inventing an
            // arbiter that does not exist.
            return false;
        }

        /// <summary>
        /// True when this machine should resolve damage landing at <paramref name="pos"/>.
        ///
        /// Single-player always resolves locally: there is nobody to arbitrate against, and the
        /// mod must not change how the game plays on its own.
        /// </summary>
        public static bool ResolvesHere(Vector3 pos)
        {
            try
            {
                // Feature dark: every machine resolves its own damage, exactly as it did before
                // the arbiter existed. See Main.CombatAuthorityEnabled for why this ships off.
                if (!Main.CombatAuthorityEnabled) return true;

                if (!NetClient.client.IsConnected) return true;   // single-player, vanilla behaviour

                int landOwner = LandOwnerTeamAt(pos);

                return CombatRule.ResolvedHere(
                    landOwner,
                    TeamPresent(landOwner),
                    HostTeam,
                    LocalTeam(),
                    NetHost.IsRunning);
            }
            catch (Exception ex)
            {
                NetLog.Error("combat authority", ex);
                return true;   // see the class note: resolving twice is worse than not at all
            }
        }

        /// <summary>
        /// Marker for "the host arbitrates", rather than the host's actual team.
        ///
        /// Whose team the host has is beside the point and can even be unset early in a session;
        /// what matters is that every machine agrees the verdict fell to the host, and that only
        /// the host then acts on it. Kept distinct from every real team id so it can never collide
        /// with one.
        /// </summary>
        public const int HostTeam = int.MinValue + 1;
    }
}
