# Save-format migration plan

Moving the multiplayer save off a custom `LoadSaveContainer` subclass and onto the game's
built-in mod-data dictionary, so every save the mod writes is a **stock vanilla save file** that
the unmodded game can still open. Written 2026-08-31 after the KaC devs flagged the current
approach as save-destroying (see `ModSaveData.md` from Michael).

## Why

`LoadSaveSaveHook` (`Main.cs`) transpiles the game's `LoadSave.Save` so it constructs a
`SessionSave : LoadSaveContainer` instead of the vanilla container. Every save written while the
mod is loaded, **including single-player saves; the swap is unconditional**, therefore contains a
serialized `KaCMultiplayer.LoadSaveOverrides.SessionSave` object. Any load that can't resolve that
type (unmodded game, a game/assembly update, the mod removed) throws in `BinaryFormatter.Deserialize`
and the save won't open. That is the "shredded save" the devs flagged; they recommended reworking it
(this remake) so it stops happening.

## Target invariant

**The `world` file is always a vanilla `LoadSaveContainer`. All mod state lives in its
`CustomSaveData` dictionary as JSON strings.** Without the mod, the save opens, the player gets their
own kingdom, and the mod's dictionary keys are ignored (and cleared on new game by `Player.Reset`).
Nothing the mod does can make a save unopenable.

## What's already vanilla vs. what needs the dictionary (verified against the decompiler)

Vanilla `LoadSaveContainer.Pack` (decompiled) packs, from global singletons:

- **All world entities of every kingdom**, `WorldSaveData` (terrain + buildings), `ShipSystem`,
  `UnitSystem`, `CartSystem`, `JobSystem`, `OrdersManager`, weather/fire/dragon/raid/siege. These
  read `World.inst` / `*System.inst`, which hold *every* player's entities with their `teamID`s. So
  remote kingdoms' buildings, villagers and ships are **already captured by a vanilla save**, we
  lose nothing by letting vanilla pack them.
- **One player**, `PlayerSaveData = Pack(Player.inst)`, i.e. the *saving machine's* local kingdom.

What vanilla does **not** capture, and what therefore has to go in the dictionary:

1. The **non-local players' `PlayerSaveData`** (their kingdom-level economy: gold, per-landmass
   resources/storage, job priorities/enabled flags, keep link, banner, teamId).
2. The **`kingdomNames`** map (steamId → town name).
3. The **identity** map (steamId → saved teamId) that `LoadIdentity` rebuilds from.

Everything the mod currently keeps in `SessionSave.players` / `SessionSave.kingdomNames` is exactly
this set. Nothing else in `SessionSave` is non-vanilla, it re-packs the same subsystems vanilla
already does.

## The one genuinely hard problem: "which player is local" differs per machine

Vanilla's single `PlayerSaveData` is *the saver's* kingdom. When another machine loads that file,
vanilla's `PlayerSaveData.Unpack(Player.inst)` would pour the **saver's** kingdom into **this
machine's** `Player.inst`, wrong on every non-saver machine. The current `SessionSave.Unpack`
avoids this by keying every player by steamId and picking `players[mySteamId]` for `Player.inst`.

The migration keeps that logic; it just has to **stop vanilla from applying the base
`PlayerSaveData` to the local player in an MP load** and drive local-player restore from the
dictionary instead. Concretely: store **every** player (saver included) in the dictionary keyed by
steamId, and on load let each machine restore `dict[mySteamId]` into `Player.inst` and the rest as
remotes, the same assignment `SessionSave.Unpack` does today, sourced from the dict.

## Risks / spikes to settle before/while building (do these first)

1. **Serializing `PlayerSaveData` to a dict string, scanner-safe. RESOLVED (Phase 0 spike, 2026-08-31).**
   The security scanner blocks new `BinaryFormatter`/`System.IO`, so the payload must be JSON.
   `JsonUtility` is out, it can't do the jagged arrays `PlayerSaveData` is full of
   (`int[][] JobPriorityOrder`, `JobFilledAvailable`, `bool[][]` flags) or `List<Guid>`. **Use
   Newtonsoft (`JsonConvert`)**, already bundled and used in `Main.cs:264` (proven scanner-safe across
   53 uploads).

   Spike ran against the game's actual `Newtonsoft.Json.dll` (v8.0.0.0) with a mock mirroring every
   awkward `PlayerSaveData` type (private `ResourceAmount`, `List<Guid>`, `int[][]`, nested SaveData
   with `Vector3`, enums, structs-with-arrays). Findings:
   - **Default settings are UNSAFE, they drop private fields.** `PlayerSaveData.Resources` (the
     stockpile) is `private`; default Newtonsoft omitted it entirely (verified: the JSON did not
     contain it). Shipping that would zero every kingdom's resources on load.
   - **A fields-based `ContractResolver`**, serialize ALL instance fields (public + private) up the
     inheritance chain, never properties, round-trips the whole structure with full fidelity
     (private stockpile, Guids, jagged arrays, Vector3, enums all survive). Ignoring properties also
     forecloses the `Vector3.normalized`/`magnitude` self-referencing loop for free.
   - `Vector3` serialized cleanly as `{"x","y","z"}`; `Guid` as a string; enums as ints.

   Implementation notes for Phase 1: (a) the resolver must **respect `[NonSerialized]`** (skip
   `FieldInfo.IsNotSerialized`) to match BinaryFormatter, else transient/cached fields leak in;
   (b) set `ReferenceLoopHandling.Ignore` as a belt-and-suspenders; (c) still-unverified because the
   spike used a mock, not a live `PlayerSaveData` (needs the Unity runtime to instantiate):
   **polymorphic/interface-typed fields** would need `TypeNameHandling` to keep their concrete type,
   validate by round-tripping a REAL in-game save in Phase 1 and diffing field-by-field before trusting it.
2. **Suppressing vanilla's local-player unpack in MP.** Need a Prefix (or a `LoadSaveContainer.Unpack`
   hook) that skips `PlayerSaveData.Unpack(Player.inst)` when the mod is going to drive player restore
   from the dictionary, without disturbing single-player, where vanilla's path is exactly right.
3. **Ordering inside vanilla `Unpack`.** The mod's reconstruction must slot into vanilla's order:
   `WorldSaveData.Unpack` (landmasses exist) → remote players → local player **last** (villager
   restore reads `Player.inst`). Vanilla runs `PlayerSaveData.Unpack` mid-sequence; the mod's restore
   has to hang off a well-defined point (a `LoadSaveContainer.Unpack` Postfix, or `OnLoadedEvent`).
   *Spike:* confirm `OnLoadedEvent` fires early enough / with the world in the right state, else use a
   Postfix on `Unpack`.
4. **The `System.IO` surface stays byte-for-byte unchanged.** We keep the existing (scanner-approved)
   `File`/`BinaryFormatter` call sites for reading the file and for deserializing network bytes. The
   migration changes the *container type* and *where player data lives*, not how bytes are read or
   written. Do **not** add a new `File`/`MemoryStream`/`BinaryFormatter` reference (see
   `open-threads.md`). The dict payload uses Newtonsoft, not binary.
5. **Backward compatibility.** Two independent decisions:
   - *Stop writing `SessionSave`*, non-negotiable, this is the whole point.
   - *Still read old `SessionSave` saves?* Optional transition-only reader: on load, if the
     deserialized object is a `SessionSave`, run the old path; if it's a plain `LoadSaveContainer`,
     run the new dict path. Cheap to keep for one release so players' existing MP saves aren't
     stranded, then remove. Old saves remain non-vanilla-openable regardless, that can't be undone.

## What carries over unchanged (most of the hard-won work survives)

The container mechanism changes; the **player-reconstruction logic does not**. All of this moves
across as-is, just reading dict-sourced data instead of `SessionSave` fields:

- `LoadIdentity` (steamId → saved teamId), the orphan-phantom fix, ghost players, `RestoreAbsentPlayer`
  with the `Player.inst` swap-and-`finally`, keep relink, the post-load summary.
- The `ResetAsIfSingleton` crash fix (no `Player.inst` swap), unrelated to the container.
- `PlayerSaveDataPackgHook` (compact `WorkersArray`, `PackJobs`), `PruneDestroyedShips`,
  `ShipUpdatePathingHook`, the `NetApply.Scope()` guard against re-broadcasting during unpack.
- `SaveTransfer` and `PackLiveSnapshot`, they ship the whole `world` file's bytes, which are now a
  vanilla container; the receiving path deserializes with the existing (approved) call site.

## Phased implementation

Each phase ends at a checker-clean, testable state (`dupes.py`, `braces.py`, `membercheck.py`, plus a
save round-trip). Do not batch phases into one upload.

- **Phase 0, spikes (no shipping).** Resolve risk 1 (Newtonsoft round-trips `PlayerSaveData`) and
  risk 3 (which load hook point) on paper / in a throwaway branch. These two decide the shape of
  everything below.
- **Phases 1 to 3 IMPLEMENTED behind `Main.UseVanillaSaveFormat` (default false), 2026-09-01, UNTESTED.**
  Everything is written and checker-clean; flag off reproduces the exact current behavior, flag on is
  the new vanilla-container + dictionary path. Pieces:
  - `LoadSaveOverrides/ModSaveData.cs`: serialization (fields resolver, spike-1-validated) + `ModSessionData`.
  - `Main.UseVanillaSaveFormat` (flag) + `Main.MakeSaveContainer()` (factory the Save transpiler now
    routes `newobj LoadSaveContainer` through, so container type follows the flag at runtime).
  - `Main.WriteModSessionToDict()` on `Broadcast.OnSaveEvent`: writes all players + kingdomNames +
    identity into the container's dictionary (no-op on the old path / outside a session).
  - `Main.LoadSaveContainerUnpackHook` (Prefix on `LoadSaveContainer.Unpack`): if the container has the
    mod block, rebuild via `SessionSave.FromContainer(container, mod).Unpack()` and skip vanilla. Fires
    only for plain containers with the block; old serialised SessionSaves dispatch to their own override
    and never reach it; vanilla/SP saves have no block and fall through.
  - `SessionSave.FromContainer`: builds a transient (never-serialised) SessionSave from the container +
    dict block, so ALL the proven Unpack logic (LoadIdentity, ghosts, orphan-phantom, keep relink) runs
    unchanged. This doubles as the transition reader (old SessionSave saves still load via their override).
  - Host loader (`LoadSaveLoadAtPathHook`) and client loader (`LoadSaveLoadHook` + `SaveTransfer`) now
    accept a base `LoadSaveContainer` (both formats) instead of casting to `SessionSave`.

  **To test:** set `UseVanillaSaveFormat = true` in Main.cs, upload, then run the vanilla-open test (below) plus a
  two-player host-from-save round-trip. Left at false so the upload stays safe until then. Still to do:
  Phase 4 cleanup (delete SessionSave + ModAssemblyBinder) after the vanilla-open test passes.

  *Original Phase 1 note:* add a save hook (`OnSaveEvent`, or a Postfix on vanilla `Pack`) that
  serializes every player + `kingdomNames` + identity into `CustomSaveData` via
  `SaveDataGeneric(modName, …)`. Leave the `SessionSave` transpiler in place for now, so saves still
  load the old way, but verify the *new* dict keys appear in the file (inspect with `jobslots.py` /
  a dict dump). No behaviour change yet.
- **Phase 2, read side.** Add the load hook that reconstructs players from the dict (porting
  `SessionSave.Unpack`'s body), guarded to run only when dict keys are present. Still behind the old
  path. Verify a Phase-1 save reconstructs identically.
- **Phase 3, flip the container.** Remove the `LoadSaveSaveHook` transpiler so vanilla writes a real
  `LoadSaveContainer`; add the MP suppression of vanilla's local-player unpack (risk 2). Now saves are
  vanilla + dict. Keep the old `SessionSave` *reader* for transition (risk 5). **Milestone: a save
  made by the mod opens in the unmodded game** (test by loading it with the mod unsubscribed, expect
  the local kingdom to load and no crash).
- **Phase 4, cleanup.** Delete `SessionSave`, `ModAssemblyBinder`, and the old load/deserialize
  paths once the transition reader is no longer needed. Confirm the `System.IO` surface is unchanged
  and Test Compile's security check passes.

## Testing & verification

- **The vanilla-open test, every phase from 3 on:** make a save with the mod, unsubscribe the mod, load it in
  vanilla KaC → must open to your own kingdom. Re-subscribe → MP still restores fully.
- **Single-player regression:** with the mod loaded, a pure SP game must save and reload normally
  (this is the case the unconditional transpiler was quietly breaking).
- **Two-player round-trip:** host-from-save + client join (uses the fixes already in this build),
  both kingdoms restored, correct teams, resources, keeps.
- **Inspect saves without loading:** `jobslots.py` walks a save; add/keep a dict-dump so we can see
  the mod keys and confirm the base container is vanilla-shaped.
- Checkers after every cross-file edit; Test Compile (security scan) is the only place a bad
  `System.IO`/type reference surfaces.

## Rollout

Test Workshop item first (never as a compile check, Test Compile is free). Only after the vanilla-open test
and the two-player round-trip pass does the public item get the update. Tell the dev when it's in
testing; send them a mod-made save to confirm it opens vanilla on their end.
