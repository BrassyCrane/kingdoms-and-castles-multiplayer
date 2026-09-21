# Changelog

## Unreleased

- Builders no longer stop working on construction sites after a player joins or rejoins. Deleting
  and replacing the site is no longer needed.

## 0.15.1

- Only a kingdom's own game settles people into its houses, so immigration no longer runs past
  the housing cap and villagers are no longer left homeless for good. Reported by BlueJay.
- Other players' news no longer shows up in your kingdom log.
- Boats are no longer drawn hot pink.
- Other players' islands are staffed by their own job settings on your machine too. The 0.14.0
  fix for this never took effect.
- The host's lobby screen closes when the game starts, as it already did for guests.
- Villagers, soldiers, carts and ships no longer stand still for good when the game fails to find
  them a route. They get "no route" and pick something else to do.

## 0.15.0

- Your kingdom is saved from your own game, not from the host's copy of it. Reloading a save no
  longer wipes a guest's resources, leaves villagers homeless for good, or lets new settlers flood
  in to fill the houses they left. Reported by BlueJay.
- Villagers who die are removed from their own kingdom everywhere. Soldiers trained out of a town
  no longer count as homeless on the other players' machines. Reported by DragonTheLarp.
- Loading a save repairs any houses, residents and homeless lists that disagree, so a session saved
  by an older version comes back healthy.
- Tax rates are synced, so the host saves the rate each player actually set.
- Houses, farms and blacksmiths belong to their own kingdom: no more taxing every house in the
  world at your rate, counting your windmills for someone else's farm, or your advisor complaining
  about another player's full blacksmith.
- A saved game now gets its own lobby: the world settings you cannot change are replaced by what
  the save holds, and the player list shows the save's kingdoms, dimming the ones whose player has
  not joined yet.
- New loading screen, and the bar is brightest where the loading is.
- The diplomacy window is wider, and Demand and Send Aid are real buttons. Demand could not be
  clicked before.
- The demand, alliance and trade popups, the resource picker and the export price window are
  rebuilt to match the rest of the mod. The picker's buttons no longer sit outside its window.
- Buttons look and sound like the game's own.
- Players joining a game in progress no longer cost the others their view of each other's kingdoms.

## 0.14.0

From Bill Kerman's community patch:

- Guests joining a loaded save keep their own kingdom instead of being handed a new one
- No more "place your castle" over a kingdom that was just loaded
- Fixed a case where loading stopped half way while restoring another player's keep
- Fixed an issue that could stop farms, orchards, fishing and livestock from harvesting
- Gold capacity counts your own throne room, gold is no longer stuck at 0 and merchant orders stay where you type them
- Buildings finished on another player's machine are now fully set up
- Fixed buildings and keeps on later islands not being recorded for their island
- Save transfer rebuilt, big saves no longer stall or drop the joining player
- Fixed a case where a kingdom loaded without its buildings, or drawn in pink
- Fixed returning to the main menu leaving the old town standing on the next map
- Fixed AI kingdoms appearing in a multiplayer game started after a single player one
- Fixed a reconnecting player being given a different team
- Fixed wolf packs growing to a different size on each machine
- Fixed production outpacing the calendar as more players joined
- An error in the raid system no longer freezes the world clock
- Fixed a loaded game coming back on Peaceful whatever difficulty it was saved on
- Dragons fly the same path and breathe fire at the same time for everyone
- The logic behind alliance requests needing both sides to agree, and two seasons' notice for war
- The logic behind demands and offers between kingdoms
- The logic behind export prices, what your kingdom charges other players
- The merchant banner now also shows when another player's ship docks at your port

More fixes:

- Tribute is paid once, both sides always agree on what was actually paid
- Tool use settings are no longer switched back on every time you load
- Dragons no longer keep restarting their fire breath
- Open offers, demands and declared wars no longer carry over into your next game
- The resource picker now closes with Escape

Other changes:

- Viking raids and army position sync, both already written and sitting behind switches, are on by
  default now
- Source code is now on GitHub

## 0.10.1 and earlier

Released on the Steam Workshop before this repository existed; see the item's change notes there.
