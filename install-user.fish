#!/usr/bin/env fish
set ROOT (dirname (status --current-filename))
set SRC "$ROOT/artifacts/linux-x64"
set DEST "$HOME/.local/opt/rhi-linux"
if not test -x "$SRC/RHI"
    echo "RHI has not been published yet. Run ./build.fish first."
    exit 1
end
mkdir -p "$DEST" "$HOME/.local/bin" "$HOME/.local/share/applications"
cp -a "$SRC/". "$DEST/"
ln -sf "$DEST/RHI" "$HOME/.local/bin/rhi-linux"
cp "$ROOT/packaging/rhi-linux.desktop" "$HOME/.local/share/applications/rhi-linux.desktop"
echo "Installed. Run: rhi-linux"
