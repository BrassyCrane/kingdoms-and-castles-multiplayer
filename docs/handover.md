# Handover, bringing another machine up to date

Written 2026-08-01 at the end of a long session on a different PC. If you are the assistant on the
receiving machine: read this, then read `open-threads.md`. Between them they carry everything that
is not obvious from the code.

## 1. Getting the code across

**Replace the old copy wholesale. Do not merge on top of it.**

A file renamed or deleted since the older version will otherwise survive in the destination and
compile as a *duplicate class definition*, which the game reports only as a wall of red text. That
has cost this project a cycle before. Delete the destination contents first.

Afterwards, from the working copy, confirm the tree is sane:

```bash
python docs/dupes.py
find . -name "*.cs" -not -path "./Riptide/*" | xargs python docs/braces.py | grep -v "curly=0 paren=0 square=0"
python docs/membercheck.py | head -2
```

Expect `no duplicate type declarations`, no brace output at all, and `37 unresolved references`
(all pre-existing, in Riptide vendor files). Anything else means the copy went wrong.

## 2. Where the build stands

The current build **passes the game's Test Compile and security check**. It has **never been run**,
nothing below has been exercised in a real session, solo or two-player.

Twelve files changed. Three are **wire-format** changes, `PeerRosterMessage` gained `TeamId`,
`SaveTransferMessage` gained `Resume`, and `PlayerRelationMessage` is new, so **both machines in a
session must run the same build**.

The riskiest single change is to `LoadSaveLoadAtPathHook`, the host's save loader. Verify a normal
save load still works before anything else.

## 3. What changed in this session

### Merchant trade

- **M1**, clicking your own merchant opened the *trade* window instead of the route editor, so
  there was no way to give a merchant a destination at all. Vanilla's `teamID == 0` branch means
  "my ship, edit its route"; in MP the local team is 5/6/7, so it never fired. Fixed on both entry
  points, `Ship.OnSelected` and `Ship.OnClickedButton`, the ❗ button, which matters because a
  merchant starts paused and that button is the first thing you would click on it.
- **M2**, a route aimed at another player's dock was destroyed within a frame.
  `LogisticsOrder.GetBuilding()` resolves through `Player.inst.GetBuilding`, which only sees one
  player's buildings, and `Ship.ValidateOrders` then permanently rewrites any unresolvable Building
  order into a position order. Fixed by pre-filling vanilla's own cache field from a cross-player
  lookup, anchored on `ValidateOrders` so it holds even if Mono inlines `GetBuilding`.
- **M3**, goods delivered to a foreign dock were free, because vanilla settles payment through an
  `AIKingdom` and this mod has none. Now settled at the dock owner's pay costs and broadcast.
- **M8**, `SessionPlayer.JobSlotCount` was 38; the game allocates 39.

### Quality of life

- **Q1**, loading a save now pauses on entry; the load branch used to return before the pause the
  fresh-world path does. Losing a player mid-game pauses too.
- **Q2**, system notices rendered into the lobby chat panel, which does not exist once the game
  starts, so every join/leave notice during play went nowhere. They now go to `KingdomLog` in play
  mode. **Player chat is still lobby-only** and needs real UI.
- **Q3**, one player opening the pause menu paused *everyone*, and closing it un-paused everyone,
  overriding a deliberate pause. Menu-driven speed changes are no longer broadcast.

### Diplomacy

- **T5**, two players could never be anything but Neutral. `World.RelationBetween` resolves player
  pairs through a `Relations[5,5]` array gated on `teamID < 5`, and MP teams are 5/6/7, so every
  pair fell through to `return Relations.Neutral`. Third fixed-size-by-team array to bite this
  project, after `OrdersManager.unitsByTeamID` and `PathCell`. Relations now live in
  `Net/PlayerRelations.cs`, synced by `PlayerRelationMessage`, with gate re-baking and dock closing
  on a declaration of war.

  A **temporary `Ctrl+Shift+D` hotkey** cycles Neutral → Enemy → Allies with every other player. The
  game's own `DiplomacyUI` is 2,300 lines built entirely around `AIKingdom` and cannot be pointed at
  a player, so this stands in until there is real UI. Remove it then.

### Disconnect, freeze and rejoin

- **D1**, a departed player's kingdom kept simulating. `NetHost` claimed it "already stops ticking
  once its owner is gone"; that was false. `PlayerUpdateFreezeHook` now skips `Player.Update` for a
  ghost. **This is the kingdom-level layer only**, buildings, villagers and ships tick through
  systems that never consult the owning `Player`, so the freeze is incomplete.
- **D2**, fresh-game team ids were `clientId + 4`, and Riptide reissues the lowest free client id,
  so a new joiner could be handed a departed player's team and land on their preserved kingdom.
  Teams are now pinned to steamId for the session, departed teams reserved, and the host's
  assignment published in the roster so no machine re-derives it.
- **D3**, `isGhost` was never actually set on disconnect; only `SessionSave` ever set it, so a
  mid-game leaver was called a ghost in logs while the flag stayed false.
- **D4**, someone who has never played this session is refused once play has started, with a reason
  shown as a dialog. There is no path that gives a newcomer a kingdom mid-game, so admitting them
  would make them a spectator.
- **Q8 / resume**, a client joining mid-game used to receive only a world seed and regenerate a
  *blank* map. They now get a live snapshot (`Main.PackLiveSnapshot`) and walk into the world when it
  unpacks. Their kingdom is in the snapshot because ghosts stay in `Main.kCPlayers`, which is what
  `Pack` iterates. **Save/load also preserves a frozen kingdom**, established by reading the code,
  not by running it.

## 4. Two traps from today that will bite again

Both are written up properly in `open-threads.md`; this is the short form.

- **Never add a new `System.IO` reference.** The Workshop security scanner rejects newly written
  methods using `File` / `MemoryStream` / `BinaryFormatter`, while accepting the six identical
  long-standing calls in `LoadSaveLoadAtPathHook` and `LoadSaveLoadHook`. Three attempts to
  characterise the rule were all wrong, so do not add a fourth from the armchair.
  `Main.PackLiveSnapshot` sidesteps it entirely by reusing the existing read path via
  `LoadSaveLoadAtPathHook.captureBytesOnly`. This fails the **security** check and not the compile
  check, so no local checker catches it, the uploader's **Test Compile** button is where it shows.
- **Check whether a method already has a Harmony patch before adding one.** `Main.cs` is 3,000+
  lines and nearly every patch is a nested class inside it, so a second hook reads as reasonable
  code in isolation. `World.RelationBetween` already had one; the clash cost a cycle. `dupes.py`
  exists because of this.

## 5. What to do next

**Testing is the bottleneck, not code.** Solo covers roughly half of it:

1. **Load a saved game**, the riskiest regression, since the save loader changed. Expect
   `[LOAD] done` and `[SPEED] paused: save loaded`, and **never**
   `[RESUME] captured N bytes without loading`.
2. **Escape, then close the menu**, expect `[SPEED] menu speed=0 kept local (not broadcast)`.
3. **Build a Dock, then a Merchant Ship, and click it**, the **route editor** must open, not the
   trade window.
4. **Top of the log**, `codec check passed for all N message types` (38 as of 2026-09-05; the
   number grows with every message added, so check it is not FEWER than last time), and
   `[PRICES] baseline gold per unit`.
5. Optional, a fresh world, `keep place: landmass N at x,z`. That path has only ever been
   exercised on its failure branch.

`Ctrl+Shift+D` does nothing solo, it iterates *other* players. Freeze, rejoin and the stranger
refusal all need two machines. Two-player steps are in `two-player-test.md`; merchant trade is
section 8b.

Open queue, roughly in priority order:

| Item | Notes |
| --- | --- |
| **T1** | Sync order targets that are not a plain `Cell`. The move hook drops non-cell targets, so attacking a unit or a building, and **boarding a transport ship**, never leave the machine. |
| **T2** | `ArmyDespawn`. Armies are spawned across the wire but never removed, so dead ones stand forever and degrade everyone's pathing. |
| **T6** | Persist relations. A war is currently forgotten on save/load and invisible to a joiner. |
| **D1 cont.** | Survey the per-owner tick paths so a frozen kingdom is actually frozen. |
| **UI canvas** | Blocks M9 (trade accept/reject), Q4 (in-game chat) and the diplomacy UI. Three features behind one piece of Unity work, the biggest single lever left. |
| **T4** | Combat authority model. Order replay cannot make combat agree: `SRand` is one global `System.Random`, pathing is threaded, and destinations resolve against live unit positions. Note raiders are suppressed outright in MP, so there is almost nothing to fight yet. |
| **M4–M7, Q5, Q6** | Smaller merchant and QoL items. |

## 6. Things the receiving assistant will not otherwise know

- **There is no compiler.** The game compiles sources at load, so `output.txt` in the workshop item
  folder is the only compile and runtime feedback that exists. `dupes.py`, `braces.py` and
  `membercheck.py` are the substitutes, run all three after any edit spanning files.
- **`ilspycmd` is ground truth for game behaviour.** Install with `dotnet tool install -g ilspycmd`.
  Decompile before writing a comment about what vanilla does: of the first four such comments checked
  against it this session, two had material errors.
- **The user drives the game; the assistant drives the filesystem.** Clear `output.txt` yourself and
  give exact in-game steps, not a vague "check that it works".
- **The user uploads, not the assistant.** Working copy → the **test** Workshop item, uploaded by
  them. `kcm-multiplayer-publish` (built by `build_publish.py`) → the **public** item, on release
  only. Do not copy into `steamapps/workshop/content/*`.
- **The test item id changes.** `3775124962` as of 2026-08-01; three predecessors were deleted during
  a Workshop outage. Ask rather than assuming.
- **Never use a Workshop upload as a compile check.** That burned 62 uploads in one day and ran the
  account into a temporary Workshop write failure that took hours to clear. If Workshop writes start
  failing, stop and wait, do not create replacement items.
- **Two players have still never run this build.** The receive halves of most handlers have never
  executed against a real peer.
