using KaCMultiplayer.Lobby;
using System;
using System.Collections.Generic;
using KaCMultiplayer.Net.Messages;

using KaCMultiplayer;
using KaCMultiplayer.LoadSaveOverrides;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Moves a saved game from host to joining client, in chunks.
    ///
    /// Two halves: the host's outgoing queue and the client's reassembly buffer. Neither is
    /// per-message state, there is exactly one transfer in flight at a time, so they live here
    /// rather than as statics on <see cref="SaveTransferMessage"/>, which stays just the thing
    /// that travels.
    ///
    /// Sending is throttled deliberately. Queueing every chunk and letting FixedUpdate drain
    /// a few per tick avoids blasting well over a thousand reliable messages in one frame,
    /// which overruns Riptide's reliable window and drops the joiner with "PoorConnection"
    /// partway through.
    /// </summary>
    public static class SaveTransfer
    {
        /// <summary>Chunks pushed per fixed tick. Tuned low enough not to flood the channel.</summary>
        public const int ChunksPerTick = 10;

        // ---- receive side -----------------------------------------------------

        /// <summary>
        /// True from the first chunk until the save is unpacked. Other systems check this to
        /// suppress work that the save itself will supply, the lobby player list, for one.
        /// </summary>
        public static bool LoadingSave;

        private static byte[] buffer = new byte[0];
        private static bool[] chunkSeen = new bool[0];
        private static int bytesReceived;

        /// <summary>
        /// True when the transfer in flight is a live snapshot of a game already running, rather
        /// than the lobby's saved game. Decides what happens once the bytes are all here.
        /// </summary>
        private static bool resuming;

        // ---- send side --------------------------------------------------------

        /// <summary>A chunk waiting to go out, and who it is for.</summary>
        public class OutgoingChunk
        {
            public ushort ClientId;
            public SaveTransferMessage Message;
        }

        public static readonly Queue<OutgoingChunk> Outgoing = new Queue<OutgoingChunk>();

        /// <summary>
        /// Clears both halves. Called when networking is torn down, so a later join cannot
        /// inherit a stale partial save or a queue of chunks for a client that has gone.
        /// </summary>
        public static void Reset()
        {
            LoadingSave = false;
            resuming = false;
            buffer = new byte[0];
            chunkSeen = new bool[0];
            bytesReceived = 0;
            Outgoing.Clear();
        }

        /// <summary>
        /// Seconds between pumps. This is the fixed tick this method used to run on, kept as a
        /// literal so the rate on the wire does not change: <see cref="ChunksPerTick"/> every
        /// interval, about 500 chunks a second.
        /// </summary>
        private const float PumpIntervalSeconds = 0.02f;

        /// <summary>Unscaled seconds banked since the last pump.</summary>
        private static float pumpClock;

        /// <summary>
        /// Sends queued chunks at a fixed real-time rate. Call once per RENDERED FRAME on the host;
        /// a no-op everywhere else.
        /// </summary>
        ///
        /// <remarks>
        /// Driven by Update and unscaled time, NOT by FixedUpdate, and that is the whole point.
        ///
        /// The host PAUSES the game before sending a live snapshot (see SessionHandlers, "X is
        /// joining"). Pausing goes through SpeedControlUI.SetSpeed(0), which sets Time.timeScale to
        /// 0, and Unity stops running FixedUpdate altogether when it does. So the one method whose
        /// job is to drain this queue was never called again: the host sat paused with ~1900 chunks
        /// still queued, the joiner waited for a save that was never sent, and its reliable
        /// messages timed out into "Could not guarantee delivery of a Reliable message after 15
        /// attempts" and a PoorConnection drop. Nothing unpaused the host either, because what
        /// unpauses it is the transfer finishing.
        ///
        /// The heartbeat shows this plainly whenever the game is paused: 'frame' keeps climbing
        /// while 'fixedTicks' stands still. A transfer has to outlive the pause that triggers it,
        /// so it is paced off wall-clock time instead of sim time.
        /// </remarks>
        public static void PumpOutgoing()
        {
            if (!NetRouter.IsServer || Outgoing.Count == 0)
            {
                pumpClock = 0f;
                return;
            }

            pumpClock += UnityEngine.Time.unscaledDeltaTime;
            if (pumpClock < PumpIntervalSeconds) return;

            // Catch up at most a few intervals' worth. A frame hitch (or a loading stall) can bank a
            // large gap, and spending it all at once would be the very burst the throttle exists to
            // prevent.
            int intervals = (int)(pumpClock / PumpIntervalSeconds);
            if (intervals > 4) intervals = 4;
            pumpClock = 0f;

            int budget = intervals * ChunksPerTick;
            for (int i = 0; i < budget && Outgoing.Count > 0; i++)
            {
                OutgoingChunk chunk = Outgoing.Dequeue();
                NetRouter.SendTo(chunk.Message, chunk.ClientId);
            }
        }

        /// <summary>
        /// Places a received chunk and, once every chunk has arrived, loads the save.
        ///
        /// Chunks are tracked individually rather than by a running count, so a duplicate
        /// cannot make an incomplete transfer look finished.
        /// </summary>
        public static void Apply(SaveTransferMessage m)
        {
            try
            {
                if (!BeginIfFirst(m)) return;

                if (m.Offset < 0 || m.Data == null || m.Offset + m.Data.Length > buffer.Length)
                {
                    NetLog.Warn("save transfer: chunk " + m.ChunkId + " does not fit the buffer, ignored");
                    return;
                }
                if (m.ChunkId < 0 || m.ChunkId >= chunkSeen.Length)
                {
                    NetLog.Warn("save transfer: chunk id " + m.ChunkId + " out of range, ignored");
                    return;
                }

                if (chunkSeen[m.ChunkId])
                {
                    NetLog.Info("save transfer: duplicate chunk " + m.ChunkId + ", ignored");
                    return;
                }

                Buffer.BlockCopy(m.Data, 0, buffer, m.Offset, m.Data.Length);
                chunkSeen[m.ChunkId] = true;
                bytesReceived += m.Data.Length;

                ShowProgress(m.SaveSize);

                if (IsComplete()) Finish();
            }
            catch (Exception ex) { NetLog.Error("save transfer chunk " + m.ChunkId, ex); }
        }

        /// <summary>
        /// Sets up the buffer on the first chunk. Returns false if the transfer can't start.
        /// </summary>
        private static bool BeginIfFirst(SaveTransferMessage m)
        {
            if (LoadingSave && buffer.Length > 0) return true;

            if (m.SaveSize <= 0 || m.TotalChunks <= 0)
            {
                NetLog.Warn("save transfer: first chunk declares size " + m.SaveSize +
                            " and " + m.TotalChunks + " chunks, ignoring transfer");
                return false;
            }

            NetLog.Info("save transfer started: " + m.SaveSize + " bytes in " + m.TotalChunks +
                        " chunks" + (m.Resume ? " (live snapshot, joining a game in progress)" : ""));

            LoadingSave = true;
            resuming = m.Resume;
            buffer = new byte[m.SaveSize];
            chunkSeen = new bool[m.TotalChunks];
            bytesReceived = 0;

            try { LobbyScreen.LoadingPanel.SetActive(true); }
            catch (Exception ex) { NetLog.Warn("save transfer: could not show the loading panel, " + ex.Message); }

            return true;
        }

        private static bool IsComplete()
        {
            for (int i = 0; i < chunkSeen.Length; i++)
                if (!chunkSeen[i]) return false;
            return true;
        }

        private static void ShowProgress(int saveSize)
        {
            if (saveSize <= 0) return;

            float fraction = (float)bytesReceived / saveSize;

            try
            {
                LobbyScreen.ProgressFill.fillAmount = fraction;
                LobbyScreen.ProgressLabel.text = (fraction * 100f).ToString("0.00") + "%";
                LobbyScreen.StatusLabel.text =
                    (bytesReceived / 1000f).ToString("0.00") + " KB / " +
                    (saveSize / 1000f).ToString("0.00") + " KB";
            }
            catch
            {
                // The lobby UI may already be gone. Progress display is not worth failing over.
            }
        }

        /// <summary>
        /// Gives the local kingdom a livery BEFORE the received world is loaded.
        ///
        /// LoadSave.Load brings the game UI up, and the build menu is built exactly once, by
        /// BuildUI.Start, which Unity never runs again. Its buttons are not sprites:
        /// BuildTab.AddButton instantiates each building's own DisplayModel, scales it and moves it
        /// onto the UI layer, so every "image" in that menu is a live object that takes its
        /// materials at the moment it is created.
        ///
        /// On this path that moment lands in a gap. The kingdom's banner is not restored until
        /// Unpack, which runs AFTER Load, so the menu was being built while bannerIdx was still -1
        /// and the livery materials did not exist yet. What comes out is a menu of buttons that
        /// keep their background and their label and show no building at all. A guest joining a
        /// FRESH world never sees it, because the banner is chosen on the name-and-banner screen
        /// before play mode begins; nor does a host, whose load runs in vanilla's own order.
        ///
        /// Seeding closes the gap at its source instead of rebuilding the menu afterwards.
        /// Rebuilding was tried first and is NOT safe: BuildUI.Start attaches its tab handlers to
        /// serialised scene objects (BuildUI.CemeteryTab and the rest), so clearing the containers
        /// to stop a second set of tabs appearing would destroy the very objects it then
        /// dereferences.
        ///
        /// Any valid index will do, because Unpack sets the real one moments later. What matters is
        /// only that a livery EXISTS before the UI reads it. A kingdom that already has a banner is
        /// left alone.
        /// </summary>
        private static void SeedLiveryBeforeLoad()
        {
            try
            {
                if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return;

                LandmassOwner owner = Player.inst.PlayerLandmassOwner;

                // Logged either way. If the banner is already set by this point, the reasoning above
                // does not hold for this run, and the log should say so plainly rather than leaving
                // it to be worked out again from the symptom.
                NetLog.Info("pre-load livery check: bannerIdx=" + owner.bannerIdx
                            + " buildMenuAlreadyBuilt=" + (BuildUI.inst != null));

                if (owner.bannerIdx >= 0) return;

                int idx = Main.localChosenBanner;
                if (idx < 0) idx = 0;
                if (World.inst != null && World.inst.liverySets != null
                    && idx >= World.inst.liverySets.Count) idx = 0;

                Main.SetKingdomBanner(owner, idx, "seeding a livery before a received save loads");
            }
            catch (Exception ex) { NetLog.Error("seeding a livery before the load", ex); }
        }

        /// <summary>
        /// Loads the assembled save. Wrapped so a failure inside Unpack cannot leave the
        /// loading panel on screen forever, all the bytes arrived, and the panel staying up
        /// looks identical to a hung transfer.
        /// </summary>
        private static void Finish()
        {
            NetLog.Info("save transfer complete, unpacking");

            try
            {
                Main.LoadSaveLoadHook.saveBytes = buffer;
                Main.LoadSaveLoadHook.fromNetwork = true;

                SeedLiveryBeforeLoad();
                LoadSave.Load();
                Main.LoadSaveLoadHook.saveContainer.Unpack(null);
                Broadcast.OnLoadedEvent.Broadcast(new OnLoadedEvent());

                NetLog.Info("shared save unpacked");

                // A lobby transfer stops here and waits for the host to press Start, which arrives
                // as SessionStart. A resume has no Start coming, the game is already running, so
                // it walks into the world itself. Unpack has already restored this player's own
                // kingdom under the teamId the snapshot carried, which is what makes reconnecting
                // resume rather than restart.
                if (resuming) EnterWorldAfterResume();
            }
            catch (Exception ex)
            {
                NetLog.Error("unpacking the shared save", ex);
                try
                {
                    ModalDialog.Show("Load failed",
                        "Could not load the shared save (see log).", "Okay", true, () => { });
                }
                catch { }
            }
            finally
            {
                resuming = false;
                try { LobbyScreen.LoadingPanel.SetActive(false); } catch { }
            }
        }

        /// <summary>
        /// Takes a resuming player from the lobby into the running world.
        ///
        /// Mirrors the save-load branch of <c>ApplySessionStart</c> rather than reusing it: that
        /// method also decides routing and would try the game's own StartGame, which is meaningless
        /// for someone arriving at a world that already exists. Paused on arrival for the same
        /// reason a loaded save is, nobody should be dropped into a moving world before they have
        /// found their kingdom.
        /// </summary>
        private static void EnterWorldAfterResume()
        {
            try
            {
                SteamLobby.loadingSave = false;
                Main.TransitionTo(MenuState.LeaveMenus);
                GameState.inst.SetNewMode(GameState.inst.playingMode);
                Main.PauseGame("joined a game in progress");
                NetLog.Info("resumed into the running world");
            }
            catch (Exception ex) { NetLog.Error("entering the world after a resume", ex); }
        }
    }
}
