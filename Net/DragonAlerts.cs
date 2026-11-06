// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using UnityEngine;

using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// The dragon panel by the minimap, "N years..." until the next attack, and the
    /// "DRAGON SIGHTED!" banner when one comes, the same on every screen.
    ///
    /// The dragons themselves are the host's (spawn, flight and fire are all host-driven), but the
    /// panel was each machine's own:
    ///
    ///   The countdown is DragonSpawn.yearsUntilNextAttack, which each machine re-rolls from its
    ///   own random numbers whenever its own countdown runs out, and a guest's runs out on a
    ///   spawn that its own copy is not allowed to make. So "8 years" on one screen and something
    ///   else on the other.
    ///
    ///   Whether the panel shows at all is DragonSpawn.AllowSpawning, which reads the population of
    ///   THIS machine's own Player; the host's counts the AI kingdoms' villagers too.
    ///
    ///   The banner goes up when DragonNotification's own per-frame check first sees a hostile
    ///   dragon on the map, on each machine separately.
    ///
    /// Now the host is the only one that decides: its countdown and "allowed" ride along with the
    /// calendar every few seconds (ClockSyncMessage), and when its banner goes up it says so
    /// (DragonSightedMessage). A guest shows its banner only when told, never on its own check.
    /// </summary>
    public static class DragonAlerts
    {
        /// <summary>True while a guest is putting up the banner because the host did.</summary>
        public static bool ApplyingHostBanner;

        private static bool haveHost;
        private static bool hostAllowsDragons;
        private static DragonNotification panel;
        private static bool pendingBanner;

        /// <summary>Guest: what AllowSpawning should answer, or null to let the game decide.</summary>
        public static bool? HostAllowsDragons
        {
            get { return (haveHost && !NetRouter.IsServer && NetRouter.IsConnected) ? hostAllowsDragons : (bool?)null; }
        }

        /// <summary>Host: fills in its countdown on the calendar message.</summary>
        public static void Pack(ClockSyncMessage m)
        {
            DragonSpawn d = DragonSpawn.inst;
            if (d == null) return;

            m.DragonYears = d.yearsUntilNextAttack;
            m.DragonTotalYears = d.totalYearsUntilNextAttack;
            m.DragonAttacks = d.totalDragonAttacks;
            m.DragonsAllowed = d.AllowSpawning();

            RaiderSystem r = RaiderSystem.inst;
            if (r != null)
            {
                m.VikingYears = r.yearsUntilNextAttack;
                m.VikingTotalYears = r.totalYearsUntilNextAttack;
                m.VikingSkirmishYears = r.yearsUntilNextSkirmish;
            }
        }

        /// <summary>Guest: takes the host's countdown.</summary>
        public static void Apply(ClockSyncMessage m)
        {
            DragonSpawn d = DragonSpawn.inst;
            if (d == null) return;

            d.yearsUntilNextAttack = m.DragonYears;
            d.totalYearsUntilNextAttack = m.DragonTotalYears;
            d.totalDragonAttacks = m.DragonAttacks;
            hostAllowsDragons = m.DragonsAllowed;
            haveHost = true;

            RaiderSystem r = RaiderSystem.inst;
            if (r != null)
            {
                r.yearsUntilNextAttack = m.VikingYears;
                r.totalYearsUntilNextAttack = m.VikingTotalYears;
                r.yearsUntilNextSkirmish = m.VikingSkirmishYears;
            }
        }

        /// <summary>Host: its banner just went up.</summary>
        public static void HostShowedBanner()
        {
            if (!NetRouter.IsServer || NetApply.InProgress) return;
            NetRouter.Broadcast(new DragonSightedMessage());
            NetLog.Info("dragon sighted: telling everyone");
        }

        /// <summary>Guest: the host's banner went up, so ours does.</summary>
        public static void Apply(DragonSightedMessage m)
        {
            if (NetRouter.IsServer) return;   // the host's own broadcast

            pendingBanner = true;
            TryShowPendingBanner();
        }

        /// <summary>Retries a host banner that arrived before this screen had a dragon panel to show it in.</summary>
        public static void Tick()
        {
            if (pendingBanner) TryShowPendingBanner();
        }

        /// <summary>Shows the dragon banner the host asked for, if the dragon panel exists yet.</summary>
        private static void TryShowPendingBanner()
        {

            try
            {
                if (panel == null) panel = UnityEngine.Object.FindObjectOfType<DragonNotification>();
                if (panel == null)
                {
                    NetLog.Info("dragon sighted: no dragon panel on this screen to show it in");
                    return;
                }

                ApplyingHostBanner = true;
                try { panel.ShowBanner(); }
                finally { ApplyingHostBanner = false; }
                pendingBanner = false;
            }
            catch (Exception e) { NetLog.Error("showing the dragon banner", e); }
        }

        /// <summary>The session ended.</summary>
        public static void Reset()
        {
            haveHost = false;
            panel = null;
            pendingBanner = false;
            ApplyingHostBanner = false;
        }
    }
}
