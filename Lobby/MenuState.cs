namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Menu screens, extended with the two this mod adds.
    ///
    /// The first twenty-two values mirror the game's own <c>MainMenuMode.State</c>, because
    /// <c>Main.TransitionTo</c> casts this straight across when handing control back to the
    /// game's menu system. **Do not reorder or insert**, a value here means whatever the game
    /// has at that ordinal, and getting it wrong sends the player to the wrong screen with no
    /// error. Append only.
    ///
    /// The values are the game's own ordinals. Nothing here should carry metadata tokens or
    /// RIDs, those are assembly-internal handles with no meaning in source.
    /// </summary>
    public enum MenuState
    {
        // ---- mirrors MainMenuMode.State: fixed order, do not touch ----
        Uninitialized = 0,
        Menu = 1,
        ChooseMode = 2,
        ChooseDifficulty = 3,
        NewMap = 4,
        NameAndBanner = 5,
        PauseMenu = 6,
        SettingsMenu = 7,
        Save = 8,
        Load = 9,
        QuitConfirm = 10,
        ExitConfirm = 11,
        LoadError = 12,
        SendSave = 13,
        Credits = 14,
        Failure = 15,
        KeepDestroyed = 16,
        BannerSelect = 17,
        GameWorkshopUI = 18,
        RivalChoiceUI = 19,
        KingdomShareFromMenu = 20,
        KingdomShareFromGame = 21,

        // ---- this mod's own screens ----

        /// <summary>
        /// First value that isn't one of the game's. Anything at or above this is a mod screen,
        /// which is how the mod's canvas decides whether to be visible. Compare against this
        /// name rather than a bare ordinal, so appending a screen does not need a second edit.
        /// </summary>
        BrowserScreen = 22,
        LobbyScreen = 23,

        /// <summary>
        /// Leave the menus entirely and hand off to gameplay. Not a real
        /// <c>MainMenuMode.State</c>, deliberately out of range, so the game's menu system
        /// transitions to nothing and stops drawing. Was written as a bare cast of <c>200</c>.
        /// </summary>
        LeaveMenus = 200,
    }

    public static class MenuStateExtensions
    {
        /// <summary>True for screens this mod owns rather than the base game.</summary>
        public static bool IsModScreen(this MenuState state)
        {
            return (int)state >= (int)MenuState.BrowserScreen;
        }
    }
}
