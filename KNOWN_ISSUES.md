# Known issues

Problems we know about in the current version, **0.16.17**. If you run into one of these, a report is
still useful, especially with your output.txt attached (see the [README](README.md#reporting-a-bug)
for where to find it). Anything not on this list, please report.

Each entry says what you see, and what to do about it if there is a workaround.

## Mac

- **The Mac version of the game is an older build than the Windows one.** The error that stopped
  the mod loading there is fixed in 0.16.5, but a Mac player in a game with Windows players has not
  been tried yet. If it goes wrong for you, please send your output.txt.
- **Players on an older game build** (including the Mac build, if it has not had the November 2026
  update) do not see each other's trees wobble while being chopped. It is cosmetic.

- **A player on an older game build cannot join an Extra Large map.** Their game has no such size
  and builds a different map. Everyone in the game needs the November 2026 game update.

## Extra Large maps

- **Extra Large maps are new and have not been played in multiplayer yet.** Rejoining a long game on
  one sends a lot more data than on smaller maps. If someone drops or the game stalls, please send
  both players' output.txt.

## Loading a saved game

- **A multiplayer save does not open from the main menu's Load list.** Open **Multiplayer** from the
  main menu, press **Load** and pick it there. You can start it alone if nobody else is joining.
- **Two copies of the mod installed together stop saves loading.** This happens when you are
  subscribed to a second Workshop item that contains the mod: an old or duplicate upload, or an
  add-on that ships its own copy. The mod says so at the main menu. Unsubscribe from the other
  item and restart the game.
- **Villagers standing still next to another player's barracks, wanting a home**, and the
  homeless count climbing with free beds. Fixed in 0.16.15: when a barracks was
  knocked down or destroyed, its soldiers came back as villagers on every machine instead of
  only on its owner's. If it still happens after that, please send both players' output.txt.

## The map

- **Army positions are corrected in batches**, so a fast chase can briefly look different on each
  screen.
- **An error in the raid system is caught** so it can no longer freeze the game clock, but what
  causes it is not known yet. A year may pass without a raid when it happens.

## Diplomacy and trade

- **The Hall of Diplomacy's conversation screen only opens for the host talking to an AI kingdom.**
  Anyone else's envoy opens the Ctrl + Shift + D window instead, which covers both players and AI
  kingdoms. (Fixed on the development version, not released yet: before that, envoys do not work
  in multiplayer at all.)

## Lobby

- **Joining a game from the list sends you back to the menu.** The list shows games from every copy
  and version of the mod, and an old one cannot play with yours. Make sure you and the host are on
  the same version and subscribed only to this mod (unsubscribe from any other copy). The next
  version tells you when this is the reason.
- **The server browser does not list games.** Join through a Steam invite instead.

## Fixed in 0.15.0

Listed here because they were on this page for a while: a guest's kingdom log showing Year 1 after a
load, a guest's food resetting to 0, tax rates going back to 0 when a session was loaded, a guest's
kingdom coming back with its resources gone and its villagers homeless, and host and guest ending up
on different maps after the Size or Rivers setting was changed.
