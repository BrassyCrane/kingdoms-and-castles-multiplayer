using KaCMultiplayer.Net;
using Harmony;
using KaCMultiplayer.Lobby;
using Riptide;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static KaCMultiplayer.NetHost;

namespace KaCMultiplayer
{
    public class NetClient : MonoBehaviour
    {
        public static Client client = new Client(Main.steamClient);

        public string Name { get; set; }

        public static NetClient inst { get; set; }


        static NetClient()
        {
            // Deliberately no Connected handler. Everything that has to happen on connect is
            // driven by the handshake exchange in Net/, which carries the identity the
            // transport-level event does not have yet.
            client.ConnectionFailed += Client_ConnectionFailed;
            client.Disconnected += Client_Disconnected;
            client.MessageReceived += KaCMultiplayer.Net.NetReceive.OnClient;

            // WE DO NOT HANG UP ON OURSELVES, which the host already decided for its own side and
            // this side never did.
            //
            // Riptide gives every connection a quality rule: if one reliable message cannot be
            // confirmed within MaxSendAttempts tries, it drops the connection outright
            // (PendingMessage.TrySend -> Disconnect(PoorConnection)). NetHost turns that off for
            // every client it accepts, because receiving a multi-megabyte save is a deliberate
            // flood and a slow ack is not a broken link. The joining player's own connection kept
            // the default and would quality-disconnect ITSELF part way through the transfer, which
            // the host then reported as an ordinary clean disconnect with nothing to explain it.
            //
            // Set on Connected rather than here, because the Connection object does not exist until
            // the handshake completes.
            client.Connected += (sender, args) =>
            {
                try
                {
                    if (client.Connection != null)
                    {
                        client.Connection.CanQualityDisconnect = false;
                        Main.helper.Log("[net] quality-disconnect disabled for our own connection; "
                                        + "a slow save transfer is not a broken link");
                    }
                }
                catch (Exception e) { Main.helper.Log("[net] could not relax the quality rule: " + e.Message); }
            };
        }

        private static void Client_Disconnected(object sender, DisconnectedEventArgs e)
        {
            Main.helper.Log($"client disconnected ({e.Reason}); tearing down session");

            // Any disconnect (drop, kick, host shutdown) must fully reset networking state,
            // otherwise the server/lobby/state stays dirty and the player can't host or
            // join again without restarting the game. ResetNetworkState is idempotent and
            // guarded, so it's safe to call here even if we initiated the disconnect.
            try { SteamLobby.ResetNetworkState(); }
            catch (Exception rex) { Main.helper.Log("Reset on disconnect failed: " + rex.Message); }

            try
            {
                // Work out what to tell the player, then leave the session, then say it. The
                // order matters, see below.
                string heading = "Disconnected from Server";
                string detail = DisconnectMessages.For(e.Reason);

                if (e.Message != null)
                {
                    Main.helper.Log("disconnect carried a payload; decoding it as a NoticeMessage");

                    // A disconnect can carry a reason payload, which the host writes as a
                    // NoticeMessage. It arrives outside the normal receive path, the
                    // connection is already gone by the time we get here, so decode it
                    // directly rather than through NetRouter.
                    try
                    {
                        KaCMultiplayer.Net.Messages.NoticeMessage notice =
                            new KaCMultiplayer.Net.Messages.NoticeMessage();
                        notice.Deserialize(e.Message);
                        heading = notice.Title;
                        detail = notice.Body;
                    }
                    catch (Exception dex)
                    {
                        // Not a notice, or malformed. Keep the generic wording rather than
                        // showing the player a decode failure.
                        Main.helper.Log("Disconnect payload was not a NoticeMessage: " + dex.Message);
                    }
                }

                // Always leave the session, message or no message. A client whose host has gone
                // is otherwise left building in a game that no longer exists and that nobody will
                // ever see. It is also the only way the reason becomes readable: the dialog is
                // parented under the main-menu UI, so it is invisible while play continues.
                //
                // Both steps below are required. SetNewMode swaps the game mode, but nothing
                // draws the menu until the menu state machine is driven to a state, with only
                // the first, play carries on with time still passing and nothing is logged.
                try { GameState.inst.SetNewMode(GameState.inst.mainMenuMode); }
                catch (Exception mex) { Main.helper.Log("Leaving to main menu failed: " + mex.Message); }

                try { Main.TransitionTo(MenuState.Menu); }
                catch (Exception tex) { Main.helper.Log("Menu transition on disconnect failed: " + tex.Message); }

                Main.helper.Log("Disconnect: left the session, queued '" + heading + "'");

                // Queued rather than shown: the menu UI needs a frame or two to come up, and
                // Show() on an inactive parent succeeds silently and displays nothing.
                ModalDialog.ShowWhenVisible(heading, detail,
                    () => Main.TransitionTo(MenuState.BrowserScreen));
            }
            catch (Exception ex)
            {
                Main.LogEx("NetClient disconnect handler", ex);
            }
            Main.helper.Log("client disconnect handling complete");
        }

        private static void Client_ConnectionFailed(object sender, ConnectionFailedEventArgs e)
        {
            Main.helper.Log($"connection rejected or unreachable: {e.Reason}");

            // A custom rejection (e.g. wrong password) carries a human-readable reason in e.Message,
            // surface that instead of the generic code so the player knows what to fix.
            string reason = DisconnectMessages.For(e.Reason);
            if (e.Reason == RejectReason.Custom && e.Message != null)
            {
                try { reason = e.Message.GetString(); } catch { }
            }

            // A rejected or failed connection raises this event rather than Disconnected, so
            // everything the connect attempt set up has to be torn down here: the "Connecting to
            // server" modal, and the half-initialised network and lobby state. Leave either
            // behind and the menu is stuck, no retry after a wrong password. Then show the
            // reason, with a button back to a browser that works.
            try { ModalDialog.Hide(); } catch { }
            try { SteamLobby.ResetNetworkState(); }
            catch (Exception rex) { Main.helper.Log("Reset on connection-fail failed: " + rex.Message); }

            ModalDialog.Show("Failed to connect", reason, "Okay", true,
                () => { Main.TransitionTo(MenuState.BrowserScreen); });
        }

        public NetClient(string name)
        {
            Name = name;
        }

        // Connect to a host. If a password is supplied (joining a locked server, or the host connecting
        // to its own locked server) it's sent as the Riptide connect-message payload, which the server's
        // HandleConnectionApproval validates. Null/empty password = no payload = unchanged behaviour for
        // unlocked servers.
        public static void Connect(string ip, string password = null)
        {
            bool locked = !string.IsNullOrEmpty(password);
            Main.helper.Log("connecting to " + ip + (locked ? " (password supplied)" : ""));

            Message connectMsg = null;
            if (locked)
            {
                connectMsg = Message.Create();
                connectMsg.AddString(password);
            }
            client.Connect(ip, message: connectMsg, useMessageHandlers: false);
        }

        private void Update()
        {
            client.Update();
        }

        // DO NOT DELETE THESE, even though the bodies do nothing.
        //
        // DO NOT DELETE THESE, empty as they are. Nothing in this mod calls
        // AddComponent<NetClient>(); the KCModHelper loader attaches this class to a GameObject
        // precisely because it declares the loader's hooks. Without them the component is never
        // created, Update() never runs, client.Update() never pumps Riptide, and the transport
        // connects while the handshake hangs forever, with nothing logged.
        private void Preload(KCModHelper helper) { }

        private void SceneLoaded(KCModHelper helper) { }
    }
}
