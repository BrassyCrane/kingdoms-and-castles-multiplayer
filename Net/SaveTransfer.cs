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
        /// Sends up to <see cref="ChunksPerTick"/> queued chunks. Call once per fixed tick on
        /// the host; a no-op everywhere else.
        /// </summary>
        public static void PumpOutgoing()
        {
            if (!NetRouter.IsServer || Outgoing.Count == 0) return;

            for (int i = 0; i < ChunksPerTick && Outgoing.Count > 0; i++)
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
