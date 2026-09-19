using KaCMultiplayer.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// "Firereach offers you 10 Gold. Accept or Decline."
    ///
    /// WHY THIS EXISTS. A demand or an offer arrived as a line of text telling the player to type
    /// /accept. That asked somebody mid-game to notice a log entry, remember a command, find the
    /// chat box and spell it correctly, and it fell apart entirely with a third player in the
    /// session, because the command had no way to tell which kingdom was meant. A gift should not
    /// need a manual.
    ///
    /// Built the same way as <see cref="AllianceRequestWindow"/>, and deliberately so: the two are
    /// the same question with different words, they should look and behave alike, and a player who
    /// has answered one already knows how to answer the other. Its own overlay canvas for the same
    /// reason as well, because MenuUi.Root is the main-menu UI and is not active during play.
    ///
    /// The typed commands still work and are still listed under /diplo. This does not replace them,
    /// it just means nobody has to know they exist.
    /// </summary>
    internal static class DealRequestWindow
    {
        private static GameObject canvas;
        private static TextMeshProUGUI kindText, kingdomText, playerText, bodyText, noteText;
        private static RawImage banner;

        private static readonly Color DemandColour = new Color(0.91f, 0.63f, 0.29f);
        private static readonly Color OfferColour = new Color(0.34f, 0.78f, 0.29f);

        private static int sender, recipient;
        private static bool awaitingReply;

        public static void Tick()
        {
            if (!NetClient.client.IsConnected || Player.inst == null || Player.inst.PlayerLandmassOwner == null)
            {
                Close();
                return;
            }

            // Never during a join. The same rule the alliance popup needed: a player still
            // receiving the world has no kingdom to answer for, the loading panel is what belongs
            // on screen, and answering from there sent a deal in the middle of a transfer.
            if (GameState.inst == null || !GameState.inst.IsPlayMode())
            {
                Close();
                return;
            }

            int me = Player.inst.PlayerLandmassOwner.teamId;

            // Already showing one: hold it up until the deal is gone, which happens when our own
            // reply comes back around the session. Closing on the click instead would let a second
            // click land on a deal that had not been cleared yet.
            if (sender != 0)
            {
                if (me != recipient || !PlayerRelations.DealStillOpen(me, sender)) Close();
                return;
            }

            int from, amount;
            FreeResourceType res;
            bool theyDemand;
            if (!PlayerRelations.PendingDealFor(me, out from, out amount, out res, out theyDemand)) return;

            Show(me, from, amount, res, theyDemand);
        }

        private static void Show(int me, int from, int amount, FreeResourceType res, bool theyDemand)
        {
            EnsureBuilt();
            if (canvas == null) return;   // both the real art and the fallback failed to build

            SessionPlayer peer = NetPlayers.ByTeam(from);

            kindText.text = theyDemand ? "Demand" : "Offer";
            kindText.color = theyDemand ? DemandColour : OfferColour;

            kingdomText.text = peer != null && !string.IsNullOrWhiteSpace(peer.kingdomName)
                ? peer.kingdomName
                : "Kingdom " + from;

            string person = peer != null ? peer.SteamPersona() : null;
            playerText.text = person ?? "";

            Texture flag = LobbyRowVisuals.BannerOf(peer);
            banner.texture = flag;
            banner.gameObject.SetActive(flag != null);

            string what = amount + " " + PlayerRelations.ResourceLabel(res);
            bodyText.text = theyDemand
                ? "Demands " + what + " from your kingdom."
                : "Is sending your kingdom " + what + ".";

            // What accepting actually costs or gains, spelled out, because the two directions read
            // almost identically at a glance and only one of them takes your resources.
            noteText.text = PlayerRelations.AtWarWith(me, from)
                ? (theyDemand ? "Accepting pays them and ends the war."
                               : "Accepting takes their payment and ends the war.")
                : (theyDemand ? "Accepting pays them from your stores."
                               : "Accepting adds it to your stores, and costs you nothing.");

            sender = from;
            recipient = me;
            awaitingReply = false;
            canvas.SetActive(true);
        }

        private static void Reply(bool accept)
        {
            if (awaitingReply || sender == 0) return;
            if (!PlayerRelations.DealStillOpen(recipient, sender)) { Close(); return; }

            awaitingReply = true;
            PlayerRelations.AnswerDeal(recipient, sender, accept);

            // Hidden, not closed. The deal stays reserved until the network echo clears it, which
            // is what stops a second click sending a second reply.
            if (canvas != null) canvas.SetActive(false);
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
                kindText = kingdomText = playerText = bodyText = noteText = null;
                banner = null;
            }
        }

        // ---- building --------------------------------------------------------------------

        /// <summary>
        /// Builds the popup from the bundle's requestui prefab, once. Just under the alliance
        /// popup, so if both arrive together they stack in a fixed order.
        /// </summary>
        private static void EnsureBuilt()
        {
            if (canvas != null) return;

            canvas = Popup.Create("DealRequest", LobbyPrefabs.Request, 5090);
            if (canvas == null) return;

            kindText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Kind");
            kingdomText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Kingdom");
            playerText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Player");
            bodyText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Body");
            noteText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Note");
            banner = Popup.Find<RawImage>(canvas, "Window/Banner");

            Popup.OnClick(Popup.Find<Button>(canvas, "Window/Decline"), () => Reply(false));
            Popup.OnClick(Popup.Find<Button>(canvas, "Window/Accept"), () => Reply(true));
        }
    }
}
