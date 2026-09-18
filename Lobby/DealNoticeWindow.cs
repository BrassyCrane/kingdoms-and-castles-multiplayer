using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// "Demand sent to Firereach: 100 Wood." One line, one button, gone when you click it.
    ///
    /// WHY THIS EXISTS. Sending a demand or a gift went out silently: click Demand, the picker
    /// closes, and the only sign anything happened was a line in the kingdom log easy to miss
    /// mid-game. The other side gets a full popup asking them to answer; the sender got nothing
    /// telling them their own click had landed. This is that acknowledgement, and the same one
    /// speaks again when the deal is resolved, for whichever side is not already looking at a
    /// popup that just told them the answer (the side that clicked Accept or Decline knows what
    /// they did; the side that sent the original request does not, until now).
    ///
    /// Built from the same real dialog art as <see cref="AllianceRequestWindow"/> and
    /// <see cref="DealRequestWindow"/>, via <see cref="KacModalStyle"/>, with the same flat-panel
    /// fallback if that art cannot be found.
    /// </summary>
    internal static class DealNoticeWindow
    {
        private const int SortingOrder = 5080;

        private static GameObject canvas;
        private static TextMeshProUGUI titleText, bodyText;
        private static Button okButton;

        /// <summary>Shown when the local player's own demand or gift goes out.</summary>
        public static void ShowSent(string title, string body)
        {
            Show(title, body);
        }

        /// <summary>Shown to the proposer once the other side answers.</summary>
        public static void ShowResolved(string title, string body)
        {
            Show(title, body);
        }

        private static void Show(string title, string body)
        {
            try
            {
                EnsureBuilt();
                if (canvas == null) return;

                titleText.text = title ?? "";
                bodyText.text = body ?? "";
                canvas.SetActive(true);
            }
            catch (System.Exception e) { Net.NetLog.Error("showing a deal notice", e); }
        }

        public static void Close()
        {
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
                okButton = null;
            }
        }

        private static void EnsureBuilt()
        {
            if (canvas != null) return;

            canvas = new GameObject("DealNotice", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas c = canvas.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = SortingOrder;

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
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
            // singleton. Not wanted here: this window's OUTER canvas is the single on/off switch,
            // so a child of it stays active in itself and follows its parent instead.
            panel.Root.SetActive(true);

            titleText = panel.Title;
            bodyText = panel.Description;

            okButton = panel.Button;
            KacModalStyle.SetLabel(okButton, "Okay");
            KacModalStyle.SetClick(okButton, Close);
        }

        // ---- fallback, used only if the real dialog art cannot be found -----------------

        private static void BuildFallback()
        {
            var backdrop = Box("Backdrop", canvas.transform, Vector2.zero, Vector2.zero);
            RectTransform bt = backdrop;
            bt.anchorMin = Vector2.zero;
            bt.anchorMax = Vector2.one;
            bt.offsetMin = bt.offsetMax = Vector2.zero;
            backdrop.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, .12f);

            var panel = Box("Notice", backdrop, Vector2.zero, new Vector2(560, 260));
            panel.gameObject.AddComponent<Image>().color = new Color(.055f, .11f, .14f, .94f);

            titleText = Label(panel, "", new Vector2(0, 70), new Vector2(520, 60), 32);
            titleText.color = new Color(0, .9f, .92f);
            bodyText = Label(panel, "", new Vector2(0, 0), new Vector2(520, 80), 22);

            var buttonRect = Box("Okay", panel, new Vector2(0, -95), new Vector2(200, 46));
            var image = buttonRect.gameObject.AddComponent<Image>();
            image.color = new Color(.16f, .35f, .48f, 1);
            okButton = buttonRect.gameObject.AddComponent<Button>();
            okButton.targetGraphic = image;
            okButton.onClick.AddListener(Close);
            Label(buttonRect, "Okay", Vector2.zero, new Vector2(190, 40), 22);
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
            label.richText = false;
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = fontSize;
            label.enableAutoSizing = true;
            label.fontSizeMin = 14;
            label.fontSizeMax = fontSize;
            label.raycastTarget = false;
            return label;
        }
    }
}
