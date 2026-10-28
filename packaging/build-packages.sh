#!/usr/bin/env bash
# Builds the RHI Linux .tar.gz, .deb and .rpm downloads from one self-contained publish.
#
#   packaging/build-packages.sh [--out DIR] [--from-publish DIR]
#
#   --out DIR           where the packages go (default: artifacts/packages)
#   --from-publish DIR  package this self-contained linux-x64 publish (e.g. the AppImage's AppDir/usr/bin)
#                       instead of publishing again; it is copied, never changed
#
# Output in DIR (the version comes from <Version> in Directory.Build.props):
#   RHI-Linux-v<version>-x86_64.tar.gz   folder RHI-Linux-v<version>/: RHI and its libraries, rhi-linux.desktop,
#                                        rhi-linux.png, INSTALL.txt
#   rhi-linux_<version>_amd64.deb        Debian, Ubuntu and derivatives
#   rhi-linux-<version>-1.x86_64.rpm     Fedora, openSUSE
#   SHA256SUMS                           every package above, plus RHI-Linux-v<version>-x86_64.AppImage when it is in DIR
#
# The .deb and .rpm (packaging/nfpm.yaml) install the app in /opt/rhi-linux, the launcher /usr/bin/rhi-linux
# (packaging/rhi-linux-launcher.sh: no environment changes), the desktop entry and the hicolor icon.
# nfpm is downloaded once over HTTPS from its GitHub release at a pinned version and used only if its SHA-256
# matches; it is cached in artifacts/package-tools (override with RHI_PACKAGE_TOOLS_DIR). File times come from
# SOURCE_DATE_EPOCH (default: the last commit's time), so the same commit gives the same package contents.
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

NFPM_VERSION="2.48.0"
NFPM_SHA256="6d7f9a28b2f397934403950fcbba83f598b08d02edbe79a0815b3b4897341750"

out_dir="$ROOT/artifacts/packages"
from_publish=""
while (( $# > 0 )); do
    case "$1" in
        --out) [[ $# -ge 2 ]] || { echo "--out needs a directory" >&2; exit 2; }; out_dir="$2"; shift 2 ;;
        --from-publish) [[ $# -ge 2 ]] || { echo "--from-publish needs a directory" >&2; exit 2; }
            from_publish="$2"; shift 2 ;;
        -h|--help) sed -n '2,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done
mkdir -p "$out_dir"
out_dir="$(cd -- "$out_dir" && pwd)"
tools_dir="${RHI_PACKAGE_TOOLS_DIR:-$ROOT/artifacts/package-tools}"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

fail() { echo "ERROR: $*" >&2; exit 1; }
for tool in curl sha256sum tar gzip; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool is required"
done
[[ -n "$from_publish" ]] || command -v dotnet >/dev/null 2>&1 || fail "dotnet is not available in PATH"

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

if command -v dotnet >/dev/null 2>&1; then
    version="$(dotnet msbuild src/RHI.Desktop/RHI.Desktop.csproj -getProperty:Version 2>/dev/null | tr -d '[:space:]')"
else
    version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
fi
[[ -n "$version" ]] || fail "could not read <Version> from Directory.Build.props"
[[ "$version" =~ ^[0-9A-Za-z.~+-]+$ ]] || fail "unexpected characters in version '$version'"

tarball="RHI-Linux-v$version-x86_64.tar.gz"
deb="rhi-linux_${version}_amd64.deb"
rpm="rhi-linux-$version-1.x86_64.rpm"
appimage="RHI-Linux-v$version-x86_64.AppImage"

SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git log -1 --format=%ct 2>/dev/null || date +%s)}"
export SOURCE_DATE_EPOCH

# nfpm: the binary is taken from the verified release archive on every run.
nfpm_archive="$tools_dir/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz"
fetch_verified "https://github.com/goreleaser/nfpm/releases/download/v$NFPM_VERSION/nfpm_${NFPM_VERSION}_Linux_x86_64.tar.gz" \
    "$nfpm_archive" "$NFPM_SHA256"

work="$(mktemp -d "$out_dir/.packages-work.XXXXXX")"
trap 'rm -rf -- "$work"' EXIT
tar -xzf "$nfpm_archive" -C "$work" nfpm
nfpm="$work/nfpm"
chmod +x "$nfpm"

publish="$work/publish"
if [[ -n "$from_publish" ]]; then
    [[ -d "$from_publish" ]] || fail "--from-publish: $from_publish is not a directory"
    echo "=== REUSE publish $from_publish ($version) ==="
    mkdir -p "$publish"
    cp -a -- "$from_publish/." "$publish/"
else
    echo "=== PUBLISH linux-x64 self-contained ($version) ==="
    dotnet publish src/RHI.Desktop/RHI.Desktop.csproj -c Release -r linux-x64 --self-contained true \
        -p:RestoreLockedMode=true -o "$publish"
fi
find "$publish" -name '*.pdb' -delete
[[ -x "$publish/RHI" && -f "$publish/libcoreclr.so" && -f "$publish/libhostfxr.so" ]] \
    || fail "not a self-contained linux-x64 publish of RHI (RHI, libcoreclr.so, libhostfxr.so)"
grep -q '"includedFrameworks"' "$publish/RHI.runtimeconfig.json" 2>/dev/null \
    || fail "RHI.runtimeconfig.json does not describe a self-contained publish"
# Owner-writable only, whatever the build host's umask; executable bits stay as published.
chmod -R u+rwX,go+rX,go-w "$publish"

desktop="$work/rhi-linux.desktop"
cp packaging/rhi-linux.desktop "$desktop"
grep -qx 'Exec=rhi-linux %u' "$desktop" || fail "packaging/rhi-linux.desktop must start rhi-linux (Exec=rhi-linux %u)"
if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "$desktop"
fi
icon=src/RHI.Desktop/Assets/providers/rhi.png

# Staged tree for nfpm (packaging/nfpm.yaml reads RHI_PKG_ROOT).
pkgroot="$work/pkgroot"
mkdir -p "$pkgroot/opt" "$pkgroot/usr/bin" "$pkgroot/usr/share/applications" \
    "$pkgroot/usr/share/icons/hicolor/96x96/apps"
cp -a -- "$publish" "$pkgroot/opt/rhi-linux"
install -m 0755 packaging/rhi-linux-launcher.sh "$pkgroot/usr/bin/rhi-linux"
install -m 0644 "$desktop" "$pkgroot/usr/share/applications/rhi-linux.desktop"
install -m 0644 "$icon" "$pkgroot/usr/share/icons/hicolor/96x96/apps/rhi-linux.png"

# Tarball folder.
tardir="$work/tar/RHI-Linux-v$version"
mkdir -p "$(dirname -- "$tardir")"
cp -a -- "$publish" "$tardir"
install -m 0644 "$desktop" "$tardir/rhi-linux.desktop"
install -m 0644 "$icon" "$tardir/rhi-linux.png"
sed "s/@VERSION@/$version/g" packaging/INSTALL.txt > "$tardir/INSTALL.txt"
chmod 0644 "$tardir/INSTALL.txt"

find "$pkgroot" "$work/tar" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +

rm -f -- "$out_dir/$tarball" "$out_dir/$deb" "$out_dir/$rpm" "$out_dir/SHA256SUMS"

echo "=== TAR.GZ $tarball ==="
tar --sort=name --owner=0 --group=0 --numeric-owner --mtime="@$SOURCE_DATE_EPOCH" \
    --format=gnu -C "$work/tar" -cf - "RHI-Linux-v$version" | gzip -n -9 > "$work/$tarball"
mv -f -- "$work/$tarball" "$out_dir/$tarball"

echo "=== NFPM $NFPM_VERSION: .deb and .rpm ==="
export RHI_VERSION="$version" RHI_PKG_ROOT="$pkgroot"
"$nfpm" package --config packaging/nfpm.yaml --packager deb --target "$out_dir/$deb"
"$nfpm" package --config packaging/nfpm.yaml --packager rpm --target "$out_dir/$rpm"

assets=("$tarball" "$deb" "$rpm")
[[ -f "$out_dir/$appimage" ]] && assets=("$appimage" "${assets[@]}")
(cd "$out_dir" && sha256sum -- "${assets[@]}" > SHA256SUMS)

echo
echo "Built in $out_dir:"
cat "$out_dir/SHA256SUMS"
