# Known issues

Problems we know about in the current version, **0.14.0**. If you run into one of these, a report is
still useful, especially with your Player.log attached (see the [README](README.md#reporting-a-bug)
for where to find it). Anything not on this list, please report.

Each entry says what you see, and what to do about it if there is a workaround.

## Loading a saved game

- **A guest's kingdom log shows Year 1 after loading a save**, instead of the save's actual year.
- **A guest's food can be reset to 0 after loading a save.** Keep an eye on your granaries right
  after a load.
- **Tax rates for other players' kingdoms are not saved** and go back to 0 when a session is
  loaded. Fixed for the next version.
- **A big save takes about a minute to send** to each player joining it. The loading bar is slow,
  not stuck.

## The map

- **Host and guest can end up on different maps** if the world Size or Rivers setting is changed in
  the lobby. Workaround: after changing either setting, press **New World** before starting.
  Fixed for the next version.

## World and units

- **Witch huts are switched off** in multiplayer.
- **Army positions are corrected in batches**, so a fast chase can briefly look different on each
  screen.
- **Longboats can look wrong on a guest's screen** (reported).
- **An error in the raid system is caught** so it can no longer freeze the game clock, but what
  causes it is not known yet. A year may pass without a raid when it happens.

## Diplomacy and trade

- **The Hall of Diplomacy is disabled in multiplayer**, because the game only opens it when AI
  kingdoms exist. Use **Ctrl + Shift + D** instead.

## Lobby

- **The server browser does not list games.** Join through a Steam invite instead.
