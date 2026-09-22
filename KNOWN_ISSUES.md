# Known issues

Problems we know about in the current version, **0.15.2**. If you run into one of these, a report is
still useful, especially with your Player.log attached (see the [README](README.md#reporting-a-bug)
for where to find it). Anything not on this list, please report.

Each entry says what you see, and what to do about it if there is a workaround.

## Loading a saved game

- **A big save takes about a minute to send** to each player joining it. The loading bar is slow,
  not stuck.

## The map

- **Witch huts are switched off** in multiplayer.
- **Army positions are corrected in batches**, so a fast chase can briefly look different on each
  screen.
- **An error in the raid system is caught** so it can no longer freeze the game clock, but what
  causes it is not known yet. A year may pass without a raid when it happens.

## Diplomacy and trade

- **The Hall of Diplomacy is disabled in multiplayer**, because the game only opens it when AI
  kingdoms exist. Use **Ctrl + Shift + D** instead.
- **Merchants only visit their own kingdom's docks.** A merchant heading for another player's port
  is removed, so merchant traffic between kingdoms does not happen yet.

## What other players see of your kingdom

These are not synced yet, so the other machines hold their own guess until something corrects it:

- **What is inside your granaries and stores.**
- **How hungry or healthy your villagers are.**
- **Which job each of your villagers is doing.** Only your own game puts your villagers to work,
  so on other players' screens your workplaces show no workers and your stores can look empty.

## Lobby

- **The server browser does not list games.** Join through a Steam invite instead.

## Fixed in 0.15.0

Listed here because they were on this page for a while: a guest's kingdom log showing Year 1 after a
load, a guest's food resetting to 0, tax rates going back to 0 when a session was loaded, a guest's
kingdom coming back with its resources gone and its villagers homeless, and host and guest ending up
on different maps after the Size or Rivers setting was changed.
