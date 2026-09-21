#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SRC="$ROOT/artifacts/linux-x64"
DEST="$HOME/.local/opt/rhi-linux"
ICON_SRC="$ROOT/src/RHI.Desktop/Assets/providers/rhi.png"
ICON_DEST="$HOME/.local/share/icons/hicolor/96x96/apps/rhi-linux.png"

if [[ ! -x "$SRC/RHI" ]]; then
    echo "RHI has not been published yet. Run ./build.sh first." >&2
    exit 1
fi

mkdir -p "$DEST" "$HOME/.local/bin" "$HOME/.local/share/applications" "$(dirname "$ICON_DEST")"
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$SRC/." "$DEST/"
ln -sfn "$DEST/RHI" "$HOME/.local/bin/rhi-linux"
cp "$ROOT/packaging/rhi-linux.desktop" "$HOME/.local/share/applications/rhi-linux.desktop"
if [[ -f "$ICON_SRC" ]]; then
    cp "$ICON_SRC" "$ICON_DEST"
fi

echo "Installed. Run: rhi-linux"
