using UnityEngine;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Access to the game's own main-menu UI, which the mod parents its screens under and
    /// borrows button styling from.
    ///
    /// Resolved on each access rather than captured once. Holding these as <c>static readonly</c>
    /// fields initialised from <c>GameState.inst</c> would run at type load: touching the type
    /// before the game state exists throws a <c>TypeInitializationException</c>, and the
    /// reference then goes stale the moment the game rebuilds its menu. A property costs one
    /// field read and cannot go stale.
    ///
    /// It also carried five more members, <c>PlayingMode</c>, <c>World</c>, the choose-mode
    /// UI, and the menu GameObject, that nothing referenced. Only the transform below was
    /// ever used.
    /// </summary>
    public static class MenuUi
    {
        /// <summary>
        /// Root transform of the main menu UI, or null if the game state isn't up yet.
        /// Callers that run during startup should check.
        /// </summary>
        public static Transform Root
        {
            get
            {
                MainMenuMode mode = Mode;
                if (mode == null || mode.mainMenuUI == null) return null;
                return mode.mainMenuUI.transform;
            }
        }

        /// <summary>The game's main-menu mode, or null before it exists.</summary>
        public static MainMenuMode Mode
        {
            get
            {
                return GameState.inst == null ? null : GameState.inst.mainMenuMode;
            }
        }

        /// <summary>
        /// Finds a node under the main menu UI by path, or null. Logs a miss, because every
        /// one of these paths is a dependency on the base game's menu hierarchy, if a game
        /// update moves something, this is where it surfaces.
        /// </summary>
        public static Transform Find(string path)
        {
            Transform root = Root;
            if (root == null)
            {
                NetLog.Warn("menu UI not available yet, looking for '" + path + "'");
                return null;
            }

            Transform found = root.Find(path);
            if (found == null)
                NetLog.Warn("main menu has no node at '" + path + "', a game update may have moved it");

            return found;
        }
    }
}
