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

            playerText.text = name + " would like to form an alliance with your kingdom.";

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
            }
        }

        // ---- building --------------------------------------------------------------------
        //
        // Tries the mod's real dialog art first (see KacModalStyle), which is the same panel and
        // button ModalDialog itself uses, instantiated under this window's own always-visible
        // canvas instead of the main-menu UI ModalDialog is parented to. Falls back to a plain
        // hand-drawn panel, unchanged from how this window looked before that art existed, if the
        // prefab cannot be found or does not have the nodes expected of it.

        private static void EnsureBuilt()
        {
            if (canvas != null) return;

            canvas = new GameObject("AllianceRequest", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var c = canvas.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 5100;
            var scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;

            KacModalStyle.Panel panel = KacModalStyle.Build(canvas.transform);
            if (panel != null) BuildFromRealArt(panel);
            else BuildFallback();

            // Hidden until Show() actually has content for it, exactly as before this window had
            // any art to choose between; one exit point for both build paths, rather than two
            // copies of the same line.
            canvas.SetActive(false);
        }

        private static void BuildFromRealArt(KacModalStyle.Panel panel)
        {
            // KacModalStyle.Build leaves the instance inactive, matching ModalDialog's own
            // singleton, which is not what is wanted for a piece parented under this window's own
            // canvas: here the OUTER canvas is the single on/off switch (see Close/Reply/Show),
            // so a child of it should stay active in itself and simply follow its parent.
            panel.Root.SetActive(true);

            // The kingdom's name carries the Title, the way it did in the hand-drawn version; the
            // player and the request itself share the one body field the real prefab has.
            kingdomText = panel.Title;
            playerText = panel.Description;

            Button acceptBtn = KacModalStyle.CloneButton(panel, "Accept");
            if (acceptBtn == null)
            {
                // The clone failed but the panel itself is fine; a single-button alliance prompt
                // that only declines is worse than the flat fallback, so drop back to it wholesale
                // rather than ship a request with no way to accept it.
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
            var panel = Box("Request", backdrop, Vector2.zero, new Vector2(720, 400));
            panel.gameObject.AddComponent<Image>().color = new Color(.055f, .11f, .14f, .94f);
            Label(panel, "ALLIANCE REQUEST", new Vector2(0, 148), new Vector2(670, 60), 38);
            kingdomText = Label(panel, "", new Vector2(0, 62), new Vector2(660, 75), 34);
            kingdomText.color = new Color(0, .9f, .92f);
            playerText = Label(panel, "", new Vector2(0, -20), new Vector2(660, 90), 24);
            FallbackButton(panel, "Decline", -145, false);
            FallbackButton(panel, "Accept", 145, true);
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

        private static TextMeshProUGUI Label(Transform parent, string value, Vector2 position, Vector2 size, float fontSize)
        {
            var label = Box("Text", parent, position, size).gameObject.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset;
            if (GameUI.inst != null)
            {
                var existing = GameUI.inst.GetComponentInChildren<TextMeshProUGUI>(true);
                if (existing != null) label.font = existing.font;
            }
            label.text = value;
            label.richText = false;
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = fontSize;
            label.enableAutoSizing = true;
            label.fontSizeMin = 16;
            label.fontSizeMax = fontSize;
            label.raycastTarget = false;
            return label;
        }

        private static void FallbackButton(Transform parent, string caption, float x, bool accept)
        {
            var rect = Box(caption, parent, new Vector2(x, -145), new Vector2(240, 48));
            var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(.16f, .35f, .48f, 1);
            var button = rect.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => Reply(accept));
            Label(rect, caption, Vector2.zero, new Vector2(220, 42), 24);
        }
    }
}
