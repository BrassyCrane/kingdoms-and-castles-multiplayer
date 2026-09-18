using KaCMultiplayer.Lobby;
using KaCMultiplayer.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Riptide;
using Harmony;
using System.Reflection;

namespace KaCMultiplayer
{
    public class NetHost : MonoBehaviour
    {
        public static Server server = new Server(Main.steamServer);
        public static bool started = false;

        static NetHost()
        {
            server.MessageReceived += KaCMultiplayer.Net.NetReceive.OnServer;
        }

        public static void StartServer()
        {
            // Stop any previous server before creating a new one. The Steam transport
            // (Main.steamServer) is shared/static, so leaving an old server running on it
            // makes the next host attempt fail until the game is restarted.
            try
            {
                if (server != null && server.IsRunning)
                    server.Stop();
            }
            catch (Exception e)
            {
                Main.helper.Log("StartServer: error stopping previous server: " + e.Message);
            }

            server = new Server(Main.steamServer);
            server.MessageReceived += KaCMultiplayer.Net.NetReceive.OnServer;

            server.Start(0, 25, useMessageHandlers: false);

            // Password gate. Setting HandleConnection makes Riptide hold every incoming connection as
            // "pending" until WE explicitly Accept or Reject it (so we MUST do one or the other, or the
            // client hangs until timeout). We use it to enforce the lobby password: unlocked servers
            // accept everyone (unchanged behaviour); locked servers accept only if the client's connect
            // message carries the matching password, else reject with a clear reason. The host's own
            // local client connects before the host configures a lock, so it lands on the accept path.
            server.HandleConnection = HandleConnectionApproval;

            server.ClientConnected += (obj, ev) =>
            {
                Main.helper.Log($"client {ev.Client.Id} connected ({server.ClientCount}/{LobbySettings.Current.MaxPlayers})");

                if (server.ClientCount > LobbySettings.Current.MaxPlayers)
                {
                    KaCMultiplayer.Net.NetRouter.SendTo(
                        new KaCMultiplayer.Net.Messages.NoticeMessage
                        {
                            Title = "Failed to connect",
                            Body = "Server is full."
                        }, ev.Client.Id);

                    server.DisconnectClient(ev.Client.Id);
                    return;
                }

                ev.Client.CanQualityDisconnect = false;

                KaCMultiplayer.Net.NetRouter.SendTo(new KaCMultiplayer.Net.Messages.HandshakeMessage
                {
                    AssignedClientId = ev.Client.Id,
                    LoadingSave = SteamLobby.loadingSave
                }, ev.Client.Id);
            };

            server.ClientDisconnected += (obj, ev) =>
            {
                // Guard every step: if a player record is already missing (e.g. a messy
                // disconnect) this handler must not throw, or the server is left in a bad state.
                try
                {
                    // NetPlayers.ById returns null for an unknown client instead of throwing, so
                    // a messy disconnect no longer needs a swallowing try/catch here.
                    SessionPlayer leaving = KaCMultiplayer.Net.NetPlayers.ById(ev.Client.Id);

                    string leavingName = leaving != null ? leaving.name : "A player";

                    KaCMultiplayer.Net.NetRouter.Broadcast(
                        new KaCMultiplayer.Net.Messages.ChatNoticeMessage
                        {
                            Text = $"{leavingName} has left the server."
                        });

                    // A player who has already started playing owns a kingdom (keep + buildings) that
                    // still exists on EVERY machine. Removing their SessionPlayer here orphans that kingdom:
                    // GetPlayerByTeamID can no longer find the owner and falls back to Player.inst
                    // (the LOCAL player), so the leaver's buildings get attributed to us and pollute
                    // our economy/jobs; and NetPlayers.ById starts returning null while clientSteamIds
                    // still maps the id. So mid-game we KEEP the record and let the kingdom sit as a frozen "ghost"
                    // (it already stops ticking once its owner is gone), this stays consistent with the
                    // other clients (which are never told to remove it either) and lets the player resume
                    // their kingdom if they reconnect (ClientConnected reconciles by steamId). Only in the
                    // lobby (no kingdom yet) do we fully remove them, cleaning BOTH maps so no lookup throws.
                    bool hasKingdom = leaving != null && leaving.inst != null
                        && (leaving.inst.keep != null
                            || (leaving.inst.Buildings != null && leaving.inst.Buildings.Count > 0));

                    if (leaving != null && leaving.steamId != null && !hasKingdom)
                    {
                        Main.kCPlayers.Remove(leaving.steamId);
                        Main.clientSteamIds.Remove(ev.Client.Id);
                    }
                    else if (hasKingdom)
                    {
                        // Actually mark it. The flag was only ever set by SessionSave, for saved
                        // players who had not joined yet, so a mid-game leaver was described as a
                        // ghost in the log and in comments while `isGhost` stayed false, and the
                        // lobby's ready-gate (which skips ghosts) went on waiting for them.
                        leaving.isGhost = true;

                        // Their team stays reserved for the rest of the session, so a later joiner
                        // handed the same Riptide client id cannot be given their team and land on
                        // top of the kingdom we are preserving.
                        KaCMultiplayer.LoadSaveOverrides.LoadIdentity.ReserveTeamFor(leaving);

                        Main.helper.Log($"[DISCONNECT] {leavingName} left mid-game, keeping their kingdom as a ghost (record preserved for consistency/reconnect).");
                    }

                    LobbyView.RemovePlayer(ev.Client.Id);

                    // Losing a player mid-game pauses everyone. Their kingdom stops ticking the moment
                    // they go (it sits as a ghost, see above), so an unpaused world just keeps running
                    // while one player's farms, mines and trade quietly do nothing, by the time anyone
                    // notices, the remaining players have banked years of advantage over a kingdom that
                    // may yet reconnect. Pausing makes it everyone's decision instead.
                    //
                    // This runs in a Riptide event handler rather than a message handler, so it is
                    // outside any NetApply scope and the SpeedControlUI hook broadcasts it, which is
                    // what carries the pause to the remaining clients. In the lobby (no game yet)
                    // PauseGame is a no-op.
                    if (hasKingdom)
                    {
                        Main.PauseGame($"{leavingName} left the game");
                        KaCMultiplayer.Net.NetRouter.Broadcast(
                            new KaCMultiplayer.Net.Messages.ChatNoticeMessage
                            {
                                Text = $"Game paused, {leavingName} left."
                            });
                    }

                    Main.helper.Log($"Client disconnected. {ev.Reason}");
                }
                catch (Exception e)
                {
                    Main.LogEx("NetHost ClientDisconnected handler", e);
                }
            };

            Main.helper.Log($"Listening on port 7777. Max {LobbySettings.Current.MaxPlayers} clients.");
        }

        // Decides whether to accept an incoming connection based on the lobby password. MUST call
        // Accept or Reject for every pending connection (or it hangs). Unlocked server → accept all.
        // Locked server → accept only if the client supplied the matching password in its connect
        // message, else reject with a readable reason the client shows. Any unexpected error rejects
        // (rather than leaving the connection hanging) so the joiner gets clear feedback.
        private static void HandleConnectionApproval(Connection pending, Message connectMessage)
        {
            try
            {
                var settings = LobbySettings.Current;
                if (settings == null || !settings.Locked)
                {
                    server.Accept(pending); // not password-protected, behave exactly as before
                    return;
                }

                string provided = "";
                try { provided = connectMessage.GetString(); } catch { provided = ""; }

                string expected = settings.Password ?? "";
                if ((provided ?? "").Trim() == expected.Trim())
                    server.Accept(pending);
                else
                {
                    Main.helper.Log("Rejected a connection: incorrect password.");
                    server.Reject(pending, Message.Create().AddString("Incorrect password."));
                }
            }
            catch (Exception e)
            {
                Main.helper.Log("Connection approval error: " + e.Message);
                try { server.Reject(pending, Message.Create().AddString("Could not verify password. Try again.")); } catch { }
            }
        }

        /// <summary>True while this machine is hosting. Read all over to pick host-only paths.</summary>
        public static bool IsRunning
        {
            get { return server.IsRunning; }
        }

        // Riptide is polled, not evented: nothing arrives until Update pumps it.
        private void Update()
        {
            server.Update();
        }

        private void OnApplicationQuit()
        {
            server.Stop();
        }

        // DO NOT DELETE THESE, empty as they are. The KCModHelper loader attaches this class to
        // a GameObject *because* it declares the loader's hooks, and that attachment is the only
        // reason Update() above, and therefore server.Update(), ever runs. Without them the
        // transport connects but the handshake never completes, no client finishes joining, and
        // nothing is logged. The same applies to NetClient.
        private void Preload(KCModHelper helper) { }

        private void SceneLoaded(KCModHelper helper) { }
    }
}
