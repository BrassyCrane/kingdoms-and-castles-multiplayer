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

## Decisions one machine should make, and currently every machine makes

Each of these is vanilla logic that runs for every kingdom on every machine, so the machines race
and disagree. The pattern is the same one already used for barracks and for trade ships: the
kingdom's own machine decides, everyone else applies what it broadcasts.

- **Immigration and housing.** `Player.UpdatePersonArrival` and `Player.TrySettlePeople` are the
  only two callers of `Villager.SetHome`, and both run for every kingdom everywhere, so several
  machines house the same arrivals off the same apparent vacancy. Almost certainly the reported
  uncapped immigration and the homelessness that never clears after a reload.
- **Job assignment.** `Job.UpdateAssignment` has the same shape. Careful with the trade: gating it
  means another player's production buildings get no workers in our copy, so their stores read
  empty here, which is the same gap that made tribute pay the wrong amount.
- **`Building.IsPlayerBuilding` answers "whoever is simulating right now"**, so construction
  sounds, damage warnings and advisor messages fire for other players' buildings.
- **`KingdomLog.TryLog` only filters AI land**, so every kingdom's news lands in everybody's log.

## Smaller, and each one visible to players

- **Ships can draw hot pink on another player's screen.** `ShipBase.UpdateMaterial` paints from the
  owner's banner material, and a boat created before that material exists gets a null one. Fishing
  boats spawn and despawn faster than the banner sweep runs, so there is nearly always a fresh one.
  This is the longboat report.
- **The host's own lobby UI is never hidden when the session starts.** Guests get the transition,
  the host's screen stays alive underneath the game.

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
