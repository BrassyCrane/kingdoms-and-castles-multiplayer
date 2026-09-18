# Prefab contract

What the C# expects to find inside each prefab in `serverbrowserpkg`. Rebuild the
prefabs however you like, layout, art, colours, hierarchy above these nodes are all
free, but every path below must resolve, with the listed component on it, or the
screen will null-ref at runtime.

**Bundle target: Unity 2019.4.40f1** (the game's own version). Build for `win64`,
`win32`, `linux`, `osx`.
Bundle name `serverbrowserpkg`; assets must live at `assets/workspace/<name>.prefab`
(lowercase, the loader passes lowercase paths to `LoadAsset`).

## How to read the tables

- **Path** is relative to the prefab root, exactly as passed to `Transform.Find`.
  `Find` walks a literal path: every segment must match by name, case-sensitive.
- **Lookup** is `Find` (path must match exactly) or `InChildren`
  (`GetComponentInChildren`, only the component type matters, the named node just has
  to contain one somewhere below it). `InChildren` rows are the forgiving ones.
- Rows marked **toggled** are only shown/hidden via `SetActive`; they need no component,
  but the node must exist.

---

## serverbrowser.prefab

Root gets no script attached; `ServerBrowser` drives it externally.

| Path | Component | Lookup |
| --- | --- | --- |
| `Container/Scroll View/Viewport/Content` | (Transform), parent for server entries | Find |
| `Container/PlayerName` |, (hidden at startup) | Find, toggled |
| `Container/Create` | `Button` | Find |
| `Container/Load` | `Button`, plus a `TextMeshProUGUI` below it | Find + InChildren |
| `Container/Back` | `Button` | Find |

## serverentryitem.prefab

`ServerRow` is attached at runtime via `AddComponent`, so do **not** add it in
the editor, you'd get two.

| Path | Component | Lookup |
| --- | --- | --- |
| `Panel/ServerName` | `TextMeshProUGUI` | Find |
| `Panel/ServerHost` | `TextMeshProUGUI` | Find |
| `ServerDifficulty` | `TextMeshProUGUI` | Find |
| `ServerPlayers` | `TextMeshProUGUI` | Find |
| `ServerLocked` |, (padlock indicator) | Find, toggled |
| `Join` | `Button` | Find |

Note `ServerName`/`ServerHost` sit under `Panel`, but `ServerDifficulty`/`ServerPlayers`
/`ServerLocked`/`Join` sit at the root. Preserve that split or update the script.

## serverlobby.prefab

`LobbyScreen` is attached at runtime via `AddComponent`.

| Path | Component | Lookup |
| --- | --- | --- |
| `Container` | (Transform), map preview is built into this at runtime | Find |
| `Container/PlayerList/Viewport/Content` | (Transform), parent for player entries | Find |
| `Container/PlayerChat/Viewport/Content` | (Transform), parent for chat entries | Find |
| `Container/TextMeshPro - InputField` | `TMP_InputField`, chat box | Find |
| `Container/SendMessage` | `Button` | Find |
| `Container/Start` | `Button`, plus a `TextMeshProUGUI` below it | Find + InChildren |
| `Container/Back` | `Button` | Find |
| `Container/ServerSettings/ServerName` | `TMP_InputField` | InChildren |
| `Container/ServerSettings/Difficulty` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/ServerAccess` | (Transform) | Find |
| `Container/ServerSettings/ServerAccess/MaxPlayers` | `TMP_InputField` | InChildren |
| `Container/ServerSettings/ServerAccess/Toggle` | `Toggle`, the "locked" switch | Find |
| `Container/ServerSettings/ServerAccess/Password` | `TMP_InputField` + `RectTransform` | InChildren |
| `Container/ServerSettings/WorldSettings/SeedInput` | `TMP_InputField` | Find |
| `Container/ServerSettings/WorldSettings/NewMap` | `Button` | Find |
| `Container/ServerSettings/WorldSettings/WorldSize` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/WorldSettings/WorldType` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/WorldSettings/WorldRivers` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/WorldSettings/Placement` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/WorldSettings/FogOfWarToggle` | `Toggle` | Find |
| `LoadingSave` |, (save-transfer overlay) | Find, toggled |
| `LoadingSave/Window/Progress Bar/Mask` | `Image`, fill is driven by `fillAmount` | Find |
| `LoadingSave/Window/Progress Bar` | `TextMeshProUGUI` (percentage label) | InChildren |
| `LoadingSave/Window/Information` | `TextMeshProUGUI` (status line) | Find |

Two things worth fixing while the prefab is open, both needing a matching one-line code
change: `TextMeshPro - InputField` is an un-renamed Unity default, and `Progress Bar`
has a space in a path segment. Neither breaks anything today.

The `Password` field's `RectTransform` is repositioned at runtime relative to
`ServerName`'s, so keep both under a layout that tolerates being moved.

## serverlobbyplayerentry.prefab

| Path | Component | Lookup |
| --- | --- | --- |
| `PlayerBanner` | `RawImage` **and** `Button` on the same node | Find |
| `PlayerName` | `TextMeshProUGUI` | Find |
| `Ready` |, (ready checkmark) | Find, toggled |

`PlayerBanner` needs both components, the banner texture is assigned to the `RawImage`
and the click handler to the `Button`.

## serverchatentry.prefab

| Path | Component | Lookup |
| --- | --- | --- |
| `PlayerName` | `TextMeshProUGUI` | Find |
| `PlayerMessage` | `TextMeshProUGUI` | Find |
| `PlayerBanner` | `RawImage` | Find |

## serverchatsystementry.prefab

| Path | Component | Lookup |
| --- | --- | --- |
| `PlayerMessage` | `TextMeshProUGUI` | Find |

## modalui.prefab

Note the extra `Modal` node, paths start one level below the root.

| Path | Component | Lookup |
| --- | --- | --- |
| `Modal/Container/Title` | `TextMeshProUGUI` | Find |
| `Modal/Container/Description` | `TextMeshProUGUI` | Find |
| `Modal/Container/Button` | `Button`, plus a `TextMeshProUGUI` below it | Find + InChildren |

---

## Base-game nodes, do not touch

These are read out of the running game's own UI, not the bundle. They are fixed by
Kingdoms and Castles; if the game updates its menu hierarchy, these break and there is
nothing we can do in the bundle about it.

| Path (under `MainMenuMode.mainMenuUI`) | Used for |
| --- | --- |
| `TopLevelUICanvas` | cloned wholesale to host our screens |
| `TopLevelUICanvas/TopLevel/Body/ButtonContainer/New` | `Button` cloned as the style template for menu buttons |

## Checklist before handing bundles back

- [ ] All 9 prefabs present at `assets/workspace/*.prefab`, lowercase (7 required, plus the 2 diplomacy ones, which load as optional)
- [ ] No `ServerRow` / `LobbyScreen` attached in-editor
- [ ] Built on Unity 2018.3.0f2
- [ ] All 4 platform targets produced
- [ ] Every texture and font in the bundle is original or licensed for redistribution,
      nothing extracted from the base game

---

## diplomacyui.prefab

Added 2026-09-05. Replaces the temporary `Ctrl+Shift+D` hotkey, which cycled every relation at
once because there was nowhere to express "this kingdom, this standing".

Driven externally like `serverbrowser`; no script attached in the editor.

| Path | Component | Lookup |
| --- | --- | --- |
| `Window/Container/Scroll View/Viewport/Content` | (Transform), parent for diplomacy rows | Find |
| `Window/Container/Close` | `Button` | Find |
| `Window/Container/Title` | `TextMeshProUGUI` | Find |
| `Window/Container/Hint` | `TextMeshProUGUI`, the "war closes docks" caption | Find |

A window (820x560) rather than a full screen, because it is consulted mid-game and should not
bury the map. The dim behind it is `ModalDim`, so the map stays faintly visible.

## diplomacyrow.prefab

One row per kingdom that is not yours. Instantiated into the Content node above.

| Path | Component | Lookup |
| --- | --- | --- |
| `PlayerBanner` | `RawImage`, the kingdom's banner texture | Find |
| `PlayerName` | `TextMeshProUGUI` | Find |
| `Relation` | `TextMeshProUGUI`, the current standing | Find |
| `Neutral` | `Button` | Find |
| `Allies` | `Button` | Find |
| `War` | `Button` | Find |

All six sit at the prefab root, as in `serverlobbyplayerentry`. The three buttons run right to
left as War, Allies, Neutral, so the most consequential one is furthest from where the cursor
rests after clicking the row.
