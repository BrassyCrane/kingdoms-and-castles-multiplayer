using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// "Demand what, and how much." A grid of every resource a kingdom can be asked for, with an
    /// amount, opened from the Demand button on a diplomacy row.
    ///
    /// BUILT IN CODE, not from a prefab. Every other screen in the mod comes from the asset
    /// bundle, and this one deliberately does not: a picker over <c>FreeResourceType</c> has to
    /// match whatever that enum holds, and a prefab would freeze today's eleven resources into
    /// art that nobody would remember to update. Reading the list from
    /// <see cref="PlayerRelations.Demandable"/> means a game update that adds a resource shows it
    /// here on its own.
    ///
    /// Styling is deliberately plain and self-contained, because it has no prefab to inherit
    /// from. It sits over the diplomacy window and closes on any choice or on Escape.
    /// </summary>
    public static class ResourcePicker
    {
        /// <summary>Amounts offered per resource. Enough range to be useful, few enough to click.</summary>
        private static readonly int[] Amounts = new int[] { 10, 25, 50, 100, 250, 500, 1000 };

        private static GameObject root;
        private static int localTeam;
        private static int targetTeam;
        private static FreeResourceType chosen = FreeResourceType.Gold;
        private static int chosenAmount = 100;

        private static readonly List<Button> resourceButtons = new List<Button>();
        private static readonly List<Button> amountButtons = new List<Button>();
        private static TextMeshProUGUI summary;

        private static readonly Color cPanel = new Color(0.10f, 0.14f, 0.20f, 0.97f);
        private static readonly Color cBorder = new Color(0.27f, 0.35f, 0.46f, 1f);
        private static readonly Color cButton = new Color(0.16f, 0.22f, 0.30f, 1f);
        private static readonly Color cChosen = new Color(0.30f, 0.52f, 0.36f, 1f);
        private static readonly Color cText = new Color(0.88f, 0.92f, 0.96f, 1f);

        public static bool IsOpen { get { return root != null && root.activeSelf; } }

        /// <summary>Opens the picker for one target kingdom.</summary>
        public static void Open(int fromTeam, int toTeam)
        {
            localTeam = fromTeam;
            targetTeam = toTeam;
            chosen = FreeResourceType.Gold;
            chosenAmount = 100;

            try
            {
                if (root == null) Build();
                if (root == null) return;

                root.SetActive(true);
                Refresh();
            }
            catch (Exception e) { NetLog.Error("opening the resource picker", e); }
        }

        public static void Close()
        {
            if (root != null) root.SetActive(false);
        }

        /// <summary>Escape closes it. Called from the diplomacy window's own tick.</summary>
        public static void Tick()
        {
            if (!IsOpen) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        /// <summary>Forgets the built UI, for a torn-down session.</summary>
        public static void Reset()
        {
            try { if (root != null) UnityEngine.Object.Destroy(root); }
            catch { }

            root = null;
            summary = null;
            resourceButtons.Clear();
            amountButtons.Clear();
        }

        private static void Build()
        {
            Transform parent = MenuUi.Root;
            if (parent == null) { NetLog.Warn("resource picker: no menu UI to attach to"); return; }

            root = Panel("ResourcePicker", parent, 560f, 430f);

            Label(root.transform, "Demand from another kingdom", 20f, FontStyles.Bold, 0f, 180f, 520f, 34f);

            // Resources, four to a row.
            resourceButtons.Clear();
            FreeResourceType[] types = PlayerRelations.Demandable;
            const float bw = 124f, bh = 34f, gapX = 6f, gapY = 6f;

            for (int i = 0; i < types.Length; i++)
            {
                int col = i % 4, rowIdx = i / 4;
                float x = (col - 1.5f) * (bw + gapX);
                float y = 130f - rowIdx * (bh + gapY);

                FreeResourceType t = types[i];
                Button b = MakeButton(root.transform, PlayerRelations.ResourceLabel(t), x, y, bw, bh,
                                      delegate { chosen = t; Refresh(); });
                resourceButtons.Add(b);
            }

            Label(root.transform, "How much", 16f, FontStyles.Normal, 0f, -20f, 520f, 26f);

            amountButtons.Clear();
            const float aw = 68f, ah = 32f, agap = 6f;
            for (int i = 0; i < Amounts.Length; i++)
            {
                float x = (i - (Amounts.Length - 1) / 2f) * (aw + agap);
                int amount = Amounts[i];
                Button b = MakeButton(root.transform, amount.ToString(), x, -56f, aw, ah,
                                      delegate { chosenAmount = amount; Refresh(); });
                amountButtons.Add(b);
            }

            summary = Label(root.transform, "", 17f, FontStyles.Normal, 0f, -104f, 520f, 30f);

            MakeButton(root.transform, "Demand it", -110f, -160f, 190f, 40f, delegate
            {
                PlayerRelations.Send(localTeam, targetTeam,
                    Net.Messages.DealKind.Demand, chosenAmount, chosen);
                Close();
            });

            MakeButton(root.transform, "Offer it instead", 110f, -160f, 190f, 40f, delegate
            {
                PlayerRelations.Send(localTeam, targetTeam,
                    Net.Messages.DealKind.Offer, chosenAmount, chosen);
                Close();
            });

            MakeButton(root.transform, "Cancel", 0f, -205f, 120f, 30f, delegate { Close(); });
        }

        /// <summary>Repaints the selection highlight and the sentence under it.</summary>
        private static void Refresh()
        {
            FreeResourceType[] types = PlayerRelations.Demandable;
            for (int i = 0; i < resourceButtons.Count && i < types.Length; i++)
                Tint(resourceButtons[i], types[i] == chosen);

            for (int i = 0; i < amountButtons.Count && i < Amounts.Length; i++)
                Tint(amountButtons[i], Amounts[i] == chosenAmount);

            if (summary != null)
            {
                // Says what will actually happen, in the order it happens, because "Demand" alone
                // does not make clear that accepting also ends a war.
                string what = chosenAmount + " " + PlayerRelations.ResourceLabel(chosen);
                bool atWar = PlayerRelations.Get(localTeam, targetTeam) == World.Relations.Enemy
                          || PlayerRelations.WarCountdown(localTeam, targetTeam) > 0;

                summary.text = atWar
                    ? ("Ask for " + what + " to end the war. They may accept or refuse.")
                    : ("Ask for " + what + ". They may accept or refuse.");
            }
        }

        private static void Tint(Button b, bool selected)
        {
            if (b == null) return;
            Image img = b.GetComponent<Image>();
            if (img != null) img.color = selected ? cChosen : cButton;
        }

        // ---- small UI builders ----------------------------------------------------------
        //
        // Deliberately minimal. This is the only screen in the mod without a prefab, so these
        // exist to keep Build() readable rather than to be a general UI toolkit.

        private static GameObject Panel(string name, Transform parent, float w, float h)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(w, h);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;

            Image img = go.GetComponent<Image>();
            img.color = cPanel;

            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = cBorder;
            outline.effectDistance = new Vector2(2f, -2f);

            go.transform.SetAsLastSibling();   // over the diplomacy window, not behind it
            return go;
        }

        private static TextMeshProUGUI Label(Transform parent, string text, float size,
                                             FontStyles style, float x, float y, float w, float h)
        {
            GameObject go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);

            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.fontStyle = style;
            tmp.color = cText;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static Button MakeButton(Transform parent, string text, float x, float y,
                                         float w, float h, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);

            go.GetComponent<Image>().color = cButton;

            Button b = go.GetComponent<Button>();
            b.onClick.AddListener(onClick);

            TextMeshProUGUI tmp = Label(go.transform, text, 15f, FontStyles.Normal, 0f, 0f, w, h);
            tmp.enableWordWrapping = false;

            return b;
        }
    }
}
