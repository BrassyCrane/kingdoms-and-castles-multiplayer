"""Lists every Harmony patch in the mod and whether its body has a multiplayer gate.

The rule being audited (asked for by the game's developers, 2026-09-05): with the mod
installed, single player must behave exactly as though it were not.

A patch is only interesting here if it can CHANGE something in single player. A patch that
merely reads, logs, or that no single-player code path can reach, is fine.
"""
import io, os, re

ROOT = "."
SKIP = ("Riptide", "RiptideSteamTransport")

GATES = [
    "IsConnected", "NetHost.IsRunning", "NetApply.InProgress", "DevTestBuild",
    "kCPlayers", "SteamLobby", "menuState", "NetClient", "NetRouter",
    "MpTeamBase", "IsRunning", "InMultiplayer", "Unpacking", "LeaveNameBannerScreen",
    "DragonSpawnPrefix", "AssignLaunchedShipGuid", "localMenuSpeedChange",
]

rows = []
for dirpath, dirnames, filenames in os.walk(ROOT):
    dirnames[:] = [d for d in dirnames if d not in SKIP and not d.startswith('.')]
    for fn in filenames:
        if not fn.endswith(".cs"):
            continue
        path = os.path.join(dirpath, fn)
        src = io.open(path, encoding="utf-8-sig", errors="replace").read()

        # Split on patch attributes, keeping the attribute with its class.
        for m in re.finditer(r'\[HarmonyPatch\(([^\n]*)\)\]\s*(?:\[[^\n]*\]\s*)*'
                             r'public\s+class\s+(\w+)', src):
            target = m.group(1).strip()
            cls = m.group(2)

            # Body: from the class to the next top-level patch attribute, capped.
            start = m.end()
            nxt = src.find("[HarmonyPatch", start)
            body = src[start: nxt if nxt != -1 else min(len(src), start + 6000)]

            gated = any(g in body for g in GATES)
            rows.append((os.path.relpath(path, ROOT).replace("\\", "/"), cls, target, gated))

rows.sort(key=lambda r: (r[3], r[0], r[1]))

ungated = [r for r in rows if not r[3]]
gated = [r for r in rows if r[3]]

print("TOTAL PATCH CLASSES: %d   gated: %d   NO GATE FOUND: %d\n"
      % (len(rows), len(gated), len(ungated)))

print("=== NO MULTIPLAYER GATE FOUND (each needs a human verdict) ===")
for path, cls, target, _ in ungated:
    print("  %-34s %-42s %s" % (cls, target[:42], path))
