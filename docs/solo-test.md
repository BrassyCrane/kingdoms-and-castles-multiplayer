# Solo testing, without a second human

Written 2026-09-03. `two-player-test.md` assumes two people and two machines. This one covers what
can be verified alone, which since the test tooling landed is most of it.

There are three layers, cheapest first. Use the cheapest layer that can answer the question.

## Layer 0, a real game launch WITHOUT the Workshop (added 2026-09-05)

**This is the compile check the project never had.** The game loads mods from its own local folder as
well as from the Workshop, so a build can be run without uploading anything:

```bash
python "E:/Games/multiplayer mod for kingdomds and castles/docs/build_publish.py"
```

Then copy `E:\Games\kcm-multiplayer-publish` to
`C:\Program Files (x86)\Steam\steamapps\common\Kingdoms and Castles\KingdomsAndCastles_Data\mods\kcm-mp-localtest`
and launch `KingdomsAndCastles.exe`. Two logs come back:

- `KingdomsAndCastles_Data\mods\log.txt` - the LOADER's log. `Compilation success` or the exact
  `error CS...` with file and line. This is the compiler this project otherwise does not have.
- `...\mods\kcm-mp-localtest\output.txt` - the mod's own log, with every startup line.

**Two hazards, both real:**

1. **Never have the local copy AND a subscribed Workshop copy at once.** Both get loaded and every
   class is defined twice, which the game reports as a wall of red. Check with
   `find "<steam>/steamapps/workshop/content/569480" -name "*.cs"` before launching; it must be
   empty. Delete `mods\kcm-mp-localtest` when finished.
2. **A running instance holds the mod.** Relaunching while the game is open does nothing at all, and
   the log looks unchanged, which reads as "my build did not take". Kill the process first.

What this DOES prove: the mod compiles, every Harmony patch resolves (including the fragile
manually-patched ones), the codec self-test passes, the asset bundle loads, and a host session can
start. What it does not prove is anything needing a second machine, or anything you have to look at.

## Layer 0b, the automated acceptance run (added 2026-09-05)

`Dev/AutoTest.cs` hosts a session, exercises every cross-player feature and writes a `[SELFTEST]`
PASS/FAIL line per check. A full run becomes: build, launch, read the log. Nothing is clicked.

Switch on BOTH `Main.DevTestBuild` and `Dev.AutoTest.Enabled`, then launch. It:

1. creates a lobby, waits for the handshake to fill the registry,
2. **invokes the real Start button** (`LobbyScreen.StartButton.onClick`) rather than reimplementing
   it, so the shipped handler and its guards are what gets tested,
3. waits for play mode, lets the world settle, then checks: session and keep, fake peer with keep,
   dock, gold and stock, trade economics against REAL dock contents, the diplomacy prefabs and
   window, chat keyboard capture, relations snapshot/restore, and combat arbitration including that
   the arbiter for a fight on your own island is this machine.

Every phase has a 45s timeout that says which phase stalled, because a run that silently never
reaches play mode otherwise looks identical to one that never started.

**It is double-gated, and deliberately more so than the other aids**, because this one creates a
lobby and starts a game by itself. That must never happen to somebody who just wanted to play.

**The one constraint: exactly one copy of the mod may be installed.** A dev build in the local mods
folder and a subscribed Workshop copy will both load. Unsubscribe (or remove the local copy) so only
the one you want to test is present.

**Measured 2026-09-05, so the failure mode is on record.** Both copies COMPILE fine, which is the
trap: the loader compiles each mod into its own assembly and reports `Compilation success` twice, so
the log looks healthy. The damage is at runtime. The second copy to load fails with
`Exception: wiring the lobby screens / System.ArgumentException: The Object you want to instantiate
is null`, because the first copy already consumed the asset bundle. So a compile check with both
present is still valid; a behaviour test is not.

### When the game will not start at all

Seen 2026-09-05 after several force-kills. Symptom: the process starts, the Unity log reaches
`Setting up 12 worker threads for Enlighten`, prints `Curl error 42: Callback aborted`, and exits at
about 45 lines. **A healthy run instead continues into a list of `...\Managed\*.dll` paths**, which
is the mod loader enumerating assemblies; if you never see those, the game died BEFORE mods loaded
and nothing in this mod is responsible.

What was ruled out, so nobody repeats it: not the launch arguments (a plain launch failed the same
way), not the mod (it dies before the loader runs), not Steam's app state
(`HKCU\Software\Valve\Steam\Apps\569480` showed `Running 0`), not the Workshop item
(`appworkshop_569480.acf` showed `NeedsUpdate 0`, fully installed), and not a stale process.

The remaining cause is Steam's own session state, and the fix is to restart Steam. **Prefer closing
the game normally over `taskkill /F`**, which is what preceded this.

### Dev switches by launch argument, no upload needed

Added 2026-09-05, and it removes the round trip that used to cost an upload per flag change:

```bash
"C:/Program Files (x86)/Steam/steamapps/common/Kingdoms and Castles/KingdomsAndCastles.exe" -kcmdev -kcmautotest
```

`-kcmdev` is the master switch (nothing else applies without it), then `-kcmcombat`,
`-kcmfreeze` and `-kcmautotest` turn on the three gated features. Every flag stays false for anyone
who does not pass the argument, so a shipped build behaves identically for players.

A launch argument rather than a settings FILE deliberately: reading a file would mean a new
`System.IO` reference, and the Workshop security scanner rejects those in newly written methods.
`Environment.GetCommandLineArgs` needs none.

## Layer 1, headless tests (no game at all)

```bash
cd E:/Games/kcm-tests && dotnet run -c Release
```

Exit 0 is a pass. This compiles the mod's **real** source files (not copies) and asserts against
them, so a regression here fails before anything is uploaded. It currently covers save serialization
(`LoadSaveOverrides/ModSaveData.cs`) and the cross-player trade economics (`Trade/TradeMath.cs`).

**How to make more code testable:** keep a module free of `Main.helper.Log`, `Player.inst` and
`UnityEngine`, then add it to `kcm-tests.csproj` as a `<Compile Include=... />`. `TradeMath.cs` is the
worked example. Most of the mod cannot be tested this way *yet* only because logging and singletons
are threaded through it.

## Layer 2, in-game solo (the fake second player)

Set `Main.DevTestBuild = true` (top of `Main.cs`), upload to the **test** Workshop item, and launch.
That one switch turns on both aids:

- **`NetLoopback`**, which applies your own relayed messages back as though a peer had sent them, so
  the receive-and-apply half of every handler runs. Your own actions therefore happen **twice**; that
  is expected, and it is why the log matters more than the on-screen numbers.
- **`FakePeer`**, which supplies the foreign player every cross-player feature needs:
  **Ctrl+Shift+F** for a second kingdom with a keep and a stocked, funded dock (trade), and
  **Ctrl+Shift+G** for war plus an enemy army beside your keep (combat, attack orders, damage
  authority). Separate keys because war closes the trade docks.

Startup must show the warning line naming the dev build. If it does not, the upload is stale.

**`Main.DevTestBuild` must be false in any build given to players.** With it on, every action applies
twice and a player could summon a second kingdom with a keypress.

### Cross-player merchant trade, solo

1. Host a fresh game alone. Confirm at the top of `output.txt`:
   - `codec check passed for all N message types`
   - the `DEV TEST BUILD` warning
2. Press **Ctrl+Shift+F**. Expect `FAKE PEER spawned` and then
   `fake peer trading post ready: dock at X,Z on landmass N, water positions W, gold 5000`.
   **`water positions 0` means the dock is landlocked** and no merchant will ever arrive; regenerate
   the map and try again rather than chasing a phantom "waiting to reach dock".
3. Build a Dock, then a Merchant Ship. Clicking your own merchant must open the **route editor**, not
   the trade window.
4. Route the merchant to the fake peer's dock with an `unloadAtEnd` set, and let it sail.
   - `[SHIPMOVE] broadcasting your MERCHANT sail` on the send side.
   - `[MERCHANT] ... ARRIVED` when it gets there.
5. On arrival the route unloads and the delivery settles. Expect
   `[MERCHANTTRADE] delivered <goods> to team T's dock for G gold ... broadcasting`, followed by the
   loopback applying it: `merchant buy cost=G ... merchant=True dock=True`.
   **`merchant=False` or `dock=False` means a guid lookup missed**, which is the failure this logging
   exists to catch.
6. Click the merchant while it sits at the fake peer's dock. The trade window opens. Because the fake
   peer is not a real player clicking, a hand trade here is settled against *you*, and the hook
   deliberately declines to sync a trade whose clicker does not own the dock; expect
   `[MERCHANTTRADE] ... NOT synced, team A is trading at team B's dock`. That log line is a **pass**,
   not a failure, it is the desync guard doing its job.

### Startup lines, check these first every time

These four say whether the build even wired itself up. A `False` anywhere means that feature is off,
and everything you test after it is testing nothing.

```
codec check passed for all 38 message types
Combat authority hooks patched (army=True, unit=True, building=True, ship=True)
Ghost-kingdom freeze hooks patched (armyGroup=True, armyGeneral=True, ship=True), chat keyboard guard=True
all 7 required UI prefabs loaded, plus the diplomacy screen
```

If the last one says "no diplomacy screen in this bundle", the mod's C# is newer than the bundle
beside it: rebuild per `unity-workflow.md` and re-run `ship_bundles.py`.

### UI, added 2026-09-05

**In-game chat.** Press **Return** during play. The box opens; type and press Return to send, Escape
to cancel. Two specific things to confirm, because both were bugs during development:
- Pressing Return **opens** the box rather than opening and instantly closing it.
- While typing, letters do NOT trigger build hotkeys. Type "dddd" and check no menus open.

Messages fade after 14 seconds; with nothing said and the box closed, nothing should be drawn at all.

**Diplomacy.** Press **Ctrl+Shift+D**. It now OPENS a window rather than cycling every relation at
once. Expect a row per other kingdom with its banner, standing, and Neutral/Allies/War. Click one
and watch the standing change after a moment (it applies when the message returns, not on click).
The list should not flicker while open. Escape or Close dismisses it.

Solo, the fake peer counts as the other kingdom, so Ctrl+Shift+F first.

**Merchant arrival prompt (M9).** With the fake peer's merchant, or a real second player's, docked at
YOUR port: an exclamation mark appears over the ship and `KingdomLog` says whose merchant it is.
Clicking the mark opens the trade window. It should disappear when the merchant leaves.

### Relations across a save

1. Ctrl+Shift+D, declare war on someone.
2. Save, then host from that save.
3. The war must still be there. Before this was persisted it silently reverted to Neutral and the
   trade docks quietly reopened. Watch for `relations: restored N pair(s)` on load.

### Troops and combat, solo

Combat could not be tested alone at all until now: raiders are suppressed in multiplayer and the fake
peer is peaceful, so nothing in the world would fight. **Ctrl+Shift+G** fixes that. Do the trade steps
FIRST if you want them, because declaring war closes the trade docks.

1. Startup must show `Combat authority hooks patched (army=True, unit=True, building=True)`. **Any
   False there means that damage path is unguarded and will resolve on every machine and diverge.**
   Also expect `codec check passed for all 36 message types`.
2. Ctrl+Shift+F to bring in the peer, then **Ctrl+Shift+G**. Expect
   `FAKE PEER ATTACK: team T army <guid> (N units) spawned at ...`, and a note that YOUR machine
   arbitrates, because the fight is on your ground.
3. Watch them fight. On your island you are the arbiter, so you should see `army health` reports going
   out as the enemy loses men. Under loopback they come straight back, which exercises the receive
   half; the applied count should match what was sent.
4. **Kill the attackers.** When the army dies the despawn must travel: expect `army despawn: released
   <guid>`. This is the fix for phantom armies that used to stand on every other machine forever and
   degrade pathing.
5. **Attack orders.** Select your own army and right-click the enemy army, and then one of the peer's
   buildings. Both are targets that are not a plain cell, and both used to stay on this machine
   entirely. Expect `[SHIPMOVE] broadcasting your army order: ... -> army <guid>` and
   `... -> building <name>`, then an `army order: ... Army at x,z` applying.
6. **The other side of the authority model.** March your own army onto the peer's island and fight
   there. The arbiter should change hands: your machine stops publishing that fight, because the
   ground is theirs. This is the half that makes the model fair rather than just consistent.

If a target cannot be resolved on the receiving side you will see
`order: Army target <guid> not found here, falling back to position`. That is by design, not a
failure: combat is not perfectly synced, so a unit may already be dead here, and walking to where it
was beats dropping the order.

### What layer 2 still cannot show

Both ends run the same build in one process, so a wire contract that is self-consistent but wrong
stays invisible, and nothing here exercises real Steam transport, network timing, or threaded-pathing
divergence between machines. Those need two real instances.

## Layer 3, two machines

`two-player-test.md`. Currently unavailable, so treat layers 1 and 2 as the standard of proof and say
plainly in any write-up which claims rest on them.
