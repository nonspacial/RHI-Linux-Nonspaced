#!/usr/bin/env bash
# Release gate for RHI Linux: tree hygiene, version identity, locked restore, a Release build
# with every compiler/analyser warning treated as an error, and the full xUnit suite.
#
# Usage: ./validate.sh [--clean]
#
#   --clean   delete every bin/ and obj/ under src/ and tests/, and artifacts/, before building.
#             Off by default so a developer tree keeps its build outputs and publishes
#             (e.g. artifacts/sc); the build is incremental and test results are rewritten.
#
# Behaviour is checked by the test suite, not by grepping source text. The product version
# lives only in Directory.Build.props (<Version>).
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

fail() {
    echo "ERROR: $*" >&2
    exit 1
}

clean=false
for arg in "$@"; do
    case "$arg" in
        --clean) clean=true ;;
        -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) fail "unknown option: $arg (see --help)" ;;
    esac
done

command -v dotnet >/dev/null 2>&1 || fail "dotnet is not available in PATH; validation was NOT RUN"

# Public-release hygiene: only user-facing Markdown belongs at the repository root.
while IFS= read -r md; do
    case "$md" in
        README.md|UPSTREAM.md) ;;
        *) fail "internal Markdown file found in public root: $md" ;;
    esac
done < <(find . -maxdepth 1 -type f -name '*.md' -printf '%f\n' | sort)

if find . -maxdepth 1 -type f \( -name 'validate-v*.fish' -o -name 'validate-v*.sh' \) -print -quit | grep -q .; then
    fail "historical versioned validators found in public release"
fi
[[ -x install-user.sh ]] || fail "universal Bash user installer missing or not executable"

if [[ "$clean" == true ]]; then
    echo "Cleaning previous build outputs (--clean)..."
    find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf -- {} +
    rm -rf -- artifacts
fi
echo "Public tree hygiene: PASS"

# Release identity: one version, read from Directory.Build.props; the README must show it.
version="$(dotnet msbuild src/RHI.Core/RHI.Core.csproj -getProperty:Version 2>/dev/null | tr -d '[:space:]')"
[[ -n "$version" ]] || fail "could not read <Version> from Directory.Build.props"
grep -Fq -- "v$version" README.md || fail "README.md does not mention the current version v$version"
echo "=== RHI Linux $version — release validation ==="

# Avoid opaque MSBuild failures when the machine is almost full.
check_disk_space() {
    local path="$1"
    local label="$2"
    local available_kb
    [[ -d "$path" ]] || return 0
    available_kb="$(df -Pk "$path" | awk 'NR==2 {print $4}')"
    [[ "$available_kb" =~ ^[0-9]+$ ]] || return 0
    if (( available_kb < 2 * 1024 * 1024 )); then
        fail "insufficient free disk space on $label: $((available_kb / 1024)) MiB available; free at least 2 GiB before validation"
    fi
    if (( available_kb < 3 * 1024 * 1024 )); then
        echo "WARNING: low free disk space on $label: $((available_kb / 1024)) MiB available; 3 GiB or more is recommended." >&2
    fi
}
check_disk_space "$ROOT" "build filesystem"
check_disk_space "${NUGET_PACKAGES:-$HOME/.nuget/packages}" "NuGet package filesystem"

echo
dotnet --version

echo
echo "=== RESTORE (locked) ==="
dotnet restore RHI-Linux.sln --locked-mode

echo
echo "=== BUILD RELEASE (warnings are errors) ==="
# NuGet vulnerability-audit notices (NU1900-NU1904) stay visible but do not fail the gate:
# a new advisory or an unreachable audit feed must not break unrelated changes.
dotnet build RHI-Linux.sln -c Release --no-restore -warnaserror \
    -warnnotaserror:NU1900,NU1901,NU1902,NU1903,NU1904 \
    || fail "Release build failed (warnings count as errors)"

echo
echo "=== XUNIT ==="
results_dir="$ROOT/artifacts/test-results"
rm -rf -- "$results_dir"
dotnet test tests/RHI.Core.Tests/RHI.Core.Tests.csproj -c Release --no-build \
    --logger "console;verbosity=minimal" \
    --logger "trx;LogFileName=RHI.Core.Tests.trx" \
    --results-directory "$results_dir" \
    || fail "tests failed"

# A runner that discovers nothing exits 0; make sure tests really ran.
trx="$results_dir/RHI.Core.Tests.trx"
[[ -f "$trx" ]] || fail "test results file $trx was not written"
counter() { grep -o '<Counters [^>]*' "$trx" | grep -o " $1=\"[0-9]*\"" | grep -o '[0-9]\+' || true; }
total="$(counter total)"; passed="$(counter passed)"; failed="$(counter failed)"
[[ "$total" =~ ^[0-9]+$ ]] && (( total > 0 )) || fail "no tests were discovered"
[[ "$failed" == 0 ]] || fail "$failed test(s) failed"

echo
echo "=== RESULT ==="
echo "PASS — RHI Linux $version: build clean, $passed of $total tests passed (results: artifacts/test-results)."
