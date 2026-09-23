// The SteamManager is designed to work with Steamworks.NET
// This file is released into the public domain.
// Where that dedication is not recognized you are granted a perpetual,
// irrevocable license to copy and modify this file as you see fit.
//
// Version: 1.0.12

#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)

#endif

using UnityEngine;
#if !DISABLESTEAMWORKS
using System.Collections;
using Steamworks;
using KaCMultiplayer;
#endif

//
// The SteamManager provides a base implementation of Steamworks.NET on which you can build upon.
// It handles the basics of starting up and shutting down the SteamAPI for use.
//
[DisallowMultipleComponent]
public class SteamBootstrap : MonoBehaviour {
#if !DISABLESTEAMWORKS
	protected static bool s_EverInitialized = false;

	protected static SteamBootstrap s_instance;
	public static SteamBootstrap Instance {
		get {
			if (s_instance == null) {
				return new GameObject("SteamBootstrap").AddComponent<SteamBootstrap>();
			}
			else {
				return s_instance;
			}
		}
	}

    protected bool m_bInitialized = false;
	public static bool Initialized {
		get {
			return Instance.m_bInitialized;
		}
	}

	protected SteamAPIWarningMessageHook_t m_SteamAPIWarningMessageHook;

	[AOT.MonoPInvokeCallback(typeof(SteamAPIWarningMessageHook_t))]
	protected static void SteamAPIDebugTextHook(int nSeverity, System.Text.StringBuilder pchDebugText) {
		Main.helper.Log(pchDebugText.ToString());
	}

#if UNITY_2019_3_OR_NEWER
	// In case of disabled Domain Reload, reset static members before entering Play Mode.
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	private static void InitOnPlayMode()
	{
		s_EverInitialized = false;
		s_instance = null;
	}
#endif

	protected virtual void Awake() {
		// Only one instance of SteamManager at a time!
		Main.helper.Log("Steam awake");
		if (s_instance != null) {
			Destroy(gameObject);
			return;
		}
		s_instance = this;

		if(s_EverInitialized) {
			// This is almost always an error.
			// The most common case where this happens is when SteamManager gets destroyed because of Application.Quit(),
			// and then some Steamworks code in some other OnDestroy gets called afterwards, creating a new SteamManager.
			// You should never call Steamworks functions in OnDestroy, always prefer OnDisable if possible.
			Main.helper.Log("Tried to Initialize the SteamAPI twice in one session!");

			return;
		}

		// We want our SteamManager Instance to persist across scenes.
		DontDestroyOnLoad(gameObject);

		if (!Packsize.Test()) {
			Main.helper.Log("[Steamworks.NET] Packsize Test returned false, the wrong version of Steamworks.NET is being run in this platform.");
		}

		if (!DllCheck.Test()) {
			Main.helper.Log("[Steamworks.NET] DllCheck Test returned false, One or more of the Steamworks binaries seems to be the wrong version.");
		}

		try {
			// If Steam is not running or the game wasn't started through Steam, SteamAPI_RestartAppIfNecessary starts the
			// Steam client and also launches this game again if the User owns it. This can act as a rudimentary form of DRM.

			// Once you get a Steam AppID assigned by Valve, you need to replace AppId_t.Invalid with it and
			// remove steam_appid.txt from the game depot. eg: "(AppId_t)480" or "new AppId_t(480)".
			// See the Valve documentation for more information: https://partner.steamgames.com/doc/sdk/api#initialization_and_shutdown
			if (SteamAPI.RestartAppIfNecessary((AppId_t)569480)) {
				//Application.Quit();
				Main.helper.Log("Attempted to restart app");
				return;
			}
		}
		catch (System.DllNotFoundException e) { // We catch this exception here, as it will be the first occurrence of it.
			Main.helper.Log("[Steamworks.NET] Could not load [lib]steam_api.dll/so/dylib. It's likely not in the correct location. Refer to the README for more details.\n" + e);

			//Application.Quit();
			return;
		}

		// Initializes the Steamworks API.
		// If this returns false then this indicates one of the following conditions:
		// [*] The Steam client isn't running. A running Steam client is required to provide implementations of the various Steamworks interfaces.
		// [*] The Steam client couldn't determine the App ID of game. If you're running your application from the executable or debugger directly then you must have a [code-inline]steam_appid.txt[/code-inline] in your game directory next to the executable, with your app ID in it and nothing else. Steam will look for this file in the current working directory. If you are running your executable from a different directory you may need to relocate the [code-inline]steam_appid.txt[/code-inline] file.
		// [*] Your application is not running under the same OS user context as the Steam client, such as a different user or administration access level.
		// [*] Ensure that you own a license for the App ID on the currently active Steam account. Your game must show up in your Steam library.
		// [*] Your App ID is not completely set up, i.e. in Release State: Unavailable, or it's missing default packages.
		// Valve's documentation for this is located here:
		// https://partner.steamgames.com/doc/sdk/api#initialization_and_shutdown
		m_bInitialized = SteamAPI.Init();
		if (!m_bInitialized) {
			Main.helper.Log("[Steamworks.NET] SteamAPI_Init() failed. Refer to Valve's documentation or the comment above this line for more information.");

			return;
		}

		s_EverInitialized = true;

		TuneNetworking();
    }

	// WHY THE DEFAULT BUFFER IS TOO SMALL FOR THIS MOD (player report, 0.15.2: a 26 megabyte log of
	// "Failed to send ... k_EResultLimitExceeded", and players dropped for no stated reason).
	//
	// Steam holds outgoing data for a connection in a buffer of its own, 512 kilobytes by default,
	// and refuses every further send with LimitExceeded until that buffer drains. Joining a game
	// pushes a whole saved kingdom down one connection, which is megabytes, and a busy session
	// pushes a steady stream of position and health updates on top. Once the buffer is full nothing
	// gets out, including the heartbeat that says the player is still there, so the other end
	// eventually decides the connection is dead and drops them.
	//
	// Four megabytes is chosen to hold a whole save transfer's worth of queued chunks rather than a
	// fraction of one. It is memory Steam only reserves while a connection is actually behind.
	//
	// Global scope, set once before any socket exists, because both the host's listen socket and the
	// joining player's connection need it and neither is created here. Failing to set it is not
	// fatal, it only means the old behaviour, so this logs and carries on.
	// Steam hangs up on its own account after ten seconds of silence, under everything Riptide
	// thinks about the connection, so raising Riptide's timeout alone would not have saved a
	// player whose game stalled through an autosave. Both have to agree, and this is the lower of
	// the two, so it is the one that decides. Kept a little above the session timeout the mod sets
	// on Riptide, so a dropped player is reported by the layer that can say why.
	private const int SteamTimeoutConnectedMs = 35000;

	private static void TuneNetworking()
	{
		SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 4 * 1024 * 1024);
		SetGlobalInt(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, SteamTimeoutConnectedMs);
	}

	/// <summary>
	/// Sets one global Steam networking option. Steamworks.NET only exposes the raw form, which
	/// wants a pointer to the value, so the value is pinned for the length of the call.
	/// </summary>
	private static void SetGlobalInt(ESteamNetworkingConfigValue option, int value)
	{
		System.IntPtr held = System.IntPtr.Zero;
		try
		{
			held = System.Runtime.InteropServices.Marshal.AllocHGlobal(sizeof(int));
			System.Runtime.InteropServices.Marshal.WriteInt32(held, value);

			bool ok = SteamNetworkingUtils.SetConfigValue(
				option,
				ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
				System.IntPtr.Zero,
				ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
				held);

			Main.helper.Log("[net] " + option + " = " + value + (ok ? " set" : " REFUSED by Steam"));
		}
		catch (System.Exception e)
		{
			Main.helper.Log("[net] could not set " + option + ": " + e.Message);
		}
		finally
		{
			if (held != System.IntPtr.Zero)
				System.Runtime.InteropServices.Marshal.FreeHGlobal(held);
		}
	}

	// This should only ever get called on first load and after an Assembly reload, You should never Disable the Steamworks Manager yourself.
	protected virtual void OnEnable() {
		if (s_instance == null) {
			s_instance = this;
		}

		if (!m_bInitialized) {
			return;
		}

		if (m_SteamAPIWarningMessageHook == null) {
			// Set up our callback to receive warning messages from Steam.
			// You must launch with "-debug_steamapi" in the launch args to receive warnings.
			m_SteamAPIWarningMessageHook = new SteamAPIWarningMessageHook_t(SteamAPIDebugTextHook);
			SteamClient.SetWarningMessageHook(m_SteamAPIWarningMessageHook);
		}
	}

	// OnApplicationQuit gets called too early to shutdown the SteamAPI.
	// Because the SteamManager should be persistent and never disabled or destroyed we can shutdown the SteamAPI here.
	// Thus it is not recommended to perform any Steamworks work in other OnDestroy functions as the order of execution can not be garenteed upon Shutdown. Prefer OnDisable().
	protected virtual void OnDestroy() {
		if (s_instance != this) {
			return;
		}

		s_instance = null;

		if (!m_bInitialized) {
			return;
		}

		SteamAPI.Shutdown();
	}

	protected virtual void Update() {
		if (!m_bInitialized) {
			return;
		}

		// Run Steam client callbacks
		SteamAPI.RunCallbacks();
	}
#else
	public static bool Initialized {
		get {
			return false;
		}
	}
#endif //!DISABLESTEAMWORKS
}
