"""Build a clean publish copy of the mod.

Allowlist, not denylist: only file types the shipped mod actually needs are copied, so
nothing can leak in by being forgotten. Docs, measurement scripts, notes, logs and the
overlap reference are all excluded by construction rather than deleted afterwards.
"""
import os
import shutil
import sys

SRC = r"E:\Games\multiplayer mod for kingdomds and castles"
DST = r"E:\Games\kcm-multiplayer-publish"

# Directories never copied, whatever they contain.
SKIP_DIRS = {"docs", ".git", ".claude", "scratchpad", "__pycache__"}

# Everything else must match one of these to be copied.
ALLOWED_EXT = {".cs"}
# The licence ships because Riptide's MIT headers point at it and MIT requires the notice to
# travel with the code. The headers name LICENSE.md; LICENSE.txt is kept in the list because
# older copies used that name and shipping both costs nothing.
ALLOWED_NAMES = {"info.json", "LICENSE.md", "LICENSE.txt"}
BUNDLE_DIRS = {"win64", "win32", "linux", "osx"}


def wanted(rel, name):
    top = rel.split(os.sep)[0] if rel != "." else ""
    if top in BUNDLE_DIRS:
        return True                      # asset bundles are extensionless
    if name in ALLOWED_NAMES:
        return True
    return os.path.splitext(name)[1].lower() in ALLOWED_EXT


def main():
    if os.path.exists(DST):
        sys.exit(f"{DST} already exists - remove it first, refusing to overwrite")

    copied, skipped = [], []
    for root, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        rel = os.path.relpath(root, SRC)
        for name in files:
            src = os.path.join(root, name)
            relpath = os.path.join(rel, name) if rel != "." else name
            if not wanted(rel, name):
                skipped.append(relpath)
                continue
            dst = os.path.join(DST, relpath)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
            copied.append(relpath)

    print(f"copied {len(copied)} files")
    print(f"excluded {len(skipped)}:")
    for s in sorted(skipped):
        print("   ", s)
    return 0


if __name__ == "__main__":
    sys.exit(main())
