namespace KaCMultiplayer.Net
{
    /// <summary>
    /// The lobby's configuration, as live state. Read all over the browser and lobby UI,
    /// and written by whoever is hosting.
    ///
    /// Plain data, deliberately separate from the message that carries it.
    /// <see cref="Messages.LobbySettingsMessage"/> copies values in on arrival, so the object
    /// everyone holds a reference to keeps its identity.
    ///
    /// Letting one object be both the shared state and the thing on the wire is tempting and
    /// wrong: the state then carries a wire id and handler methods, receiving an update replaces
    /// the object out from under every holder, and any code touching a lobby setting acquires a
    /// reason to know about networking.
    /// </summary>
    public class LobbySettings
    {
        /// <summary>
        /// The live settings for the current lobby. One instance for the process, replaced
        /// never and mutated in place, so anything holding a reference stays correct.
        /// </summary>
        public static readonly LobbySettings Current = new LobbySettings();

        /// <summary>Fewest players a multiplayer lobby makes sense with.</summary>
        public const int MinPlayers = 2;

        /// <summary>
        /// Ceiling used when the world hasn't been generated yet and the real landmass
        /// count isn't known. Also the absolute upper bound, the Riptide server is started
        /// with room for 25 connections, so nothing above that could connect anyway.
        /// </summary>
        public const int AbsoluteMaxPlayers = 25;

        /// <summary>
        /// How many players this map can actually hold: one kingdom per landmass.
        ///
        /// The host assigns each player their own landmass when placing keeps, so a
        /// two-island map cannot seat three players however large a number gets typed in.
        /// Falls back to <see cref="AbsoluteMaxPlayers"/> before a map exists, so the field
        /// isn't pinned to the minimum during setup.
        /// </summary>
        public static int MaxPlayerCap
        {
            get
            {
                try
                {
                    int landmasses = World.inst == null ? 0 : World.inst.NumLandMasses;
                    if (landmasses < MinPlayers) return AbsoluteMaxPlayers;   // no map yet
                    return landmasses > AbsoluteMaxPlayers ? AbsoluteMaxPlayers : landmasses;
                }
                catch
                {
                    return AbsoluteMaxPlayers;
                }
            }
        }

        public string ServerName { get; set; }

        private int maxPlayers = MinPlayers;

        /// <summary>
        /// Player limit, clamped on assignment so an out-of-range value can never be stored
        /// from anywhere, the UI, a received message, or a future caller. Silently
        /// correcting beats validating at every call site and missing one.
        /// </summary>
        public int MaxPlayers
        {
            get { return maxPlayers; }
            set
            {
                int cap = MaxPlayerCap;
                maxPlayers = value < MinPlayers ? MinPlayers : (value > cap ? cap : value);
            }
        }

        public bool Locked { get; set; }
        public string Password { get; set; }

        /// <summary>Index into the game's Player.Difficulty, not a named enum.</summary>
        public int Difficulty { get; set; }

        public string WorldSeed { get; set; }
        public World.MapSize WorldSize { get; set; }
        public World.MapBias WorldType { get; set; }
        public World.MapRiverLakes WorldRivers { get; set; }

        /// <summary>0 means keeps are placed randomly by the host.</summary>
        public int PlacementType { get; set; }

        public bool FogOfWar { get; set; }

        public LobbySettings()
        {
            // Same defaults the packet class had. A single space for the password rather
            // than empty is what the UI expects for "unset".
            MaxPlayers = MinPlayers;
            Password = " ";
            WorldRivers = World.MapRiverLakes.Some;
        }

        /// <summary>
        /// Re-applies the cap after the map changes. Generating a smaller map can leave the
        /// limit above what the new island count supports, and nothing else would notice.
        /// Returns true if the value had to come down.
        /// </summary>
        public bool ClampToWorld()
        {
            int before = maxPlayers;
            MaxPlayers = maxPlayers;    // the setter re-clamps against the current cap
            return maxPlayers != before;
        }

        /// <summary>
        /// Copies another set of settings into this one, in place, so everyone holding a
        /// reference sees the update. Replacing the object instead would silently strand every
        /// reference taken before the update.
        /// </summary>
        public void CopyFrom(LobbySettings other)
        {
            if (other == null) return;

            ServerName = other.ServerName;
            MaxPlayers = other.MaxPlayers;
            Locked = other.Locked;
            Password = other.Password;
            Difficulty = other.Difficulty;
            WorldSeed = other.WorldSeed;
            WorldSize = other.WorldSize;
            WorldType = other.WorldType;
            WorldRivers = other.WorldRivers;
            PlacementType = other.PlacementType;
            FogOfWar = other.FogOfWar;
        }

        /// <summary>
        /// Pushes the world-generation fields onto World, which is where the game actually
        /// reads them from when generating a map.
        /// </summary>
        public void ApplyToWorld()
        {
            World.inst.mapSize = WorldSize;
            World.inst.mapBias = WorldType;
            World.inst.mapRiverLakes = WorldRivers;
        }
    }
}
