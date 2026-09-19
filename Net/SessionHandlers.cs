using KaCMultiplayer.Lobby;
using System;
using System.Collections.Generic;
using System.Linq;
using KaCMultiplayer.Net.Messages;
using Steamworks;
using UnityEngine;

using KaCMultiplayer;
using KaCMultiplayer.LoadSaveOverrides;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Handlers for joining a session: the host's handshake, and a client announcing itself.
    ///
    /// These live here rather than in <see cref="NetRegistrations"/> because they are the
    /// bulkiest handlers in the mod, between them they set up the camera and weather, create
    /// the local player, derive its team id, and drive the host's entire catch-up sequence.
    /// The registration table should stay readable in one screen.
    /// </summary>
    public static class SessionHandlers
    {
        /// <summary>
        /// Bytes per save-transfer chunk. Sized to fit inside one reliable message with room
        /// for the header, larger and Riptide fragments or rejects it.
        /// </summary>
        private const int SaveChunkBytes = 900;

        /// <summary>
        /// The host has accepted us. Sets up the view, creates our player record, works out
        /// our team id, and moves to the right lobby screen.
        /// </summary>
        public static void OnHandshake(HandshakeMessage m)
        {
            NetLog.Info("handshake: assigned client id " + m.AssignedClientId +
                        (m.LoadingSave ? ", loading a save" : ", fresh game"));

            ModalDialog.Hide();
            Main.TransitionTo(MenuState.LobbyScreen);
            SfxSystem.PlayUiSelect();

            // Presentation defaults for the lobby view.
            Cam.inst.desiredDist = 80f;
            Cam.inst.desiredPhi = 45f;
            CloudSystem.inst.threshold1 = 0.6f;
            CloudSystem.inst.threshold2 = 0.8f;
            CloudSystem.inst.BaseFreq = 4.5f;
            Weather.inst.SetSeason(Weather.Season.Summer);

            NetClient.inst = new NetClient(SteamFriends.GetPersonaName());
            Main.kCPlayers.Add(Main.PlayerSteamID,
                new SessionPlayer(NetClient.inst.Name, m.AssignedClientId, Main.PlayerSteamID));

            // Fresh game: teamId is derived from the client id. Loaded game: the saved teamId
            // wins. LoadIdentity resolves both, and has to be the same resolver SessionPlayer uses
            // or the local player and every remote copy of them disagree about who they are.
            //
            // On a client joining a loaded lobby the registry may still be empty here, the
            // save arrives after the handshake, so this can set the derived id;
            // SessionSave.Unpack re-asserts the saved one once the registry
            // exists. Harmless, because nothing team-owned exists before unpack.
            Player.inst.PlayerLandmassOwner.teamId =
                LoadIdentity.TeamIdFor(Main.PlayerSteamID, m.AssignedClientId);

            // Diagnostic: "Load (beta)" reportedly lands in an empty lobby instead of the save
            // picker. Both conditions have to hold to reach the Load screen, and if neither branch
            // is taken the player is simply left on the ServerLobby set above, silently. Log the
            // decision so the next run says which input was wrong rather than leaving us guessing.
            NetLog.Info("handshake routing: LoadingSave=" + m.LoadingSave +
                        " NetHost.IsRunning=" + NetHost.IsRunning +
                        " SteamLobby.loadingSave=" + SteamLobby.loadingSave);

            if (m.LoadingSave && NetHost.IsRunning)
            {
                NetLog.Info("handshake routing -> Load (save picker)");
                Main.TransitionTo(MenuState.Load);
            }
            else if (!m.LoadingSave)
            {
                NetLog.Info("handshake routing -> NameAndBanner");
                Main.TransitionTo(MenuState.NameAndBanner);
            }
            else
            {
                // Joining a host that is resuming a save. Do NOT send them to NameAndBanner: their
                // kingdom already exists in that save and naming a new one is how a returning
                // player ends up placing a second keep beside their own city.
                //
                // Nothing to decide yet either, because the save has not arrived. It is streamed
                // in chunks (see SaveTransfer) and SessionSave.Unpack then matches this player by
                // Steam id and restores their kingdom, or sends them to NameAndBanner if the save
                // genuinely has no kingdom for them. The lobby is the right place to wait.
                NetLog.Info("handshake routing -> waiting for the host's save; kingdom comes from it");
            }

            // Announce our kingdom name, then ourselves. Order matters only in that both
            // need to happen after the player record above exists.
            NetRouter.Send(new KingdomLabelMessage { KingdomName = TownNameUI.inst.townName });

            NetRouter.Send(new ClientJoinedMessage
            {
                Name = NetClient.inst.Name,
                SteamId = Main.PlayerSteamID
            });
        }

        /// <summary>
        /// Host side of a client announcing itself. Records them, tells everyone, then brings
        /// the newcomer up to date, either by shipping the save or by syncing the map.
        /// </summary>
        public static void OnClientJoinedServer(ClientJoinedMessage m, NetContext ctx)
        {
            ushort joiner = ctx.SenderId;
            NetLog.Info("client joined: " + m.Name + " id " + joiner + " steam " + m.SteamId);

            // Once play has started, only people this session already knows may come in. A returning
            // player has a kingdom waiting for them, kept frozen since they left, and packed into
            // the snapshot below, so they resume. A stranger has nothing: no kingdom, no keep, no
            // landmass, and they would arrive as a spectator in someone else's world. Turning them
            // away with a reason beats letting them in to discover that.
            //
            // Checked here rather than in the connection-approval callback because that runs before
            // the client has told us who they are; the steamId only arrives with this message.
            if (GameState.inst != null && GameState.inst.IsPlayMode()
                && !Main.kCPlayers.ContainsKey(m.SteamId))
            {
                NetLog.Info("refused " + m.Name + " (" + m.SteamId + "): game in progress and they have no kingdom here");
                RefuseJoin(joiner, "Game in progress",
                    "This game has already started and you have no kingdom in it. " +
                    "Only players who were in the session can rejoin.");
                return;
            }

            // Register the joiner in OUR registry first, synchronously.
            //
            // Synchronously, and before the relay below. RelayIncludingSender only puts bytes
            // on the wire, the host's own local client receives that echo on a later frame, so
            // relying on it means BroadcastRoster() runs while the registry still holds nobody
            // but the host. The joining client then receives a roster of one, and because
            // ApplyRoster clears the client's registry before repopulating it, the joiner's own
            // record is wiped. The client can see the host but never
            // itself.
            //
            // OnClientJoinedClient is idempotent and AddPlayer skips a client that already has a
            // row, so the relayed echo arriving later is harmless.
            m.Origin = joiner;
            OnClientJoinedClient(m);

            NetRouter.RelayIncludingSender(m, ctx);

            BroadcastRoster();

            NetRouter.Broadcast(new ChatNoticeMessage { Text = m.Name + " has joined the server." });
            NetRouter.Broadcast(LobbySettingsMessage.From(LobbySettings.Current), NetClient.client.Id);

            SendRelationsTo(joiner);
            SendStreamerEffectsTo(joiner);

            // Three ways to bring a newcomer up to date, and picking the wrong one is silent.
            //
            // A game already in progress has to be sent a LIVE snapshot: the lobby's save bytes
            // describe the world as it was before anyone played, and the fresh-world path sends
            // only a seed, which regenerates an empty map. That is what used to happen, a player
            // joining or reconnecting mid-game landed in a pristine world with no buildings, no
            // villagers and no kingdoms, with nothing logged to say so.
            if (GameState.inst != null && GameState.inst.IsPlayMode())
                QueueResumeTransfer(joiner, m.Name);
            else if (SteamLobby.loadingSave)
                QueueSaveTransfer(joiner);
            else
                SyncFreshWorld(joiner);
        }

        /// <summary>
        /// Client side: record or update a player. Runs on the joiner too, from its own echo.
        /// </summary>
        public static void OnClientJoinedClient(ClientJoinedMessage m)
        {
            SessionPlayer player;
            if (Main.kCPlayers.TryGetValue(m.SteamId, out player))
            {
                player.id = m.Origin;
                player.name = m.Name;
                player.steamId = m.SteamId;

                // A saved player reconnecting to a loaded game takes over their ghost entry.
                // Its inst already holds their unpacked kingdom under their saved teamId, so
                // there is nothing to rebuild.
                if (player.isGhost)
                {
                    player.isGhost = false;
                    Main.ClearFrozenLog(player.steamId);   // so a second departure reports again
                    NetLog.Info(m.Name + " reconnected and took over their kingdom (ghost -> live, simulation resumes)");
                }
            }
            else
            {
                Main.kCPlayers.Add(m.SteamId, new SessionPlayer(m.Name, m.Origin, m.SteamId));
            }

            Main.clientSteamIds[m.Origin] = m.SteamId;

            // During a save load the lobby list is rebuilt from the save instead.
            if (!SaveTransfer.LoadingSave)
                LobbyView.AddPlayer(m.Origin);
        }

        /// <summary>Sends the host's full view of the roster to every client but itself.</summary>
        internal static void BroadcastRoster()
        {
            List<SessionPlayer> players = Main.kCPlayers.Values.OrderBy(p => p.id).ToList();
            if (players.Count == 0) return;

            NetRouter.Broadcast(new PeerRosterMessage
            {
                Players = players.Select(p => new PeerRosterMessage.Entry
                {
                    ClientId = p.id,
                    SteamId = p.steamId,
                    // A saved kingdom whose player has not joined has no session name yet.
                    Name = string.IsNullOrEmpty(p.name) ? p.SteamPersona() : p.name,
                    KingdomName = p.kingdomName,
                    Banner = p.banner,
                    Ready = p.ready,
                    Ghost = p.isGhost,
                    // The host's team assignment travels with the roster so no client has to
                    // re-derive it from a client id Riptide may have recycled.
                    TeamId = (p.inst != null && p.inst.PlayerLandmassOwner != null)
                        ? p.inst.PlayerLandmassOwner.teamId : 0
                }).ToList()
            }, NetClient.client.Id);
        }

        /// <summary>
        /// Splits the save into chunks and queues them for a joining client.
        ///
        /// Queued rather than sent: Main.FixedUpdate drains a few per tick. Sending them all
        /// at once floods the reliable channel and drops the client around 73% with
        /// "PoorConnection".
        /// </summary>
        /// <summary>
        /// Turns a client away with a reason they will actually see.
        ///
        /// Riptide lets a disconnect carry a payload, and <c>NetClient</c> decodes that as a
        /// <see cref="NoticeMessage"/>, so this reaches the player as a titled dialog rather than
        /// the generic "Disconnected from Server". Sending an ordinary message first would race the
        /// disconnect, which is why the reason travels with it.
        /// </summary>
        private static void RefuseJoin(ushort clientId, string title, string body)
        {
            try
            {
                NoticeMessage notice = new NoticeMessage { Title = title, Body = body };
                Riptide.Message payload = Riptide.Message.Create();
                notice.Serialize(payload);
                NetHost.server.DisconnectClient(clientId, payload);
            }
            catch (Exception ex)
            {
                NetLog.Error("refusing client " + clientId, ex);
                try { NetHost.server.DisconnectClient(clientId); } catch { }
            }
        }

        /// <summary>
        /// Sends somebody joining a game in progress a snapshot of the world as it stands.
        ///
        /// The host pauses for the duration. The transfer is deliberately throttled across many
        /// seconds, and letting the world run through it would mean the joiner arrives at a state
        /// that has already moved on from the one they were sent, a fairness problem and a source
        /// of divergence. It stays paused afterwards rather than auto-resuming, so whoever is at
        /// the keyboard decides when to carry on.
        /// </summary>
        private static void QueueResumeTransfer(ushort clientId, string joinerName)
        {
            if (clientId == NetClient.client.Id) return;   // the host is already in this world

            byte[] snapshot = Main.PackLiveSnapshot();
            if (snapshot == null || snapshot.Length == 0)
            {
                NetLog.Warn("resume: could not pack a snapshot for client " + clientId +
                            ", they would land in an empty world, so sending nothing");
                return;
            }

            Main.PauseGame(joinerName + " is joining");
            NetRouter.Broadcast(new ChatNoticeMessage
            {
                Text = "Game paused, sending the world to " + joinerName + "."
            });

            SendSaveBytes(clientId, snapshot, resume: true);
        }

        /// <summary>
        /// Tells a newcomer who is at war with whom.
        ///
        /// Relations live only in memory on each machine, so without this a joiner arrived believing
        /// everyone was Neutral: they would see no war, their pathing gates and dock policy would be
        /// wrong, and the disagreement would persist for the rest of the session because nothing
        /// re-states a relation once it is set.
        ///
        /// Sent as ordinary PlayerRelationMessages, one per pair, rather than a new bulk message.
        /// The pairs number in the single digits for any real session, and reusing the existing
        /// message means the joiner applies them through exactly the same path that handles a live
        /// declaration, including the gate rebake and dock policy, with no second implementation to
        /// keep in step.
        /// </summary>
        private static void SendRelationsTo(ushort clientId)
        {
            try
            {
                var pairs = PlayerRelations.Snapshot();
                if (pairs.Count == 0) return;

                foreach (System.Collections.Generic.KeyValuePair<long, World.Relations> entry in pairs)
                {
                    NetRouter.SendTo(new PlayerRelationMessage
                    {
                        TeamA = TeamPair.Low(entry.Key),
                        TeamB = TeamPair.High(entry.Key),
                        Relation = (int)entry.Value,

                        // These already happened. Consent was given when they were agreed, and
                        // asking for it again on arrival is what turned a joiner's existing
                        // alliance into an unanswered proposal.
                        Sync = true
                    }, clientId);
                }

                NetLog.Info("sent " + pairs.Count + " relation pair(s) to client " + clientId);
            }
            catch (Exception ex) { NetLog.Error("sending relations to a joiner", ex); }
        }

        /// <summary>
        /// Tells a newcomer which streamer effects are running.
        ///
        /// The effect flags are not in the save and are not re-stated on a timer; the poll in
        /// StreamerEffectSync speaks only when the set CHANGES. So without this, somebody joining
        /// after the vote that turned an effect on would simulate with it off, and stay wrong until
        /// the next vote. That is the same shape as the relations gap above, and it gets the same
        /// answer: state it once, on arrival, through the ordinary message.
        ///
        /// Nothing is sent when nothing is running, which is every session where nobody is in
        /// streamer mode.
        /// </summary>
        private static void SendStreamerEffectsTo(ushort clientId)
        {
            try
            {
                int effects = StreamerEffectSync.CurrentEffects();
                if (effects == 0) return;   // nothing running, nothing to say

                NetRouter.SendTo(new StreamerEffectsMessage { Effects = effects }, clientId);
                NetLog.Info("sent the streamer effect set (mask " + effects + ") to client " + clientId);
            }
            catch (Exception ex) { NetLog.Error("sending streamer effects to a joiner", ex); }
        }

        private static void QueueSaveTransfer(ushort clientId)
        {
            if (clientId == NetClient.client.Id) return;   // the host already has the save

            byte[] save = Main.LoadSaveLoadAtPathHook.saveData;
            if (save == null || save.Length == 0)
            {
                NetLog.Warn("save transfer: no save data to send to client " + clientId);
                return;
            }

            SendSaveBytes(clientId, save, resume: false);
        }

        /// <summary>
        /// Sends one client the chunks it says it never got.
        ///
        /// Cut fresh from the same bytes with the same arithmetic as the first pass, so a repaired
        /// chunk is byte-for-byte the one that went missing. Nothing is remembered between passes.
        /// </summary>
        public static void ResendSaveChunks(ushort clientId, List<int> chunkIds)
        {
            try
            {
                if (chunkIds == null || chunkIds.Count == 0) return;

                // The bytes THIS client is being sent. A mid-game joiner is receiving a packed
                // snapshot rather than the file on disk, so reading the disk copy here would answer
                // a request for chunk 900 with a different world's chunk 900.
                byte[] save = SaveTransfer.Remembered(clientId);
                if (save == null || save.Length == 0)
                {
                    NetLog.Warn("save resend: client " + clientId + " asked for "
                                + chunkIds.Count + " chunk(s) but there is no save to cut");
                    return;
                }

                int total = (save.Length + SaveChunkBytes - 1) / SaveChunkBytes;
                int sent = 0;

                for (int i = 0; i < chunkIds.Count; i++)
                {
                    int id = chunkIds[i];
                    if (id < 0 || id >= total) continue;   // not a chunk of this save

                    int offset = id * SaveChunkBytes;
                    int size = Math.Min(SaveChunkBytes, save.Length - offset);
                    if (size <= 0) continue;

                    byte[] chunk = new byte[size];
                    Buffer.BlockCopy(save, offset, chunk, 0, size);

                    SaveTransfer.Outgoing.Enqueue(new SaveTransfer.OutgoingChunk
                    {
                        ClientId = clientId,
                        Message = new SaveTransferMessage
                        {
                            ChunkId = id,
                            TotalChunks = total,
                            SaveSize = save.Length,
                            Offset = offset,
                            Resume = false,
                            Data = chunk
                        }
                    });
                    sent++;
                }

                // Worth a line every time. If these rounds keep coming, and keep being large, the
                // send rate is losing more than the repair can recover and the rate is the thing to
                // change. That is a judgement this log makes possible and guesswork otherwise.
                NetLog.Info("save resend: client " + clientId + " asked for " + chunkIds.Count
                            + " chunk(s), re-queued " + sent);
            }
            catch (Exception e) { NetLog.Error("resending save chunks", e); }
        }

        /// <summary>Cuts a save into chunks and queues them for one client.</summary>
        private static void SendSaveBytes(ushort clientId, byte[] save, bool resume)
        {
            try
            {
                // Anything still queued for this client is from an attempt they have abandoned.
                // They are starting again from chunk zero, so the remains only take up room in a
                // queue everybody shares.
                SaveTransfer.BeginSendingTo(clientId);

                int offset = 0;
                int total = (save.Length + SaveChunkBytes - 1) / SaveChunkBytes;

                // Kept so the repair path can re-cut any chunk later. Both send paths use it: a
                // fresh load reads the save from disk, a mid-game join packs a snapshot, and after
                // this point neither is reachable any other way.
                SaveTransfer.Remember(clientId, save);

                // ONLY THE FIRST WINDOW GOES OUT UNPROMPTED. The client asks for the rest as it
                // takes delivery, which is what keeps the number of unacknowledged messages on the
                // wire bounded. See SaveTransfer.WindowChunks for why that bound is the whole
                // point.
                int firstWindow = Math.Min(total, SaveTransfer.WindowChunks);

                for (int i = 0; i < firstWindow; i++)
                {
                    int size = Math.Min(SaveChunkBytes, save.Length - offset);
                    byte[] chunk = new byte[size];
                    Buffer.BlockCopy(save, offset, chunk, 0, size);

                    SaveTransfer.Outgoing.Enqueue(new SaveTransfer.OutgoingChunk
                    {
                        ClientId = clientId,
                        Message = new SaveTransferMessage
                        {
                            ChunkId = i,
                            TotalChunks = total,
                            SaveSize = save.Length,
                            Offset = offset,
                            Resume = resume,
                            Data = chunk
                        }
                    });

                    offset += size;
                }

                NetLog.Info((resume ? "resume" : "save") + " transfer: " + total + " chunks ("
                            + save.Length + " bytes) for client " + clientId + "; sent the first "
                            + firstWindow + ", they will ask for the rest");
            }
            catch (Exception ex) { NetLog.Error("save transfer queue", ex); }
        }

        /// <summary>
        /// Brings a joining client's map in line for a fresh game: seed first so it
        /// regenerates, then the hazards the host has already placed.
        /// </summary>
        private static void SyncFreshWorld(ushort clientId)
        {
            NetRouter.Broadcast(WorldSeedMessage.ForCurrentWorld(), NetClient.client.Id);

            // Only to the joiner: players already in the session have these, and a second
            // copy would duplicate them. The seed above is sent first, so the client
            // regenerates, suppressing its own hazards, before these arrive.
            if (clientId == NetClient.client.Id) return;

            try
            {
                int dens = SendHazards<WolfDen>(clientId, 0);
                int huts = SendHazards<WitchHut>(clientId, 1);
                NetLog.Info("hazard catch-up for client " + clientId + ": " + dens + " dens, " + huts + " huts");

                // The messages above say a den EXISTS. They say nothing about what is living in it,
                // and a joiner starts with every pack empty, because a machine that does not
                // arbitrate a den no longer spawns into it. Left alone, the sweep would stay silent
                // about any den that happened to be quiet, and those packs would still be empty an
                // hour later. Asking it to report everything once puts the newcomer straight.
                KaCMultiplayer.Combat.CombatSync.RepublishWolves();

                // Prices too. A joiner who does not have them would open a visiting hold and be
                // quoted the game's default for goods the seller has priced differently, then be
                // charged the seller's real price when the transaction settled.
                KaCMultiplayer.Trade.ExportPrices.SendAllTo(clientId);
            }
            catch (Exception ex) { NetLog.Error("hazard catch-up", ex); }
        }

        /// <summary>
        /// Sends every hazard of one kind to a client. Returns how many were sent, a cell
        /// that can't be resolved is skipped and logged rather than sent as garbage.
        /// </summary>
        private static int SendHazards<T>(ushort clientId, int hazardType) where T : Component
        {
            int sent = 0;
            T[] found = UnityEngine.Object.FindObjectsOfType<T>();

            for (int i = 0; i < found.Length; i++)
            {
                Cell cell = World.inst.GetCellData(found[i].transform.position);
                if (cell == null)
                {
                    NetLog.Warn("hazard catch-up: " + typeof(T).Name + " at " +
                                found[i].transform.position + " has no cell, skipped");
                    continue;
                }

                NetRouter.SendTo(new HazardSpawnMessage
                {
                    X = cell.x,
                    Z = cell.z,
                    HazardType = hazardType
                }, clientId);

                sent++;
            }
            return sent;
        }
    }
}
