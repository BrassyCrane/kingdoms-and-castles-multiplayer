"""Copy freshly built asset bundles from the Unity project into the mod.

Run after the Unity side has produced AssetBundles/, i.e. after:

    "C:/Program Files/Unity/Hub/Editor/2019.4.40f1/Editor/Unity.exe" -batchmode -quit \
        -projectPath "E:/Games/KCM" -executeMethod BuildBundles.GenerateTagAndBuild -logFile -

Two things this does that a plain copy does not.

It drops the ``.manifest`` files. Unity writes one next to every bundle; they are build metadata,
the game never reads them, and shipping them puts unreferenced files in the Workshop item.

It refuses to copy a platform folder that has no ``serverbrowserpkg_*`` in it. A build that failed
half way leaves the folder present but empty, and copying that over a good bundle would replace
working UI with nothing, which shows up in game as a lobby with no controls rather than as an error.
"""
import os
import shutil
import sys

UNITY = r"E:\Games\KCM\AssetBundles"
MOD = r"E:\Games\multiplayer mod for kingdomds and castles"

PLATFORMS = ("win64", "win32", "linux", "osx")


def main():
    if not os.path.isdir(UNITY):
        sys.exit("no AssetBundles at {0}; build them in Unity first".format(UNITY))

    copied, skipped = 0, []

    for platform in PLATFORMS:
        src = os.path.join(UNITY, platform)
        dst = os.path.join(MOD, platform)

        if not os.path.isdir(src):
            skipped.append("{0}: not built".format(platform))
            continue

        names = [n for n in os.listdir(src) if not n.endswith(".manifest")]

        # Take only the NEWEST serverbrowserpkg bundle. Unity builds with
        # AppendHashToAssetBundleName, so a content change writes a new filename and leaves the
        # previous one sitting beside it; the build folder accumulates every bundle ever built.
        # Copying them all would hand the loader several to choose from, and it takes the first it
        # finds, which is a coin toss between the new UI and one from months ago.
        pkgs = [n for n in names if n.startswith("serverbrowserpkg")]
        if not pkgs:
            skipped.append("{0}: no serverbrowserpkg bundle, refusing to copy".format(platform))
            continue

        newest = max(pkgs, key=lambda n: os.path.getmtime(os.path.join(src, n)))
        stale = [n for n in pkgs if n != newest]

        # The platform manifest bundle is named after its folder (win64, osx, ...). The game does
        # not read it, but it has always shipped, so keep shipping it rather than changing two
        # things at once.
        names = [newest] + [n for n in names if n == platform]

        for n in stale:
            print("  (ignoring stale bundle {0}/{1})".format(platform, n))

        # Clear the destination so a bundle whose hash changed does not leave the old one
        # behind. The loader takes the first serverbrowserpkg_* it finds, so two would be
        # a coin toss between the new UI and the previous one.
        if os.path.isdir(dst):
            for name in os.listdir(dst):
                os.remove(os.path.join(dst, name))
        else:
            os.makedirs(dst)

        for name in names:
            shutil.copy2(os.path.join(src, name), os.path.join(dst, name))
            copied += 1
            print("  {0}/{1}".format(platform, name))

    print("copied {0} file(s)".format(copied))
    for s in skipped:
        print("  SKIPPED", s)

    return 1 if skipped else 0


if __name__ == "__main__":
    sys.exit(main())
