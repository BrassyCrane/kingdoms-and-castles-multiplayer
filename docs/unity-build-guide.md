# Unity build guide, `serverbrowserpkg`

You are building the UI asset bundle for a Kingdoms and Castles mod. The mod's C# is
already written and is not changing. It loads a bundle called `serverbrowserpkg`, pulls
seven prefabs out of it by name, then finds specific child objects inside each one by
path and drives them at runtime.

Your job: produce those seven prefabs and ship the bundle for four platforms.

Nothing in the mod's code needs to change if you follow the naming in this document.
Everything *not* specified here, layout, colours, fonts, spacing, backgrounds, extra
decorative nodes, the entire look of the thing, is yours to design.

---

## 0. Renames to apply while rebuilding

The node names below are inherited from the bundle being replaced. Most are the obvious
word for what the node is and should stay. These seven are not, they are leftover Unity
defaults, technique names, or arbitrary structure, and they are being changed as part of
the rebuild. **Build to the right-hand column**; the C# will be updated to match in the
same pass.

| Prefab | Old name | Build as |
| --- | --- | --- |
| serverlobby | `Container/TextMeshPro - InputField` | `Container/ChatInput` |
| serverlobby | `LoadingSave/Window/Progress Bar` | `LoadingSave/Window/ProgressBar` |
| serverlobby | `LoadingSave/Window/Progress Bar/Mask` | `LoadingSave/Window/ProgressBar/Fill` |
| serverlobby | `LoadingSave/Window/Information` | `LoadingSave/Window/StatusText` |
| serverlobby | `…/ServerAccess/Toggle` | **removed**, see §3.7, lock is derived from `Password` |
| serverentryitem | `Panel/ServerName` | `ServerName` (no `Panel`) |
| serverentryitem | `Panel/ServerHost` | `ServerHost` (no `Panel`) |

The tables further down still show the old names. Where they differ, this section wins.

A `Panel` node can still exist in `serverentryitem` as visual backing, just don't put
`ServerName`/`ServerHost` inside it. There was no reason for two of its six nodes to be
nested and the other four not.

**Everything else keeps its name.** `Container`, `Create`, `Load`, `Back`, `Join`,
`PlayerName`, `PlayerBanner`, `PlayerMessage`, `Ready`, `ServerName`, `ServerHost`,
`ServerDifficulty`, `ServerPlayers`, `ServerLocked`, `SendMessage`, `Start`,
`ServerSettings`, `ServerAccess`, `MaxPlayers`, `Password`, `Difficulty`, `WorldSettings`,
`SeedInput`, `NewMap`, `WorldSize`, `WorldRivers`, `FogOfWarToggle`,
`Placement`, `LoadingSave`, `Window`, `Modal`, `Title`, `Description`, `Button`, and
Unity's own `Scroll View / Viewport / Content` chain.

The rule if you hit a name not listed: if it's the obvious word for what the node does,
keep it. If it's a leftover default, a technique name, or arbitrary nesting, rename it.

## 1. Ground rules

**Names are a contract.** The C# calls `Transform.Find("Container/Create")` and similar.
`Find` walks a literal, case-sensitive path. If a node is renamed, moved, or has a
parent inserted above it, the lookup returns null and the mod null-refs on that screen.
Where this document gives a path, reproduce it exactly.

**Everything must be authored from scratch.** All textures, sprites, fonts and materials
in this bundle must be original work or something we hold a redistribution licence for.
Do not import assets extracted from any existing asset bundle, and do not pull art out
of the base game's files. If you need a reference for what a screen should look like,
run the mod and take a screenshot.

**Do not attach the mod's scripts in the editor.** Two of these prefabs get their
controller script attached at runtime with `AddComponent`. If you also add it in the
editor the object ends up with two copies and the screen misbehaves in ways that are
painful to diagnose. Flagged per prefab below.

**Build with Unity 2019.4.40f1.** That is the exact version the game itself is built
with (confirmed from `UnityPlayer.dll` and `globalgamemanagers`), and matching it is the
only way to be sure the bundle loads and that TextMeshPro's serialized data lines up with
the TMP the game ships.

Asset bundles are backward compatible but not forward compatible: a runtime loads bundles
from its own version or older, never newer. So an older editor would probably work and a
newer one definitely will not. Don't rely on "probably", use 2019.4.40f1. It's an LTS
release, available through Unity Hub → *Installs* → *Add* → *Archive*.

---

## 2. Project setup

1. New project in Unity 2019.4.40f1. Either the **2D** or **3D** template works, this
   bundle is nothing but uGUI and TextMeshPro, and none of the template differences
   (camera projection, skybox, lighting) apply to it. 2D is marginally more convenient
   because new textures import as `Sprite (2D and UI)` by default and so drop straight
   into an `Image` component; under 3D you set the Texture Type by hand each time. Note
   this affects `Image` only, `RawImage`, which the banner nodes use, takes a `Texture`
   directly either way.
2. Import **TextMeshPro** (*Window → TextMeshPro → Import TMP Essential Resources*).
   Every text field in this bundle is a TMP component, not Unity's legacy `Text`.
3. Create the folder `Assets/Workspace/`. All seven prefabs live directly in it, the
   loader asks for `assets/workspace/<name>.prefab` in lowercase, and that path comes
   from the folder structure, so the folder must be named `Workspace` at the project root.
4. Make a scratch scene with a Canvas to build inside. The scene is never shipped; it
   just gives you a place to lay things out. Set the Canvas to *Screen Space - Overlay*
   with a *Canvas Scaler* at *Scale With Screen Size*, reference resolution 1920×1080.
5. Everything you build is UI. Every node should have a `RectTransform`, that comes free
   if you always create nodes via *GameObject → UI → …* rather than *Create Empty*.

At runtime these prefabs are instantiated under a copy of the game's own top-level
canvas, so they inherit its scaling. Build them to look right at 1920×1080 and let the
anchors do the rest.

---

## 3. Build order

Work in this order. The first four are small and teach you the conventions; the lobby is
by far the biggest and is much easier once the rest feel routine.

| # | Prefab | Nodes | Notes |
| --- | --- | --- | --- |
| 1 | `serverchatsystementry` | 1 | warm-up |
| 2 | `serverchatentry` | 3 | |
| 3 | `serverlobbyplayerentry` | 3 | one node needs two components |
| 4 | `serverentryitem` | 6 | mind the `Panel` split |
| 5 | `modalui` | 3 | extra wrapper node |
| 6 | `serverbrowser` | 5 | first scroll view |
| 7 | `serverlobby` | 25 | the big one |

Legend for the tables below:

- **Find**, the path must match exactly, character for character.
- **InChildren**, the code searches downward for the component type. The named node
  must exist, but the component can sit on it *or* on any descendant. These are the
  forgiving ones; a TMP label nested inside a button works fine.
- **toggled**, the code only calls `SetActive` on it. No component required, but the
  node must exist at that exact path.

---

### 3.1 `serverchatsystementry`

One line of centred text. Used for "PlayerX joined the game" notices in lobby chat.

| Path | Component | Lookup |
| --- | --- | --- |
| `PlayerMessage` | `TextMeshProUGUI` | Find |

Root: give it a `LayoutElement` with a sensible preferred height. These get dropped into
a vertical list at runtime, so they need to report a height.

---

### 3.2 `serverchatentry`

One chat line: who said it, what they said, and their banner.

| Path | Component | Lookup |
| --- | --- | --- |
| `PlayerBanner` | `RawImage` | Find |
| `PlayerName` | `TextMeshProUGUI` | Find |
| `PlayerMessage` | `TextMeshProUGUI` | Find |

`PlayerBanner` is a `RawImage`, not an `Image`, the code assigns a `Texture` to it at
runtime. Leave its texture empty in the editor. Give it a fixed square size.

`PlayerMessage` should wrap and grow: set *Overflow* to wrap and pair a
`ContentSizeFitter` (vertical: preferred) on the root with a `LayoutElement` so long
messages don't clip.

---

### 3.3 `serverlobbyplayerentry`

One row in the lobby's player list.

| Path | Component | Lookup |
| --- | --- | --- |
| `PlayerBanner` | `RawImage` **and** `Button` | Find |
| `PlayerName` | `TextMeshProUGUI` | Find |
| `Ready` |, (ready indicator) | Find, toggled |

**`PlayerBanner` needs both components on the same node.** The code assigns the banner
texture to the `RawImage` and hooks a click handler onto the `Button`, clicking your own
banner cycles your kingdom colours. Add `Button` and leave its *Target Graphic* pointing
at the same node's `RawImage`.

`Ready` is a checkmark or tick that gets shown and hidden. Default it to **inactive**.

---

### 3.4 `serverentryitem`

One row in the server browser list.

| Path | Component | Lookup |
| --- | --- | --- |
| `Panel/ServerName` | `TextMeshProUGUI` | Find |
| `Panel/ServerHost` | `TextMeshProUGUI` | Find |
| `ServerDifficulty` | `TextMeshProUGUI` | Find |
| `ServerPlayers` | `TextMeshProUGUI` | Find |
| `ServerLocked` |, (padlock) | Find, toggled |
| `Join` | `Button` | Find |

**Watch the hierarchy split.** `ServerName` and `ServerHost` live under a child node
called `Panel`. The other four sit at the prefab root, *not* inside `Panel`. That's
inherited from how the code addresses them and it has to be preserved.

`ServerPlayers` is filled with text like `3/8`. `ServerDifficulty` gets a word such as
`Easy`. `ServerLocked` is a padlock shown only for password-protected servers, default
it **inactive**.

**Do not attach `ServerRow` in the editor.** It is added at runtime.

---

### 3.5 `modalui`

A general-purpose message box: title, body, one dismiss button.

| Path | Component | Lookup |
| --- | --- | --- |
| `Modal/Container/Title` | `TextMeshProUGUI` | Find |
| `Modal/Container/Description` | `TextMeshProUGUI` | Find |
| `Modal/Container/Button` | `Button` + a `TextMeshProUGUI` below it | Find + InChildren |

Note the paths start at `Modal`, one level *below* the prefab root. So the hierarchy is
`<root> → Modal → Container → {Title, Description, Button}`. The root is typically a
full-screen dimmed backdrop and `Modal` is the window itself.

The button's label text is set at runtime via `GetComponentInChildren`, so the usual
Unity button layout (a `Button` with a TMP child) is exactly right.

---

### 3.6 `serverbrowser`

The server list screen.

| Path | Component | Lookup |
| --- | --- | --- |
| `Container/Scroll View/Viewport/Content` |, (Transform) | Find |
| `Container/PlayerName` |, | Find, toggled |
| `Container/Create` | `Button` | Find |
| `Container/Load` | `Button` + a `TextMeshProUGUI` below it | Find + InChildren |
| `Container/Back` | `Button` | Find |

**Depth is no longer fixed.** The chat pane's auto-scroll used to walk up exactly three
parents to find the `ScrollRect`, so any extra wrapper object broke it. It now searches
upward instead, so you are free to nest the content however the design needs, only the
final node name in each path has to match.

`Container/Scroll View/Viewport/Content` is the standard hierarchy Unity generates for
*GameObject → UI → Scroll View*, keep the default child names and you get this path for
free. Copies of `serverentryitem` are parented to `Content` at runtime, so put a
`VerticalLayoutGroup` and a `ContentSizeFitter` (vertical: preferred) on it. Leave it
empty in the editor.

`Container/PlayerName` is hidden by the code as soon as the screen opens. It has to
exist, but it will never be seen, a leftover. Put an empty node there.

Three buttons: `Create` opens the host flow, `Load` starts from a saved game (its label
is rewritten at runtime, hence the TMP requirement), `Back` returns to the main menu.

---

### 3.7 `serverlobby`

The pre-game lobby. Three regions: a player list, a chat pane, and a settings column,
plus a save-transfer overlay that is hidden most of the time.

**Do not attach `LobbyScreen` in the editor.** It is added at runtime.

#### Player list and chat

| Path | Component | Lookup |
| --- | --- | --- |
| `Container` |, (Transform) | Find |
| `Container/PlayerList/Viewport/Content` |, (Transform) | Find |
| `Container/PlayerChat/Viewport/Content` |, (Transform) | Find |
| `Container/TextMeshPro - InputField` | `TMP_InputField` | Find |
| `Container/SendMessage` | `Button` | Find |

`PlayerList` and `PlayerChat` are both Scroll Views. Create each via *GameObject → UI →
Scroll View* and then rename only the top node, the `Viewport/Content` chain underneath
keeps its default names. Both `Content` nodes need a `VerticalLayoutGroup` plus a
`ContentSizeFitter`, same as the browser.

`Container/TextMeshPro - InputField` is the chat box. That name is Unity's default for a
new TMP input field and it was never renamed, it looks like a mistake but the code
depends on it, so leave it. The space-hyphen-space is part of the name.

The code leaves room at the top of `Container` for a map preview panel that is built
entirely in code at runtime, roughly a bordered image with a caption. Don't build one;
just don't fill every pixel of `Container` either.

#### Buttons

| Path | Component | Lookup |
| --- | --- | --- |
| `Container/Start` | `Button` + a `TextMeshProUGUI` below it | Find + InChildren |
| `Container/Back` | `Button` | Find |

`Start` doubles as the ready button, its label is rewritten between `Start` and `Ready`
depending on whether you're the host, so it must have a TMP child.

#### Settings column

| Path | Component | Lookup |
| --- | --- | --- |
| `Container/ServerSettings/ServerName` | `TMP_InputField` | InChildren |
| `Container/ServerSettings/Difficulty` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/ServerAccess` |, (Transform) | Find |
| `Container/ServerSettings/ServerAccess/MaxPlayers` | `TMP_InputField` | InChildren |
| `Container/ServerSettings/ServerAccess/Password` | `TMP_InputField` | InChildren |
| `Container/ServerSettings/WorldSettings/SeedInput` | `TMP_InputField` | Find |
| `Container/ServerSettings/WorldSettings/NewMap` | `Button` | Find |
| `Container/ServerSettings/WorldSettings/WorldSize` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/WorldSettings/WorldRivers` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/WorldSettings/Placement` | `TMP_Dropdown` | InChildren |
| `Container/ServerSettings/WorldSettings/FogOfWarToggle` | `Toggle` | Find |

Two groups under `ServerSettings`: `ServerAccess` (who may join) and `WorldSettings`
(what map to generate).

Most of these are **InChildren**, which is the forgiving lookup, so `ServerName` can be
a labelled group containing an input field rather than being the input field itself. That
gives you freedom to add captions. `SeedInput`, `FogOfWarToggle` and the two `Toggle`
nodes are **Find** with the component on the node itself.

**There is no lock toggle.** A server is locked if and only if `Password` is non-empty,
one field with one meaning, rather than a switch and a field that can contradict each
other. `Password` is therefore always visible; caption it so the host knows that leaving
it blank leaves the server open.

**The code repositions `Password`'s `RectTransform` at runtime**, copying `ServerName`'s x
and width, so don't put `Password` inside a layout group that would fight a manual
position. If `ServerAccess` exactly fills `ServerSettings` (which is how it's built), both
rects already agree and the fix-up is a no-op.

`FogOfWarToggle` must exist because the lookup is a `Find`, but the setting is
unimplemented, nothing in the game consumes it. It ships **inactive** and uncaptioned, and
`LobbyScreen` deactivates it again at runtime.

**The dropdowns must carry their own option lists.** Nothing in the code ever calls
`AddOptions`, the prefab is the only source of options, and an empty list renders as a
blank control.

For most of these the selected index is cast straight to a game enum
(`(Player.Difficulty)Difficulty.value`, `World.MapSize`, `World.MapRiverLakes`). So the
**count and order must match those enums exactly**; the visible labels are yours to word
however you like, but option 0 must mean the enum's value 0, and so on. Get the order wrong
and the lobby silently applies the wrong setting, no error, just a different map than
anyone asked for.

**There is no `WorldType` control**, and there is no node for it. Map bias is not a player
choice: every kingdom needs its own landmass, so `World.MapBias.Land` caps the server at one
player, and `Random` can roll `Land`, so it is no safer. `Island` is the only valid value,
and `LobbyScreen` hard-sets it (`MultiplayerMapBias`). `LobbySettings.WorldType` stays
on the wire so clients keep applying whatever the host sends.

The lists:

| Dropdown | Options, in this exact order |
| --- | --- |
| `Difficulty` | Peaceful, Easy, Hard, Survival |
| `WorldSize` | Small, Medium, Large, Random |
| `WorldRivers` | None, Some, Random |
| `Placement` | two options; index **0 must be "random placement"** |

**Do not add a fifth Difficulty option.** The game's enum ends with a `NumSettings`
sentinel that exists only to count the others, it is not a difficulty, and offering it
would let a player select a value the game never expects.

The mod re-logs these on every startup, so if a game update changes them, look for the
`[ui]` lines near the top of `output.txt` rather than trusting this table.

#### Save-transfer overlay

| Path | Component | Lookup |
| --- | --- | --- |
| `LoadingSave` |, | Find, toggled |
| `LoadingSave/Window/Progress Bar/Mask` | `Image` | Find |
| `LoadingSave/Window/Progress Bar` | `TextMeshProUGUI` | InChildren |
| `LoadingSave/Window/Information` | `TextMeshProUGUI` | Find |

This sits at the **prefab root**, a sibling of `Container`, not inside it. It covers the
screen while a save file transfers from host to joining player. Default it **inactive**.

`Progress Bar/Mask` is driven by setting `Image.fillAmount` from 0 to 1, so it must be an
`Image` with *Image Type* = **Filled**, *Fill Method* = **Horizontal**, *Fill Origin* =
**Left**. This is the one node where getting the component settings wrong produces a bar
that silently never moves.

`Progress Bar` also needs a TMP label somewhere beneath it for the percentage. Note both
rows point into the same node, one wants the `Image` on `Mask`, the other wants a TMP
component anywhere under `Progress Bar`. A label as a sibling of `Mask` satisfies both.

`Information` is a status line ("Waiting for host…").

Mind the spaces in `Progress Bar`. They're part of the name.

---

## 4. Building the bundle

Assign every one of the seven prefabs to a bundle named **`serverbrowserpkg`** (select
the prefab, then the *AssetBundle* dropdown at the bottom of the Inspector).

Then add this editor script at `Assets/Editor/BuildBundles.cs`:

```csharp
using UnityEditor;
using UnityEngine;

public static class BuildBundles
{
    [MenuItem("Build/Asset Bundles (all platforms)")]
    public static void BuildAll()
    {
        Build("win64",  BuildTarget.StandaloneWindows64);
        Build("win32",  BuildTarget.StandaloneWindows);
        Build("linux",  BuildTarget.StandaloneLinux64);
        Build("osx",    BuildTarget.StandaloneOSX);
        Debug.Log("Asset bundles built to AssetBundles/");
    }

    static void Build(string folder, BuildTarget target)
    {
        string path = "AssetBundles/" + folder;
        System.IO.Directory.CreateDirectory(path);
        BuildPipeline.BuildAssetBundles(
            path,
            BuildAssetBundleOptions.AppendHashToAssetBundleName,
            target);
    }
}
```

Run *Build → Asset Bundles (all platforms)*.

Each output folder ends up with:

- `serverbrowserpkg_<hash>`, the bundle (the hash suffix comes from
  `AppendHashToAssetBundleName`, and the loader expects that naming)
- `<foldername>`, e.g. `win64`, the manifest bundle Unity names after the output folder
- assorted `.manifest` text files

**Delete the `.manifest` text files.** They're build metadata and aren't shipped.

Ship the two remaining files per folder. The mod expects exactly this layout:

```
win64/serverbrowserpkg_<hash>
win64/win64
win32/serverbrowserpkg_<hash>
win32/win32
linux/serverbrowserpkg_<hash>
linux/linux
osx/serverbrowserpkg_<hash>
osx/osx
```

---

## 5. Testing

Drop the four folders into the mod directory, replacing what's there, and launch the game
with the mod enabled.

The mod writes a log to `output.txt` in its own folder. On a successful load you'll see a
line listing every asset name in the bundle, followed by `Loaded assets`. If a prefab is
missing or misnamed, the name list is your first diagnostic, it prints what actually
made it into the bundle.

Then walk the screens: main menu → multiplayer → the server browser appears → *Create* →
the lobby appears with the settings column populated. A null-reference in `output.txt`
naming one of the paths in this document means that node is missing, renamed, or
reparented.

---

## 6. Delivery checklist

- [ ] All seven prefabs exist under `Assets/Workspace/`
- [ ] Every path in this document resolves, with the listed component
- [ ] `PlayerBanner` in `serverlobbyplayerentry` has **both** `RawImage` and `Button`
- [ ] `LoadingSave/Window/Progress Bar/Mask` is a **Filled / Horizontal / Left** `Image`
- [ ] `ServerLocked`, `Ready` and `LoadingSave` default to **inactive**
- [ ] `ServerRow` / `LobbyScreen` are **not** attached in the editor
- [ ] All seven prefabs tagged into bundle `serverbrowserpkg`
- [ ] Built on Unity 2019.4.40f1, all four platform targets
- [ ] `.manifest` files deleted
- [ ] Every texture, sprite and font is original or licensed, nothing extracted from an
      existing bundle or from the base game
- [ ] Loads in-game with `Loaded assets` in `output.txt` and no null-refs walking the screens
