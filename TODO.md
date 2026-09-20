# To do

Work we mean to do on the multiplayer mod. Bugs players hit in the released version are described
for players in [KNOWN_ISSUES.md](KNOWN_ISSUES.md); this list is what we intend to build or fix.

## Next

- **Check everyone is running the same mods when a game is created or joined.** Different mod lists
  between host and guest desync the session in ways that look like our own bugs. Send the enabled
  mod list (Workshop id and version) with the join handshake, compare it, and if it differs show the
  player exactly what is wrong with a one click fix: switch on or off the mods they already have,
  and open the Workshop page for anything they are missing. Worth checking first whether the game's
  mod loader can enable a mod without a restart; if it cannot, the fix button has to apply the
  change and restart the game.
- **Builders forget to finish construction after a player rejoins.** Reported 2026-09-20, workaround
  is to delete the site and place it again. Construction progress on another player's sites is not
  synced, which is the first place to look.
- **Longboats look wrong on a guest's screen.**
- **The raid system throws and the error is only caught.** The clock no longer freezes, but a year
  can pass with no raid and the cause is still unknown.
- **The Hall of Diplomacy is disabled in multiplayer**, because the game only opens it when AI
  kingdoms exist. Ctrl + Shift + D stands in for it. Make the real screen open instead.
- **Witch huts are switched off in multiplayer.** Turn them back on with their spawning owned by one
  machine, the way wolves are.

## Waiting on a decision: six fixes in Bill Kerman's 20 Sep build

His build is our 0.14.0 plus these. None of them are in ours, and they are his own work, so they
need his agreement (a pull request from him is the clean route) before any of it is taken.

- **Immigration and housing decided only by the kingdom's owner.** `Player.UpdatePersonArrival` and
  `Player.TrySettlePeople` run for every kingdom on every machine, so three machines race to house
  the same arrivals. This is the likely cause of the reported uncapped immigration and permanent
  homelessness after a reload.
- **Job assignment decided only by the owner** (`Job.UpdateAssignment`). Stops the same race over
  who works where. Note the trade: another player's storage buildings then sit empty in your copy,
  which is what makes their granary contents unreadable to you.
- **Ships painted pink on a guest's screen** (`ShipBase.UpdateMaterial`): a boat created before its
  owner's banner material exists gets a null material. Skip the paint and repaint later instead.
  This is our longboat report.
- **Another kingdom's news in your kingdom log** (`KingdomLog.TryLog`): vanilla only filters AI
  land, so every human kingdom's events announce themselves on every machine.
- **`Building.IsPlayerBuilding` answers "whoever is simulating right now"**, so construction
  sounds, damage warnings and advisor messages fire for other players' buildings.
- **The host's lobby UI is never hidden** when the session starts, only the guests'.

## Not synced yet, and each one is a way for two machines to drift apart

- **Building storage contents** (granaries, stores). `EconomySnapshotMessage` exists but nothing
  ever sends it. Diplomacy tribute works around it by settling on whichever machine answers for the
  payer.
- **Villager hunger and health.**
- **Job assignments.**
- **Construction progress on another player's sites**, see the builder report above.
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
- **Foreign merchants cannot visit another player's docks.** Any merchant heading for a dock outside
  the local player's landmasses is destroyed. Letting them trade between kingdoms means provisioning
  them from the right kingdom again.
- **State the game keeps once, that multiplayer needs per kingdom**: WorldMask, UnitSystem,
  SiegeCatapult, DragonSpawn, and the static `Home.BuildGatherTypeOrder` and
  `HomeSaveData.UnloadVillager`. Each one is a place where two kingdoms share something they should
  not.
