using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KaCMultiplayer.Lobby
{
    internal static class AllianceRequestWindow
    {
        private static GameObject canvas;
        private static TextMeshProUGUI kingdomText, playerText;
        private static RawImage banner;
        private static int sender, recipient;
        private static bool awaitingReply;

        public static void Tick()
        {
            if (!NetClient.client.IsConnected || Player.inst == null || Player.inst.PlayerLandmassOwner == null)
            {
                Close();
                return;
            }

            // Never before the world is running. A player still receiving it has no kingdom to
            // answer for yet, the loading panel is what belongs on screen, and answering from there
            // sent a relation change in the middle of a transfer and dropped them out of it.
            if (GameState.inst == null || !GameState.inst.IsPlayMode())
            {
                Close();
                return;
            }
            int me = Player.inst.PlayerLandmassOwner.teamId;
            if (sender != 0)
            {
                var peer = NetPlayers.ByTeam(sender);
                if (me != recipient || peer == null || peer.isGhost || PlayerRelations.AllianceOfferFrom(me, sender) != sender)
                {
                    Close();
                    return;
                }
                return;
            }

            // ANSWERED BY MOUSE ONLY, deliberately. This used to accept on A and decline on D,
            // which are two of the movement keys: an offer arriving while you were walking the
            // camera answered itself with whichever key you happened to be holding. There is no
            // keyboard shortcut for a decision this final.
            foreach (var peer in Main.kCPlayers.Values)
            {
                if (peer == null || peer.isGhost || peer.inst == null || peer.inst.PlayerLandmassOwner == null) continue;
                int team = peer.inst.PlayerLandmassOwner.teamId;
                if (team == me || PlayerRelations.AllianceOfferFrom(me, team) != team) continue;
                Show(peer, me, team);
                break;
            }
        }

        private static void Show(SessionPlayer peer, int me, int team)
        {
            EnsureBuilt();
            if (canvas == null) return;   // both the real art and the fallback failed to build

            kingdomText.text = string.IsNullOrWhiteSpace(peer.kingdomName) ? "Kingdom " + team : peer.kingdomName;

            // Shared with the diplomacy list and the deal popup, so the three can never disagree
            // about what to call somebody.
            string name = peer.SteamPersona();
            if (string.IsNullOrWhiteSpace(name)) name = "Steam player " + peer.steamId;

            playerText.text = name;

            Texture flag = LobbyRowVisuals.BannerOf(peer);
            banner.texture = flag;
            banner.gameObject.SetActive(flag != null);

            sender = team;
            recipient = me;
            awaitingReply = false;
            canvas.SetActive(true);
        }

        private static void Reply(bool accept)
        {
            if (awaitingReply || sender == 0 || PlayerRelations.AllianceOfferFrom(recipient, sender) != sender) return;
            awaitingReply = true;
            Main.RequestRelationChange(sender, accept ? World.Relations.Allies : (World.Relations)PlayerRelationMessage.DeclineAlliance);
            // Keep the request reserved until its network echo removes it, avoiding duplicate replies.
            canvas.SetActive(false);
        }

        private static void Close()
        {
            sender = 0;
            recipient = 0;
            awaitingReply = false;
            if (canvas != null) canvas.SetActive(false);
        }

        /// <summary>Drops the window entirely, for the end of a session.</summary>
        public static void Reset()
        {
            Close();
            if (canvas != null)
            {
                Object.Destroy(canvas);
                canvas = null;
                kingdomText = null;
                playerText = null;
                banner = null;
            }
        }

        // ---- building --------------------------------------------------------------------

        /// <summary>Builds the popup from the bundle's requestui prefab, once.</summary>
        private static void EnsureBuilt()
        {
            if (canvas != null) return;

            canvas = Popup.Create("AllianceRequest", LobbyPrefabs.Request, 5100);
            if (canvas == null) return;

            Popup.Find<TextMeshProUGUI>(canvas, "Window/Kind").text = "Alliance Request";
            Popup.Find<TextMeshProUGUI>(canvas, "Window/Body").text = "Would like to form an alliance with your kingdom.";
            Popup.Find<TextMeshProUGUI>(canvas, "Window/Note").gameObject.SetActive(false);

            kingdomText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Kingdom");
            playerText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Player");
            banner = Popup.Find<RawImage>(canvas, "Window/Banner");

            Popup.OnClick(Popup.Find<Button>(canvas, "Window/Decline"), () => Reply(false));
            Popup.OnClick(Popup.Find<Button>(canvas, "Window/Accept"), () => Reply(true));
        }
    }
}
