namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// The game's difficulty settings, for turning a lobby's stored difficulty index into a
    /// label the server browser can show.
    ///
    /// The order matches the game's <c>Player.Difficulty</c>, because the lobby stores the
    /// difficulty as a raw dropdown index and casts it across. Do not reorder.
    ///
    /// Beware of naming these after anything else in the game with four values. Region names
    /// (Paxlon, Sommern, Vintar, Falle) line up positionally and compile cleanly, and the only
    /// symptom is the server browser advertising every lobby under the wrong word.
    ///
    /// The game's enum ends with a <c>NumSettings</c> counter, which is deliberately not
    /// mirrored here, it is not a difficulty and must never be offered as one.
    /// </summary>
    public enum GameDifficulty
    {
        Peaceful = 0,
        Easy = 1,
        Hard = 2,
        Survival = 3,
    }

    public static class GameDifficultyExtensions
    {
        /// <summary>
        /// Label for a stored difficulty index. Returns "Unknown" rather than a number for an
        /// out-of-range value, which can happen if a peer on a different build sends one.
        /// </summary>
        public static string Label(int difficultyIndex)
        {
            switch (difficultyIndex)
            {
                case (int)GameDifficulty.Peaceful: return "Peaceful";
                case (int)GameDifficulty.Easy: return "Easy";
                case (int)GameDifficulty.Hard: return "Hard";
                case (int)GameDifficulty.Survival: return "Survival";
                default: return "Unknown";
            }
        }

        /// <summary>
        /// Turns a label back into an index, the lobby reads the difficulty a server
        /// advertised as a string and has to restore the dropdown from it.
        ///
        /// Returns 0 for anything unrecognised instead of throwing. The previous code used
        /// <c>Enum.Parse</c>, which throws on an unknown name, and it was parsing a string
        /// that arrived over the network from a peer that might be on a different build.
        /// </summary>
        public static int IndexOf(string label)
        {
            if (string.IsNullOrEmpty(label)) return 0;

            switch (label.Trim())
            {
                case "Peaceful": return (int)GameDifficulty.Peaceful;
                case "Easy": return (int)GameDifficulty.Easy;
                case "Hard": return (int)GameDifficulty.Hard;
                case "Survival": return (int)GameDifficulty.Survival;
                default: return (int)GameDifficulty.Peaceful;
            }
        }
    }
}
