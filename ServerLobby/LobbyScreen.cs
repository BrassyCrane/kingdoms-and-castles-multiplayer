using KaCMultiplayer.Lobby;
using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KaCMultiplayer
{
    public class LobbyScreen : MonoBehaviour
    {
        public static ServerRow serverDetails { get; set; }

        public static Transform RosterContent { get; set; }
        public static Transform ChatContent { get; set; }

        public static Button StartButton { get; set; }

        public static TMP_InputField ChatBox { get; set; }
        public static Button SendButton { get; set; }

        public static TMP_InputField NameBox { get; set; }

        public static TMP_InputField SeatsBox { get; set; }
        // Lock state is no longer a control. A server is locked if, and only if, the host typed
        // something into PasswordBox - one field instead of a toggle plus a field that could
        // disagree with each other. See LockedFromPassword().

        // Map bias is not a player choice. Every kingdom needs its own landmass, so
        // World.MapBias.Land caps the server at one player - and Random can roll Land, so it
        // is no safer. Islands is the only valid value, so there is no World Type control and
        // the lobby hard-sets this. LobbySettings.WorldType stays on the wire: clients still
        // apply whatever the host sends, which keeps older/newer hosts interoperable.
        private const World.MapBias MultiplayerMapBias = World.MapBias.Island;

        /// <summary>
        /// Tells a client what it can actually know about the password, which is only whether
        /// one exists.
        ///
        /// The host's placeholder is "blank = anyone can join". On a client's screen that is not
        /// neutral, it asserts the server is open, on a server that may well be locked. And the
        /// field is empty for a client either way, because the password is never sent to them:
        /// it is validated host-side at connect time.
        /// </summary>
        private static void UpdatePasswordPlaceholder()
        {
            try
            {
                if (PasswordBox == null) return;

                var placeholder = PasswordBox.placeholder as TextMeshProUGUI;
                if (placeholder == null) return;

                bool locked = LobbySettings.Current != null && LobbySettings.Current.Locked;
                string wanted = locked ? "•  •  •   (set by the host)" : "no password";

                if (placeholder.text != wanted) placeholder.text = wanted;
            }
            catch (Exception e) { Main.helper.Log("Password placeholder: " + e.Message); }
        }

        private static bool LockedFromPassword()
        {
            return PasswordBox != null && !string.IsNullOrWhiteSpace(PasswordBox.text);
        }
        public static TMP_InputField PasswordBox { get; set; }

        public static TMP_Dropdown DifficultyPicker { get; set; }

        public static TMP_InputField SeedBox { get; set; }
        public static Button RerollButton { get; set; }

        public static TMP_Dropdown SizePicker { get; set; }
        public static TMP_Dropdown RiversPicker { get; set; }

        public static TMP_Dropdown PlacementPicker { get; set; }
        public static Toggle FogToggle { get; set; }

        public static LobbyScreen inst { get; set; }


        public static GameObject LoadingPanel { get; set; }
        public static Image ProgressFill { get; set; }
        public static TextMeshProUGUI ProgressLabel { get; set; }
        public static TextMeshProUGUI StatusLabel { get; set; }

        // --- Map preview: a small runtime-generated minimap of the lobby world. ---
        // Set mapPreviewDirty = true after the world is (re)generated; SyncSettings rebuilds the
        // texture on the next tick once the lobby UI exists.
        public static bool mapPreviewDirty = false;
        private static GameObject mapPreviewObj;
        private static RawImage mapPreviewImage;
        private static AspectRatioFitter mapPreviewFitter;

        /// <summary>
        /// Drops every cached reference into the lobby's UI, so the next lobby rebuilds them.
        ///
        /// Called when the mod's screens are recreated, which happens on every scene load, so
        /// once per return to the main menu. Without this, <see cref="EnsurePreviewImage"/> sees a
        /// non-null <c>mapPreviewObj</c> from the previous generation of screens and returns
        /// early, leaving the new lobby's preview slot permanently empty. Same shape of problem
        /// for the texture and the fitter.
        /// </summary>
        public static void ForgetCachedUi()
        {
            mapPreviewObj = null;
            mapPreviewFitter = null;

            if (mapPreviewImage != null && mapPreviewImage.texture != null)
            {
                // Ours, generated per world. Nothing else will free it.
                try { UnityEngine.Object.Destroy(mapPreviewImage.texture); } catch { }
            }
            mapPreviewImage = null;

            mapPreviewDirty = true;   // the new lobby needs one
        }


        public void Start()
        {
            inst = this;
        }

        // Prefab lookup helpers.
        //
        // Awake wires ~20 controls out of the bundle, and written longhand each one is a
        // transform.Find(...).GetComponent<T>() chain whose only failure mode is a
        // NullReferenceException three frames away from the path that was actually wrong.
        // These name the path in the exception instead, which matters because the bundle and
        // this file are built separately, a renamed node in GeneratePrefabs.cs shows up here
        // and nowhere else.

        private Transform Node(string path)
        {
            Transform t = transform.Find(path);
            if (t == null)
                throw new InvalidOperationException(
                    $"serverlobby prefab has no node '{path}', bundle and LobbyScreen are out of sync");
            return t;
        }

        private T Bind<T>(string path) where T : Component
        {
            T c = Node(path).GetComponent<T>();
            if (c == null)
                throw new InvalidOperationException(
                    $"serverlobby node '{path}' has no {typeof(T).Name}");
            return c;
        }

        private T BindInChildren<T>(string path) where T : Component
        {
            T c = Node(path).GetComponentInChildren<T>();
            if (c == null)
                throw new InvalidOperationException(
                    $"serverlobby node '{path}' has no {typeof(T).Name} in its children");
            return c;
        }

        public void Awake()
        {
            // One line for the whole wiring pass. Tracing each control as it is found prints
            // little more than "name (Type)" per line, and the Bind helpers below already name
            // the exact path in the exception when a node is missing.
            Main.helper.Log("ServerLobby: wiring prefab controls");
            try
            {
                inst = this;

                RosterContent = Node("Container/PlayerList/Viewport/Content");
                ChatContent = Node("Container/PlayerChat/Viewport/Content");
                ChatBox = Bind<TMP_InputField>("Container/ChatInput");
                SendButton = Bind<Button>("Container/SendMessage");
                StartButton = Bind<Button>("Container/Start");

                NameBox = BindInChildren<TMP_InputField>("Container/ServerSettings/ServerName");
                NameBox.text = $"{SteamFriends.GetPersonaName()}'s Server";

                SeatsBox = BindInChildren<TMP_InputField>("Container/ServerSettings/ServerAccess/MaxPlayers");
                // One kingdom per island, so the useful range is small. Restrict the field
                // rather than letting someone type 9999 and be silently corrected later.
                SeatsBox.contentType = TMP_InputField.ContentType.IntegerNumber;
                SeatsBox.characterLimit = 2;
                SeatsBox.text = LobbySettings.MinPlayers.ToString();
                // No lock toggle any more - see LockedFromPassword(). The prefab's caption tells
                // the host that leaving PasswordBox blank leaves the server open.
                PasswordBox = BindInChildren<TMP_InputField>("Container/ServerSettings/ServerAccess/Password");
                // Empty, not " ": empty means "no password" and lets the placeholder show.
                PasswordBox.text = "";

                // Match the width of the server name box above; the prefab's own value is wider.
                // In the rebuilt prefab ServerAccess exactly fills ServerSettings, so both rects
                // already agree and this is a no-op. Left in as a guard.
                try
                {
                    RectTransform pwRect = PasswordBox.GetComponent<RectTransform>();
                    RectTransform snRect = NameBox.GetComponent<RectTransform>();
                    if (pwRect != null && snRect != null)
                    {
                        pwRect.anchorMin = new Vector2(snRect.anchorMin.x, pwRect.anchorMin.y);
                        pwRect.anchorMax = new Vector2(snRect.anchorMax.x, pwRect.anchorMax.y);
                        pwRect.pivot = new Vector2(snRect.pivot.x, pwRect.pivot.y);
                        pwRect.sizeDelta = new Vector2(snRect.sizeDelta.x, pwRect.sizeDelta.y);
                        pwRect.anchoredPosition = new Vector2(snRect.anchoredPosition.x, pwRect.anchoredPosition.y);
                    }
                }
                catch (Exception e) { Main.helper.Log("Password width fix error: " + e.Message); }

                const string settings = "Container/ServerSettings/";
                const string world = settings + "WorldSettings/";

                DifficultyPicker = BindInChildren<TMP_Dropdown>(settings + "Difficulty");
                DifficultyPicker.value = 0;

                SeedBox = Bind<TMP_InputField>(world + "SeedInput");
                SeedBox.text = " ";
                RerollButton = Bind<Button>(world + "NewMap");

                SizePicker = BindInChildren<TMP_Dropdown>(world + "WorldSize");

                RiversPicker = BindInChildren<TMP_Dropdown>(world + "WorldRivers");
                RiversPicker.value = 1;

                FogToggle = Bind<Toggle>(world + "FogOfWarToggle");
                FogToggle.isOn = true;

                PlacementPicker = BindInChildren<TMP_Dropdown>(world + "Placement");
                PlacementPicker.value = 0;

                // TMP_Dropdown's popup will not render under this game's canvas, so the settings
                // dropdowns are driven by Prev/Next arrows instead. See DropdownCycler.
                DropdownCycler.AttachAll(transform);

                PlacementPicker.enabled = true;
                FogToggle.enabled = false;
                FogToggle.gameObject.SetActive(false);

                LoadingPanel = Node("LoadingSave").gameObject;
                ProgressFill = Bind<Image>("LoadingSave/Window/ProgressBar/Fill");
                ProgressLabel = BindInChildren<TextMeshProUGUI>("LoadingSave/Window/ProgressBar");
                StatusLabel = Bind<TextMeshProUGUI>("LoadingSave/Window/StatusText");

                if (!NetHost.IsRunning)
                {
                    // Guests can look but not touch: the host owns every setting, and anything
                    // typed here would be overwritten by the next settings broadcast anyway.
                    LockDown(NameBox, SeatsBox, PasswordBox);
                    LockDown(DifficultyPicker, SizePicker, RiversPicker, PlacementPicker);
                    LockDown(SeedBox);
                    LockDown(RerollButton);

                    UpdatePasswordPlaceholder();

                    StartButton.onClick.RemoveAllListeners();
                    StartButton.GetComponentInChildren<TextMeshProUGUI>().text = "Ready";
                    StartButton.onClick.AddListener(() =>
                    {
                        // The value is a placeholder. The host holds the authoritative ready
                        // state, toggles it, and broadcasts the result back to everyone.
                        NetRouter.Send(new PlayerReadyMessage { IsReady = true });
                    });
                }
                else
                {
                    StartButton.onClick.RemoveAllListeners();
                    StartButton.GetComponentInChildren<TextMeshProUGUI>().text = "Start";
                    StartButton.onClick.AddListener(() =>
                    {
                        // The player registry is filled by the handshake, which completes a
                        // moment after the lobby appears. Starting before then goes through
                        // silently otherwise: the session begins, but the keep-placement loop
                        // below iterates an empty registry and nobody gets a keep, which reads
                        // as the placement setting being ignored.
                        if (Main.kCPlayers.Count == 0)
                        {
                            Main.helper.Log("[lobby] Start ignored, still connecting, no players registered yet.");
                            return;
                        }

                        NetRouter.Broadcast(new SessionStartMessage());

                        if (PlacementPicker.value != 0 || SteamLobby.loadingSave)
                            return;

                        // One landmass each. Shuffle the available indices rather than
                        // re-rolling until a free one turns up, which cannot terminate when
                        // there are more players than islands.
                        List<int> available = new List<int>();
                        for (int i = 0; i < World.inst.NumLandMasses; i++)
                            available.Add(i);

                        for (int i = available.Count - 1; i > 0; i--)
                        {
                            int j = SRand.Range(0, i + 1);
                            int swap = available[i]; available[i] = available[j]; available[j] = swap;
                        }

                        int next = 0;
                        foreach (SessionPlayer kcPlayer in Main.kCPlayers.Values)
                        {
                            if (next >= available.Count)
                            {
                                Main.helper.Log($"[lobby] no island left for {kcPlayer.name}, {World.inst.NumLandMasses} islands, {Main.kCPlayers.Count} players.");
                                break;
                            }

                            int idx = available[next++];
                            Main.helper.Log($"[lobby] placing {kcPlayer.name}'s keep on landmass {idx}");

                            NetRouter.SendTo(new KeepPlaceRandomMessage
                            {
                                LandmassIndex = idx
                            }, kcPlayer.id);
                        }
                    });

                }


                SendButton.onClick.AddListener(SendChatLine);

                RerollButton.onClick.AddListener(() =>
                {
                    // A brand new map. Seed 0 asks the game to pick a random one.
                    if (NetHost.IsRunning) RegenerateWorld(0, "new world button");
                });

                // Once immediately so the lobby is not blank for the first second, then on a
                // timer. InvokeRepeating resolves by name, so this string has to track the
                // method, a rename that misses it fails silently at runtime, not at compile.
                SyncSettings();

                // CancelInvoke first, because InvokeRepeating stacks rather than replaces. If
                // Awake runs a second time on an object that survived, the result is two timers
                // and the settings go out twice a second instead of once. That is what the logs
                // showed, and it is a no-op on the normal single-Awake path.
                CancelInvoke("SyncSettings");
                InvokeRepeating("SyncSettings", 0, 1f);

                // Details can arrive before this prefab is wired: clicking a row in the browser
                // calls SetDetails, and Awake runs afterwards. They are kept in the static
                // serverDetails and shown here, once the controls actually exist.
                ShowDetails();
            }
            catch (Exception ex)
            {
                Main.LogEx("LobbyScreen.Awake", ex);
            }
        }

        /// <summary>
        /// Makes controls inert, greyed out and unresponsive, but still readable.
        ///
        /// Both flags are needed. <c>interactable</c> alone leaves a Selectable able to take
        /// focus, and text fields additionally have to be told to drop the caret, or one that was
        /// already focused keeps accepting keystrokes after being disabled.
        /// </summary>
        private static void LockDown(params Selectable[] controls)
        {
            foreach (Selectable control in controls)
            {
                TMP_InputField field = control as TMP_InputField;
                if (field != null) field.DeactivateInputField();

                control.enabled = false;
                control.interactable = false;
            }
        }

        public void Update()
        {
            // Enter sends the chat line, wherever focus happens to be. Routed through the
            // button's own handler so there is one send path rather than two that can drift.
            if (Input.GetKeyDown(KeyCode.Return))
                SendButton.onClick.Invoke();
        }

        /// <summary>
        /// Puts the typed line on the wire and clears the box.
        ///
        /// Nothing is shown locally here. The host echoes every line back, including our own, and
        /// the receive handler renders it, so the sender sees the same message everyone else
        /// does, in the order the host chose, rather than an optimistic local copy that can end up
        /// out of sequence.
        /// </summary>
        private static void SendChatLine()
        {
            if (ChatBox.text.Length == 0) return;

            NetRouter.Send(new ChatSayMessage
            {
                PlayerName = NetClient.inst.Name,
                Text = ChatBox.text,
            });

            ChatBox.text = "";
        }

        /// <summary>
        /// Keeps the lobby controls and <see cref="LobbySettings.Current"/> in step, once a
        /// second. Which way the data flows depends on who is running: the host owns the
        /// settings and reads them off its own controls, everyone else receives them and
        /// displays what arrived.
        /// </summary>
        public void SyncSettings()
        {
            try
            {
                // Once play begins this tick has nothing left to do, and doing it anyway costs
                // real bandwidth and buries the log.
                //
                // Everything below is lobby work: the controls are unreachable, the Start button
                // has already been pressed, and ApplyWorldSettings refuses to touch difficulty in
                // play mode anyway. The one effect that did survive into the game was the host
                // rebroadcasting its settings on every tick for the whole session, which arrived
                // as roughly 600 logged messages in five minutes. The log is the main way this
                // mod gets debugged, so drowning it is a real cost.
                //
                // Joining or reconnecting mid-game is unaffected: the host sends the settings
                // explicitly in the join catch-up sequence (see SessionHandlers), so a newcomer
                // never depended on catching one of these ticks. Password state is read off the
                // controls here, so it now freezes at whatever the lobby settled on, which is
                // correct, the password cannot be edited once the lobby screen is gone.
                if (GameState.inst != null && GameState.inst.IsPlayMode()) return;

                if (NetHost.IsRunning)
                    ReadSettingsFromControls();
                else
                    ShowSettingsFromHost();

                // Rebuild the map preview after a (re)generation, once the lobby UI exists.
                if (mapPreviewDirty && BrowserScreen.serverLobbyRef != null)
                {
                    RefreshMapPreview();
                    mapPreviewDirty = false;
                }
            }
            catch (Exception ex)
            {
                Main.LogEx("lobby settings tick", ex);
            }
        }

        /// <summary>Guest side: display whatever the host last sent.</summary>
        private void ShowSettingsFromHost()
        {
            LobbySettings s = LobbySettings.Current;

            NameBox.text = s.ServerName;
            SeatsBox.text = s.MaxPlayers.ToString();
            SeedBox.text = s.WorldSeed;
            DifficultyPicker.value = s.Difficulty;
            SizePicker.value = (int)s.WorldSize;
            RiversPicker.value = (int)s.WorldRivers;
            PlacementPicker.value = s.PlacementType;

            // Here rather than in Awake: at Awake the host's settings have not arrived, so
            // Locked is still false and every client is told "no password" even for a locked
            // server. On this tick the real settings are in.
            UpdatePasswordPlaceholder();

            ApplyWorldSettings(s);
        }

        /// <summary>Host side: the controls are the source of truth, so read them and publish.</summary>
        private void ReadSettingsFromControls()
        {
            LobbySettings s = LobbySettings.Current;

            // Loading a save takes the map and difficulty from the save file, so those controls
            // must not be editable.
            bool canEditWorld = !SteamLobby.loadingSave;
            SeedBox.interactable = canEditWorld;
            SizePicker.interactable = canEditWorld;
            RiversPicker.interactable = canEditWorld;
            PlacementPicker.interactable = canEditWorld;
            DifficultyPicker.interactable = canEditWorld;

            s.ServerName = NameBox.text;

            // TryParse, not Parse: non-numeric text is normal while someone is typing, and a
            // FormatException here takes the whole settings tick with it.
            int typedMax;
            if (!int.TryParse(SeatsBox.text, out typedMax))
                typedMax = LobbySettings.MinPlayers;
            s.MaxPlayers = typedMax;

            // The setter clamps to the island count, so show what was actually stored rather
            // than what was typed, otherwise the field disagrees with the server.
            string clamped = s.MaxPlayers.ToString();
            if (SeatsBox.text != clamped) SeatsBox.text = clamped;

            s.Password = PasswordBox.text;

            // One source of truth: typing a password locks the server, clearing it opens it
            // again. NetHost's connection gate reads Locked, so it has to track PasswordBox on
            // every tick rather than at some point in between.
            s.Locked = LockedFromPassword();

            // Which way the world settings flow depends on whether a save is being loaded.
            //
            // Greying the controls out was only half of "the save decides". They were still READ
            // back into the settings on every tick, so a disabled picker still showing Peaceful
            // overwrote the difficulty the save had just restored, and then the host broadcast that
            // Peaceful to everyone. A Hard save came back Peaceful and could not be put right,
            // because difficulty is chosen at world creation and there is no way back to it once
            // play has begun. Dragons do not spawn on Peaceful at all, so this is not cosmetic.
            //
            // SessionSave.Unpack has already put the save's own values into LobbySettings by the
            // time this runs. So when a save is in play the controls are told what the save chose
            // rather than asked what they are showing, which is what the comment above always
            // claimed was happening.
            if (canEditWorld)
            {
                s.Difficulty = DifficultyPicker.value;
                s.WorldSeed = SeedBox.text;
                s.WorldSize = (World.MapSize)SizePicker.value;
                s.WorldRivers = (World.MapRiverLakes)RiversPicker.value;
                s.PlacementType = PlacementPicker.value;
            }
            else
            {
                if (DifficultyPicker.value != s.Difficulty) DifficultyPicker.value = s.Difficulty;
                if (SeedBox.text != s.WorldSeed) SeedBox.text = s.WorldSeed;
                if (SizePicker.value != (int)s.WorldSize) SizePicker.value = (int)s.WorldSize;
                if (RiversPicker.value != (int)s.WorldRivers) RiversPicker.value = (int)s.WorldRivers;
                if (PlacementPicker.value != s.PlacementType) PlacementPicker.value = s.PlacementType;
            }

            s.WorldType = MultiplayerMapBias;   // not a player choice, see MultiplayerMapBias

            ApplyWorldSettings(s);

            // A changed setting means a changed map. See RegenerateIfStale. Not while a save is
            // being loaded: the save decides the world then, and its controls are locked.
            if (canEditWorld) RegenerateIfStale(s);

            // Ghosts are saved players who have not reconnected to this loaded game. They must
            // not block Start, the host can resume without them, and they take their kingdom
            // back when they rejoin. Skip(1) is the host's own entry.
            StartButton.interactable = Main.kCPlayers.Values
                .Skip(1).Where(p => !p.isGhost).All(p => p.ready);
            RerollButton.interactable = canEditWorld;

            if (Main.kCPlayers.Count > 0)
                NetRouter.Broadcast(LobbySettingsMessage.From(s), NetClient.client.Id);
        }

        /// <summary>
        /// Host only: rebuilds the world from <paramref name="seed"/> (0 for a random one) with the
        /// lobby's current settings, clears every kingdom off the old map, and sends the result to
        /// every guest.
        ///
        /// The one path for both the New World button and a changed setting. It used to live
        /// inside the button's click handler, and changing Size, Rivers or the seed never reached
        /// it at all: the host went on looking at a map built with the OLD settings while a guest
        /// built the new ones, so the two saw different islands.
        /// </summary>
        private static void RegenerateWorld(int seed, string why)
        {
            try
            {
                // Guarded per player, because the whole reroll used to ride on every
                // one of them being fully built. A joiner whose kingdom object is still
                // being assembled has a null inst, and the throw did not just skip them:
                // it abandoned the loop, so every player after them kept their old
                // kingdom and its buildings survived onto the new map. Same shape as the
                // roster bug; one unready player should cost that player, not the reroll.
                int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                    ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;

                foreach (var player in Main.kCPlayers.Values)
                {
                    try
                    {
                        if (player == null || player.inst == null
                            || player.inst.PlayerLandmassOwner == null) continue;
                        // The local player used to be skipped here, on the assumption
                        // that the game resets its own Player. It does not on a map
                        // reroll, so the host's previous kingdom survived into the new
                        // world: after loading a save and then rerolling, the old city
                        // was still standing on the new map, an orphaned ghost town the
                        // player owned nothing of ("I just made this world and this
                        // kingdom was already here"). Every kingdom is cleared now.
                        //
                        // Safe to include ourselves because ResetKingdomSafely holds
                        // the world's cave container out of Reset's reach; calling
                        // Reset directly is what broke harvesting across the session.
                        Main.ResetKingdomSafely(player.inst);
                    }
                    catch (Exception ex)
                    {
                        Main.LogEx("resetting a kingdom for the map reroll", ex);
                    }
                }

                Main.helper.Log("[lobby] regenerating the world (" + why + ")"
                                + (seed != 0 ? ", seed " + seed : ", new random seed"));
                World.inst.Generate(seed);

                // The published browser thumbnail is cached; the map just changed.
                MapThumbnail.Invalidate();
                SeedBox.text = World.inst.GetTextSeed();
                mapPreviewDirty = true;

                // A new map can have fewer islands than the last, leaving the
                // player limit above what it can seat. Nothing else would notice,
                // so re-clamp here and correct the field.
                if (LobbySettings.Current.ClampToWorld())
                {
                    SeatsBox.text = LobbySettings.Current.MaxPlayers.ToString();
                    Main.helper.Log($"[lobby] max players reduced to {LobbySettings.Current.MaxPlayers} ({World.inst.NumLandMasses} islands on the new map)");
                }

                // Guarded per player, and this one matters more than it looks: the
                // broadcast below is inside the same try, so a single player with a
                // half-built kingdom used to abort this whole block and the NEW WORLD
                // SEED never went out. Every client would keep the old map while the
                // host looked at the new one, with nothing logged to say why.
                foreach (var player in Main.kCPlayers.Values)
                {
                    try
                    {
                        if (player != null && player.inst != null)
                            player.inst.SetupJobPriorities();
                    }
                    catch (Exception ex)
                    {
                        Main.LogEx("setting up job priorities after a reroll", ex);
                    }
                }

                NetRouter.Broadcast(WorldSeedMessage.ForCurrentWorld(), NetClient.client.Id);
            }
            catch (Exception ex)
            {
                Main.LogEx("regenerating the lobby world (" + why + ")", ex);
            }
        }

        /// <summary>The settings key last regenerated for, so a mismatch the game refuses to fix is tried once, not every second.</summary>
        private static string lastRegeneratedFor;

        /// <summary>
        /// Host only, every lobby tick: regenerates the world if it no longer matches what the
        /// lobby shows.
        ///
        /// Compared against World's generated* fields, which record what the current map was
        /// ACTUALLY built with, so this cannot be fooled by a picker and a world that merely look
        /// alike. "Random" never counts as a mismatch: the map was built with some concrete value
        /// for it, and treating that as stale would regenerate forever.
        ///
        /// A typed seed is only acted on once the seed box has lost focus, so typing a number
        /// does not rebuild the world once per keystroke. Anything that does not parse as a seed is
        /// replaced by the seed the map really has.
        /// </summary>
        private static void RegenerateIfStale(LobbySettings s)
        {
            if (World.inst == null || SeedBox == null) return;

            int seed = World.inst.seed;
            string why = null;

            string current = World.inst.GetTextSeed();
            if (!SeedBox.isFocused && SeedBox.text != current)
            {
                int typed;
                if (TryParseSeed(SeedBox.text, out typed) && typed != World.inst.seed)
                {
                    seed = typed;
                    why = "seed typed";
                }
                else
                {
                    SeedBox.text = current;
                }
            }

            if (why == null && s.WorldSize != World.MapSize.Random && s.WorldSize != World.inst.generatedMapSize)
                why = "size changed";
            if (why == null && s.WorldRivers != World.MapRiverLakes.Random && s.WorldRivers != World.inst.generatedRiverLakes)
                why = "rivers changed";
            if (why == null && s.WorldType != World.MapBias.Random && s.WorldType != World.inst.generatedMapsBias)
                why = "world type changed";

            if (why == null) return;

            string key = seed + "/" + s.WorldType + "/" + s.WorldSize + "/" + s.WorldRivers;
            if (key == lastRegeneratedFor)
            {
                return;   // already tried exactly this; the log line from that attempt says why it did not take
            }
            lastRegeneratedFor = key;

            RegenerateWorld(seed, why);
        }

        /// <summary>
        /// Reads a seed as the game writes one: letters for the settings, then the number
        /// (e.g. "ISN123456789"). Only the number is taken; the pickers own the settings.
        /// </summary>
        private static bool TryParseSeed(string text, out int seed)
        {
            seed = 0;
            if (string.IsNullOrEmpty(text)) return false;

            int i = 0;
            while (i < text.Length && !char.IsDigit(text[i])) i++;

            return i < text.Length && int.TryParse(text.Substring(i).Trim(), out seed) && seed > 0;
        }

        /// <summary>
        /// Pushes the world-shape settings into the game itself. Both sides do this, the guest
        /// needs it so the map it generates locally matches the host's.
        /// </summary>
        private static void ApplyWorldSettings(LobbySettings s)
        {
            World.inst.mapBias = s.WorldType;
            World.inst.mapRiverLakes = s.WorldRivers;
            World.inst.mapSize = s.WorldSize;

            // Difficulty is settled in the lobby and owned by the game once play begins.
            //
            // This runs on EVERY lobby-settings broadcast, and those go out roughly twice a
            // second. On a loaded game the save restores the real difficulty and then the very
            // next broadcast overwrote it with whatever the lobby happened to hold, which for a
            // lobby that was never told about the save is 0, Peaceful. A Hard world became
            // Peaceful within half a second of loading and would not stay changed, because every
            // later broadcast put it back.
            //
            // In the lobby this still applies normally, which is how a guest receives the host's
            // choice. LobbySettings.Current is also seeded from the save on load (SessionSave), so
            // the two agree rather than fight.
            if (GameState.inst != null && GameState.inst.IsPlayMode()) return;

            Player.inst.difficulty = (Player.Difficulty)s.Difficulty;
        }

        // Rebuilds the map-preview texture from the current world cell data. Water is blue and
        // each landmass gets a distinct colour so the separate-island layout is easy to read.
        public static void RefreshMapPreview()
        {
            try
            {
                int w = World.inst.GridWidth;
                int h = World.inst.GridHeight;
                if (w <= 0 || h <= 0) return;

                Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;

                // Same palette the browser thumbnails use, so a server looks the same in both.
                Color water = MapThumbnail.WaterColour;
                Color[] pixels = new Color[w * h];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = water;

                Cell[] cells = World.inst.GetCellsData();
                foreach (Cell cell in cells)
                {
                    if (cell == null) continue;
                    if (cell.x < 0 || cell.x >= w || cell.z < 0 || cell.z >= h) continue;

                    Color c;
                    if (cell.deepWater || cell.saltWater)
                        c = water;
                    else if (cell.landMassIdx >= 0)
                        c = MapThumbnail.LandmassColour(cell.landMassIdx);
                    else
                        c = MapThumbnail.PlainLandColour;

                    pixels[cell.z * w + cell.x] = c;
                }

                tex.SetPixels(pixels);
                tex.Apply();

                EnsurePreviewImage();
                if (mapPreviewImage != null)
                {
                    if (mapPreviewImage.texture != null)
                        UnityEngine.Object.Destroy(mapPreviewImage.texture);
                    mapPreviewImage.texture = tex;
                    // Keep the map square to the real world aspect inside its slot.
                    if (mapPreviewFitter != null)
                        mapPreviewFitter.aspectRatio = (float)w / h;
                }
            }
            catch (Exception e)
            {
                Main.helper.Log("Map preview error: " + e.Message);
            }
        }

        private static void EnsurePreviewImage()
        {
            if (mapPreviewObj != null) return;
            if (BrowserScreen.serverLobbyRef == null) return;

            // --- Framed container, parented INSIDE the lobby window panel ("Container") so it
            // sits on the dark UI background instead of floating over the game world. Its exact
            // position/size is set by LayoutMiddleColumn() (top of the chat column). ---
            // The prefab owns the position. MapPreviewSlot is an empty box the layout leaves at
            // the bottom of the Players column; fill it and the design can move or resize the
            // preview without this file changing. The fallback covers a bundle with no slot.
            Transform container = BrowserScreen.serverLobbyRef.transform.Find("Container");
            Transform slot = container != null ? container.Find("MapPreviewSlot") : null;
            Transform parent = slot != null
                ? slot
                : (container != null ? container : BrowserScreen.serverLobbyRef.transform);

            mapPreviewObj = new GameObject("MapPreview");
            mapPreviewObj.transform.SetParent(parent, false);
            RectTransform rect = mapPreviewObj.AddComponent<RectTransform>();

            if (slot != null)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            else
            {
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(0f, 0f);
                rect.pivot = new Vector2(0f, 0f);
                rect.sizeDelta = new Vector2(280f, 280f);
                rect.anchoredPosition = new Vector2(50f, 100f);
            }

            // When the prefab supplies MapPreviewSlot, the slot already draws the frame, the dark
            // inset and the "Map Preview" caption - so all this code contributes is the image.
            // Drawing them again here would double the border and stack two captions.
            if (slot == null)
            {
                Image border = mapPreviewObj.AddComponent<Image>();
                border.color = new Color(0.55f, 0.62f, 0.70f, 0.5f);
                border.raycastTarget = false;

                GameObject bgObj = new GameObject("BG");
                bgObj.transform.SetParent(mapPreviewObj.transform, false);
                RectTransform brect = bgObj.AddComponent<RectTransform>();
                brect.anchorMin = Vector2.zero; brect.anchorMax = Vector2.one;
                brect.offsetMin = new Vector2(3f, 3f); brect.offsetMax = new Vector2(-3f, -3f);
                Image bg = bgObj.AddComponent<Image>();
                bg.color = new Color(0.06f, 0.11f, 0.17f, 0.92f);
                bg.raycastTarget = false;

                GameObject capObj = new GameObject("Caption");
                capObj.transform.SetParent(mapPreviewObj.transform, false);
                RectTransform caprect = capObj.AddComponent<RectTransform>();
                caprect.anchorMin = new Vector2(0f, 1f); caprect.anchorMax = new Vector2(1f, 1f);
                caprect.pivot = new Vector2(0.5f, 1f);
                caprect.sizeDelta = new Vector2(0f, 30f);
                caprect.anchoredPosition = new Vector2(0f, -6f);
                TMPro.TextMeshProUGUI cap = capObj.AddComponent<TMPro.TextMeshProUGUI>();
                cap.text = "Map Preview";
                cap.alignment = TMPro.TextAlignmentOptions.Center;
                cap.fontSize = 20f;
                cap.color = new Color(0.84f, 0.90f, 0.96f, 1f);
                cap.raycastTarget = false;
                try { if (NameBox != null && NameBox.textComponent != null) cap.font = NameBox.textComponent.font; } catch { }
            }

            // The map image fills the panel below the caption; an AspectRatioFitter keeps it
            // square (to the real world aspect) and letterboxed within whatever slot it gets.
            GameObject imgObj = new GameObject("MapImage");
            imgObj.transform.SetParent(mapPreviewObj.transform, false);
            RectTransform irect = imgObj.AddComponent<RectTransform>();
            irect.anchorMin = new Vector2(0f, 0f); irect.anchorMax = new Vector2(1f, 1f);
            // In a prefab slot the box is sized to hold a square map with a uniform inset, and
            // the column already has a "World & Map" heading, so no room is reserved at the
            // top. The fallback path draws its own caption and still needs 34.
            irect.offsetMin = new Vector2(8f, 8f);
            irect.offsetMax = slot != null ? new Vector2(-8f, -8f) : new Vector2(-8f, -34f);
            mapPreviewFitter = imgObj.AddComponent<AspectRatioFitter>();
            mapPreviewFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            mapPreviewFitter.aspectRatio = 1f;
            mapPreviewImage = imgObj.AddComponent<RawImage>();
            mapPreviewImage.raycastTarget = false;
        }

        /// <summary>
        /// Records the lobby a player picked in the browser, and shows it if this screen is ready.
        ///
        /// The two halves are separate because the ordering is not guaranteed: ServerRow calls this
        /// the moment a row is clicked, which can be BEFORE the lobby prefab's Awake has bound any
        /// controls. It used to write straight into them and threw a NullReferenceException when it
        /// lost that race (seen in a user log, Rednax 2026-09-02, immediately before the
        /// "wiring prefab controls" line). The details are static and survive, so Awake shows them
        /// once the controls exist.
        /// </summary>
        public void SetDetails(ServerRow details)
        {
            serverDetails = details;
            ShowDetails();
        }

        /// <summary>
        /// Writes the recorded lobby details into the controls, doing nothing until both the
        /// details and the controls are present. Safe to call more than once.
        /// </summary>
        private void ShowDetails()
        {
            try
            {
                if (serverDetails == null) return;
                if (NameBox == null || SeatsBox == null || DifficultyPicker == null)
                {
                    Main.helper.Log("lobby screen: details arrived before the controls were wired; showing them at Awake");
                    return;
                }

                NameBox.text = serverDetails.Name;
                SeatsBox.text = serverDetails.MaxPlayers.ToString();
                DifficultyPicker.value = GameDifficultyExtensions.IndexOf(serverDetails.Difficulty);

                Main.helper.Log($"lobby screen showing '{serverDetails.Name}' ({serverDetails.MaxPlayers} max)");
            }
            catch (Exception ex)
            {
                Main.LogEx("LobbyScreen.ShowDetails", ex);
            }
        }
    }
}
