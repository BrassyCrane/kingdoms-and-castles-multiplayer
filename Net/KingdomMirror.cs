using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Every player's own copy of their own kingdom, sent to the host so the host can SAVE it.
    ///
    /// WHY THIS EXISTS. The host used to save its own copy of everyone's kingdom, and that copy is
    /// a parallel simulation rather than the real one. Three things make it drift, and all three
    /// reached players in one report (Workshop and Bill Kerman's Discord, 2026-09-19):
    ///
    ///   Player.Update is suppressed on a remote kingdom's Player object, and Update is what
    ///   recomputes a kingdom's resource totals from what its buildings actually hold, and what
    ///   takes a villager off the homeless list once they have a house.
    ///
    ///   Nothing syncs the contents of a building. A build message carries where a granary is and
    ///   how healthy it is, never the grain inside it.
    ///
    ///   A home assignment is dropped silently when the house has not arrived yet.
    ///
    /// So a guest who rejoined got their own kingdom back as the host imagined it: resources gone,
    /// half the town homeless (vanilla adds every villager with no house to the homeless list when
    /// it loads), and the empty houses left behind invited a flood of new settlers.
    ///
    /// The fix is to stop guessing. Each player packs their own kingdom with the game's own save
    /// code and sends it to the host; the host writes those bytes into the save instead of its own
    /// copy (see SessionSave.Pack). Whatever we sync or fail to sync, the SAVE is then the owner's
    /// own state.
    ///
    /// ONE TRANSFER PER SAVE, NOT PER TICK. The host asks for a fresh copy right after it saves, so
    /// each kingdom is packed once per autosave (a season) rather than continuously, and the copy
    /// waiting for the next save is at most one season old. Chunks are paced exactly like the save
    /// transfer, for the same reason: a flood of reliable messages turns into a resend storm.
    /// </summary>
    public static class KingdomMirror
    {
        /// <summary>Bytes per chunk. Same as the save transfer: one reliable message, with room
        /// for the header.</summary>
        private const int ChunkBytes = 900;

        private const int ChunksPerPump = 16;
        private const float PumpIntervalSeconds = 0.02f;

        /// <summary>Host side: the latest kingdom each player sent, by Steam id.</summary>
        private static readonly Dictionary<string, Player.PlayerSaveData> mirrors =
            new Dictionary<string, Player.PlayerSaveData>();

        /// <summary>Host side: when each one arrived, so a stale copy can be reported.</summary>
        private static readonly Dictionary<string, float> arrivedAt = new Dictionary<string, float>();

        /// <summary>Host side: partial transfers, by the client sending them.</summary>
        private static readonly Dictionary<ushort, byte[]> incoming = new Dictionary<ushort, byte[]>();
        private static readonly Dictionary<ushort, bool[]> seen = new Dictionary<ushort, bool[]>();

        /// <summary>Sender side: chunks still to go out.</summary>
        private static readonly Queue<KingdomMirrorMessage> outgoing = new Queue<KingdomMirrorMessage>();
        private static float pumpClock;

        /// <summary>Guest side: whether the host has our kingdom at all yet.</summary>
        private static bool sentOnce;

        /// <summary>
        /// Guest side: makes sure the host has a copy without waiting for the first save.
        ///
        /// A player who joins and then leaves before the first autosave would otherwise be saved
        /// from this machine's view of them, which is the very thing this class exists to avoid.
        /// Called on a slow timer; it sends once and then leaves the cadence to the host.
        /// </summary>
        public static void EnsureFirstCopy()
        {
            if (sentOnce || NetRouter.IsServer) return;
            if (!NetClient.client.IsConnected) return;
            if (GameState.inst == null || !GameState.inst.IsPlayMode()) return;

            sentOnce = true;
            SendOurs();
        }

        /// <summary>Forgets everything, for the end of a session.</summary>
        public static void Reset()
        {
            mirrors.Clear();
            arrivedAt.Clear();
            incoming.Clear();
            seen.Clear();
            outgoing.Clear();
            pumpClock = 0f;
            sentOnce = false;
        }

        /// <summary>Host side: the kingdom this player last sent, or null if none has arrived.</summary>
        public static Player.PlayerSaveData For(string steamId, out float ageSeconds)
        {
            ageSeconds = 0f;
            if (steamId == null) return null;

            Player.PlayerSaveData data;
            if (!mirrors.TryGetValue(steamId, out data)) return null;

            float at;
            if (arrivedAt.TryGetValue(steamId, out at)) ageSeconds = UnityEngine.Time.unscaledTime - at;
            return data;
        }

        /// <summary>
        /// Host side: asks every guest for a fresh copy. Called right after a save, so the copy is
        /// ready well before the next one.
        /// </summary>
        public static void RequestFromEveryone()
        {
            try
            {
                if (!NetRouter.IsServer) return;
                NetRouter.Broadcast(new KingdomMirrorRequestMessage(), NetClient.client.Id);
            }
            catch (Exception e) { NetLog.Error("asking for kingdom copies", e); }
        }

        /// <summary>
        /// Guest side: packs our own kingdom with the game's own save code and queues it for the
        /// host. Packing is what an autosave already does every season, so the cost is known.
        /// </summary>
        public static void SendOurs()
        {
            try
            {
                if (NetRouter.IsServer) return;                       // the host has its own
                if (Player.inst == null) return;
                if (GameState.inst == null || !GameState.inst.IsPlayMode()) return;

                byte[] bytes;
                using (MemoryStream ms = new MemoryStream())
                {
                    new BinaryFormatter().Serialize(ms, new Player.PlayerSaveData().Pack(Player.inst));
                    bytes = ms.ToArray();
                }

                int total = (bytes.Length + ChunkBytes - 1) / ChunkBytes;
                outgoing.Clear();   // only the newest copy is worth sending

                for (int i = 0; i < total; i++)
                {
                    int offset = i * ChunkBytes;
                    int size = Math.Min(ChunkBytes, bytes.Length - offset);
                    byte[] slice = new byte[size];
                    Buffer.BlockCopy(bytes, offset, slice, 0, size);

                    outgoing.Enqueue(new KingdomMirrorMessage
                    {
                        TotalBytes = bytes.Length,
                        TotalChunks = total,
                        ChunkId = i,
                        Data = slice
                    });
                }

                NetLog.Info("sending our kingdom to the host: " + (bytes.Length / 1024) + " KB in " + total + " chunk(s)");
            }
            catch (Exception e) { NetLog.Error("packing our kingdom for the host", e); }
        }

        /// <summary>
        /// Sends queued chunks at a fixed real-time rate. Call once per RENDERED frame.
        ///
        /// Unscaled time rather than the fixed tick, the same as SaveTransfer.PumpOutgoing: the
        /// host pauses the world at times a transfer has to survive, and FixedUpdate does not run
        /// while the game is paused.
        /// </summary>
        public static void Pump()
        {
            if (outgoing.Count == 0) { pumpClock = 0f; return; }

            pumpClock += UnityEngine.Time.unscaledDeltaTime;
            if (pumpClock < PumpIntervalSeconds) return;

            int intervals = (int)(pumpClock / PumpIntervalSeconds);
            if (intervals > 4) intervals = 4;
            pumpClock = 0f;

            int budget = ChunksPerPump * intervals;
            while (budget-- > 0 && outgoing.Count > 0)
                NetRouter.Send(outgoing.Dequeue());
        }

        /// <summary>
        /// Host side: takes one chunk, and rebuilds the kingdom once the last one lands.
        ///
        /// A chunk that arrives for a client with no Steam id yet is dropped rather than buffered:
        /// without an id there is nothing to file the finished kingdom under.
        /// </summary>
        public static void Receive(KingdomMirrorMessage m)
        {
            try
            {
                if (!NetRouter.IsServer || m == null || m.Data == null) return;
                if (m.TotalBytes <= 0 || m.TotalChunks <= 0) return;

                string steamId;
                if (!Main.clientSteamIds.TryGetValue(m.Origin, out steamId) || string.IsNullOrEmpty(steamId))
                {
                    NetLog.Warn("kingdom copy: no Steam id for client " + m.Origin + ", dropping it");
                    return;
                }

                byte[] buffer;
                if (!incoming.TryGetValue(m.Origin, out buffer) || buffer.Length != m.TotalBytes)
                {
                    buffer = new byte[m.TotalBytes];
                    incoming[m.Origin] = buffer;
                    seen[m.Origin] = new bool[m.TotalChunks];
                }

                bool[] have = seen[m.Origin];
                if (m.ChunkId < 0 || m.ChunkId >= have.Length) return;

                int offset = m.ChunkId * ChunkBytes;
                if (offset + m.Data.Length > buffer.Length) return;

                Buffer.BlockCopy(m.Data, 0, buffer, offset, m.Data.Length);
                have[m.ChunkId] = true;

                for (int i = 0; i < have.Length; i++)
                    if (!have[i]) return;   // still missing pieces

                using (MemoryStream ms = new MemoryStream(buffer))
                {
                    Player.PlayerSaveData data = new BinaryFormatter().Deserialize(ms) as Player.PlayerSaveData;
                    if (data == null)
                    {
                        NetLog.Warn("kingdom copy from client " + m.Origin + " did not deserialise");
                        return;
                    }

                    mirrors[steamId] = data;
                    arrivedAt[steamId] = UnityEngine.Time.unscaledTime;
                    NetLog.Info("kingdom copy received from client " + m.Origin + " ("
                                + (buffer.Length / 1024) + " KB); saves will use it");
                }

                incoming.Remove(m.Origin);
                seen.Remove(m.Origin);
            }
            catch (Exception e)
            {
                NetLog.Error("receiving a kingdom copy", e);
                incoming.Remove(m.Origin);
                seen.Remove(m.Origin);
            }
        }

        /// <summary>Drops a leaver's partial transfer. Their last complete copy is kept, because it
        /// is still the best record of the kingdom they left behind.</summary>
        public static void Forget(ushort clientId)
        {
            incoming.Remove(clientId);
            seen.Remove(clientId);
        }
    }
}
