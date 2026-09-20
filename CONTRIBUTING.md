# Contributing

Thank you for wanting to help. Fixes sent here are reviewed, merged, and shipped on the main
Workshop item with credit to you, in the change notes and in the Git history.

## How changes get in

- **`main`** is what is published on the Workshop. **`dev`** is the next release.
- Only the maintainers push to `main` and `dev`. Everyone else: fork the repository, make your
  change on a branch, and open a **pull request against `dev`**. A maintainer reviews it.
- One problem per pull request. Say what was wrong, how you found it, and how you tested the fix.

## Before you open a pull request

1. **It compiles.** Build it against the game's own assemblies in
   `KingdomsAndCastles_Data\Managed` before sending it, and say that you did. A pull request that
   does not compile cannot be reviewed.
2. **It works across machines.** Most bugs in this mod only appear with two or more players on
   separate PCs, a guest joining a loaded save, or three kingdoms instead of two. Test the case your
   fix is about, and say which one you tested.
3. **It follows the house style.** Every method gets a comment saying what it does and **why**; read
   any file in `Net/` or `Combat/` for the tone. Loops over per-player or per-entity data guard each
   item, and loops over an `ArrayExt` stop at `.Count`, never `.data.Length`.
4. **Logs.** If you found the bug from a log, quote the relevant lines. Check `Player.log`, not only
   `output.txt`: many exceptions only ever reach `Player.log`.

## Rights

This mod is not open-source licensed: its code is © its authors, all rights reserved. The bundled
RiptideNetworking library is the one exception and keeps its own MIT licence (see LICENSE.md). You
are welcome to read the code, fork it on GitHub to prepare a pull request, and run it for your own
games.

By opening a pull request you agree that your contribution may be included in this mod and
distributed with it under these same terms, with credit to you.

Republishing this mod, or a modified copy of it, on the Steam Workshop or anywhere else requires
my written permission, and the same goes for reusing its code, art or assets in another project.
Players ending up split across several copies is the one outcome that helps nobody. If you have
fixes, send them here and they will reach everyone through the main item, with your name on them.
