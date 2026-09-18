# Two-player test plan

Nothing in this build has ever run with two real players. Not one session.

That matters more than the raw statement suggests: **the host excludes itself from its own
broadcasts by design**, so solo testing exercises only the send half of every message. The
receive halves below have never executed against a real peer, and two of them write directly
into building and save state:

| Handler | Never run | Writes to |
| --- | --- | --- |
| `BuildPlace` | receive | building state |
| `BuildSnapshot` | receive | building state |
| `SaveTransfer` | receive | save file on disk |
| `PeerRoster` | receive | player list |
| `PlayerReady` | receive | lobby readiness |
| `ClientJoined` | receive | player list, kingdom assignment |

`NetLoopback.Enabled` in `Main.cs` can apply relayed messages locally as though a peer sent
them, which exercises client-side handlers alone. It is not a substitute, with it on, your
own actions apply twice, and it must be **false** in anything uploaded.

---

## Before you start

- Both machines on the **same mod version**. Verify by comparing `output.txt`'s startup lines,
  not by assuming Steam synced.
- Both need the game and the mod installed; the joining player does **not** need the same save.
- Delete `output.txt` on **both** machines first, so each log covers one session.
- Windows only for now, `linux`/`osx` have no bundle, so the UI silently fails to load there.

## Run

### 1. Lobby join

1. **Host:** Multiplayer → Create. Leave Password blank.
2. **Client:** Multiplayer. The host's server should appear in the browser, with a **map
   thumbnail** matching the host's lobby preview.
3. **Client:** Join.

Watch for: `ClientJoined` receive on the host, `PeerRoster` receive on the client. Both players
should appear in each other's player list, each with a banner.

### 2. Readiness

4. **Client:** click Ready. **Host:** confirm the client's row shows the ready tick.
5. **Client:** click Ready again to un-ready. Confirm it clears on the host.

This is the only test of `PlayerReady` receive in both directions.

### 3. Banner colours

6. **Client:** click your own banner a few times to cycle kingdom colours.
7. **Host:** confirm the colour changes appear.

### 4. Password gate

8. **Host:** Back to browser, Create again, type a password.
9. **Client:** join, expect the password prompt *before* connecting.
10. Enter it wrong once (expect rejection), then correctly.

Exercises `NetHost.cs`'s connection gate. **Also the known off-by-one:**
`server.ClientCount > LobbySettings.Current.MaxPlayers` uses `>` not `>=`, and `ClientCount`
includes the host's own local client, so the effective limit is probably one higher than the
number shown. With Max Players set to 2, try to get a third player in; if it succeeds, that
confirms it.

### 5. Save transfer, the risky one

11. **Host:** Create → Load a saved game.
12. **Client:** join.

The `LoadingSave` overlay should appear on the client with a moving progress bar and a status
line. This is the first time `SaveTransfer` receive has ever written a file. **Back up the
client's saves first.**

### 6. Start and build

13. **Host:** Start.
14. Both players land in the world on separate landmasses.
15. **Each player:** place a few buildings, a road, then dismantle one.
16. Confirm each sees the other's changes.

`BuildPlace` and `BuildSnapshot` receive, writing into building state. Watch for buildings
appearing twice, in the wrong place, or on the wrong landmass.

### 7. Chat

17. Send messages both ways, including one long enough to wrap.

### 8. Building attribution, new, and the reason to watch closely

Pass 1 of the `Main.cs` rewrite replaced `GetPlayerByTeamID`. It used to resolve the owning
player with `FirstOrDefault(...).inst`, let the resulting `NullReferenceException` fall into a
catch, and return the **local** player. That fallback still exists, it has to, because callers
assign the result straight into `Player.inst`, but the lookup around it is new code.

If it regressed, the symptom is **the other player's buildings counting toward your economy**:
their houses feeding your population, their farms your food. Check:

18. With both players established, note your own population and food.
19. Have the other player place several houses and a farm, and let a year tick.
20. Confirm **your** numbers did not move because of their buildings.
21. Check `output.txt` for `no player owns team N; attributing to the local player`. That
    warning now fires **once per team id** rather than every frame. Seeing it during normal play
    with both players connected means attribution is going wrong, that is the line to report.

### 8b. Merchants, the cross-player trade loop

New, and the first time any of it can work. Do the solo gate below first; if the route editor does
not open there, nothing in this section will run.

**Solo gate (one machine, five minutes).** Host, Start, then build a **Dock** and next to it a
**Merchant Ship** (`playermerchant`). When it launches, click the boat, or the ❗ above it.

24. The **route editor** must open (waypoints, pause/play, the yearly-cycle control), *not* the
    buy/sell trade window. The trade window means the fix did not take.
25. Log: `[MERCHANT] route editor opened for your PlayerMerchant`.

**Two players.** Both established, both with a Dock, and
`[DOCKS] trade docks open: your team N <-> player team M` in both logs.

26. **Player A:** open your merchant's route editor and add a stop at **your own** dock, with a load
    order for something you have plenty of.
27. **Player A:** add a second stop at **player B's** dock, and set an unload order there. B's dock
    should be selectable, if it shows *"Use an envoy to open foreign docks"*, `DocksOpen` is not
    holding and the rest will not work.
28. **Player A:** un-pause the route. Watch the merchant load at home and sail for B's island.

Watch for, in A's log:

- `[MERCHANT] kept PlayerMerchant route order to another player's 'dock' (team M, guid …)`, the
  order survived `ValidateOrders`. **If this line never appears, the route was still discarded** and
  the merchant is sailing to open water rather than to the dock. That would most likely mean Mono
  inlined `GetBuilding` past the patch; the `ValidateOrders` Prefix exists to make that impossible,
  so if it happens the Prefix itself did not run.
- `[MERCHANT] route order resolved to another player's building …`, same resolution, from the
  `GetBuilding` Postfix rather than the pre-resolve. Either line is a pass; both is normal.
- On arrival: `[MERCHANTTRADE] delivered … to team M's dock for N gold … broadcasting`.

And in **B's** log: `merchant buy cost=N buyer=M seller=…` from `ApplyMerchantTrade`.

29. Both players check gold: A's should have gone **up** by N, B's **down** by N, and B's dock
    should now hold the goods. A partial payment logs
    `owes N gold for a delivery but holds …`, that is deliberate (the goods are already delivered
    by the time the payment is worked out), not a bug.
30. **Player B:** click A's merchant while it sits at your dock. The **trade window** should open
    here, this is the one case that should still go to `MerchantUI`, not the route editor.

### 9. Settings steppers

TMP_Dropdown's popup does not render under this game's canvas, so every settings control is now
a `◀ value ▶` stepper (see `DropdownCycler`).

22. **Host:** step every setting, Difficulty, World Size, World Rivers, Placement, through a
    full wrap in both directions.
23. **Client:** confirm the same values appear on your side, and that you cannot change them.

### 10. Non-Windows, if you have a machine for it

All four platform bundles are now ours (Windows, Linux, macOS), but **only Windows has ever been
run**. If either of you can start the game on Linux or macOS, the single thing to check is that
the multiplayer UI appears at all, a missing or bad bundle logs
`asset bundle 'serverbrowserpkg' failed to load` and the screens simply never show.

---

## Afterwards

Send **both** `output.txt` files. Two logs from the same session read against each other are
worth far more than either alone, a message that sends cleanly and never arrives only shows up
in the pair.

Known noise, not worth reporting:

- `StartGame reflection failed, falling back`, appears every session. The game's private
  `MainMenuMode.StartGame` throws in multiplayer because there are no AI kingdoms, and the code
  falls back to entering playing mode directly. It works; the exception is thrown and caught
  every start.
- `codec check passed for all N message types`, expected, and the first thing to look for.
