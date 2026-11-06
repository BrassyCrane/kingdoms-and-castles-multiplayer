// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using UnityEngine;

using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Keeps every guest's calendar on the host's.
    ///
    /// Each machine runs its own calendar (Weather.Update counts seasonTime down and turns the
    /// season over, and a new summer is a new year), and nothing ever compared them. Speed is
    /// shared, so two machines that never stop stay together, but any stall on one of them is
    /// never made up: an Escape pause on a build that still paused, a machine that stopped while
    /// alt-tabbed, a long hitch, a load. Stalls only ever add up. In a real session a guest
    /// ended up two whole years behind the host, which put the host's dragon attack in a
    /// different year on each screen.
    ///
    /// The host says where its calendar is every few seconds (ClockSyncMessage). A guest
    /// measures the gap in game-seconds and closes it:
    ///
    ///   BEHIND:  it runs forward. Within a season that is just less time left in it. Across a
    ///              season, the season is ended and the game's own Weather.Update turns it over
    ///              (harvest, snow, a new year and all), one season a frame, until caught up. The
    ///              work that would have happened in the skipped time does not, but every season
    ///              event does, in order.
    ///   AHEAD:   its current season runs longer until the host catches up. Nothing that has
    ///              already happened is undone.
    ///   WILDLY OFF (more than MaxYearsBehind years behind, or a year ahead: a load gone wrong
    ///              rather than drift): the calendar is set straight outright.
    ///
    /// Also keeps the game running while its window is not in front
    /// (Application.runInBackground), for the length of a session. A game that stops when
    /// alt-tabbed stops its calendar, its simulation and its networking with it.
    /// </summary>
    public static class ClockSync
    {
        private static ClockSyncMessage latestHost;
        private const float SendEverySeconds = 5f;

        /// <summary>Gaps smaller than this, in game-seconds, are message transit and frame timing.</summary>
        private const float Tolerance = 2f;

        private const int MaxYearsBehind = 5;

        private static float sendTimer;
        private static bool inGame;

        /// <summary>
        /// True from the moment this machine's session is running until its connection goes,
        /// through menus, which Main.IsSessionRunning is not, and never while a save is being
        /// unpacked here. What "the game is actually on" means for anything that must not touch a
        /// world that is about to be replaced (see AiMirror). A guest joining a save has already
        /// unpacked it in the lobby by the time the session runs.
        /// </summary>
        public static bool InGame
        {
            get { return inGame && NetRouter.IsConnected && !LoadSaveOverrides.SessionSave.Unpacking; }
        }

        /// <summary>Game-seconds this guest still has to run forward to reach the host.</summary>
        private static float pendingAdvance;

        private static float nextLogAt;
        private static bool? backgroundBefore;

        /// <summary>Every frame, from Main.Update.</summary>
        public static void Tick()
        {
            if (!NetRouter.IsConnected)
            {
                if (inGame || backgroundBefore != null) Reset();
                return;
            }

            // In a game from the moment the session runs until the connection goes. Not
            // Main.IsSessionRunning itself: that is false while any menu is open, and the
            // calendar does not stop for a menu.
            if (Main.IsSessionRunning) inGame = true;
            if (!inGame) return;

            KeepRunningInBackground();

            Weather w = Weather.inst;
            Player p = Player.inst;
            if (w == null || p == null || LoadSaveOverrides.SessionSave.Unpacking) return;

            if (NetRouter.IsServer)
            {
                sendTimer += Time.unscaledDeltaTime;
                if (sendTimer < SendEverySeconds) return;
                sendTimer = 0f;

                var sync = new ClockSyncMessage
                {
                    Year = p.CurrYear,
                    Season = (int)w.season,
                    SeasonTime = w.seasonTime
                };
                DragonAlerts.Pack(sync);   // the dragon countdown rides along
                NetRouter.Broadcast(sync);
                return;
            }

            RunForward(w);

            // Catching up can cross a local year boundary, whose vanilla callbacks decrement the
            // threat counters again. Reassert the host's counters after every catch-up step.
            if (latestHost != null)
            {
                try { DragonAlerts.Apply(latestHost); }
                catch (Exception e) { NetLog.Error("threat countdown convergence", e); }
            }
        }

        /// <summary>Guest: the host's calendar arrived.</summary>
        public static void Apply(ClockSyncMessage m)
        {
            if (NetRouter.IsServer || m == null || !inGame) return;   // the host's own broadcast

            Weather w = Weather.inst;
            Player p = Player.inst;
            if (w == null || p == null || LoadSaveOverrides.SessionSave.Unpacking) return;

            latestHost = m;
            try { DragonAlerts.Apply(m); }
            catch (Exception e) { NetLog.Error("dragon countdown sync", e); }

            try
            {
                float summer = w.SummerTime, winter = w.GetWinterTime();
                float yearLength = summer + winter;
                if (yearLength <= 0f) return;

                double host = Elapsed(m.Year, m.Season, m.SeasonTime, summer, winter);
                double here = Elapsed(p.CurrYear, (int)w.season, w.seasonTime, summer, winter);
                double gap = host - here;   // positive: this machine is behind

                if (Math.Abs(gap) < Tolerance)
                {
                    pendingAdvance = 0f;
                    return;
                }

                if (gap > MaxYearsBehind * yearLength || -gap > yearLength)
                {
                    SetStraight(m, w, p, gap);
                    return;
                }

                if (gap > 0)
                {
                    // The latest measurement replaces the last: anything still queued from the
                    // previous one is part of this gap.
                    pendingAdvance = (float)gap;
                    Report("behind", gap, m, p, w);
                }
                else
                {
                    pendingAdvance = 0f;
                    w.seasonTime += (float)-gap;   // this season runs on until the host catches up
                    Report("ahead of", -gap, m, p, w);
                }
            }
            catch (Exception e) { NetLog.Error("clock sync", e); }
        }

        /// <summary>
        /// Guest, every frame: spends pendingAdvance. Within the season it is taken off the time
        /// left; past the end of it, the season is ended and Weather.Update (this frame or the
        /// next) turns it over the game's own way and starts the next one full, and the rest waits
        /// for the next frame.
        /// </summary>
        private static void RunForward(Weather w)
        {
            if (pendingAdvance <= 0f) return;

            if (w.seasonTime > pendingAdvance)
            {
                w.seasonTime -= pendingAdvance;
                pendingAdvance = 0f;
                return;
            }

            // Already ended and waiting for Weather.Update to turn it over: wait for that.
            if (w.seasonTime < 0f) return;

            pendingAdvance -= w.seasonTime;
            w.seasonTime = -0.001f;   // negative even while paused, when Weather.Update subtracts nothing
        }

        /// <summary>Calendar position in game-seconds since year 0 (a new year starts with summer).</summary>
        private static double Elapsed(int year, int season, float seasonTime, float summer, float winter)
        {
            double yearStart = (double)year * (summer + winter);
            return season == 0
                ? yearStart + (summer - seasonTime)
                : yearStart + summer + (winter - seasonTime);
        }

        /// <summary>Sets this guest's calendar to the host's outright, for a gap too big to be drift.</summary>
        private static void SetStraight(ClockSyncMessage m, Weather w, Player p, double gap)
        {
            NetLog.Warn("clock: " + (gap > 0 ? "behind" : "ahead of") + " the host by "
                        + (int)Math.Abs(gap) + " game-seconds (year " + p.CurrYear + " " + (Weather.Season)(int)w.season
                        + " here, year " + m.Year + " " + (Weather.Season)m.Season + " there); setting the calendar to the host's");

            w.SetSeason((Weather.Season)m.Season);
            w.seasonTime = m.SeasonTime;
            p.CurrYear = m.Year;
            pendingAdvance = 0f;
        }

        /// <summary>Logs how far behind or ahead this guest is, at most every 30 seconds.</summary>
        private static void Report(string how, double gameSeconds, ClockSyncMessage m, Player p, Weather w)
        {
            if (gameSeconds < 10.0 || Time.unscaledTime < nextLogAt) return;
            nextLogAt = Time.unscaledTime + 30f;

            NetLog.Info("clock: " + how + " the host by " + (int)gameSeconds + " game-seconds (year "
                        + p.CurrYear + " " + w.season + " here, year " + m.Year + " " + (Weather.Season)m.Season
                        + " there); " + (how == "behind" ? "running forward" : "holding this season longer"));
        }

        /// <summary>
        /// Keeps the game running while its window is not in front, for the length of a session, so
        /// alt-tabbing does not stop this machine's calendar and networking. Put back at Reset.
        /// </summary>
        private static void KeepRunningInBackground()
        {
            if (backgroundBefore != null) return;
            try
            {
                backgroundBefore = Application.runInBackground;
                Application.runInBackground = true;
            }
            catch (Exception e) { NetLog.Error("keeping the game running in the background", e); }
        }

        /// <summary>The session ended.</summary>
        public static void Reset()
        {
            inGame = false;
            pendingAdvance = 0f;
            sendTimer = 0f;
            DragonAlerts.Reset();
            latestHost = null;

            if (backgroundBefore != null)
            {
                try { Application.runInBackground = backgroundBefore.Value; }
                catch { }
                backgroundBefore = null;
            }
        }
    }
}
