using System.Reflection;
using UnityEngine;

namespace KaCMultiplayer
{
    /// <summary>
    /// One participant in a multiplayer session, and the <see cref="Player"/> object that
    /// carries their kingdom.
    ///
    /// The local player reuses the game's own <c>Player.inst</c>. Every remote player gets a
    /// second, parallel <see cref="Player"/> on its own GameObject, the game was built around
    /// a single Player singleton, so remote kingdoms only work because Main's transpiler
    /// rewrites <c>Player.inst</c> to the receiver inside Player's instance methods.
    /// </summary>
    public class SessionPlayer
    {
        // Number of job/tool slots the game allocates per landmass, taken from the game rather
        // than guessed: Player.Awake does `defaultEnabledFlags = new bool[39]`, and
        // JobCategory.NumCategories is 39 on the current build. It was hardcoded at 38, which
        // left the LAST job category disabled on every remote player, silently, since a missing
        // job just means nobody ever takes it. The save path already reads the real length off
        // the live array (live.GetLength(0) in PlayerSaveData.Pack); this is the same fix.
        private const int JobSlotCount = 39;

        public ushort id;
        public string name;

        public string steamId;

        public string kingdomName;
        public int banner = 0;
        public bool ready = false;

        public Player inst;
        public GameObject gameObject;

        // A placeholder for a SAVED player who hasn't (re)connected to this loaded session. Their kingdom
        // is fully unpacked (buildings/ownership under their saved teamId) but nobody is driving it, and
        // they must not block the lobby's ready/Start gating. Cleared if the real player reconnects.
        public bool isGhost = false;

        public SessionPlayer(string name, ushort id, string steamId)
        {
            this.name = name;
            this.id = id;
            this.steamId = steamId;
            this.kingdomName = " ";

            if (id == NetClient.client.Id)
            {
                // Us: adopt the singleton the game already built rather than making a rival.
                gameObject = Player.inst.gameObject;
                inst = Player.inst;
            }
            else
            {
                gameObject = BuildRemotePlayer(name, id, steamId, out inst);
            }
        }

        public SessionPlayer(ushort id, Player player)
        {
            this.id = id;
            inst = player;
            gameObject = player.gameObject;
        }

        /// <summary>
        /// Creates the parallel <see cref="Player"/> that represents a remote participant,
        /// wired up with the components its kingdom logic expects to find.
        /// </summary>
        private static GameObject BuildRemotePlayer(string name, ushort id, string steamId, out Player player)
        {
            GameObject host = new GameObject($"Client Player ({id} {name})");

            player = host.AddComponent<Player>();
            player.irrigation = host.AddComponent<IrrigationManager>();
            player.PlayerLandmassOwner = host.AddComponent<LandmassOwner>();

            // Fresh game: teamId is clientId + 4, computed from the Riptide client id alone so
            // every machine arrives at the same answer without anyone having to publish the
            // assignment. The +4 clears the game's own range, the per-team pathing arrays are
            // size 5, so it uses 0..4, and keeping the ids compact matters because those arrays
            // are indexed by team. This formula must agree with the one in ServerHandshake.
            //
            // Loaded game: the saved teamId wins, because the kingdom in the save carries it and
            // join order need not repeat. LoadIdentity returns the saved id when this steamId
            // appears in the save, and falls back to the formula otherwise.
            player.PlayerLandmassOwner.teamId = LoadSaveOverrides.LoadIdentity.TeamIdFor(steamId, id);

            // Hazard pay is a local-player UI affordance; a remote kingdom needs the timer to
            // exist so the game's code can read it, but never to run.
            player.hazardPayWarmup = new Timer(5f);
            player.hazardPayWarmup.Enabled = false;

            EnableAllJobSlots(player);
            ResetAsIfSingleton(player);

            return host;
        }

        /// <summary>
        /// Turns on every job slot. <c>defaultEnabledFlags</c> is private and left null on a
        /// Player the game did not construct itself, so it has to be seeded by reflection or
        /// the first job lookup dereferences null.
        /// </summary>
        private static void EnableAllJobSlots(Player player)
        {
            bool[] enabled = new bool[JobSlotCount];
            for (int i = 0; i < enabled.Length; i++)
                enabled[i] = true;

            FieldInfo flags = typeof(Player).GetField(
                "defaultEnabledFlags", BindingFlags.NonPublic | BindingFlags.Instance);
            flags.SetValue(player, enabled);
        }

        /// <summary>
        /// Runs <see cref="Player.Reset"/> on a remote player to initialise its own collections,
        /// buildingContainer, and the per-landmass job/resource arrays that
        /// <c>Reset -> ResetPerLandMassData -> SetupJobPriorities</c> allocates (including
        /// <c>JobFilledAvailable</c>, which is otherwise null on a Player the game never Awoke).
        ///
        /// It must NOT point <c>Player.inst</c> at the fresh remote player first, even though the
        /// name says "as if singleton". Reset's own body carries no <c>Player.inst</c> reference,
        /// the singleton transpiler rewrites none inside it (the startup log shows no "in Reset"
        /// line), so it works on its receiver regardless of the singleton. But Reset also makes
        /// GLOBAL calls, and one of them reads the literal <c>Player.inst</c> deep inside:
        /// <c>JobSystem.ClearAllJobs -> Villager.QuitJob -> Job.OnEmployeeQuit</c> does
        /// <c>Player.inst.JobFilledAvailable.data[...]</c>. A just-built remote player's
        /// JobFilledAvailable is still null at that point (Reset allocates it only later), so
        /// aiming the singleton at it made ClearAllJobs throw NullReferenceException the moment the
        /// world already held an employed villager. That is what aborted a load/rejoin into a
        /// populated game mid-unpack and left the client with no kingdom (0 resources, no keep,
        /// villagers starving); a fresh lobby join slipped through only because it had no jobs to
        /// clear. Leaving Player.inst on the real local player lets ClearAllJobs, and every other
        /// global call Reset makes, read a valid, fully-initialised player.
        ///
        /// The finally still restores Player.inst defensively, in case any patched call touches it.
        /// </summary>
        private static void ResetAsIfSingleton(Player player)
        {
            Player previous = Player.inst;
            try
            {
                player.Reset();
            }
            finally
            {
                Player.inst = previous;
            }
        }
    }
}
