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
        private static TextMeshProUGUI titleText, bodyText;

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

            titleText.text = theyDemand ? "DEMAND" : "TRADE OFFER";
            titleText.color = theyDemand ? new Color(.95f, .62f, .28f) : new Color(.45f, .85f, .55f);

            string kingdom = peer != null && !string.IsNullOrWhiteSpace(peer.kingdomName)
                ? peer.kingdomName
                : "Kingdom " + from;

            string person = peer != null ? peer.SteamPersona() : null;
            string who = string.IsNullOrWhiteSpace(person) ? kingdom : kingdom + "  (" + person + ")";

            string what = amount + " " + PlayerRelations.ResourceLabel(res);
            string terms = theyDemand
                ? "Demands " + what + " from your kingdom."
                : "Is sending your kingdom " + what + ".";

            // What accepting actually costs or gains, spelled out, because the two directions read
            // almost identically at a glance and only one of them takes your resources.
            string note = PlayerRelations.AtWarWith(me, from)
                ? (theyDemand ? "Accepting pays them and ends the war."
                               : "Accepting takes their payment and ends the war.")
                : (theyDemand ? "Accepting pays them from your stores."
                               : "Accepting adds it to your stores, and costs you nothing.");

            // One field carries all of it in the real-art path, the same one ModalDialog itself
            // uses for a whole paragraph of explanation, so this is well within what it is for.
            bodyText.text = who + "\n\n" + terms + "\n" + note;

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
                titleText = null;
                bodyText = null;
            }
        }

        // ---- building --------------------------------------------------------------------
        //
        // Tries the mod's real dialog art first (see KacModalStyle); falls back to the original
        // hand-drawn panel if the prefab cannot be found or is missing a node it expects.

        private static void EnsureBuilt()
        {
            if (canvas != null) return;

            canvas = new GameObject("DealRequest", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var c = canvas.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;

            // Just under the alliance window, so that if both ever arrive together they stack in a
            // fixed order rather than whichever was built last.
            c.sortingOrder = 5090;

            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;

            KacModalStyle.Panel panel = KacModalStyle.Build(canvas.transform);
            if (panel != null) BuildFromRealArt(panel);
            else BuildFallback();

            // Hidden until Show() actually has content for it; one exit point for both build
            // paths, rather than two copies of the same line.
            canvas.SetActive(false);
        }

        private static void BuildFromRealArt(KacModalStyle.Panel panel)
        {
            // KacModalStyle.Build leaves the instance inactive, matching ModalDialog's own
            // singleton. Not wanted here: this window's OUTER canvas is the single on/off switch
            // (see Close/Reply/Show), so a child of it stays active in itself and follows its
            // parent instead.
            panel.Root.SetActive(true);

            titleText = panel.Title;
            bodyText = panel.Description;

            Button acceptBtn = KacModalStyle.CloneButton(panel, "Accept");
            if (acceptBtn == null)
            {
                UnityEngine.Object.Destroy(panel.Root);
                BuildFallback();
                return;
            }

            KacModalStyle.SetLabel(panel.Button, "Decline");
            KacModalStyle.SetClick(panel.Button, () => Reply(false));

            KacModalStyle.SetLabel(acceptBtn, "Accept");
            KacModalStyle.SetClick(acceptBtn, () => Reply(true));

            KacModalStyle.SplitHorizontally(panel, panel.Button, acceptBtn);
        }

        // ---- fallback, used only if the real dialog art cannot be found -------------------

        private static void BuildFallback()
        {
            var backdrop = Box("Backdrop", canvas.transform, Vector2.zero, Vector2.zero);
            backdrop.anchorMin = Vector2.zero;
            backdrop.anchorMax = Vector2.one;
            backdrop.offsetMin = backdrop.offsetMax = Vector2.zero;
            backdrop.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .12f);

            var panel = Box("Deal", backdrop, Vector2.zero, new Vector2(720, 400));
            panel.gameObject.AddComponent<Image>().color = new Color(.055f, .11f, .14f, .94f);

            titleText = Label(panel, "TRADE OFFER", new Vector2(0, 148), new Vector2(670, 60), 38);
            bodyText = Label(panel, "", new Vector2(0, 10), new Vector2(660, 220), 24);

            FallbackButton(panel, "Decline", -145, new Color(.42f, .19f, .19f, 1), false);
            FallbackButton(panel, "Accept", 145, new Color(.16f, .35f, .48f, 1), true);
        }

        private static RectTransform Box(string name, Transform parent, Vector2 position, Vector2 size)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            var rect = obj.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return rect;
        }

        private static TextMeshProUGUI Label(Transform parent, string value, Vector2 position,
                                             Vector2 size, float fontSize)
        {
            var label = Box("Text", parent, position, size).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset;
            if (GameUI.inst != null)
            {
                var existing = GameUI.inst.GetComponentInChildren<TextMeshProUGUI>(true);
                if (existing != null) label.font = existing.font;
            }
            label.text = value;

            // Kingdom names are player-typed, so rich text stays off: a name containing something
            // that looks like a tag must appear as itself rather than as markup.
            label.richText = false;
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = fontSize;
            label.enableAutoSizing = true;
            label.fontSizeMin = 14;
            label.fontSizeMax = fontSize;
            label.raycastTarget = false;
            return label;
        }

        private static void FallbackButton(Transform parent, string caption, float x, Color colour, bool accept)
        {
            var rect = Box(caption, parent, new Vector2(x, -145), new Vector2(240, 48));
            var image = rect.gameObject.AddComponent<Image>();
            image.color = colour;
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => Reply(accept));
            Label(rect, caption, Vector2.zero, new Vector2(220, 42), 24);
        }
    }
}
