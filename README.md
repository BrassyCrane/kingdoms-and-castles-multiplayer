# Kingdoms and Castles Multiplayer

Steam multiplayer for [Kingdoms and Castles](https://store.steampowered.com/app/569480/). Host or
join through a Steam lobby; every player builds and runs their own kingdom on a shared map, with
troops, trade, diplomacy and saved sessions.

**Install:** subscribe on the Steam Workshop:
<https://steamcommunity.com/sharedfiles/filedetails/?id=3751021307>

This repository is the source of that Workshop item. It is still in beta.

## Reporting a bug

A report with logs gets fixed. A report without them usually cannot be. Please attach **both**:

- **Player.log**, which is where most errors actually end up:
  `%USERPROFILE%\AppData\LocalLow\LionShield\Kingdoms and Castles\Player.log`
  (paste that into the Windows Explorer address bar). It is overwritten every launch, so copy it
  **before** starting the game again.
- **output.txt**, the mod's own log: in Steam, right-click Kingdoms and Castles, *Manage >
  Browse local files*, go up to `steamapps\workshop\content\569480\3751021307\`. It grows across
  runs, so the end of the file is the part that matters.

Say how many players were in the session, who was hosting, and whether it was a new game or a
loaded save. Open an issue here, or comment on the Workshop page.

## Contributing

Fixes are very welcome, and they ship on the main Workshop item with your name on them. See
[CONTRIBUTING.md](CONTRIBUTING.md).

## Credits

Created by **BrassyCrane**. Maintained by **BrassyCrane** and **Mr.Sajtos**.

**Bill Kerman** found and fixed, in his community patch (merged in 0.14.0):

- farms never harvesting (a destroyed cave container made every autosave fail, which stopped the
  season change reaching the farms)
- other players' kingdoms having no gold capacity, so gold stayed at zero and merchant orders
  snapped back to 0
- every island being staffed by the local player's job settings
- buildings finished on another player's machine never being fully set up
- per-island records sized before the map existed, which left buildings, keeps included, missing
  from their island
- guests joining a loaded save: being given a new kingdom, being asked to place a castle again,
  the load stopping half way, and starting out looking at the host's castle
- the save transfer stalling while the host is paused, and a guest disconnecting itself during a
  large transfer; he also rebuilt the transfer to request chunks in windows
- kingdoms loading without their buildings and drawn in pink
- the old game staying live behind the main menu, and a previous save's town left on a new map
- AI kingdoms appearing in multiplayer games
- a reconnecting player being given a different team
- wolf packs growing differently on each machine
- the world ticking once per kingdom instead of once per frame
- a raid error being able to freeze the world clock (he contains the error; its cause is still unknown)
- loaded games reverting to Peaceful difficulty

He also added dragon flight sync, alliance requests that need both sides to agree, two seasons'
notice for war, demands and offers between kingdoms, the diplomacy popups, per-kingdom export
prices, and the merchant arrival banner for other players' ships.

Networking uses **[RiptideNetworking](https://github.com/RiptideNetworking/Riptide)** by Tom
Weiland, under the MIT licence, which requires its notice to ship with it; see
[LICENSE.md](LICENSE.md).
