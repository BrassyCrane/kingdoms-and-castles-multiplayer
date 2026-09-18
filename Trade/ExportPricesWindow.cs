using System;
using System.Collections.Generic;
using KaCMultiplayer.Net;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KaCMultiplayer.Trade
{
    /// <summary>
    /// "What my kingdom charges." One row per commodity, a price you can raise, lower, or refuse.
    ///
    /// Hand-built rather than taken from the prefab bundle, for the reason the other mod windows
    /// are: nothing in the bundle is this shape, and ten rows of three buttons is less code than
    /// authoring a panel would be. Its own overlay canvas, because MenuUi.Root is the main-menu UI
    /// and is not active while a game is running.
    ///
    /// GOLD IS NOT LISTED. It is what everything else is priced IN, so a price for gold in gold is
    /// not a thing a player can mean, and offering the row would only invite the question.
    ///
    /// Every change goes out as it is made. There is no Save button, deliberately: a price list
    /// that is only half sent is worse than one that is a second old, and the alternative is
    /// remembering to press something before the buyer's window opens.
    /// </summary>
    public static class ExportPricesWindow
    {
        private const int SortingOrder = 5200;

        private static GameObject canvasObj;
        private static GameObject root;
        private static int localTeam;

        private static readonly List<FreeResourceType> rows = new List<FreeResourceType>();
        private static readonly List<TextMeshProUGUI> priceLabels = new List<TextMeshProUGUI>();

        private static readonly Color cPanel = new Color(0.10f, 0.14f, 0.20f, 0.97f);
        private static readonly Color cBorder = new Color(0.27f, 0.35f, 0.46f, 1f);
        private static readonly Color cButton = new Color(0.16f, 0.22f, 0.30f, 1f);
        private static readonly Color cRefuse = new Color(0.42f, 0.19f, 0.19f, 1f);
        private static readonly Color cText = new Color(0.88f, 0.92f, 0.96f, 1f);
        private static readonly Color cMuted = new Color(0.62f, 0.68f, 0.75f, 1f);

        public static bool IsOpen { get { return root != null && root.activeSelf; } }

        public static void Toggle()
        {
            if (IsOpen) { Close(); return; }

            if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return;
            localTeam = Player.inst.PlayerLandmassOwner.teamId;

            Build();
            if (root == null) return;

            Refresh();
            root.SetActive(true);
        }

        public static void Close()
        {
            if (root != null) root.SetActive(false);
        }

        public static void Tick()
        {
            if (!IsOpen) return;

            // Escape closes, and nothing else here reads the keyboard: the prices are set with the
            // mouse so a stray keypress cannot change what a kingdom charges.
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        public static void Reset()
        {
            rows.Clear();
            priceLabels.Clear();
            root = null;

            if (canvasObj != null)
            {
                UnityEngine.Object.Destroy(canvasObj);
                canvasObj = null;
            }
        }

        // ---- the list ------------------------------------------------------

        private static void Step(FreeResourceType type, int delta)
        {
            int now = ExportPrices.PriceFor(localTeam, type);
            ExportPrices.SetLocal(localTeam, type, now + delta);
            Refresh();
        }

        private static void Withhold(FreeResourceType type)
        {
            // A second click puts it back at the game's own price rather than at zero, so "not for
            // sale" is a toggle and not a one-way door.
            bool selling = ExportPrices.ForSale(localTeam, type);
            ExportPrices.SetLocal(localTeam, type,
                                  selling ? ExportPrices.NotForSale : ExportPrices.DefaultPrice(type));
            Refresh();
        }

        private static void Refresh()
        {
            for (int i = 0; i < rows.Count && i < priceLabels.Count; i++)
            {
                FreeResourceType type = rows[i];
                TextMeshProUGUI label = priceLabels[i];
                if (label == null) continue;

                if (!ExportPrices.ForSale(localTeam, type))
                {
                    label.text = "not for sale";
                    label.color = cMuted;
                }
                else
                {
                    label.text = ExportPrices.PriceFor(localTeam, type) + "g";
                    label.color = cText;
                }
            }
        }

        // ---- building ------------------------------------------------------

        private static Transform EnsureCanvas()
        {
            if (canvasObj != null) return canvasObj.transform;

            canvasObj = new GameObject("KcmExportPricesCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

            Canvas c = canvasObj.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = SortingOrder;

            CanvasScaler scaler = canvasObj.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            if (UnityEngine.Object.FindObjectOfType<EventSystem>() == null)
            {
                GameObject es = new GameObject("KcmExportEventSystem",
                    typeof(EventSystem), typeof(StandaloneInputModule));
                es.transform.SetParent(canvasObj.transform, false);
            }

            return canvasObj.transform;
        }

        private static void Build()
        {
            if (root != null) return;

            try
            {
                Transform parent = EnsureCanvas();

                rows.Clear();
                priceLabels.Clear();

                // Everything tradeable except gold, which is the unit of account.
                FreeResourceType[] all = PlayerRelations.Demandable;
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != FreeResourceType.Gold) rows.Add(all[i]);

                const float rowHeight = 42f;
                float panelHeight = 150f + rows.Count * rowHeight;

                root = Panel("ExportPrices", parent, 620f, panelHeight);

                string kingdom = Main.KingdomNameForTeam(localTeam);
                Label(root.transform, string.IsNullOrEmpty(kingdom) ? "Export Prices" : kingdom + " - Export Prices",
                      22f, FontStyles.Bold, 0f, panelHeight / 2f - 34f, 560f, 34f);

                Label(root.transform, "What other kingdoms pay for goods your merchants carry to them.",
                      15f, FontStyles.Normal, 0f, panelHeight / 2f - 64f, 560f, 24f)
                    .color = cMuted;

                float top = panelHeight / 2f - 96f;
                for (int i = 0; i < rows.Count; i++)
                {
                    FreeResourceType type = rows[i];
                    float y = top - i * rowHeight;

                    Label(root.transform, PlayerRelations.ResourceLabel(type), 17f, FontStyles.Normal,
                          -220f, y, 180f, 30f).alignment = TextAlignmentOptions.Left;

                    // Captured per row, which is the whole reason these are locals: a loop variable
                    // shared by every handler would leave all ten buttons editing the last resource.
                    FreeResourceType captured = type;

                    MakeButton(root.transform, "-", -60f, y, 34f, 30f, cButton, delegate { Step(captured, -1); });
                    priceLabels.Add(Label(root.transform, "", 17f, FontStyles.Bold, 10f, y, 110f, 30f));
                    MakeButton(root.transform, "+", 80f, y, 34f, 30f, cButton, delegate { Step(captured, 1); });

                    MakeButton(root.transform, "-10", 128f, y, 44f, 30f, cButton, delegate { Step(captured, -10); });
                    MakeButton(root.transform, "+10", 178f, y, 44f, 30f, cButton, delegate { Step(captured, 10); });

                    MakeButton(root.transform, "Hold", 240f, y, 60f, 30f, cRefuse, delegate { Withhold(captured); });
                }

                MakeButton(root.transform, "Close", 0f, -panelHeight / 2f + 32f, 160f, 36f, cButton, Close);

                root.SetActive(false);
            }
            catch (Exception e)
            {
                NetLog.Error("building the export price window", e);
                Reset();
            }
        }

        private static GameObject Panel(string name, Transform parent, float w, float h)
        {
            GameObject border = new GameObject(name, typeof(RectTransform), typeof(Image));
            RectTransform rt = border.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w + 4f, h + 4f);
            rt.anchoredPosition = Vector2.zero;
            border.GetComponent<Image>().color = cBorder;

            GameObject inner = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            RectTransform irt = inner.GetComponent<RectTransform>();
            irt.SetParent(border.transform, false);
            irt.anchorMin = Vector2.zero;
            irt.anchorMax = Vector2.one;
            irt.offsetMin = new Vector2(2f, 2f);
            irt.offsetMax = new Vector2(-2f, -2f);
            inner.GetComponent<Image>().color = cPanel;

            return border;
        }

        private static TextMeshProUGUI Label(Transform parent, string text, float size,
                                             FontStyles style, float x, float y, float w, float h)
        {
            GameObject obj = new GameObject("Label", typeof(RectTransform));
            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);

            TextMeshProUGUI t = obj.AddComponent<TextMeshProUGUI>();
            t.font = TMP_Settings.defaultFontAsset;
            if (GameUI.inst != null)
            {
                TextMeshProUGUI existing = GameUI.inst.GetComponentInChildren<TextMeshProUGUI>(true);
                if (existing != null) t.font = existing.font;
            }

            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = cText;

            // Kingdom names are player-typed and reach this window, so a name containing something
            // that looks like a tag must appear as itself rather than as markup.
            t.richText = false;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.raycastTarget = false;
            return t;
        }

        private static Button MakeButton(Transform parent, string text, float x, float y,
                                         float w, float h, Color colour, UnityEngine.Events.UnityAction onClick)
        {
            GameObject obj = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);

            Image img = obj.GetComponent<Image>();
            img.color = colour;

            Button b = obj.GetComponent<Button>();
            b.targetGraphic = img;
            b.onClick.AddListener(onClick);

            Label(obj.transform, text, 16f, FontStyles.Normal, 0f, 0f, w - 4f, h - 4f);
            return b;
        }
    }
}
