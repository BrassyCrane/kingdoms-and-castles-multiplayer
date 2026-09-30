# To do

Work we mean to do on the multiplayer mod. Bugs players hit in the released version are described
for players in [KNOWN_ISSUES.md](KNOWN_ISSUES.md); this list is what we intend to build or fix.

## Next

- **Find why a guest can still time out of a long game.** Seen on 0.16.0 after about an hour, year
  25. Both logs are in: neither game froze. The link backed up (the host's chat took about 16
  seconds to reach the guest, a full 4 MB send buffer at Steam's fixed 256 KB/s) until the host
  stopped hearing the guest's heartbeat. 0.16.3 logs a [LINK] line every 15 seconds with the queue,
  rate and top senders; the next report says what fills the link.

- **Check everyone is running the same mods when a game is created or joined.** Different mod lists
  between host and guest desync the session in ways that look like our own bugs. Send the enabled
  mod list (Workshop id and version) with the join handshake, compare it, and if it differs show the
  player exactly what is wrong with a one click fix: switch on or off the mods they already have,
  and open the Workshop page for anything they are missing. Worth checking first whether the game's
  mod loader can enable a mod without a restart; if it cannot, the fix button has to apply the
  change and restart the game.
- **Rebuilding the lobby map throws once when the host opens a lobby** ("regenerating the lobby
  world (size changed)", a NullReferenceException that is caught). Nobody has joined yet at that
  point, but whatever the method does after the throw is skipped.

## Base game systems switched off or broken in multiplayer

Everything the game has that a multiplayer session does not have yet. Each one should come back,
with one machine owning its decisions and the others applying them.

- **Witch huts.** Switched off because their live sync was unreliable. Turn them back on with their
  spawning owned by one machine, the way wolves are.
- **The Hall of Diplomacy** is not set up for multiplayer. Ctrl + Shift + D stands in for it. Make
  the real screen work for players and AI kingdoms.
- **Viking raids run, but the raid system throws and the error is only caught.** The clock no longer
  freezes, but a year can pass with no raid and the cause is still unknown.

## Decisions one machine should make, and currently every machine makes

Each of these is vanilla logic that runs for every kingdom on every machine, so the machines race
and disagree. The pattern is the same one already used for barracks and for trade ships: the
kingdom's own machine decides, everyone else applies what it broadcasts.

Done on `dev`, waiting on a real two machine test: housing and immigration, `IsPlayerBuilding`,
the kingdom log filter, the pink ships, and the host's lobby screen staying alive under the game.

## Not synced yet, and each one is a way for two machines to drift apart

- **Villager hunger and health.**
- **Job assignments.** Decided by the owner only now, but not sent, so another player's workers
  show as idle in our copy.
- **Army positions are corrected in batches**, so a fast chase can briefly look different on each
  screen. Smooth it out.
- **A kingdom whose player left is only half frozen.** `Main.FreezeGhostKingdomsFully` gates the
  rest, ships off and has never been run. Buildings and villagers keep ticking for a player who is
  gone, because gating them costs a component lookup per entry per frame.

## Later

- **The server browser lists nothing.** Joining works through Steam invites. Deferred on purpose.
  When it is picked up, check that the `SetLobbyData` publish and the `RequestLobbyList` filter
  really round trip, with logging on both ends.
- **A big save takes about a minute to reach each joining player.** It is sent in 64 chunk windows;
  make it quicker or make the wait clearer.
- **State the game keeps once, that multiplayer needs per kingdom**: WorldMask, UnitSystem,
  SiegeCatapult, DragonSpawn, and the static `Home.BuildGatherTypeOrder` and
  `HomeSaveData.UnloadVillager`. Each one is a place where two kingdoms share something they should
  not.
- **Look into whether a co-op mode is feasible**: two or more players running one shared kingdom
  instead of one kingdom each. Find out what the game ties to a single player (the local `Player`,
  job settings, the build queue, resources) and whether one machine could own the kingdom while the
  others send their actions to it.
