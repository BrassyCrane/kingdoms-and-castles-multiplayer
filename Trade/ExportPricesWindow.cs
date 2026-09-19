using System;
using System.Collections.Generic;
using KaCMultiplayer.Net;
using TMPro;
using UnityEngine;
using KaCMultiplayer.Lobby;
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

        // The popup's canvas, from the bundle's exportpricesui prefab.
        private static GameObject root;
        private static int localTeam;

        private static readonly List<FreeResourceType> rows = new List<FreeResourceType>();
        private static readonly List<TextMeshProUGUI> priceLabels = new List<TextMeshProUGUI>();
        private static readonly List<Button> holdButtons = new List<Button>();

        private static readonly Color cText = Color.white;
        private static readonly Color cMuted = new Color(0.49f, 0.53f, 0.58f);

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
            holdButtons.Clear();

            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
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

                bool selling = ExportPrices.ForSale(localTeam, type);
                label.text = selling ? ExportPrices.PriceFor(localTeam, type) + "g" : "Not for sale";
                label.color = selling ? cText : cMuted;
                label.fontSize = selling ? 20f : 16f;
                if (i < holdButtons.Count) Popup.SetLabel(holdButtons[i], selling ? "Hold" : "Sell");
            }
        }

        // ---- building ------------------------------------------------------

        /// <summary>
        /// Builds the window once, one row per tradeable resource, cloned from the prefab's
        /// hidden row so a resource the game adds later gets a row without new art.
        /// </summary>
        private static void Build()
        {
            if (root != null) return;

            try
            {
                root = Popup.Create("KcmExportPrices", LobbyPrefabs.ExportPrices, SortingOrder);
                if (root == null) return;

                rows.Clear();
                priceLabels.Clear();
                holdButtons.Clear();

                string kingdom = Main.KingdomNameForTeam(localTeam);
                Popup.Find<TextMeshProUGUI>(root, "Window/Title").text =
                    string.IsNullOrEmpty(kingdom) ? "Export Prices" : kingdom + " Export Prices";

                GameObject template = Popup.Find<Transform>(root, "Window/List/Viewport/Content/Row").gameObject;

                // Everything tradeable except gold, which is the unit of account.
                foreach (FreeResourceType type in PlayerRelations.Demandable)
                {
                    if (type == FreeResourceType.Gold) continue;

                    GameObject row = UnityEngine.Object.Instantiate(template, template.transform.parent);
                    row.SetActive(true);
                    Transform r = row.transform;

                    r.Find("Name").GetComponent<TextMeshProUGUI>().text = PlayerRelations.ResourceLabel(type);

                    // Captured per row, which is the whole reason this is a local: a loop variable
                    // shared by every handler would leave all the buttons editing the last resource.
                    FreeResourceType captured = type;
                    Popup.OnClick(r.Find("Minus10").GetComponent<Button>(), delegate { Step(captured, -10); });
                    Popup.OnClick(r.Find("Minus").GetComponent<Button>(), delegate { Step(captured, -1); });
                    Popup.OnClick(r.Find("Plus").GetComponent<Button>(), delegate { Step(captured, 1); });
                    Popup.OnClick(r.Find("Plus10").GetComponent<Button>(), delegate { Step(captured, 10); });

                    Button hold = r.Find("Hold").GetComponent<Button>();
                    Popup.OnClick(hold, delegate { Withhold(captured); });

                    rows.Add(type);
                    priceLabels.Add(r.Find("Price").GetComponent<TextMeshProUGUI>());
                    holdButtons.Add(hold);
                }

                Popup.OnClick(Popup.Find<Button>(root, "Window/Close"), Close);
            }
            catch (Exception e)
            {
                NetLog.Error("building the export price window", e);
                Reset();
            }
        }
    }
}
