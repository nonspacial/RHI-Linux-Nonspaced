#!/usr/bin/env bash
# Builds the self-contained RHI Linux AppImage from this source tree.
#
#   packaging/appimage/build-appimage.sh [--out DIR]
#
# Layout (the same as the published AppImage):
#   AppRun                        runs usr/bin/RHI (no PATH/LD_LIBRARY_PATH changes)
#   rhi-linux.desktop, rhi-linux.png, .DirIcon
#   usr/bin/                      self-contained linux-x64 publish (no .pdb); needs no host .NET
#   usr/share/applications/rhi-linux.desktop
#   usr/share/icons/hicolor/96x96/apps/rhi-linux.png
#
# Output: DIR/RHI-Linux-v<version>-x86_64.AppImage and a .sha256 holding only the file name
# (default DIR: artifacts/appimage). The version comes from <Version> in Directory.Build.props.
#
# appimagetool and the AppImage type-2 runtime are downloaded once over HTTPS from the
# AppImage project's GitHub releases at pinned versions and used only if their SHA-256 matches.
# They are cached in artifacts/appimage-tools (override with RHI_APPIMAGE_TOOLS_DIR).
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

APPIMAGETOOL_VERSION="1.9.1"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
RUNTIME_VERSION="20251108"
RUNTIME_SHA256="2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"

out_dir="$ROOT/artifacts/appimage"
while (( $# > 0 )); do
    case "$1" in
        --out) [[ $# -ge 2 ]] || { echo "--out needs a directory" >&2; exit 2; }; out_dir="$2"; shift 2 ;;
        -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done
mkdir -p "$out_dir"
out_dir="$(cd -- "$out_dir" && pwd)"
tools_dir="${RHI_APPIMAGE_TOOLS_DIR:-$ROOT/artifacts/appimage-tools}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

fail() { echo "ERROR: $*" >&2; exit 1; }
command -v dotnet >/dev/null 2>&1 || fail "dotnet is not available in PATH"
command -v curl >/dev/null 2>&1 || fail "curl is required to fetch appimagetool"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is required"

# Fetches $1 to $2 unless a copy with SHA-256 $3 is already there; never uses an unverified file.
fetch_verified() {
    local url="$1" dest="$2" sha="$3" tmp
    if [[ -f "$dest" ]] && echo "$sha  $dest" | sha256sum --check --status; then
        return 0
    fi
    mkdir -p "$(dirname -- "$dest")"
    tmp="$(mktemp "$dest.XXXXXX.part")"
    if ! curl --proto '=https' --proto-redir '=https' --tlsv1.2 -fsSL --retry 3 -o "$tmp" "$url"; then
        rm -f -- "$tmp"
        fail "download failed: $url"
    fi
    if ! echo "$sha  $tmp" | sha256sum --check --status; then
        rm -f -- "$tmp"
        fail "SHA-256 mismatch for $url (expected $sha); refusing to use it"
    fi
    mv -f -- "$tmp" "$dest"
}

version="$(dotnet msbuild src/RHI.Desktop/RHI.Desktop.csproj -getProperty:Version 2>/dev/null | tr -d '[:space:]')"
[[ -n "$version" ]] || fail "could not read <Version> from Directory.Build.props"
name="RHI-Linux-v$version-x86_64.AppImage"

appimagetool="$tools_dir/appimagetool-$APPIMAGETOOL_VERSION-x86_64.AppImage"
runtime="$tools_dir/runtime-$RUNTIME_VERSION-x86_64"
fetch_verified "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage" \
    "$appimagetool" "$APPIMAGETOOL_SHA256"
fetch_verified "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_VERSION/runtime-x86_64" \
    "$runtime" "$RUNTIME_SHA256"
chmod +x "$appimagetool"

appdir="$out_dir/AppDir"
rm -rf -- "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/96x96/apps"

echo "=== PUBLISH linux-x64 self-contained ($version) ==="
dotnet publish src/RHI.Desktop/RHI.Desktop.csproj -c Release -r linux-x64 --self-contained true \
    -p:RestoreLockedMode=true -o "$appdir/usr/bin"
find "$appdir/usr/bin" -name '*.pdb' -delete
[[ -x "$appdir/usr/bin/RHI" && -f "$appdir/usr/bin/libcoreclr.so" ]] \
    || fail "publish did not produce a self-contained RHI (usr/bin/RHI, libcoreclr.so)"

install -m 0755 packaging/appimage/AppRun "$appdir/AppRun"
sed -e 's/^Exec=.*/Exec=RHI/' packaging/rhi-linux.desktop > "$appdir/rhi-linux.desktop"
printf 'X-AppImage-Version=%s\n' "$version" >> "$appdir/rhi-linux.desktop"
cp "$appdir/rhi-linux.desktop" "$appdir/usr/share/applications/rhi-linux.desktop"
install -m 0644 src/RHI.Desktop/Assets/providers/rhi.png "$appdir/rhi-linux.png"
install -m 0644 src/RHI.Desktop/Assets/providers/rhi.png "$appdir/usr/share/icons/hicolor/96x96/apps/rhi-linux.png"
ln -sfn rhi-linux.png "$appdir/.DirIcon"
if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$appdir/rhi-linux.desktop"
fi

# Stable timestamps: rebuilding the same commit gives the same file metadata in the image.
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct 2>/dev/null || date +%s)}"
export SOURCE_DATE_EPOCH
find "$appdir" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +

echo "=== APPIMAGETOOL $APPIMAGETOOL_VERSION (runtime $RUNTIME_VERSION) ==="
rm -f -- "$out_dir/$name" "$out_dir/$name.sha256"
# Extract-and-run: no FUSE needed on the build host (CI containers have none).
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" --no-appstream --runtime-file "$runtime" "$appdir" "$out_dir/$name"
chmod +x "$out_dir/$name"
(cd "$out_dir" && sha256sum "$name" > "$name.sha256")

echo
echo "Built: $out_dir/$name"
cat "$out_dir/$name.sha256"
