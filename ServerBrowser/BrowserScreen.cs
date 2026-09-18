using KaCMultiplayer.Lobby;
using KaCMultiplayer.Net;
using Harmony;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace KaCMultiplayer
{
    public class BrowserScreen : MonoBehaviour
    {
        // The two mod screens, and the scroll containers inside them that get rows added.
        //
        // Static because the screens outlive any one component: SceneLoaded rebuilds them on
        // every scene change and everything else reaches them through these. ForgetScreens()
        // below is what keeps that honest.
        public static GameObject serverBrowserRef;
        public static Transform serverBrowserContentRef;

        public static GameObject serverLobbyRef;
        public static Transform serverLobbyPlayerRef;
        public static Transform serverLobbyChatRef;

        /// <summary>Rows currently in the server list, so a refresh can clear them.</summary>
        public static List<GameObject> ServerEntries = new List<GameObject>();

        private void Start()
        {
            StartCoroutine(LobbyHeartbeat());
        }

        IEnumerator LobbyHeartbeat()
        {
            while (true)
            {
                // Host: publish our Steam lobby so it appears in other players' browsers.
                try
                {
                    if (NetHost.IsRunning && SteamLobby.CurrentLobby.IsValid())
                    {
                        SteamMatchmaking.SetLobbyData(SteamLobby.CurrentLobby, "kcm_mp", "1");
                        string nm = (LobbySettings.Current != null && !string.IsNullOrWhiteSpace(LobbySettings.Current.ServerName))
                            ? LobbySettings.Current.ServerName : (SteamFriends.GetPersonaName() + "'s Server");
                        SteamMatchmaking.SetLobbyData(SteamLobby.CurrentLobby, "name", nm);
                        SteamMatchmaking.SetLobbyData(SteamLobby.CurrentLobby, "difficulty",
                            GameDifficultyExtensions.Label(LobbySettings.Current.Difficulty));
                        // Publish lock state so a joining client knows to prompt for a password BEFORE
                        // connecting (the actual password is never published, it's validated host-side).
                        SteamMatchmaking.SetLobbyData(SteamLobby.CurrentLobby, "locked",
                            LobbySettings.Current.Locked ? "1" : "0");

                        // A 32x32 picture of the map, so browsers can see the world before
                        // committing to a join and a save download. Cached against the seed, so
                        // this heartbeat does not re-walk every cell each tick.
                        // The key covers everything that changes the generated map. Size and
                        // rivers regenerate it while keeping the same seed, so seed alone would
                        // publish a stale picture.
                        string mapKey = string.Concat(
                            LobbySettings.Current.WorldSeed, "|",
                            LobbySettings.Current.WorldSize.ToString(), "|",
                            LobbySettings.Current.WorldRivers.ToString(), "|",
                            LobbySettings.Current.WorldType.ToString());
                        string thumb = MapThumbnail.ForKey(mapKey);
                        if (!string.IsNullOrEmpty(thumb))
                            SteamMatchmaking.SetLobbyData(SteamLobby.CurrentLobby,
                                MapThumbnail.LobbyDataKey, thumb);
                    }
                }
                catch (System.Exception e) { Main.helper.Log("Lobby publish error: " + e.Message); }

                if (serverBrowserRef != null && serverBrowserRef.activeInHierarchy)
                {
                    // Query Steam for multiplayer lobbies (no external backend needed).
                    SteamMatchmaking.AddRequestLobbyListStringFilter("kcm_mp", "1", ELobbyComparison.k_ELobbyComparisonEqual);
                    SteamAPICall_t listCall = SteamMatchmaking.RequestLobbyList();

                    bool lobbyListDone = false;
                    int matchingLobbies = 0;
                    CallResult<LobbyMatchList_t> listResult = CallResult<LobbyMatchList_t>.Create((res, fail) =>
                    {
                        if (!fail) matchingLobbies = (int)res.m_nLobbiesMatching;
                        lobbyListDone = true;
                    });
                    listResult.Set(listCall);

                    float listWaited = 0f;
                    while (!lobbyListDone && listWaited < 5f) { listWaited += Time.unscaledDeltaTime; yield return null; }

                    DestroyServerEntries();

                    for (int li = 0; li < matchingLobbies; li++)
                    {
                        CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(li);
                        CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobby);

                        GameObject entry = Instantiate(LobbyPrefabs.ServerEntry, serverBrowserContentRef);
                        var s = entry.AddComponent<ServerRow>();

                        string lobbyName = SteamMatchmaking.GetLobbyData(lobby, "name");
                        s.Name = string.IsNullOrEmpty(lobbyName) ? "Multiplayer Server" : lobbyName;
                        s.Host = SteamFriends.GetFriendPersonaName(owner);
                        int memberLimit = SteamMatchmaking.GetLobbyMemberLimit(lobby);
                        s.MaxPlayers = memberLimit > 0 ? memberLimit : 2;
                        // The host publishes "locked" alongside name and difficulty, and the
                        // padlock in the list reads it. Display only, SteamLobby.HandleLobbyEntered
                        // reads the same key and does the prompting, but a list that shows every
                        // server as open is worse than no padlock at all.
                        s.Locked = SteamMatchmaking.GetLobbyData(lobby, "locked") == "1";
                        s.PlayerCount = SteamMatchmaking.GetNumLobbyMembers(lobby);
                        s.Difficulty = SteamMatchmaking.GetLobbyData(lobby, "difficulty");
                        s.PlayerId = owner.ToString();
                        s.LobbyId = lobby.m_SteamID;
                        s.MapThumb = SteamMatchmaking.GetLobbyData(lobby, MapThumbnail.LobbyDataKey);

                        ServerEntries.Add(entry);
                    }
                }

                yield return new WaitForSecondsRealtime(2.0f);
            }
        }

        // Prefab lookup helpers. Both screens are instantiated from the asset bundle, which is
        // built separately from this file, so a renamed node in GeneratePrefabs.cs surfaces
        // here and nowhere else. Naming the path in the exception turns that from a bare
        // NullReferenceException into something that says which node moved.

        private static Transform Node(GameObject root, string path)
        {
            Transform t = root.transform.Find(path);
            if (t == null)
                throw new InvalidOperationException(
                    $"prefab '{root.name}' has no node '{path}', bundle and BrowserScreen are out of sync");
            return t;
        }

        private static T Bind<T>(GameObject root, string path) where T : Component
        {
            T c = Node(root, path).GetComponent<T>();
            if (c == null)
                throw new InvalidOperationException(
                    $"prefab '{root.name}' node '{path}' has no {typeof(T).Name}");
            return c;
        }

        /// <summary>
        /// Clears the server list. Called before every refresh, so the rows are rebuilt from the
        /// current lobby results rather than appended to the previous ones.
        /// </summary>
        public static void DestroyServerEntries()
        {
            ServerEntries.ForEach(Destroy);
            ServerEntries.Clear();
        }

        public static Transform ModCanvas { get; set; }

        private void SceneLoaded(KCModHelper helper)
        {
            Main.helper.Log("Serverbrowser scene loaded");

            // A local mod loads earlier than a workshop one, early enough that this can run before
            // the main menu scene has built its UI. There is nothing to graft the browser onto yet,
            // and MenuUi.Find would hand back null for every lookup below.
            //
            // Returning is safe rather than merely quiet: the mod loader calls SceneLoaded on EVERY
            // scene load, so the menu scene gets its own call, and that is the one that finds a UI.
            // The teardown below has nothing to tear down on this pass either, since no screens
            // were built.
            if (MenuUi.Root == null)
            {
                Main.helper.Log("Serverbrowser: the main menu UI is not up yet, deferring to the next scene load");
                return;
            }

            // The mod loader calls SceneLoaded on EVERY scene load, so starting a game and coming
            // back to the menu runs this again. Tear the previous generation down first.
            //
            // Building another set without destroying the first leaves two complete copies of
            // the mod's screens in the scene, while the statics below point at only the newest.
            // The player then looks at one lobby while the code drives another: banners stop
            // refreshing, the map preview freezes, and settings edits never reach clients, with
            // nothing logged, because every individual reference is still valid.
            if (ModCanvas != null)
            {
                Main.helper.Log("Replacing the previous mod UI canvas");
                try { Destroy(ModCanvas.gameObject); }
                catch (Exception e) { Main.helper.Log("Destroying previous mod canvas: " + e.Message); }
            }

            ModCanvas = null;
            serverBrowserRef = null;
            serverBrowserContentRef = null;
            serverLobbyRef = null;
            serverLobbyPlayerRef = null;
            serverLobbyChatRef = null;

            // Row lists and cached lobby widgets belonged to the screens just destroyed.
            try { LobbyView.ClearPlayers(); LobbyView.ClearChat(); }
            catch (Exception e) { Main.helper.Log("Clearing lobby views: " + e.Message); }
            try { LobbyScreen.ForgetCachedUi(); }
            catch (Exception e) { Main.helper.Log("Resetting lobby UI cache: " + e.Message); }

            try
            {
                GameObject modCanvas = Instantiate(MenuUi.Find("TopLevelUICanvas").gameObject);

                for (int i = 0; i < modCanvas.transform.childCount; i++)
                    Destroy(modCanvas.transform.GetChild(i).gameObject);

                modCanvas.name = "ModCanvas";
                modCanvas.transform.SetParent(MenuUi.Root);

                ModCanvas = modCanvas.transform;

                // The prefabs are authored against a fixed canvas width, and this canvas is a
                // copy of the game's own - so its scaler, not ours, decides how many units wide
                // the screen is. Log it: if the prefab was built for a different width, every
                // absolute position in it is wrong by that ratio, which reads as a layout bug
                // rather than a scaling one. Look for "[canvas]" in output.txt.
                try
                {
                    var cv = modCanvas.GetComponent<Canvas>();
                    var sc = modCanvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                    var rt = modCanvas.GetComponent<RectTransform>();
                    Main.helper.Log(string.Format(
                        "[canvas] screen={0}x{1} scaleFactor={2} rect={3}x{4}",
                        Screen.width, Screen.height,
                        cv != null ? cv.scaleFactor.ToString("0.####") : "?",
                        rt != null ? rt.rect.width.ToString("0.##") : "?",
                        rt != null ? rt.rect.height.ToString("0.##") : "?"));
                    if (sc != null)
                        Main.helper.Log(string.Format(
                            "[canvas] scaler mode={0} reference={1}x{2} matchMode={3} match={4} scaleFactor={5}",
                            sc.uiScaleMode, sc.referenceResolution.x, sc.referenceResolution.y,
                            sc.screenMatchMode, sc.matchWidthOrHeight.ToString("0.##"),
                            sc.scaleFactor.ToString("0.####")));
                    else
                        Main.helper.Log("[canvas] no CanvasScaler - canvas is unscaled pixels");
                }
                catch (System.Exception e) { Main.helper.Log("[canvas] probe failed: " + e.Message); }

                serverBrowserRef = GameObject.Instantiate(LobbyPrefabs.BrowserScreen, ModCanvas.transform);
                CanvasFit.Attach(serverBrowserRef);
                serverBrowserRef.SetActive(false);
                serverBrowserContentRef = Node(serverBrowserRef, "Container/Scroll View/Viewport/Content");

                // The prefab carries a player-name prompt the mod does not use; the name comes
                // from Steam. Hidden rather than removed so the bundle layout stays stable.
                Node(serverBrowserRef, "Container/PlayerName").gameObject.SetActive(false);

                serverLobbyRef = GameObject.Instantiate(LobbyPrefabs.ServerLobby, ModCanvas.transform);
                CanvasFit.Attach(serverLobbyRef);
                serverLobbyPlayerRef = Node(serverLobbyRef, "Container/PlayerList/Viewport/Content");
                serverLobbyChatRef = Node(serverLobbyRef, "Container/PlayerChat/Viewport/Content");
                serverLobbyRef.SetActive(false);

                var lobbyScript = serverLobbyRef.GetComponent<LobbyScreen>();
                if (lobbyScript == null)
                    lobbyScript = serverLobbyRef.AddComponent<LobbyScreen>();

                OnClick(serverBrowserRef, "Container/Create", "create lobby",
                    () => SteamLobby.Active.CreateLobby());

                OnClick(serverBrowserRef, "Container/Back", "leave browser",
                    () => Main.TransitionTo(MenuState.Menu));

                OnClick(serverLobbyRef, "Container/Back", "leave lobby", () =>
                {
                    SteamLobby.Active.LeaveLobby();
                    SteamLobby.loadingSave = false;
                });

                // Loading takes the same route as hosting, Steam lobby, then server, but with
                // loadingSave set, which sends the host to the game's save picker instead of the
                // name-and-banner screen.
                Button load = Bind<Button>(serverBrowserRef, "Container/Load");
                load.interactable = true;

                TMPro.TextMeshProUGUI loadLabel = load.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (loadLabel != null) loadLabel.text = "Load";

                OnClick(load, "load lobby", () => SteamLobby.Active.CreateLobby(true));
            }
            catch (Exception ex)
            {
                Main.LogEx("wiring the lobby screens", ex);
            }
        }

        /// <summary>
        /// Wires a button in <paramref name="root"/>'s prefab to <paramref name="action"/>, with
        /// the click sound and an exception guard around it.
        ///
        /// The guard matters more than it looks: an exception escaping a Unity click handler is
        /// swallowed by the event system, so a broken button does nothing at all and reports
        /// nothing. <paramref name="what"/> names it in the log when that happens.
        /// </summary>
        private static void OnClick(GameObject root, string path, string what, Action action)
        {
            OnClick(Bind<Button>(root, path), what, action);
        }

        private static void OnClick(Button button, string what, Action action)
        {
            button.onClick.AddListener(() =>
            {
                try
                {
                    SfxSystem.PlayUiSelect();
                    action();
                }
                catch (Exception ex)
                {
                    Main.LogEx(what, ex);
                }
            });
        }

        // Required by the mod loader: it attaches this component because the type declares the
        // loader's hooks, and that attachment is the only reason Update runs on it. An empty body
        // is fine; removing the method is not.
        private void Preload(KCModHelper helper)
        {
        }
    }
}
