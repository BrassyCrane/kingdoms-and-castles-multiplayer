# Changelog

## Next version (not released yet)

- Envoys from the Hall of Diplomacy work in multiplayer. Walking one to another kingdom's keep
  opens diplomacy with that kingdom, and a gift it carries can be handed to another player or
  to an AI kingdom. Envoys used to throw errors every moment they existed.
- The Hall of Diplomacy tells you when a gift is ready for your envoy to pick up.
- Joining a game hosted on a different version of the mod now says so, instead of sending
  you back to the menu. Old copies of the mod still show their games in the list.

## 0.16.17

- Witch huts are back in multiplayer. Every player sees the same huts and has their own
  dealings with the witch, and the spell is paid from your own gold.
- Joining a game is about four times quicker: the save is packed before it is sent.
- New lobby setting, "Witch spells hit everyone": when ticked, a witch's spells and curses
  reach every kingdom instead of only the kingdom of the player who dealt with her.

## 0.16.16

- Custom banners now show on every player's screen, on flags, armies, ships and in the lobby,
  instead of only on your own. Other players used to see a built-in banner in its place.
- Other players' workplaces now show their workers on your screen. Their villagers used to
  stand idle in your view because only their own game knew who worked where.
- Other players' villagers are now as hungry, healthy or sick on your screen as on theirs.

## 0.16.15

- Fixed villagers appearing next to another player's barracks when it was knocked down or
  destroyed, standing there forever wanting a home and pushing the homeless count up.

## 0.16.14

- Fixed chopping a tree sometimes removing a different tree, or two trees, on the other players'
  screens.
- Fixed a merchant ship sometimes not appearing for a guest when it was sailing to another
  player's dock.
- Fixed an error when the host opens a lobby.
- Players whose Workshop mods differ from the host's can no longer join. They are told which
  mods are missing or extra, and one button subscribes and unsubscribes on Steam and closes the
  game, ready to restart and rejoin. Different mods made the game drift apart in ways that looked
  like multiplayer bugs.
- Mods copied into the game's local mods folder are compared too. A player whose local mods
  differ from the host's is told which ones, by the name of their code, and cannot join until
  they match.
- Fixed other players' houses, markets and bakeries on your screen thinking there was nothing
  to collect, so they gathered differently than on their owner's screen.

## 0.16.13

- Players on a different version of the mod can no longer join a game. They are told which
  version the host is running and how to update. Different versions read each other's messages
  differently, which caused errors that did not look like a version problem.

## 0.16.12

- Joining a game whose host had already left froze for a moment and dropped you at the main
  menu with no reason. It now says the game has ended.
- Fixed the mod failing to read a building's construction progress since the November 2026 game
  update (only affected a log line).

## 0.16.11

- Fixed a guest's kingdom coming back from a multiplayer save with its resources gone or below
  zero and its villagers homeless. Saves now keep each player's kingdom as that player had it.

## 0.16.10

- Fixed a multiplayer game started since the November 2026 game update where only one player
  could place their castle, the other saw their kingdom's stats as all zeroes and could not build.

## 0.16.9

- The new Extra Large map size can be picked in the multiplayer lobby.
- Fixed Random in the lobby's map size list making an Extra Large map since the November 2026 game
  update.

## 0.16.8

- Fixed the mod failing to load after the November 2026 game update ("Compilation failed").

## 0.16.7

- Fixed most of the villagers in a kingdom standing frozen after a multiplayer save was loaded,
  with frozen copies of them left standing on the map. This affected every loaded game since
  0.16.0.

## 0.16.6

- The mod now warns you at the main menu when a second copy of it is installed: an old or
  duplicate upload, or an add-on that ships its own copy. With two copies everything ran twice, and
  loading a saved game froze or crashed the game. That load is now stopped with a message instead.
- Picking a multiplayer save in the main menu's own Load list tells you where to open it (Multiplayer,
  then Load), instead of ending on a load error with the game running broken behind it.
- Players on different versions of the mod are told so when they join each other again. The check
  had compared every version since 0.16.0 as equal.

## 0.16.5

- Fixed an error that stopped the mod loading on the Mac version of the game.

## 0.16.4

- Fixed players dropping out of a long game with "Timed out", usually around year 20 to 25, once
  their kingdom had grown large.
- The game sends far less data between players in normal play.

## 0.16.3

- One log to send: output.txt now also holds the game's warnings and errors, so a bug report no
  longer needs Player.log as well.
- The log shows each connection's ping, send queue and data rate, and which messages send the most,
  every 15 seconds. This is to find why a player can drop out of a long game with "Timed out".

## 0.16.1

- Leaving a game and coming back gives you your own kingdom as you left it. Since 0.15.0 the host
  was meant to save each player's kingdom from that player's own copy, but every copy was thrown
  away on arrival, so a returning player got the host's guess of their town instead.

By Bill Kerman:
- The Ctrl + Shift + E window and the Demand and Send Aid buttons use the game's own art again.
- An export price can no longer be set to 0, and the price buttons are greyed out for goods you
  are holding back.

## 0.16.0

AI kingdoms, by Bill Kerman:
- AI kingdoms in multiplayer. The host adds them in the lobby with "Add AI", sets each one's
  difficulty, and can change its name and flag. A loaded save lists its own AI kingdoms.
- Players pick their islands first; AI kingdoms settle after everyone has a castle.
- Every player sees the AI kingdoms' islands, castles, buildings and flags. The AI runs on the host.
- Ctrl + Shift + D works with AI kingdoms for everyone: alliances, war, peace, tribute and gifts,
  with the AI's opinion of you shown. Their demands, peace offers and warnings reach every player as
  popups.
- Your villagers no longer move into AI houses or take AI jobs, which left AI people frozen and
  starving.

Also by Bill Kerman:
- The year and season follow the host, so a menu or lag no longer leaves players years apart.
- The dragon countdown and the "DRAGON SIGHTED!" banner are the same on every screen.
- Foreign merchant ships and Viking raids are run by the host and shown the same to everyone.
  Merchants now visit every player's docks.
- What is inside warehouses and granaries is the same on every screen.
- A cleared wolf den disappears on every screen.
- Allies' land no longer goes back under the fog after loading a save.
- Joining no longer stalls part way through loading a big save.
- A save that fails no longer freezes the game. The protection meant to stop this never worked.
- A player on a different version of the mod is told so when they join, and so is the host.
- The Ctrl + Shift + D window uses the game's own art.
- Opening a menu in multiplayer no longer pauses your own game, so you stay in step with everyone
  else. Single player still pauses.

## 0.15.5

- Rejoining a game in progress gives you the game you left. A player who rejoined while the host
  had a menu open used to be sent a new map instead of the world, so their kingdom was gone and
  they started again on their own map while the host played on.
- Rejoining players no longer land on the name-and-banner screen. Your kingdom is still there and
  it comes from the host, so the lobby now shows the world arriving instead.
- A player joining a lobby no longer rebuilds the map for everyone already in it.
- Players with no kingdom in a game that has started are turned away even when the host has a menu
  open.

## 0.15.4

- The game no longer grinds to a halt and then drops you out of the session. A fault in the
  networking library made it resend the same messages over and over, twice as many every frame,
  until nothing else could get through. This is what 0.15.3 raised the timeouts for; the timeouts
  were only holding the door open longer.

## 0.15.3

- Players are no longer dropped from a session for no reason. The game pausing for a few seconds,
  an autosave on a big map for instance, no longer counts as a player having left.
- Player.log no longer grows to tens of megabytes when a connection is busy. The one line that was
  repeated hundreds of thousands of times is now a short report every few seconds.
- A joining player's own caves turn into wolf dens again, and every player sees the same dens in
  the same places. They used to leave bare ground and an error on every frame.

## 0.15.2

- Builders no longer stop working on construction sites after a player joins or rejoins. Deleting
  and replacing the site is no longer needed.
- Only a kingdom's own game decides which villager takes which job, so two games no longer pick
  different workers for the same job. By Bill Kerman.

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
