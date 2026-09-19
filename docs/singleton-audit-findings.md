# The singleton audit: first run, triaged

`docs/singleton_audit.ps1` lists every place the GAME reads `Player.inst` that neither of our
transpilers covers. The script is judgement-free by design: the game reads the singleton 1,328
times across 627 methods, and **1,197 of those reads, in 572 methods across 239 types, are
unclaimed** by any transpiler or patch of ours. Most are legitimately about the local player, which
is why the script prints a list rather than a verdict.

This file is the judgement layer, from the first run (2026-09-16), against our 0.10.1. It is the
answer to "which rows matter", and it should be re-triaged whenever the script's output changes
shape, not on a schedule.

The question asked of every row is the same one: **if two kingdoms existed, would this method still
be right?**

## Why this list exists at all

`LandmassOwner.CalcMaxGold` read the singleton and gave every remote kingdom zero gold capacity,
which meant they could never hold gold, which meant every number typed into a merchant order
snapped back to 0. `JobSystem.Update` read it sixteen times and staffed every island in the world
by the local player's decrees. Both reached players. Both were months apart. Both were sitting in
plain sight in the IL.

Nothing about finding them was clever, and that is the point: they were found by a player noticing
a symptom rather than by anyone looking. The list below is what looking produces.

## Confirmed, fix these

**`ResourceLineItemUI.ClampOrder` (2 reads).** Already known as the visible symptom of the gold
bug, and confirmed here as an unclaimed read in its own right. Worth re-checking after the
`CalcMaxGold` fix lands rather than fixing blind: it may simply become correct once the number it
reads is right.

## Fixed since the first run

**`Home` (12 reads across 7 methods).** Tax, food consumption records and the homeless list all
read the local kingdom for every house in the world. Home's instance methods now go through the
owner transpiler, like Building's (ShowOverlay excepted, it is about the local player's overlay).
Tax rates were also never sent to other machines, so the host saved 0 for every guest; they are now
synced with `TaxRateMessage`. Still open: the static `Home.BuildGatherTypeOrder` reads the local
kingdom's per-island stock, and `HomeSaveData.UnloadVillager` looks residents up on the local
kingdom only.

**`Field.Tick`, `DeferredYield`, `RefreshBonuses`, `ProducerBasePlural.DoYield`,
`CheckProductionPipeline`.** Farms counted the local player's windmills for their bonus, and farm
and blacksmith output, crop rot and the "production blocked" advisor all went to the local kingdom.
These methods are in the same transpiler table now. Left local on purpose: `Field.AddFieldInstances`,
`RemoveFieldInstances` and `FieldSystem.Tick`, because the local player's field system is the only
one that ticks and it draws every farm's wheat, and the irrigation subscriptions in `Field.OnInit` /
`OnDestroy`.

## Strong suspects, in the order I would read them

**`DragonSpawn.SetWildAdultActions` (5), `SetWildBabyActions` (4), `OnSeasonChange` (8).** Dragon
target selection, which the community patch found independently from the other end: its
`DragonUpdateActions` suppression note says target selection reads `Player.inst`, so each copy was
"attacking a different village". These rows are the same fault at the spawn end, and they say the
puppet approach is treating a symptom. Worth deciding deliberately which end to fix.

**`WorldMask.AddBuildingsOfType` (1), `AddBuildingsOfTypeOnLandmass` (1).** Territory masks. A mask
built from the wrong kingdom's buildings is a plausible cause of placement being refused on ground
a player owns.

**`UnitSystem.ReleaseUnit` (2), `MoveGeneralTo` (1), `UpdateUnitState` (1), `TryRecooperate` (1).**
Combat bookkeeping. Low read counts but a high blast radius, and combat arbitration has still never
run across two real machines.

**`SiegeCatapult.Disband` / `OnDestroy` / `Start` / `OnEnableInternal` (1 each).** Catapults are
synced, so a disband crediting the wrong kingdom would desync the pair.

## Deliberately ignored

**`LandmassOwner.GetPayCosts` (2 reads).** Only reads `Player.defaultPayCost`, a serialized field on
the Player prefab that every kingdom is cloned from, so every kingdom gets the same table. Per-kingdom
prices are handled by `Trade/ExportPrices.cs`.

**`TownSquare` (7 reads).** Festivals are never replayed on other machines, so these methods only
ever run on the owner's machine, where the local player is the owner.

**`Villager` (12 reads).** Cosmetic or session-wide: sounds, walk bounce, the keep as a fallback drop
position, the creative-mode eating option, thought bubbles. `TeleportTo` adds a villager that
changes island to the local homeless list, which only matters if a remote villager is teleported
across islands.

**`DiplomacyUI` (87 reads across 39 methods) and `AIKingdom` (41 across 9).** By far the two largest
groups, and both irrelevant: a multiplayer session has no AI kingdoms, the Hall of Diplomacy is
disabled, and our own diplomacy is a separate system keyed on team pairs. If AI kingdoms are ever
enabled in multiplayer, this becomes the largest single piece of work in the project.

**Local-only UI: `BuildPriorityItem` (32), `HappinessUI` (30), `CharcoalInfo` (6), `ResourceUI` (3),
`TileInfoUI` (3), `ResearchUI` (3), `TownNameUI` (2), `BarracksUI` (1).** These draw the local
player's own panels, which is what `Player.inst` correctly means. No action.

**`WitchHut` (11).** Witch huts are switched off in multiplayer. Revisit only if that changes.

**`RaiderSystem` (17) and `Weather.Update` (6).** Raiders are hostile to everyone and not owned by a
kingdom, and `Weather` is deliberately singleton-driven: the community patch's work on the world
clock depends on there being exactly one calendar. Both are correct as they stand, and
`Weather.Update` is already patched for other reasons.

**`World.Setup` (3), `World.GenLand` (1), `World.IsValidStartLandmass` (1), `LoadSave.*` (6),
`VRMenuMode` (2).** World generation and load paths, which run once with one kingdom that is
genuinely the local one.

## How to use the script

```bash
powershell -ExecutionPolicy Bypass -File docs/singleton_audit.ps1
```

It cross-references what the mod already patches out of our own source, so a method we take over
stops appearing. `-All` includes the claimed rows, which is the form to use when checking whether a
patch actually covers what you thought it did.
