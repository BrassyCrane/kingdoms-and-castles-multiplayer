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
    /// Built from the bundle's pickerui prefab. The prefab holds one hidden option button for
    /// resources and one for amounts, and this clones them from <see cref="PlayerRelations.Demandable"/>,
    /// so a resource a game update adds still shows up here without new art.
    ///
    /// It sits over the diplomacy window and closes on any choice or on Escape.
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

        /// <summary>
        /// True when the picker was opened to GIVE rather than to ask.
        ///
        /// The grid is identical either way, so the window is shared rather than duplicated; only
        /// its heading and the two action buttons change, and the buttons read this at click time
        /// so one set of handlers serves both.
        /// </summary>
        private static bool aidMode;

        private static TextMeshProUGUI titleLabel;
        private static Button primaryButton, secondaryButton;

        public static bool IsOpen { get { return root != null && root.activeSelf; } }

        /// <summary>Opens the picker to demand from one target kingdom.</summary>
        public static void Open(int fromTeam, int toTeam)
        {
            Open(fromTeam, toTeam, false);
        }

        /// <summary>Opens the picker, either to demand from a kingdom or to send it aid.</summary>
        public static void Open(int fromTeam, int toTeam, bool aid)
        {
            localTeam = fromTeam;
            targetTeam = toTeam;
            aidMode = aid;
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

        /// <summary>Escape closes it. Called every frame from Main.Update, beside the other popups.</summary>
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
            titleLabel = null;
            primaryButton = secondaryButton = null;
            resourceButtons.Clear();
            amountButtons.Clear();
        }

        /// <summary>
        /// Builds the picker once. Ordered just under the alliance popup, so a request that
        /// arrives while this is open still lands on top.
        /// </summary>
        private static void Build()
        {
            root = Popup.Create("ResourcePicker", LobbyPrefabs.Picker, 5090);
            if (root == null) return;

            titleLabel = Popup.Find<TextMeshProUGUI>(root, "Window/Title");
            summary = Popup.Find<TextMeshProUGUI>(root, "Window/Summary");

            resourceButtons.Clear();
            GameObject resourceTemplate = Popup.Find<Transform>(root, "Window/Resources/Option").gameObject;
            foreach (FreeResourceType t in PlayerRelations.Demandable)
            {
                FreeResourceType captured = t;
                resourceButtons.Add(Popup.AddOption(resourceTemplate, PlayerRelations.ResourceLabel(t),
                                                    delegate { chosen = captured; Refresh(); }));
            }

            amountButtons.Clear();
            GameObject amountTemplate = Popup.Find<Transform>(root, "Window/Amounts/Option").gameObject;
            foreach (int a in Amounts)
            {
                int captured = a;
                amountButtons.Add(Popup.AddOption(amountTemplate, a.ToString(),
                                                  delegate { chosenAmount = captured; Refresh(); }));
            }

            // Both handlers read aidMode when they are CLICKED rather than when they are built,
            // so the same two buttons serve a demand and a gift and simply swap which is which.
            primaryButton = Popup.Find<Button>(root, "Window/Primary");
            Popup.OnClick(primaryButton, delegate
            {
                PlayerRelations.Send(localTeam, targetTeam,
                    aidMode ? Net.Messages.DealKind.Offer : Net.Messages.DealKind.Demand,
                    chosenAmount, chosen);
                Close();
            });

            secondaryButton = Popup.Find<Button>(root, "Window/Secondary");
            Popup.OnClick(secondaryButton, delegate
            {
                PlayerRelations.Send(localTeam, targetTeam,
                    aidMode ? Net.Messages.DealKind.Demand : Net.Messages.DealKind.Offer,
                    chosenAmount, chosen);
                Close();
            });

            Popup.OnClick(Popup.Find<Button>(root, "Window/Cancel"), Close);
        }

        /// <summary>Repaints the selection highlight and the sentence under it.</summary>
        private static void Refresh()
        {
            // Retitled per opening, because the same grid means two opposite things and the only
            // thing telling them apart is the wording.
            string target = Main.KingdomNameForTeam(targetTeam);
            if (string.IsNullOrWhiteSpace(target)) target = "another kingdom";
            if (titleLabel != null)
                titleLabel.text = aidMode ? "Send aid to " + target : "Demand from " + target;
            Popup.SetLabel(primaryButton, aidMode ? "Send it" : "Demand it");
            Popup.SetLabel(secondaryButton, aidMode ? "Demand it instead" : "Offer it instead");

            FreeResourceType[] types = PlayerRelations.Demandable;
            for (int i = 0; i < resourceButtons.Count && i < types.Length; i++)
                Popup.Select(resourceButtons[i], types[i] == chosen);

            for (int i = 0; i < amountButtons.Count && i < Amounts.Length; i++)
                Popup.Select(amountButtons[i], Amounts[i] == chosenAmount);

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
    }
}
