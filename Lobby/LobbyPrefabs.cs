using System;
using UnityEngine;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Loads the mod's UI prefabs out of its asset bundle.
    ///
    /// Every prefab is checked and named on failure. An unchecked <c>LoadAsset</c> result
    /// turns a prefab missing from the bundle into a silent null, which surfaces much later as
    /// a null-ref inside whichever screen tried to use it, with nothing pointing at the cause.
    /// That matters while the bundle is being rebuilt: a prefab that never got tagged into it
    /// is exactly the mistake to expect, and it should say so at load.
    /// </summary>
    public static class LobbyPrefabs
    {
        /// <summary>Bundle name, without platform folder or hash suffix.</summary>
        private const string BundleName = "serverbrowserpkg";

        /// <summary>Where assets sit inside the bundle. Lowercase, the loader expects it.</summary>
        private const string AssetRoot = "assets/workspace/";

        public static AssetBundle Bundle { get; private set; }

        public static GameObject BrowserScreen { get; private set; }
        public static GameObject ServerEntry { get; private set; }
        public static GameObject ServerLobby { get; private set; }
        public static GameObject PlayerEntry { get; private set; }
        public static GameObject ChatEntry { get; private set; }
        public static GameObject ChatSystemEntry { get; private set; }
        public static GameObject Modal { get; private set; }

        /// <summary>
        /// The diplomacy window and its row. OPTIONAL, unlike everything above.
        ///
        /// A missing lobby prefab means the mod cannot show a lobby at all, so it fails the load
        /// loudly. Diplomacy is one screen: if the shipped bundle predates it, the right outcome is
        /// that the screen is unavailable and everything else works, not that the lobby breaks.
        /// Callers check for null.
        /// </summary>
        public static GameObject DiplomacyScreen { get; private set; }
        public static GameObject DiplomacyRow { get; private set; }

        /// <summary>True when the bundle loaded and every prefab was found.</summary>
        public static bool Ready { get; private set; }

        /// <summary>
        /// Loads the bundle and every prefab. Returns false if anything is missing, having
        /// logged which, and logs the bundle's actual contents, so a name mismatch is
        /// diagnosable in one look.
        /// </summary>
        public static bool Load(KCModHelper helper)
        {
            // Idempotent, because this is reached two ways: the mod loader's PreScriptLoad
            // hook, and a fallback in Main.Preload in case the loader doesn't pick the hook
            // up. Whichever runs first wins and the other is a no-op.
            if (Ready) return true;

            try
            {
                Bundle = KCModHelper.LoadAssetBundle(helper.modPath, BundleName);
                if (Bundle == null)
                {
                    NetLog.Warn("asset bundle '" + BundleName + "' failed to load from " + helper.modPath);
                    return false;
                }

                NetLog.Info("bundle contents: " + string.Join(", ", Bundle.GetAllAssetNames()));

                int missing = 0;

                BrowserScreen = Take("serverbrowser", ref missing);
                ServerEntry = Take("serverentryitem", ref missing);
                ServerLobby = Take("serverlobby", ref missing);
                PlayerEntry = Take("serverlobbyplayerentry", ref missing);
                ChatEntry = Take("serverchatentry", ref missing);
                ChatSystemEntry = Take("serverchatsystementry", ref missing);
                Modal = Take("modalui", ref missing);

                // Optional, so an older bundle costs the diplomacy screen and nothing else.
                DiplomacyScreen = TakeOptional("diplomacyui");
                DiplomacyRow = TakeOptional("diplomacyrow");

                if (missing > 0)
                {
                    NetLog.Warn(missing + " prefab(s) missing from the bundle, the listing above " +
                                "shows what it actually contains");
                    return false;
                }

                Ready = true;

                bool diplomacy = DiplomacyScreen != null && DiplomacyRow != null;
                NetLog.Info("all 7 required UI prefabs loaded" +
                            (diplomacy ? ", plus the diplomacy screen"
                                       : "; no diplomacy screen in this bundle"));
                return true;
            }
            catch (Exception ex)
            {
                NetLog.Error("loading the UI asset bundle", ex);
                return false;
            }
        }

        /// <summary>
        /// Loads a prefab whose absence is survivable. Says so at info level rather than warning,
        /// because an older bundle is a known state, not a fault.
        /// </summary>
        private static GameObject TakeOptional(string name)
        {
            GameObject prefab = Bundle.LoadAsset(AssetRoot + name + ".prefab") as GameObject;
            if (prefab == null)
                NetLog.Info("bundle has no optional prefab '" + name + "'; that feature stays off");
            return prefab;
        }

        private static GameObject Take(string name, ref int missing)
        {
            string path = AssetRoot + name + ".prefab";

            GameObject prefab = Bundle.LoadAsset(path) as GameObject;
            if (prefab == null)
            {
                NetLog.Warn("bundle has no prefab at '" + path + "'");
                missing++;
            }

            return prefab;
        }
    }
}
