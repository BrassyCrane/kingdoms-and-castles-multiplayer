# Open threads

Bugs, traps and working notes for the multiplayer mod. Current as of 2026-07-31.

## Fixed 2026-09-07

- ~~**Building flags show the local player's banner for everyone.**~~ **ROOT-CAUSED AND FIXED.**
  Two separate causes, and the second was only found because the acceptance run printed what it
  could see.

  First, nothing ever repainted. Both flag systems resolve their owner once and then wait on
  `Player.updateBanner` for a refresh: `FlagMaterialUpdater` reads
  `World.GetLandmassOwner(cell.landMassIdx)` in `OnEnable` and falls back to `Player.inst` when that
  is null, and `IGBanner` reads `World.GetLandmassOwnerByTeamId(b.TeamID())` in
  `OnBuildingPlacement`. Both run before `LandmassOwner.TakeOwnership` can have claimed the island,
  because a keep must exist before it can claim the ground it stands on. Only
  `Player.SetIndexedBanner` invokes that delegate, on the player it is called on.

  Second, and worse: a flag subscribes to whatever `Player.inst` was when it woke up, and the mod
  deliberately swaps `Player.inst` to the owning remote player while placing their buildings. So the
  flags on another player's town are subscribed to THAT player's delegate, which nothing in the game
  ever invokes. Refreshing only the local list would have fixed our own flags and left theirs
  exactly as wrong.

  Fix: `Main.RefreshAllBanners` walks EVERY kingdom's delegate, invoking subscribers one at a time
  (a multicast invoke stops at the first that throws, and `IGBanner` and `Building.TeamID()` both
  dereference a landmass owner with no null check). A dirty flag set from a `LandmassOwner.
  TakeOwnership` postfix coalesces the repaint so a burst of placements costs one pass.
  Asserted by `the peer's keep flag flies the peer's colours`.

- ~~**Witch huts came back when a multiplayer save was loaded.**~~ **FIXED.** Live spawns were
  suppressed correctly, but the suppression had an exception for "while unpacking a save", written
  believing the save restores huts through `WitchHutSaveData.Unpack`. It does not:
  `WitchHut.WitchHutSaveData` has no methods and nothing references it. The only callers of
  `World.AddWitchHut` are `MapEdit.ApplyWitch`, `World.PlaceCavesWitches`/`GenLand` and
  `World.UpscaleFeatures`, and `LoadSaveContainer.Unpack` runs `UpscaleFeatures` to turn saved
  terrain back into objects. A flag around `UpscaleFeatures` now tells "the world is being rebuilt"
  apart from "a save is restoring a specific hut". Found by reading the one diagnostic line the hook
  had been leaving in the log for exactly this purpose.

- **Siege catapults, wolves and streamer effects now sync.** See [[sync_coverage]] in memory. The
  catapult was the larger job than it looked: they existed on ONE machine, since `Barracks.Tick` is
  gated to the owner and the catapult branch never went through `MakeArmy`.

## Known bugs, not yet fixed

- **Hosting from a save silently loaded nothing.** ROOT-CAUSED AND FIXED 2026-09-03, from a user log
  (Rednax, 2026-09-02).

  The host did pick a save (`menu state -> Load`, then `host: loading multiplayer save 'world'`), but
  `[LOAD] reading` never followed, so the only route out of `LoadSaveLoadAtPathHook.Prefix` was
  `if (!File.Exists(savePath)) return false;`, which logged nothing at all. Both joiners then got
  `no save data to send`, the session started anyway announcing `paused: save loaded`, and the host
  quit within a minute, twice, before giving up and hosting a fresh world.

  Why the file was missing: multiplayer saves live in `Saves/Multiplayer/<guid>/`, and the load list
  is built from `GetSavePaths()`, which is `Directory.GetDirectories(GetSaveDir())`. The mod's
  `GetSaveDir` redirect was gated on `NetClient.client.IsConnected` **alone**, so a list built before
  the host's own client socket connected enumerated `Saves/` instead. `Saves/` holds every
  single-player save plus the `Multiplayer` folder itself, and that folder is the only entry with no
  `world` file inside it. Picking it produced `Saves/Multiplayer/world`, which does not exist. (The
  deduction is tight: had the list come from `Saves/Multiplayer/*`, every entry has a `world` and
  `File.Exists` would have succeeded.)

  Two fixes: the redirect now tests MP **intent** (`IsConnected || NetHost.IsRunning ||
  SteamLobby.loadingSave`), so it is right from lobby creation onward rather than from socket
  connection onward; and the missing-file branch now logs `[LOAD] FAILED, no save file at '<path>'`
  and shows the host a dialog, naming the "you picked the container folder" case specifically.
  **Vanilla's own `LoadAtPath` returns silently in this situation too, which is what the mod was
  inheriting.** Single-player is untouched: none of the three conditions hold there.

- **A repeated `CompleteBuild` on one already-built building.** Same log: a single `smallhouse`
  guid hit the idempotency guard 510 times in four seconds, about once per frame. The guard did its
  job, but nothing should be re-completing a built building at frame rate. Vanilla removes a
  building from `Building.ConstructionList` as soon as `IsBuilt()` is true, so a repeat should be
  impossible, and the cause is **not pinned**. The log is now throttled to one line per building so
  it cannot drown a session again; the throttle is not a fix. Note `ConstructionList.Add` has no
  duplicate check, which is one place to start looking.


- ~~**The connection gate is off by one.**~~ **Not a bug. Settled 2026-09-03 against a real
  3-player log.** `MaxPlayers` means TOTAL players including the host (and is clamped to the landmass
  count, one kingdom per island). The lobby display uses the same `ClientCount`, so the two agree:
  the log shows `client 3 connected (3/3)` accepted with a host and two others, and a fourth would
  be `4 > 3` and rejected. The original note assumed `MaxPlayers` meant "besides the host", which it
  does not. **Left here deliberately: changing `>` to `>=` would CREATE the off-by-one it was
  supposed to fix.**

- ~~**Auto keep placement has only been tested on the failure path.**~~ **VERIFIED WORKING
  2026-09-05.** A real fresh-host session logged `[lobby] placing BrassyCrane's keep on landmass 1`
  followed by `[net] keep place: landmass 1 at 12,28`, which is exactly the success line this entry
  asked for. The success path has now run.

- ~~**`StartGame reflection failed, falling back` is BACK.**~~ **Explained and quieted 2026-09-05.**
  It was never intermittent, it happens on every multiplayer session, and the old message was wrong
  about the cause. `MainMenuMode.StartGame`'s LAST act is to build the rival-kingdom config by
  reading `RivalKingdomSettingsUI.inst.rivalItems`; that screen is never shown in multiplayer, so
  `inst` is null and it throws there. Everything the world needs has already run by that point:
  `SetNewMode(playingMode)`, `SetupInitialPathCosts`, `CombineStone`, `GenerateStoneUIs`. Only the AI
  kingdom setup is lost, which is exactly what this mode does not want anyway.

  So the catch IS the normal path and now says so. Two real improvements came out of writing that
  down. The fallback used to call `SetNewMode(playingMode)` a second time, because it assumed the
  throw meant play mode was never entered; it now checks `GameState.inst.IsPlayMode()` first and
  skips it (verified in a run: `already in play mode: True`). And the message unwraps
  `ex.InnerException`, because reflection reports every failure as the same useless "Exception has
  been thrown by the target of an invocation" and there would be no way to notice this starting to
  fail for a DIFFERENT reason.

- ~~**Rebuilding rubble is local only.**~~ **Fixed 2026-09-03.** Half of it already worked and that
  is why the symptom was odd: `Rubble.Rebuild` places the replacement buildings through
  `World.Place`, which is hooked and broadcast, so other players DID get the new building. What
  never travelled was the REMOVAL of the ruins, leaving them with the new building standing on
  rubble that was, for them, never cleared.

  Now broadcast as `RubbleClearMessage` from a Prefix/Postfix on `Rubble.Rebuild`. **Identified by
  CELL, not guid**, and that is the crux: rubble is created independently on every machine when a
  building falls, each creation rolling its own `Guid.NewGuid()`, so the same ruin has a different
  id everywhere and a guid lookup finds nothing. Position is the one thing every machine agrees on.
  The cell is captured in the Prefix because by the Postfix the rubble is gone.

  One cell suffices for a cluster: the receiver hands what it finds to the public
  `World.TryClearRelatedRubbles`, which walks out to the rest of the ruin itself. The receiver also
  checks the cell actually holds rubble before removing anything, since the replacement building
  arrives on its own message and the two can land in either order.

- ~~**Foreign merchants are provisioned from the wrong kingdom.**~~ **Moot as of the foreign-merchant
  fix; re-checked 2026-09-03.** `StartSailing` does stock the hold and roll prices from `Player.inst`,
  but `MerchantShipStartSailingHook` now destroys every foreign merchant whose destination dock is
  not on one of the LOCAL player's landmasses. So every merchant that survives to be provisioned is
  visiting the local player, and `Player.inst` is exactly the right kingdom to provision from. This
  becomes live again the moment foreign merchants are allowed to visit other players' docks.

- **A departed kingdom is only partly frozen.** PARTLY ADDRESSED 2026-09-03, behind
  `Main.FreezeGhostKingdomsFully` (default false, untested).

  `PlayerUpdateFreezeHook` stops `Player.Update` for a ghost, which covers kingdom-level simulation.
  **Armies and ships are now gated too**, via `Combat/FrozenKingdoms.cs`: a cached set of absent
  teams, rebuilt once a second, so the per-tick question is a set lookup and the set is empty (and
  short-circuited) whenever nobody has left. Armies are gated at `UnitSystem.UpdateGroup` /
  `UpdateGeneral`, ships at `ShipBase.Tick`. Both patched manually and guarded, because the two army
  methods are PRIVATE and a rename in a game update must cost only this feature, not the atomic
  `PatchAll`. **`UpdateGeneral`'s return value decides whether an army is destroyed, so the skipped
  path returns true**; returning false would delete the kingdom the freeze exists to preserve.

  **Buildings are deliberately NOT gated.** They tick as individual `Tickable` components through a
  flat 25,000-entry loop (`Tickable.TickAll`) that has no owner reference to hand, so gating them
  means a `GetComponent` per entry per frame. That is exactly the per-tick cost that once starved
  this mod's simulation into a freeze, and it is not worth paying for a departed kingdom's
  production. Villagers are likewise still simulated. A cheaper route, if it ever matters, would be
  caching the owning team on the component when a building is placed.

  Ship subclasses still run their own `Tick` bodies (a merchant's route logic, say); only the shared
  base, where movement and pathing live, is gated. The ship stops moving rather than stopping
  thinking, which is the half other players can see.

- ~~**A route delivery and a counter trade can both charge for the same goods.**~~ **Re-examined
  2026-09-03 against the decompiler; it does not reproduce as described.** The two paths measure
  disjoint quantities and cannot overlap:
  - `SettleForeignDelivery` bills a *hold difference measured across one `Ship.Tick`*, so it can only
    ever charge for goods that left during that tick.
  - A hand trade removes from the hold via `ApplyMerchantTrade`, which runs while the router is
    processing messages, never inside a `Ship.Tick` call. Prefix, original and Postfix are one
    invocation, so a network apply cannot land between the two observations of the hold.

  So the same units cannot be counted by both. Left here as a *statically* verified conclusion, not a
  runtime-observed one. What the re-examination did turn up is a genuine desync, now fixed, see the
  clicker entry below.

- **Vanilla settles a merchant trade against the CLICKER, not the dock owner.** Fixed 2026-09-03.
  `MerchantUI`'s buy lambda gates on and deducts from `Player.inst.PlayerLandmassOwner.Gold`, and its
  sell lambda credits `World.GetLandmassOwner(dock.LandMass())`. The mod's hook modelled the buyer as
  the *dock owner*, which is the same kingdom only in the intended flow. Selection has no team check
  (`GameUI.AddToSelection` calls `OnSelected` without one), so anyone can open a docked merchant, and
  when a non-dock-owner did, vanilla charged one kingdom while the broadcast told every other machine
  a different one had paid. `PrepPlayerMerchantTrade` now treats the clicker as the buyer and refuses
  to sync a trade where the clicker does not own the dock, logging when it declines.

## M9 merchant trade prompt (done 2026-09-05), and a log-noise pass

**M9 needed no new UI.** `ShipBase` gives EVERY ship an `issueButton`, the in-world exclamation,
whose click sends `OnClickedButton`; `Ship.OnClickedButton` opens `MerchantUI` for a PlayerMerchant.
So switching that button on when another player's merchant docks at your port IS the trade prompt:
the marker appears over the visiting ship and clicking it opens the trade window. Paired with a
`KingdomLog` line naming the kingdom, since a marker on a ship you are not looking at is easy to
miss. The marker is taken down when the merchant leaves or moves to somebody else's port, so a stale
exclamation cannot hang over a ship with nothing to offer.

**`GameUI.merchantNotification` is deliberately NOT used**, even though vanilla pairs it with the
issue button for foreign merchants. Its view button resolves through `GetDesiredTrackingPos`, which
only counts ships of type `Merchant`; a player merchant is type `PlayerMerchant` and therefore
invisible to it, so clicking through would centre the camera on your own keep instead of the visitor.

### Log noise, cut where it was drowning the signal

In the one real 3-player log we have, **551 of 2,276 lines were `[HEARTBEAT]`** - a quarter of the
session's diagnostics. Each also walks every villager in the world to sum their positions, thirty
times a minute. The freeze it was written for is long since root-caused (a failing autosave), but it
is still the thing that would catch a recurrence, so it stays and its cadence now depends on the
build: every 2s under `DevTestBuild`, every 15s otherwise. A freeze lasts minutes, so 15s still
catches one with room to spare, at a seventh of the noise and a seventh of the work.

The two per-frame merchant transit dumps (`[MERCHANTPATH]`, player and foreign) are now behind
`DevTestBuild` as well. They were written to diagnose the "waiting to reach dock" stall and write per
merchant every couple of seconds; that is a debugging tool, not something to ship to players.

## Diplomacy screen (done 2026-09-05), and the Ctrl+Shift+D hotkey retired

`Lobby/DiplomacyWindow.cs` drives `diplomacyui`/`diplomacyrow` from the bundle: one row per other
kingdom, its banner, where you stand, and Neutral / Allies / War.

**Ctrl+Shift+D now opens it instead of cycling.** The hotkey used to walk this kingdom's standing
toward EVERY player at once, Neutral to Enemy to Allies, purely because there was nowhere to express
"this kingdom, this standing" (the game's own DiplomacyUI is built around AIKingdom and cannot be
pointed at a player). The key keeps its meaning; the blunt instrument is gone, and
`CycleRelationsWithEveryone` was deleted with it.

Three decisions worth keeping:

- **It builds its OWN Canvas.** `ModalDialog` parents under `MenuUi.Root`, which is
  `mainMenuMode.mainMenuUI` and is INACTIVE once play begins, so a window hung there would be
  invisible exactly when wanted. An own canvas also drops the dependency on the base game's menu
  hierarchy that `MenuUi` itself warns is what breaks on a game update. It creates an EventSystem
  only if the scene has none, since without one no button responds.
- **The prefabs load as OPTIONAL** in `LobbyPrefabs`. A missing lobby prefab means no lobby at all
  and rightly fails loudly; diplomacy is one screen, so an older bundle should cost that screen and
  nothing else. Startup says which it got.
- **Clicking a button changes nothing locally.** It sends the request and lets the relation apply
  when the message comes back, which is the same path a peer's declaration takes, so there is one
  code path performing the gate rebake and dock closing. A refresh every 20 frames while the window
  is open is what makes someone else's declaration appear without reopening it.

**Untested in game**, like everything else awaiting a session.

## In-game chat (Q4, done 2026-09-05) and what the UI canvas actually blocks

**Q4 is done without touching Unity.** `Lobby/InGameChat.cs` is an IMGUI overlay, the same approach
`PasswordPrompt` uses: no prefab, no Canvas wiring, no asset bundle, so it cannot render invisible
and it ships without a bundle rebuild. Return opens and sends, Escape cancels, messages fade after
14s, and with nothing said and the box closed it draws nothing at all.

`RenderChatLine` now splits on play mode exactly as `RenderChatNotice` already did: chat rows are
instantiated into the lobby prefab, which is gone once the session starts, so a mid-game message used
to reach the handler and land nowhere anybody could see.

**The subtle part is the keyboard.** Typing into an IMGUI field does not stop the game reading the
keyboard, so a "d" mid-sentence would drive the build hotkeys underneath. `GameUI.UpdateForKeyboard`
is the single place those bindings are read, so it is Prefix-suppressed while the box is open. That
method is PRIVATE, so it is patched in the guarded manual block; it fails open, because a stuck
suppression would leave a player unable to control anything.

**Correcting the standing note that the UI canvas blocks three features.** It does not block them the
way that note implied:
- **Q4 in-game chat**: done, IMGUI, no bundle needed.
- **Diplomacy UI**: done, as a bundle prefab (`Lobby/DiplomacyWindow.cs`).
- **M9 trade accept/reject**: done, and it needed no new UI at all, see below.

**All three are now built, and the "one piece of Unity work" framing was wrong.** Each took a
different route, and only one of them needed the bundle.

A bundle is only needed for UI that must match the game's own typeface and art. **Note the Unity
project is NOT on this machine and no Unity editor is installed** (`docs/unity-workflow.md` still
points at the old machine's path). The prefabs are regenerable from `docs/prefab-contract.md` plus
the generator scripts, but that needs Unity 2019.4.40f1 installed and the project restored first.
Until then, IMGUI is the route that works.

## Hardening pass over the receive handlers (2026-09-03, finished 2026-09-05)

Second half of the pass, the lobby and world handlers:

- **`ApplyRoster` could lose the whole lobby to one bad entry.** The per-player work was unguarded
  and chained straight through `Main.kCPlayers[id].inst.PlayerLandmassOwner.SetBannerIdx(...)`, so a
  player whose kingdom object was not built yet threw and **abandoned the loop**, leaving everybody
  after them out of the roster. That is exactly the "empty player list" symptom the remote-player
  Reset crash produced. Now guarded per entry with a count reported at the end, and a player with no
  kingdom yet keeps their roster slot while their banner is deferred to the next roster.

- **`ApplyBanner` had the same unguarded chain**, and inconsistently: the *less* critical
  `SetIndexedBanner` below it was already wrapped, while the line that would throw first was not.
  The recorded choice is now stored before anything can throw, and the kingdom-dependent half is
  skipped for a player still joining, whose banner arrives again with the roster anyway.

- **`ApplyBuildPlace` needs no `NetApply.Scope()`, and that is correct, not an omission.** It places
  via `World.PlaceFromLoad`, which calls `PlaceInternal` directly; the mod patches the public
  `World.Place`, which `PlaceFromLoad` never touches. So no broadcast fires and there is nothing to
  suppress. (`FakePeer` does use `World.Place`, which is why its fixtures ARE scoped.)

- **The doubled `[SPEED] applied remote speed=` lines are not a bug.** `SpeedControlUI.SetSpeed`
  assigns Unity `Toggle.isOn`, whose setter fires `onValueChanged`, which re-enters `SetSpeed` with
  the same value. The second pass is still inside the apply, so nothing echoes. The log now prints
  only on an actual change, which removes the duplicate without losing any real transition.

## Hardening pass over the receive handlers (2026-09-03)

The receive halves have never run against a real peer, so they got a deliberate read-through rather
than waiting for a tester to find things. Four results:

- **`ApplyVillagerWarp` was broken.** It looked its target up with
  `Villager.villagers.data.Where(...)`, LINQ over the BACKING ARRAY. `ArrayExt.data` is longer than
  `Count`: unfilled capacity is null, and `RemoveAtSwap` decrements `Count` while leaving a stale
  reference past it. So the pass either dereferenced a null, which the catch swallowed as an error
  and no villager moved, or matched a villager that had already been removed and warped a corpse.
  Now a `Count`-bounded, null-checked loop, the same shape `ApplyVillagerSnapshot` already used. A
  sweep found this was the only site of that mistake; every other `.data[` access is Count-bounded.

- **Three receive handlers did a full scene scan each.** `FindObjectsOfType<Building>()` in the keep
  upgrade, terrain demolish and merchant trade handlers walks every object in the scene, per
  message. Drag-demolishing a row of twenty buildings sends twenty messages, so every remote did
  twenty back-to-back scene scans in one frame. They now call `Main.FindBuildingByGuidAnywhere`,
  which asks each player's own registry first and **falls back to the scan**, so a building that is
  in no registry yet (rubble, anything mid-placement) is still found. Correctness unchanged, cost
  removed from the common case.

- **The load-side twin of the `PackCell` crash.** `SessionSave.Unpack` refreshed materials with
  `restored.data[i].UpdateMaterialSelection()` and no null check, so one destroyed building in the
  list threw and took the whole load down. Now guarded per building with a count reported once.
  Refreshing a material is cosmetic; losing the save is not.

- **The relations pair key was open-coded in three places** that had to agree, two of which take a
  key apart. Extracted to `Net/TeamPair.cs` (pure, no Unity) and covered by headless tests, since a
  packing/unpacking disagreement would show up as wars reappearing between the wrong kingdoms after
  a load.

## Relations now persist (T6, done 2026-09-03)

A war used to be forgotten the moment anyone loaded, and was invisible to anyone who joined.
Relations live only in memory, so a reloaded session came back with everyone Neutral, silently
undoing a declaration and re-opening the docks it had closed. That mattered little while there was
nothing to fight over; it matters now that combat exists.

- **Saved** in the mod's dictionary block (`ModSessionData.relations`), keyed by the packed team pair
  `PlayerRelations` already uses, and restored at the very END of `SessionSave.Unpack`. Last on
  purpose: restoring rebakes the pathing gates and re-applies dock policy, which must happen against
  a world where every kingdom already exists and owns its landmasses.
- **Sent to a joiner** as ordinary `PlayerRelationMessage`s, one per pair, from
  `SessionHandlers.SendRelationsTo`. Reusing the live message means a joiner applies them through
  exactly the same path as a fresh declaration, gate rebake and dock policy included, with no second
  implementation to drift.
- `PlayerRelations.Restore` REPLACES rather than merges, because the snapshot is the whole truth
  about who is at war; merging would let a stale local entry survive a load. Gates are rebaked once
  at the end rather than per pair, since rebaking walks the map.

**The packed long key is the one fragile part and it is tested.** Every other dictionary in the save
block is string-keyed; this one is not, and JSON object keys are always strings, so it depends on
Newtonsoft converting a non-string key out and back. A headless test in `kcm-tests` asserts the
round trip, because the failure mode is silent: a save would load with every war forgotten.

## Troops and combat, the model

Written 2026-09-03. **Landmass authority: whoever owns the ground a blow lands on decides what that
blow did.** Unowned ground (open water, neutral islands) and ground whose owner has left both fall to
the host.

Deterministic lockstep was ruled out against the decompiler, not assumed: `Army.TakeProjectileDamage`
distributes a standard hit through `units.RandomElement()`, an `SRand` draw, and `SRand` is one global
RNG shared with everything; pathing is threaded; attacks resolve against live positions. Two machines
replaying the same orders reach different battles.

The reason this model works, rather than a single host arbitrating everything, is that it is derived
from POSITION ALONE. Every machine reads the same cell, finds the same owner, and reaches the same
verdict with no election and no message, which matters because any agreement about who is in charge
would have to arrive before the first hit lands. It is also even-handed: an invader does not get to
decide the defender's losses, and no one machine carries every fight in the world.

Pieces: `Combat/CombatRule.cs` (the rule, pure and headless-tested), `Combat/CombatAuthority.cs` (the
live lookups), `Combat/CombatSync.cs` (a sweep every 10 fixed ticks publishing changed armies), and
the two suppression Prefixes in `Main.cs`.

**`Army`'s damage method is an explicit interface implementation**, so its real name is
`IProjectileHitable.TakeProjectileDamage`. It is patched manually in a guarded block for the same
reason the merchant lambdas are: an attribute patch on the plain name would fail to resolve and abort
the atomic `PatchAll`, half-patching the mod. Watch startup for
`Combat authority hooks patched (army=True, unit=True)`.

Only the living unit COUNT and the general's life travel, never per-soldier hit points. A battle has
to agree on how many men are standing; which particular soldier is wounded is invisible and was
chosen by a random draw that never matched across machines anyway. Receivers converge by releasing
surplus units and never by adding, since adding would mean inventing a soldier's position and job.

**Coverage, settled 2026-09-03.** Every damage path in the game funnels through the victim's
`IProjectileHitable.TakeProjectileDamage`, melee included, so the victim is the one true choke point
and all of these are suppression Prefixes on it:

| Victim | Arbitration | How its health reaches everyone |
| --- | --- | --- |
| Army / Unit | suppressed off-arbiter | `ArmyHealthMessage`, living-unit count + general life |
| Building | suppressed off-arbiter | already: `BuildingWatcher` sends `Life` on change |
| Ship | suppressed off-arbiter | `ShipHealthMessage`; each machine sinks it locally at zero life |
| Villager | **nothing to do** | see below |

**Villagers need no arbitration, and adding it would have been dead code.**
`Villager.TakeProjectileDamage` is a no-op in vanilla, it returns `HitSfxResult.None` without
touching anything, so villagers take no projectile damage at all. The only way combat reaches a
villager is KIDNAPPING by a `ArmyType.Thief` army, which is a raider mechanic, and raiders are
suppressed outright in multiplayer. Checked before building rather than after.

A building always stands on its owner's landmass, so the landmass arbiter for it is its owner, which
is already the machine publishing its `Life`. Buildings therefore needed only the suppression half.
Ships sit on water, which nobody owns, so naval combat falls to the host by the same rule.

**Still open:** army POSITIONS are not synced. Only orders travel, so during a chase two machines can
have the same armies in different places, and a client may watch a fight resolve where it cannot see
one. Health will still agree, because the arbiter states it. Fixing it means periodic position
correction for armies, which is cheap (armies are few, unlike villagers) but has not been done.
**None of this has been run in game.** It ships dark behind `Main.CombatAuthorityEnabled`.

## Troops and combat: what the first real battle found (2026-09-05)

The acceptance run (`Dev/AutoTest.cs`) now fights an actual battle rather than only checking that
armies exist, and the first time it could get that far it found that **armies had never worked in
multiplayer at all**, for anyone, in any build.

- **No unit categories for multiplayer teams.** `UnitSystem.InitCategoriesGen` generates a
  `UnitCategory` per (ArmyType, team) for teams **0, 2, 3 and 4** only. The loop writes `j`, then
  `j + 1` once past 1, because team 1 is the vikings. Multiplayer teams are 5 and up, so the private
  `UnitSystem.GetCategory` returned null and `MakeArmy` died in `GetUnitFromCategory` with an NRE.
  Every army path goes through it: a barracks finishing its first army, a peer's army arriving over
  `ArmySpawn`, and an army restored from a save.

  Fixed with a Prefix on `GetCategory` taking `ref int teamId` and mapping multiplayer teams onto
  `{0, 2, 3, 4}` by `(mp - 5) % 4`, so consecutive kingdoms borrow different categories and stay
  visually distinct. **It has to be `GetCategory` and not `MakeArmy`**: `MakeArmy` does
  `army.teamId = teamId`, so rewriting its parameter would hand every multiplayer army to the wrong
  kingdom. `GetCategory` is private, so it is patched manually alongside the other fragile hooks.

  This is the THIRD place vanilla assumed teams 0 to 4, after `PathCell`'s six `[5]` arrays and
  `OrdersManager`'s two. A re-scan of the decompile says the family is now closed: nothing else
  team-indexed is a raw array (`standings`, `resourcePayCosts` and `tradeFavor` are Dictionaries).

- **New melee armies stood still.** `MakeArmy` sets the opening stance with
  `if (at == ArmyType.Default && teamId == 0) armyBehavior = Attack`. `Hold` is the enum's zero, and
  stance decides the auto-engage radius in the army tick (Hold 0 tiles, Attack 10, Pursue 99), so
  every multiplayer melee army held while an enemy walked past it and the player had to pick Attack
  by hand each time. Vanilla teams 2 to 4 are AI kingdoms whose brain sets a stance explicitly, so
  only team 0, the human, needed the default. Fixed with a Postfix. Safe against save loading, which
  assigns the stored `armyBehavior` after `MakeArmy` returns. Archers are unaffected: their vanilla
  rule sets Hold, which is already the default.

- **The unit panel was empty.** `UnitUI.TryCollectSelectedUnits` skips anything failing
  `TeamID() != 0 && !Player.inst.creativeMode`, so a multiplayer player's own troops were all
  skipped: no squad icons, no tabs, no stance buttons for the army you just clicked. Fixed by
  running the method once with `creativeMode` on and putting it straight back. That is safe here for
  a specific reason: selection is ALREADY restricted to your own units by this mod's
  `IsUnitSelectable` hook, so "everything selected" and "everything of mine" are the same set, and
  the method is synchronous with nothing that could observe the flag.

- **Troop transports lost their landing orders.** `TroopTransportShip` decides "is this mine" with
  `TeamID() == 0` in five places. Two matter: loading an army clears its `playerSetMoveTarget` and
  unloading one sets that target to the landing cell. That field is the anchor an army walks back to
  once a fight ends, so in multiplayer a landed army had no anchor and a loaded one kept a stale one
  pointing at the beach it left. Shipping troops to another player's island IS the PvP attack, so
  every assault went in with its orders half applied. Fixed with Postfixes on `TryForceLoad` and
  `TryUnloadAt(IMoveableUnit)`. The other three gates only auto-swap the selection between ship and
  cargo, which this mod already handles through its own selection hooks, so they are left alone.

- **`UnitSystem.UpdateGeneral` is NOT a bug**, despite sitting on the punch-list. It is fully
  team-relative (`RelationBetween` and `ClosestEnemyUnit` with `army.teamId`) and contains no literal
  `== 0`. It got there from a name-based scan. Do not "fix" it.

**What the battle actually proved.** Two 16-man armies at war, both on Pursue, at max game speed for
900 frames: 170 life each down to 54 and 40. The arbiter published **36** combat updates during it,
asserted through the new `CombatSync.PublishedUpdates` counter, since the sweep is deliberately
silent and a battle whose results were never sent looks identical to one that worked. With
`-kcmcombat` off the same check flips and asserts nothing was published.

## Traps that have already cost a debugging cycle

- **`Preload(KCModHelper)` / `SceneLoaded(KCModHelper)` are never dead code, however empty the
  body.** Nothing calls `AddComponent<NetClient>()` or `AddComponent<NetHost>()`. The KCModHelper
  loader attaches those classes to a GameObject *because they expose its hooks*, and that
  attachment is the only reason their `Update()`, and therefore `client.Update()` /
  `server.Update()`, ever runs. Deleting the empty stubs stopped Riptide being pumped: the
  transport connected, the handshake never completed, the lobby showed no players (not even the
  host) and Start did nothing, **with no error in the log**. `BrowserScreen` relies on this too;
  `LobbyScreen` and `CanvasFit` do not (they arrive via prefab/AddComponent).

- **`RiptideSteamTransport/` is a vendor folder containing mod code.** `SteamBootstrap.cs` lives
  there and carries no MIT banner. Any script that skips vendor directories by name will silently
  miss it, that is how a file went unmeasured for the entire life of the project, and how a
  tree-wide rename left it referencing six types that no longer existed.

- **`output.txt` must stay out of the working copy.** It used to live there, so it was uploaded
  with the mod and came back down on every re-download. The game *appends*, so a "fresh" log could
  open with thousands of lines from weeks earlier and the current session buried at the end,
  and deleting it from the workshop folder did not help, because the next sync restored it. The
  publish allowlist already excludes it; keep it that way.

- **A per-tick log inside a `catch` will drown the log.** An unguarded one in the barracks
  ownership gate wrote 2,177 identical lines in 45 seconds and buried everything else in the
  session. If a catch sits on a tick path, report once and set a flag.

- **"Test Compile" is the security check.** The uploader's Test Compile button runs the compile
  *and* the security scan, and costs nothing, this is where a rejection surfaces, before any
  upload is attempted. Every security failure below was caught there.

- **Adding any new `System.IO` call gets the mod rejected, and the rule is NOT characterised.**
  What is established, and only this:

  - The six long-standing call sites in `LoadSaveLoadAtPathHook` / `LoadSaveLoadHook`
    (`File.Exists`, `File.ReadAllBytes`, `new MemoryStream(bytes)` ×2, `BinaryFormatter` ×2) pass,
    and have through 53 successful uploads.
  - Every newly written method using those same APIs was rejected, in `SessionSave` **and** in
    `Main`, as *"illegal indirect type reference to type [System.IO.File]"*.
  - `new MemoryStream()` (parameterless) and `BinaryFormatter.Serialize` were rejected too.

  Three theories were tried and all three were wrong: "no serializing", "not inside a
  `[Serializable]` type", "fine in `Main`". Do not add a fourth from the armchair, the messages
  come from `ModCompiler` via `Trivial.CodeSecurity` (`IllegalTypeReferences` /
  `IllegalNamespaceReferences`), so the real allowlist is readable in the game's assemblies if it
  ever matters enough.

  **The practical rule: do not introduce new `System.IO` references. Reuse an existing one.**
  `Main.PackLiveSnapshot` needs a save's bytes and gets them by setting
  `LoadSaveLoadAtPathHook.captureBytesOnly` and calling `LoadSave.LoadAtPath`, so the read happens
  at the call site that already passes and the mod's `System.IO` surface stays byte-for-byte
  unchanged.

  All of this passes the **compile** check and fails only the **security** check, so `dupes.py`,
  `braces.py` and `membercheck.py` cannot catch it. If Test Compile reports no compile error but
  the item still will not upload, look here first.

- **A destroyed building in a cell list used to abort every save.** Fixed 2026-09-03.
  `PlayerSaveDataPackgHook.PackCell` filtered on `building.TeamID()` and `building.transform`
  *outside* its try/catch, so a destroyed Building left in `cell.OccupyingStructure` threw at
  `Component.get_transform`, escaped `PackCell`, and aborted the whole save. Because the `Finalizer`
  on `LoadSave.Save` swallows the throw rather than stalling the sim, the only symptom was a run of
  "Problem during save" in the error log and, silently, **no autosave ever reaching disk** (17 in one
  user session). The whole body is now inside the try, with an explicit `building == null` skip
  (Unity's overloaded null is true for a destroyed object) and a per-save summary count. Same family
  as the destroyed-ship corpses `PruneDestroyedShips` clears.

- **`braces.py` takes filenames as arguments.** Run bare, `python docs/braces.py`, it iterates an
  empty `sys.argv[1:]`, prints nothing and exits 0, which reads exactly like a pass. It was verified
  against a deliberately unbalanced file: bare it reported nothing; given the filename it reported
  `curly=1`. Always pass paths, and treat *no output at all* as "the tool did not run":

  ```bash
  find . -name "*.cs" -not -path "./Riptide/*" | xargs python docs/braces.py | grep -v "curly=0 paren=0 square=0"
  ```

- **Before adding a Harmony patch, check the method does not already have one.** `Main.cs` is over
  3,000 lines and nearly every patch is a nested class inside it, so a second hook on the same
  method reads as perfectly reasonable code in isolation, and the clash only surfaces as
  *"The type 'Main' already contains a definition for 'X'"* on the workshop upload screen, after a
  publish and a launch. This cost a test cycle on `World.RelationBetween`, which already had a hook
  rewriting team 0 to the local team. `grep` for the method name first, and run `docs/dupes.py`
  after. Note the pre-existing hook may also already do part of what you are about to add.

- **`Player.inst.GetBuildingList(hash)` is global; `Player.inst.GetBuilding(guid)` is not.** They
  read as a pair and behave nothing alike. `GetBuildingList` walks `globalBuildingRegistry` and
  returns every player's buildings of that type; `GetBuilding` walks that one player's `Buildings`
  array and returns null for anyone else's. Any vanilla code reaching for another player's building
  by guid is therefore silently broken, and `Ship.ValidateOrders` is the case where it was actively
  destructive.

## Testing

There is no `.csproj`. The game compiles the sources at load, so `output.txt` in the workshop item
folder is the only compiler and runtime feedback that exists. That makes the checkers below worth
more than they look.

**Always verify the shipped build matches before launching.** A stale upload has produced a
misleading log more than once:

```bash
diff -rq --strip-trailing-cr "C:/Users/User/Labs/kcm-multiplayer" "C:/Program Files (x86)/Steam/steamapps/workshop/content/569480/3775124962" | grep -v "output.txt\|docs"
```

Silence means they match. `Only in …` means files were added or removed and did not sync, and a
renamed file leaves the old one behind, which compiles as a duplicate class.

What to look for in a clean run:

- `codec check passed for all N message types`, the startup self-test round-trips every
  registered message through the real wire format.
- `save transpiler: 1 container construction(s) redirected to SessionSave`, if this says
  `FOUND NO LoadSaveContainer CONSTRUCTOR`, saves will contain no player data.
- `CompleteBuild: 1 TryAddJobs call(s) routed through the job-category guard`
- `Player.inst transpiler: 144 instance methods targeted` / `Building owner transpiler: 103`

`NetLoopback.Enabled` in `Main.cs` makes a solo session apply relayed messages as though a peer
sent them, useful for exercising client-side handlers alone. **Leave it false in anything
uploaded**; with it on, your own actions apply twice.

## Tooling

| Script | What it is for |
| --- | --- |
| `braces.py` | Brace/paren balance. Understands comments, char literals and verbatim/interpolated strings, so it does not trip on `$"{a}"` or `'}'`. |
| `membercheck.py` | Reports any `Type.Member` reference where the member is not declared on that type. Stands in for the compiler this project does not have, it catches a bad rename in a second instead of a wall of red text in-game. |
| `jobslots.py` | Walks a BinaryFormatter save and reports every primitive-array record's length. The only way to see inside a save without loading it; this is what confirmed the job tables were being written at the wrong length. |
| `dupes.py` | Two types declared with the same qualified name. Nearly every Harmony patch is a nested class inside `Main`, which is 3,000+ lines, adding a hook for a method that already has one is invisible from where you are working, and the game reports it only after a publish and a launch. Names are qualified by enclosing type, so `PeerRosterMessage.Entry` and `NetRegistry.Entry` are correctly distinct. |
| `build_publish.py` | Builds the shippable copy by allowlist (`.cs`, `info.json`, `LICENSE.txt`, the four bundle dirs). Refuses to overwrite an existing output folder. **Its `SRC`/`DST` still point at `C:\Users\User\Labs\…` from the old machine; fix them before the next public release.** Walks recursively and takes any `.cs`, so new source folders ship without an edit. |
| `ilspycmd` | Decompiles the game's own assemblies. See below, this is new and it changes how much of this project has to be guesswork. |
| `E:/Games/kcm-tests` | Headless regression tests over the mod's **real** source files (compiled in, not copied), run with `dotnet run -c Release`, exit 0 is a pass. Covers save serialization and the cross-player trade economics. Lives outside the mod folder so it is never uploaded. Anything pure enough to compile without Unity or `Main.helper` belongs here; see `Trade/TradeMath.cs` for the shape that makes code testable. |

### Reading the game's source

`ilspycmd` (installed with `dotnet tool install -g ilspycmd`, lives in `~/.dotnet/tools`) decompiles
`Assembly-CSharp.dll` to readable C#:

```bash
ilspycmd -o /tmp/kc -t MerchantShip "C:/Program Files (x86)/Steam/steamapps/common/Kingdoms and Castles/KingdomsAndCastles_Data/Managed/Assembly-CSharp.dll"
```

`-t` takes a type name, `-l c,s,i,e` lists types by kind. Namespaced types need the full name
(`Assets.Code.ResourceAmount`, not `ResourceAmount`) or `-t` silently matches nothing and writes an
empty file, check the output is non-empty.

This matters more than a convenience. Every comment in this project describing what a vanilla method
does was written from inference, and the first four checked against the decompiler had two material
errors in them (see the `MerchantShipStartSailingHook` entry above). `membercheck.py` only knows the
mod's own types, so nothing was catching it. **Before writing a comment about what game code does,
read the game code.**

**Before any tree-wide rename, print the diff and read it.** A rename that looked correct would
have rewritten `"Container/ChatInput"` to `"Container/ChatBox"` inside the prefab path strings,
that compiles cleanly, breaks every control binding at runtime, and `membercheck` cannot see it.
Mask string literals during substitution.

## Leaving, rejoining, and frozen kingdoms

Designed 2026-07-31. A player who leaves keeps their kingdom; it stops dead until they return.

- **Leaving** marks their `SessionPlayer.isGhost` and reserves their team id
  (`LoadIdentity.ReserveTeamFor`), so a later joiner handed the same recycled Riptide client id
  cannot be given their team.
- **Frozen** means `PlayerUpdateFreezeHook` skips `Player.Update` for any ghost. That covers
  kingdom-level simulation only, see the known-bugs entry above for what still ticks.
- **Others keep playing.** The leave pauses the game once so nobody misses it; unpausing is
  normal and the departed kingdom stays frozen regardless of speed.
- **Rejoining mid-session** sends them a live snapshot rather than a seed. Their kingdom is in it
  because ghosts stay in `Main.kCPlayers`, which is what `Pack` iterates.
- **Across a save/load** it also holds: `Pack` writes the ghost's kingdom, and `Unpack` recreates
  the ghost with its saved team, so it comes back frozen and thaws when they reconnect. This only
  works *because* of the freeze hook, before it, a loaded ghost kingdom simulated too.
- **Strangers are refused** once play has started. There is no path that gives a newcomer a
  kingdom mid-game, so they are turned away with a reason instead of admitted as a spectator.

The one thing tying it together is that identity is the **steamId**, never the client id. Riptide
recycles client ids as players leave; anything keyed on them will hand one player's kingdom to
somebody else.

## Architecture notes worth keeping

- **The two singleton transpilers are what make multiplayer work at all.** Vanilla `Player` methods
  reach for the `Player.inst` singleton, which is correct with one player and wrong with several.
  `PlayerReferencePatch` rewrites `ldsfld Player::inst` to `ldarg_0` inside Player's instance
  methods; `BuildingPlayerReferencePatch` rewrites it to `GetPlayerByBuilding(this)` inside
  Building's. Both go through `SingletonRewrite`.
- **Prefer a transpiler over replacing a method.** Wholesale replacements drift, the game's own
  fixes stop running, silently. `LoadSave.Save` was a 100-line copy whose only real difference was
  the *type* of container it packed; it is now a one-instruction transpiler. `CompleteBuild` went
  the same way, with a Prefix carrying only the idempotency guard.
- **`Player.Reset` deliberately has no patch.** The singleton transpiler reports no rewrites
  inside it, so it already operates on its own receiver and is correct on a cloned remote player
  as shipped. It also expires `HealthTimer`, which is private and unreachable from a
  reimplementation.
- **Save/load ordering:** in `SessionSave.Unpack`, absent players are restored first and the local
  player last, because villager restore reads through `Player.inst`. `RestoreAbsentPlayer` points
  the singleton at a placeholder and restores it in a `finally`, leaving it aimed at someone
  else's kingdom silently corrupts every later read.

- **One machine simulates a merchant; the rest are puppeted.** `Ship.Awake` calls `SetPause(true)`
  and nothing in this mod syncs `Ship.paused`, so a remote copy of another player's merchant never
  enters `Tick`'s route branch: it does not move itself, load, unload or settle. Only the owner's
  machine runs the route. Everything the other players see arrives as `ShipMove` (sailing) and
  `MerchantTrade` (goods and gold). Anything new on the merchant path therefore has to be applied
  *and broadcast* by the owner, a symmetric "both machines compute it" design would silently do
  nothing on the receiving side.

## Backups

- `../kcm-bundles-original/`, the four platform bundles the project started from.
- `../kcm-bundles-mine/`, the first Unity rebuild (win32/win64). Structurally correct, unstyled,
  dropdowns empty. Superseded by the redesign.
- `../kcm-snapshot-pre-tier4/`, full source snapshot from before the save-state migration.
