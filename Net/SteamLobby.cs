using KaCMultiplayer.Lobby;
using KaCMultiplayer.LoadSaveOverrides;
using Steamworks;
using System;
using UnityEngine;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// The Steam side of hosting and joining: creating the lobby, reacting to Steam's lobby
    /// callbacks, and tearing everything down again.
    ///
    /// Steam lobbies do the discovery here, there is no backend of our own. Hosting creates a
    /// public lobby and the browser publishes what it needs into its lobby data; joining enters
    /// the lobby, reads the host's Steam id off it, and connects to that.
    ///
    /// One <see cref="MonoBehaviour"/> because Steamworks callbacks need a live object to keep
    /// their <see cref="Callback{T}"/> handles alive. Let those go out of scope and Steam stops
    /// calling back, silently.
    /// </summary>
    public class SteamLobby : MonoBehaviour
    {
        private static SteamLobby active;

        /// <summary>
        /// The live instance, or null before the scene has built one.
        ///
        /// A second one is destroyed rather than allowed to replace the first: its callbacks
        /// would be registered alongside the first one's, and every lobby event would then be
        /// handled twice.
        /// </summary>
        internal static SteamLobby Active
        {
            get { return active; }
        }

        // Held as fields on purpose. Steamworks keeps only a weak grip on these, so a callback
        // that is not stored somewhere stops firing once it is collected.
        private Callback<LobbyCreated_t> onCreated;
        private Callback<GameLobbyJoinRequested_t> onJoinRequested;
        private Callback<LobbyEnter_t> onEntered;

        private CSteamID lobby;

        /// <summary>The lobby we are hosting, or <c>Nil</c> when not hosting.</summary>
        public static CSteamID CurrentLobby
        {
            get { return active != null ? active.lobby : CSteamID.Nil; }
        }

        /// <summary>
        /// Set while the session being started is restoring a save rather than generating a
        /// world. The handshake reads it to decide whether the host goes to the save picker or
        /// to the name-and-banner screen.
        /// </summary>
        public static bool loadingSave = false;

        private void Awake()
        {
            if (active != null && active != this)
            {
                Main.helper.Log("a second Steam lobby manager was created; destroying it");
                Destroy(this);
                return;
            }

            active = this;
        }

        private void Start()
        {
            if (!SteamBootstrap.Initialized)
            {
                Main.helper.Log("Steam is not initialised; multiplayer is unavailable this session");
                return;
            }

            onCreated = Callback<LobbyCreated_t>.Create(HandleLobbyCreated);
            onJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(HandleJoinRequested);
            onEntered = Callback<LobbyEnter_t>.Create(HandleLobbyEntered);
        }

        /// <summary>
        /// Hosts a new session. <paramref name="restoringSave"/> routes the host to the save
        /// picker instead of world generation.
        /// </summary>
        internal void CreateLobby(bool restoringSave = false)
        {
            // Any previous session is torn down first. A disconnect leaves the server, the Steam
            // lobby and the player registry in a half-live state, and hosting on top of that
            // fails in ways that persist until the game is restarted.
            ResetNetworkState();

            loadingSave = restoringSave;

            // An automated run must not advertise itself to strangers. A real player found and
            // tried to join one of these test lobbies, and only the host's own "game in progress
            // and they have no kingdom here" guard turned them away. That guard is not the place
            // to rely on: the lobby should never have been listed. Private lobbies are invite-only
            // and do not appear in the browser, so a test run is invisible while a player's own
            // hosting is unchanged.
            ELobbyType visibility = (Main.DevTestBuild && Dev.AutoTest.Enabled)
                ? ELobbyType.k_ELobbyTypePrivate
                : ELobbyType.k_ELobbyTypePublic;

            // Deliberately larger than any real session. Steam's own cap is not the seat limit,
            // that is LobbySettings.MaxPlayers, clamped to the island count, and enforced by the
            // host when a client actually connects.
            SteamMatchmaking.CreateLobby(visibility, 25);
        }

        internal void JoinLobby(ulong id)
        {
            SteamMatchmaking.JoinLobby(new CSteamID(id));
        }

        public void LeaveLobby()
        {
            ResetNetworkState();
            Main.TransitionTo(MenuState.BrowserScreen);
        }

        /// <summary>
        /// Returns every piece of session state to its pre-session condition, so the next host
        /// or join starts clean.
        ///
        /// Each step is attempted independently. They are all "put this back how it was", and
        /// one failing is never a reason to skip the rest, a half-reset session is exactly the
        /// state this exists to get out of.
        /// </summary>
        public static void ResetNetworkState()
        {
            Attempt("stop the server", delegate
            {
                if (NetHost.server != null && NetHost.server.IsRunning) NetHost.server.Stop();
            });

            Attempt("disconnect the client", delegate
            {
                if (NetClient.client != null && NetClient.client.IsConnected) NetClient.client.Disconnect();
            });

            // Published combat state is per-session: army guids do not survive into the next game,
            // and a stale entry would suppress the first real report for a reused guid.
            Attempt("clear published combat state", delegate
            {
                KaCMultiplayer.Combat.CombatSync.Reset();
                KaCMultiplayer.Combat.FrozenKingdoms.Reset();
                KaCMultiplayer.Combat.ArmyPositionSync.Reset();
                KaCMultiplayer.Combat.DragonFlightSync.Reset();
                StreamerEffectSync.Reset();
                Main.BuildingCompleteBuildHook.Reset();
                InGameChat.Reset();
                KaCMultiplayer.Lobby.DiplomacyWindow.Reset();
                KaCMultiplayer.Trade.ExportPrices.Reset();
                KaCMultiplayer.Net.KingdomMirror.Reset();
                KaCMultiplayer.Trade.ExportPricesWindow.Reset();

                KaCMultiplayer.Lobby.DealRequestWindow.Reset();
                KaCMultiplayer.Lobby.DealNoticeWindow.Reset();
                KaCMultiplayer.Lobby.AllianceRequestWindow.Reset();
                KaCMultiplayer.Lobby.ResourcePicker.Reset();
            });

            Attempt("leave the Steam lobby", delegate
            {
                if (active == null || !active.lobby.IsValid()) return;

                SteamMatchmaking.LeaveLobby(active.lobby);
                active.lobby = CSteamID.Nil;
            });

            // Each remote participant is a second Player living on a "Client Player (…)" GameObject
            // the mod created (SessionPlayer.BuildRemotePlayer), and that object's buildingContainer
            // holds their whole kingdom. Clearing kCPlayers below only drops the dictionary
            // references, the GameObjects, and every building parented under them, stay in the scene
            // and reappear in the next game ("I placed a building, exited, started a new game and it
            // was still there"). Destroy them here first. Never the local player: it reuses the
            // game's own Player.inst.gameObject, which the game owns, resets and reuses itself.
            Attempt("destroy remote kingdoms", delegate
            {
                Player local = Player.inst;
                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == local) continue;
                    if (local != null && kp.gameObject != null && kp.gameObject == local.gameObject) continue;

                    // THE BUILDINGS GO TOO, and they are not children of the kingdom object.
                    //
                    // Player.Reset creates buildingContainer as its own ROOT GameObject named
                    // "Buildings" and every structure is parented under that, not under the Player.
                    // So destroying the kingdom on its own removed the bookkeeping and left the
                    // town standing in the scene, with nothing referencing it and nothing left to
                    // clean it up.
                    //
                    // What that looked like: load a save, back out without pressing Start, start a
                    // fresh kingdom, and the previous save's castle and houses were still sitting
                    // on the new map. It survived generating a whole new world, because world
                    // generation has no idea these objects exist.
                    if (kp.inst != null && kp.inst.buildingContainer != null)
                        UnityEngine.Object.Destroy(kp.inst.buildingContainer);

                    // AND THE PEOPLE, who are a third separate thing again.
                    //
                    // A villager is its own scene object. It is not a child of the kingdom and not
                    // a child of the building container, and Player.Reset only Clear()s the lists
                    // that track them, which drops the bookkeeping and leaves the objects. So a
                    // kingdom torn down without this left its population behind: villagers walking
                    // around the next world, across open water where the island they were standing
                    // on used to be.
                    RemoveVillagers(kp.inst, kp.inst != null ? kp.inst.Workers : null);
                    RemoveVillagers(kp.inst, kp.inst != null ? kp.inst.Homeless : null);

                    if (kp.gameObject != null)
                        UnityEngine.Object.Destroy(kp.gameObject);
                }
            });

            Attempt("clear the player registry", delegate
            {
                Main.kCPlayers.Clear();
                Main.clientSteamIds.Clear();
                LoadIdentity.Clear();
            });

            Attempt("clear the log-once guards", delegate
            {
                Main.ResetPerSessionLogs();
            });

            Attempt("clear the lobby lists", delegate
            {
                LobbyView.ClearPlayers();
                LobbyView.ClearChat();
            });

            // A partial save left in the buffers would be treated as the start of the next
            // transfer and reassembled into nonsense.
            Attempt("reset the save transfer", delegate
            {
                SaveTransfer.Reset();
                BuildingWatcher.Reset();
            });

            // A transfer interrupted mid-load leaves this panel up, where it sits over the menu
            // for the rest of the session.
            Attempt("hide the loading panel", delegate
            {
                if (LobbyScreen.LoadingPanel != null) LobbyScreen.LoadingPanel.SetActive(false);
            });

            loadingSave = false;
        }

        /// <summary>
        /// Takes a departing kingdom's people out of the world.
        ///
        /// A Villager is not a MonoBehaviour and has no GameObject to destroy: it derives from
        /// InstanceSystem.Owner and is drawn by the instance system, with its body, head and legs
        /// as separate transforms. So it cannot simply be Destroyed, and clearing the list that
        /// tracks it only drops the bookkeeping, which is what left a kingdom's population walking
        /// around the next world, out across open water where their island used to be.
        ///
        /// Player.RemovePersonFromWorld is the game's own removal: it takes them off the villager
        /// grid and the landmass, makes them leave home and quit their job, and removes them from
        /// both lists. Held resources go first, because a villager carrying wood when the world
        /// ends leaves the wood behind otherwise.
        ///
        /// Walked backwards, because RemovePersonFromWorld removes from the very list being
        /// iterated.
        /// </summary>
        private static void RemoveVillagers(Player owner, ArrayExt<Villager> people)
        {
            if (owner == null || people == null) return;

            for (int i = people.Count - 1; i >= 0; i--)
            {
                Villager v = people.data[i];
                if (v == null) continue;

                // Per villager, so one awkward case cannot strand the rest of the population.
                try
                {
                    v.DestroyHeldResources();
                    owner.RemovePersonFromWorld(v);
                }
                catch (Exception e)
                {
                    Main.helper.Log("could not remove a departing villager: " + e.Message);
                }
            }

            people.Clear();
        }

        private static void Attempt(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Main.helper.Log("could not " + what + " while resetting: " + e.Message);
            }
        }

        private void HandleLobbyCreated(LobbyCreated_t created)
        {
            if (created.m_eResult != EResult.k_EResultOK)
            {
                Main.helper.Log("Steam refused to create the lobby: " + created.m_eResult);
                return;
            }

            lobby = new CSteamID(created.m_ulSteamIDLobby);

            NetHost.StartServer();
            Main.TransitionTo(MenuState.LobbyScreen);

            try
            {
                // The host joins its own server as an ordinary client, so one code path serves
                // everyone. Its own password goes with it, otherwise setting a password locks
                // the host out of the game they are hosting.
                NetClient.Connect("127.0.0.1",
                    LobbySettings.Current != null ? LobbySettings.Current.Password : null);

                // Every kingdom needs its own island, so the map has to be an island map
                // regardless of what the world settings say.
                World.inst.mapBias = World.MapBias.Island;

                // Size and rivers from the lobby too. Only the bias used to be set here, so the
                // host's first map was built with whatever the world object last held, which is
                // not necessarily what the lobby shows or what a guest will be told to build.
                if (LobbySettings.Current != null)
                {
                    World.inst.mapSize = LobbySettings.Current.WorldSize;
                    World.inst.mapRiverLakes = LobbySettings.Current.WorldRivers;
                }

                World.inst.Generate();

                LobbyScreen.SeedBox.text = World.inst.GetTextSeed();
                LobbyScreen.mapPreviewDirty = true;

                LobbyView.ClearPlayers();
            }
            catch (Exception e)
            {
                Main.LogEx("starting the hosted session", e);
            }
        }

        private void HandleJoinRequested(GameLobbyJoinRequested_t request)
        {
            SteamMatchmaking.JoinLobby(request.m_steamIDLobby);
        }

        private void HandleLobbyEntered(LobbyEnter_t entered)
        {
            // The host enters its own lobby too, and has already started everything.
            if (NetHost.IsRunning) return;

            lobby = new CSteamID(entered.m_ulSteamIDLobby);
            CSteamID host = SteamMatchmaking.GetLobbyOwner(lobby);

            // Ask before connecting when the host published the lobby as locked. The password
            // itself is never published, only the fact that there is one, and the host
            // validates the answer at connect time and rejects a wrong one.
            if (SteamMatchmaking.GetLobbyData(lobby, "locked") == "1")
            {
                string address = host.ToString();
                PasswordPrompt.Request(
                    onSubmit: password => NetClient.Connect(address, password),
                    onCancel: LeaveLobby);
                return;
            }

            NetClient.Connect(host.ToString());
        }
    }
}
