#!/usr/bin/env bash
# Installs the published build (./build.sh -> artifacts/linux-x64) for the current user:
#   ~/.local/opt/rhi-linux                program files (the previous install is kept as rhi-linux.prev)
#   ~/.local/bin/rhi-linux                command-line launcher (symlink)
#   $XDG_DATA_HOME/applications           desktop entry (absolute Exec path, so menus work without PATH)
#   $XDG_DATA_HOME/icons/hicolor/96x96    icon
# The new files are staged next to the install and swapped in; a failed swap puts the old one back.
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SRC="$ROOT/artifacts/linux-x64"
DEST="$HOME/.local/opt/rhi-linux"
PREV="$DEST.prev"
BIN_DIR="$HOME/.local/bin"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
APPS_DIR="$DATA_HOME/applications"
ICON_THEME_DIR="$DATA_HOME/icons/hicolor"
ICON_SRC="$ROOT/src/RHI.Desktop/Assets/providers/rhi.png"
ICON_DEST="$ICON_THEME_DIR/96x96/apps/rhi-linux.png"

if [[ ! -x "$SRC/RHI" ]]; then
    echo "RHI has not been published yet. Run ./build.sh first." >&2
    exit 1
fi

mkdir -p "$(dirname -- "$DEST")" "$BIN_DIR" "$APPS_DIR" "$(dirname -- "$ICON_DEST")"

# Stage on the same filesystem so the swap is two renames.
STAGE="$(mktemp -d "$DEST.new.XXXXXX")"
trap 'rm -rf -- "$STAGE"' EXIT
cp -a "$SRC/." "$STAGE/"

if [[ -e "$DEST" || -L "$DEST" ]]; then
    if [[ -e "$PREV" || -L "$PREV" ]]; then
        echo "Removing the older backup $PREV"
        rm -rf -- "$PREV"
    fi
    mv -- "$DEST" "$PREV"
    if ! mv -- "$STAGE" "$DEST"; then
        mv -- "$PREV" "$DEST" || { echo "Could not install or restore $DEST; the previous install is at $PREV." >&2; exit 1; }
        echo "Could not move the new files into $DEST; the previous install was put back." >&2
        exit 1
    fi
    echo "Previous install kept at $PREV"
else
    mv -- "$STAGE" "$DEST"
fi

ln -sfn "$DEST/RHI" "$BIN_DIR/rhi-linux"

# Desktop Exec quoting: inside double quotes \ " ` $ take a backslash, and that backslash is written doubled because
# the Desktop Entry string unescape runs first (\\$ in the file -> \$ for the Exec rule -> $; a backslash is four).
# A single backslash before " ` $ is not a valid string escape: GLib refuses the whole value. Any % is doubled.
# sed, not ${var//}: bash 5.2 changed how backslashes in a ${var//pattern/replacement} replacement are read.
exec_path="$(printf '%s' "$DEST/RHI" | sed -e 's/\\/\\\\\\\\/g' -e 's/["`$]/\\\\&/g' -e 's/%/%%/g')"
desktop_tmp="$(mktemp "$APPS_DIR/.rhi-linux.desktop.XXXXXX")"
while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == Exec=* ]]; then
        # %u: a Nexus Mods "Mod Manager Download" (nxm://) link, when the desktop hands one to RHI (#99).
        printf 'Exec="%s" %%u\n' "$exec_path"
    else
        printf '%s\n' "$line"
    fi
done < "$ROOT/packaging/rhi-linux.desktop" > "$desktop_tmp"
chmod 0644 "$desktop_tmp"
mv -f -- "$desktop_tmp" "$APPS_DIR/rhi-linux.desktop"

if [[ -f "$ICON_SRC" ]]; then
    cp "$ICON_SRC" "$ICON_DEST"
fi

# Refresh menu and icon caches so the launcher appears without logging out.
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q "$APPS_DIR" || echo "WARNING: update-desktop-database failed; the menu entry may appear after the next login." >&2
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f "$ICON_THEME_DIR" || echo "WARNING: gtk-update-icon-cache failed; the icon may appear after the next login." >&2
fi

echo "Installed to $DEST"
case ":$PATH:" in
    *":$BIN_DIR:"*) echo "Run: rhi-linux (or use the RHI Linux menu entry)" ;;
    *) echo "WARNING: $BIN_DIR is not on PATH, so 'rhi-linux' will not be found in a terminal." >&2
       echo "Add it to PATH, or run $DEST/RHI directly; the menu entry works either way." ;;
esac
