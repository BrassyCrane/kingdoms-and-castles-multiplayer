using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// The diplomacy screen: one row per other kingdom, showing where you stand with them and the
    /// three ways to change it.
    ///
    /// Replaces the temporary Ctrl+Shift+D hotkey, which cycled the relation with EVERY player at
    /// once because there was nowhere to say "this kingdom, this standing". The hotkey now opens
    /// this instead, so the muscle memory survives and the blunt behaviour does not.
    ///
    /// It builds its OWN Canvas rather than parenting under the game's menu UI the way ModalDialog
    /// does. That parent is `mainMenuMode.mainMenuUI`, which is inactive once play starts, so a
    /// window hung under it would be invisible exactly when it is wanted. An own canvas also means
    /// no dependency on the base game's menu hierarchy, which MenuUi itself warns is the thing that
    /// breaks on a game update.
    /// </summary>
    public static class DiplomacyWindow
    {
        /// <summary>Above the game's own UI, so the window is not buried by it.</summary>
        private const int SortingOrder = 5000;

        /// <summary>Frames between refreshes while open, so an incoming change shows up.</summary>
        private const int RefreshEveryFrames = 20;

        private static GameObject canvas;
        private static GameObject root;
        private static Transform content;
        private static int frameCounter;

        private static readonly List<GameObject> rows = new List<GameObject>();

        /// <summary>
        /// What the list last showed, so an unchanged roster is left alone.
        ///
        /// Rebuilding unconditionally would visibly flicker. Unity defers Destroy to the end of the
        /// frame, so freshly instantiated rows sit alongside the doomed ones for the rest of it and
        /// the layout group lays out both, three times a second. Comparing first means the rebuild
        /// only happens when somebody's standing or presence actually changed.
        /// </summary>
        private static string shownSignature;

        public static bool IsOpen { get { return root != null && root.activeSelf; } }

        /// <summary>Whether the screen can be shown at all: needs the prefabs and a live session.</summary>
        public static bool Available
        {
            get
            {
                return LobbyPrefabs.DiplomacyScreen != null
                    && LobbyPrefabs.DiplomacyRow != null
                    && NetClient.client.IsConnected;
            }
        }

        /// <summary>Tears the window down, for a session ending.</summary>
        public static void Reset()
        {
            try
            {
                ClearRows();
                if (canvas != null) UnityEngine.Object.Destroy(canvas);
            }
            catch (Exception ex) { NetLog.Error("tearing down the diplomacy window", ex); }
            finally
            {
                canvas = null;
                root = null;
                content = null;
                shownSignature = null;
            }
        }

        public static void Toggle()
        {
            if (IsOpen) Hide();
            else Show();
        }

        public static void Show()
        {
            if (!Available)
            {
                NetLog.Info("diplomacy screen unavailable (no prefab in this bundle, or no session)");
                return;
            }
            if (!EnsureBuilt()) return;

            root.SetActive(true);
            shownSignature = null;   // force a build on open, whatever was up last time
            Refresh();
        }

        public static void Hide()
        {
            if (root != null) root.SetActive(false);
        }

        /// <summary>
        /// Called every frame from Main.Update. Closes on Escape and refreshes periodically, so a
        /// relation someone else changed appears without the player reopening the window.
        /// </summary>
        public static void Tick()
        {
            if (!IsOpen) return;

            if (Input.GetKeyDown(KeyCode.Escape)) { Hide(); return; }

            // A session can end while the window is up.
            if (!NetClient.client.IsConnected) { Hide(); return; }

            if (++frameCounter < RefreshEveryFrames) return;
            frameCounter = 0;
            Refresh();
        }

        /// <summary>
        /// Creates the canvas and the window once. Returns false having said why if anything the
        /// prefab promised is missing, rather than half-building something that null-refs later.
        /// </summary>
        private static bool EnsureBuilt()
        {
            if (root != null) return true;

            try
            {
                canvas = new GameObject("KcmDiplomacyCanvas",
                    typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));

                Canvas c = canvas.GetComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = SortingOrder;

                CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(UiScale.DesignWidth, UiScale.DesignHeight);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                // Buttons do nothing without one, and the game's own may not be present in every
                // mode. Creating one is harmless when another already exists; Unity uses the first.
                if (UnityEngine.Object.FindObjectOfType<EventSystem>() == null)
                {
                    GameObject es = new GameObject("KcmEventSystem",
                        typeof(EventSystem), typeof(StandaloneInputModule));
                    es.transform.SetParent(canvas.transform, false);
                    NetLog.Info("diplomacy: no EventSystem in the scene, added one");
                }

                root = UnityEngine.Object.Instantiate(LobbyPrefabs.DiplomacyScreen, canvas.transform);

                RectTransform rt = root.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }

                content = root.transform.Find("Window/Container/Scroll View/Viewport/Content");
                Transform closeNode = root.transform.Find("Window/Container/Close");
                Button close = closeNode == null ? null : closeNode.GetComponent<Button>();

                if (content == null || close == null)
                {
                    NetLog.Warn("diplomacy prefab is missing Content or Close; screen unavailable");
                    Reset();
                    return false;
                }

                close.onClick.AddListener(Hide);

                root.SetActive(false);
                return true;
            }
            catch (Exception ex)
            {
                NetLog.Error("building the diplomacy window", ex);
                Reset();
                return false;
            }
        }

        private static void ClearRows()
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null) UnityEngine.Object.Destroy(rows[i]);
            rows.Clear();
        }

        /// <summary>
        /// Rebuilds the list from the current roster.
        ///
        /// Rebuilt wholesale rather than updated in place: the roster changes when players join,
        /// leave or are recreated from a save, and reconciling rows against that is more code than
        /// it saves for a list that is never more than a handful long.
        /// </summary>
        private static void Refresh()
        {
            if (content == null) return;

            try
            {
                int localTeam = LocalTeam();
                if (localTeam == int.MinValue) return;

                // Who should be listed, and how they stand, in a stable order.
                List<SessionPlayer> peers = new List<SessionPlayer>();
                foreach (SessionPlayer peer in Main.kCPlayers.Values)
                {
                    if (peer == null || peer.inst == null || peer.inst.PlayerLandmassOwner == null) continue;

                    int otherTeam = peer.inst.PlayerLandmassOwner.teamId;
                    if (otherTeam == localTeam) continue;                       // never yourself
                    if (!PlayerRelations.IsPlayerPair(localTeam, otherTeam)) continue;

                    peers.Add(peer);
                }

                peers.Sort(delegate (SessionPlayer a, SessionPlayer b)
                {
                    return a.inst.PlayerLandmassOwner.teamId.CompareTo(b.inst.PlayerLandmassOwner.teamId);
                });

                // Everything the rows display. If none of it moved, the rows on screen are already
                // right and rebuilding them would only make them blink.
                System.Text.StringBuilder sig = new System.Text.StringBuilder();
                for (int i = 0; i < peers.Count; i++)
                {
                    int otherTeam = peers[i].inst.PlayerLandmassOwner.teamId;
                    sig.Append(otherTeam).Append(':')
                       .Append((int)PlayerRelations.Get(localTeam, otherTeam)).Append(':')
                       .Append(peers[i].isGhost ? '1' : '0').Append(':')
                       .Append(peers[i].kingdomName).Append('|');
                }

                string signature = sig.ToString();
                if (signature == shownSignature) return;
                shownSignature = signature;

                ClearRows();
                for (int i = 0; i < peers.Count; i++)
                    AddRow(peers[i], localTeam, peers[i].inst.PlayerLandmassOwner.teamId);
            }
            catch (Exception ex) { NetLog.Error("refreshing the diplomacy list", ex); }
        }

        private static void AddRow(SessionPlayer peer, int localTeam, int otherTeam)
        {
            GameObject row = UnityEngine.Object.Instantiate(LobbyPrefabs.DiplomacyRow, content);
            rows.Add(row);

            // Kingdom AND player. A kingdom name does not say which of your friends chose it, and
            // with more than two players in a session that stops being obvious at a glance.
            string label = RowName(peer);
            if (peer.isGhost) label += "  (away)";
            SetText(row, "PlayerName", label, Color.white);

            World.Relations now = PlayerRelations.Get(localTeam, otherTeam);
            SetText(row, "Relation", RelationLabel(now), RelationColour(now));

            SetBanner(row, otherTeam);

            // Captured into the closure, so each row commands its own kingdom. Relations apply when
            // the message comes back rather than on click, which is why nothing is written locally
            // here; the periodic refresh picks the new standing up.
            int target = otherTeam;
            Wire(row, "Neutral", delegate { Main.RequestRelationChange(target, World.Relations.Neutral); });
            Wire(row, "War", delegate { Main.RequestRelationChange(target, World.Relations.Enemy); });

            // Only the buttons that mean something right now. The row's Actions group closes up
            // around hidden buttons, so each state shows just its own choices:
            //
            //   neutral   Demand  Send Aid  Ally            War
            //   allied    Demand  Send Aid  Break Alliance
            //   at war    Demand  Send Aid  Make Peace
            bool allied = now == World.Relations.Allies;
            bool atWar = now == World.Relations.Enemy;

            SetActive(row, "Neutral", atWar);
            SetButtonText(row, "Neutral", "Make Peace");

            SetActive(row, "Allies", !atWar);
            SetButtonText(row, "Allies", allied ? "Break Alliance" : "Ally");
            Wire(row, "Allies", delegate
            {
                Main.RequestRelationChange(target, allied ? World.Relations.Neutral : World.Relations.Allies);
            });

            // Hidden while allied as well as while already at war: an ally has to be renounced
            // before they can be fought, so Break Alliance is the only way to that button.
            SetActive(row, "War", !atWar && !allied);

            Wire(row, "Demand", delegate { ResourcePicker.Open(localTeam, target, false); });
            Wire(row, "SendAid", delegate { ResourcePicker.Open(localTeam, target, true); });
        }

        /// <summary>
        /// Sets a BUTTON's caption, whose label is a child rather than the button itself, which is
        /// what separates this from SetText.
        /// </summary>
        private static void SetButtonText(GameObject row, string node, string caption)
        {
            Transform t = Button(row, node);
            if (t == null) return;

            TextMeshProUGUI label = t.GetComponentInChildren<TextMeshProUGUI>();
            if (label == null) return;

            label.text = caption;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
        }

        /// <summary>Shows or hides one of a row's buttons.</summary>
        private static void SetActive(GameObject row, string node, bool visible)
        {
            Transform t = Button(row, node);
            if (t != null) t.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Names a row as "Kingdom  (Steam name)".
        ///
        /// The Steam name is asked of Steam rather than taken from the session, because the name
        /// that travelled in the handshake is whatever the sender happened to be called then, and
        /// it is blank for a kingdom restored from a save whose owner has not reconnected yet.
        ///
        /// Degrades rather than showing nonsense: either part alone when the other is missing, and
        /// one name rather than "Firereach (Firereach)" when they happen to match.
        /// </summary>
        private static string RowName(SessionPlayer peer)
        {
            string kingdom = string.IsNullOrWhiteSpace(peer.kingdomName) ? null : peer.kingdomName.Trim();

            // Shared with the deal and alliance popups, so the three can never disagree about what
            // to call somebody.
            string persona = peer.SteamPersona();
            if (string.IsNullOrWhiteSpace(persona)) persona = null;

            if (string.IsNullOrWhiteSpace(kingdom)) return persona ?? "Unknown kingdom";
            if (string.IsNullOrWhiteSpace(persona) || persona == kingdom) return kingdom;

            return kingdom + "  (" + persona + ")";
        }

        /// <summary>A row button, from the row's Actions group.</summary>
        private static Transform Button(GameObject row, string node)
        {
            return row.transform.Find("Actions/" + node);
        }

        private static void Wire(GameObject row, string node, UnityEngine.Events.UnityAction action)
        {
            Transform t = Button(row, node);
            Button b = t == null ? null : t.GetComponent<Button>();
            if (b == null)
            {
                NetLog.Warn("diplomacy row has no button '" + node + "'");
                return;
            }

            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(action);
        }

        private static void SetText(GameObject row, string node, string text, Color colour)
        {
            Transform t = row.transform.Find(node);
            TextMeshProUGUI tmp = t == null ? null : t.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return;

            tmp.text = text;
            tmp.color = colour;
        }

        /// <summary>
        /// Shows the other kingdom's banner. Skipped silently when the texture is not resolvable:
        /// a remote player's livery is set from their banner choice, which can arrive after they
        /// do, and a missing flag is not worth a warning per refresh.
        /// </summary>
        private static void SetBanner(GameObject row, int teamId)
        {
            try
            {
                Transform t = row.transform.Find("PlayerBanner");
                RawImage img = t == null ? null : t.GetComponent<RawImage>();
                if (img == null) return;

                LandmassOwner owner = World.GetLandmassOwnerByTeamId(teamId);
                if (owner == null || owner.BannerTexture == null) return;

                img.texture = owner.BannerTexture;
            }
            catch { /* cosmetic only */ }
        }

        private static string RelationLabel(World.Relations r)
        {
            if (r == World.Relations.Enemy) return "At war";
            if (r == World.Relations.Allies) return "Allied";
            return "Neutral";
        }

        private static Color RelationColour(World.Relations r)
        {
            if (r == World.Relations.Enemy) return new Color(0.90f, 0.42f, 0.38f);
            if (r == World.Relations.Allies) return new Color(0.47f, 0.80f, 0.45f);
            return new Color(0.79f, 0.82f, 0.85f);
        }

        private static int LocalTeam()
        {
            return (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
        }
    }
}
