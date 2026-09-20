# To do

Planned work for the multiplayer mod, roughly in the order we mean to get to it. Bugs players hit in
the released version live in [KNOWN_ISSUES.md](KNOWN_ISSUES.md); this list is what we intend to
build or fix next.

## Next

- **Check everyone is running the same mods when a game is created or joined.** Different mod lists
  between host and guest desync the session in ways that look like our own bugs. Send the enabled
  mod list (Workshop id and version) with the join handshake, compare it, and if it differs show
  the player exactly what is wrong with a one click fix: switch on or off the mods they already
  have, and open the Workshop page for anything they are missing. Worth checking first whether the
  game's mod loader can enable a mod without a restart; if it cannot, the fix button has to apply
  the change and restart the game.
- **Builders forget to finish construction after a player rejoins.** Reported 2026-09-20.
  Workaround for now is to delete the site and place it again.
- **A guest's kingdom log shows Year 1 after a load**, and **a guest's food can reset to 0**. Both
  parked, neither reproduces solo yet.
- **Test round for 0.15.0**: guest resources after a reload, homeless counts, immigration, and that
  every popup opens and its buttons work.

## Later

- **The server browser lists nothing.** Joining works through Steam invites, so this is deferred.
- **Building storage contents are not synced** (granaries, stores). Diplomacy tribute works around
  it by settling on one machine.
- **Villager hunger, health, job assignments and construction progress** on another player's sites
  are not synced either, and are the most likely home of the builder report above.
