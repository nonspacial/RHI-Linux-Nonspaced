#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

expected_version="0.9.23.4-beta.1"
expected_facts=604
expected_inline=137
expected_total=741

fail() {
    echo "ERRO: $*" >&2
    exit 1
}

require_literal() {
    local needle="$1"
    local file="$2"
    local label="$3"
    grep -Fq -- "$needle" "$file" || fail "$label"
}

echo "=== RHI Linux $expected_version — public release validation ==="
echo

# Public-release hygiene: only user-facing Markdown belongs at repository root.
while IFS= read -r md; do
    case "$md" in
        README.md|UPSTREAM.md) ;;
        *) fail "internal Markdown file found in public root: $md" ;;
    esac
done < <(find . -maxdepth 1 -type f -name '*.md' -printf '%f\n' | sort)

if find . -maxdepth 1 -type f \( -name 'validate-v*.fish' -o -name 'validate-v*.sh' \) -print -quit | grep -q .; then
    fail "historical versioned validators found in public release"
fi

# Keep validation re-runnable without letting stale build output influence results.
if find src tests -type d \( -name bin -o -name obj \) -print -quit | grep -q . || [[ -d artifacts ]]; then
    echo "Cleaning previous build outputs..."
    find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf -- {} +
    rm -rf -- artifacts
fi

# Public source should not carry obsolete implementation artifacts.
[[ ! -e src/RHI.Core/Models/GameRuntimeContext.cs ]] || fail "obsolete GameRuntimeContext model is still present"
[[ -x install-user.sh ]] || fail "universal Bash user installer missing or not executable"

echo "Public tree hygiene: PASS"

# Release identity and public documentation.
require_literal "public const string Version = \"$expected_version\";" src/RHI.Core/RhiProductInfo.cs "release version identity mismatch"
require_literal "public const string UserAgent = \"RHI-Linux/$expected_version\";" src/RHI.Core/RhiProductInfo.cs "User-Agent identity mismatch"
require_literal "v$expected_version" README.md "README release version missing"
require_literal "functional parity" README.md "README functional-parity policy missing"
require_literal "DLSS5 Feeder / Neural Rendering workflows are experimental" README.md "README DLSS5 experimental status missing"
require_literal "RenoDX DLSS5" README.md "README RenoDX DLSS5 default missing"

echo "Release identity/documentation: PASS"

# Core release contracts that should never silently regress.
require_literal 'public const Dlss5FeederConsumer DefaultConsumer = Dlss5FeederConsumer.RenoDxDlss5;' src/RHI.Platform.Linux/Deployment/Dlss5FeederFullDeploymentService.cs "Feeder default is not RenoDX DLSS5"
require_literal 'internal static class LinuxComponentDependencyPolicy' src/RHI.Platform.Linux/Deployment/LinuxComponentDependencyPolicy.cs "automatic dependency policy missing"
require_literal 'InstallWithManagedDependenciesAsync' src/RHI.Platform.Linux/Deployment/LinuxManagedComponentService.cs "automatic managed dependency installer missing"
require_literal 'public static bool HasKeyParity' src/RHI.Desktop/Localization/UiLocalizationService.cs "localization key-parity contract missing"
require_literal 'public const string PinnedManifestCommit = "a94a574515da3a0864f76a6d120dc4957f61e91f";' src/RHI.Platform.Linux/Deployment/UpstreamRhiManifestService.cs "upstream 2.7.5 fallback pin missing"
require_literal '["Ui.StreamlineUpdate"] = "Streamline — Update"' src/RHI.Desktop/Localization/UiLocalizationService.cs "Streamline update label missing"
require_literal 'public bool HasAction => component.Kind is not (ManagedComponentKind.Dlss or ManagedComponentKind.Streamline);' src/RHI.Desktop/ViewModels/ComponentRowViewModel.cs "Streamline/DLSS informational-row action guard missing"
require_literal '{DynamicResource Ui.StreamlineUpdateNote}' src/RHI.Desktop/Views/MainWindow.axaml "Streamline update description missing"
require_literal 'DeployStreamlineDlssEnabler' src/RHI.Platform.Linux/Deployment/OptiScalerDeploymentService.Advanced.cs "OptiScaler Streamline/DLSS Enabler deploy contract missing"
require_literal 'EnsureStreamlineCachedAsync' src/RHI.Core/Services/DlssManagerService.cs "exact Streamline cache contract missing"
require_literal 'dlss-enabler-headless.dll' src/RHI.Platform.Linux/Deployment/OptiScalerDeploymentService.Streamline.cs "OptiScaler DLSS Enabler payload contract missing"
require_literal 'FGNvngxReplacement' src/RHI.Platform.Linux/Deployment/OptiScalerDeploymentService.Advanced.cs "OptiScaler FG Nvngx replacement control missing"
require_literal 'HasManagedOptiScalerFrameGeneration' src/RHI.Platform.Linux/Deployment/Rtx40MfgStandaloneDeploymentService.cs "Universal MFG managed OptiScaler eligibility contract missing"
require_literal 'var neuralVersionCatalogTask = EnsureNeuralVersionCatalogAsync();' src/RHI.Desktop/ViewModels/MainWindowViewModel.cs "automatic NR version catalog loading missing"
require_literal '_ = RefreshSelectedLatestVersionsAsync(value);' src/RHI.Desktop/ViewModels/MainWindowViewModel.cs "automatic UAL/ShortFuse latest-version refresh missing"
require_literal 'CheckShortFuseLatestReleaseAsync' src/RHI.Platform.Linux/Deployment/LinuxManagedComponentService.cs "ShortFuse upstream-latest check contract missing"
require_literal '["Ui.LoadHistory"] = "Refresh versions"' src/RHI.Desktop/Localization/UiLocalizationService.cs "NR manual refresh label missing"
require_literal ".Split(new[] { ' ', '\\t', '\\r', '\\n'" src/RHI.Desktop/Localization/UiLocalizationService.cs "explicit char[] localization tokenizer missing"

echo "Release contracts: PASS"

# Source test inventory is an explicit release contract.
facts="$(grep -Rho '\[Fact\]' tests --include='*.cs' | wc -l | tr -d '[:space:]')"
inline="$(grep -Rho '\[InlineData' tests --include='*.cs' | wc -l | tr -d '[:space:]')"
total=$((facts + inline))

echo "Test inventory: $facts Fact + $inline InlineData = $total"
if [[ "$facts" -ne "$expected_facts" || "$inline" -ne "$expected_inline" || "$total" -ne "$expected_total" ]]; then
    fail "expected $expected_facts Fact + $expected_inline InlineData = $expected_total tests"
fi

echo "Test inventory: PASS"

command -v dotnet >/dev/null 2>&1 || fail "dotnet is not available in PATH; executable validation was NOT RUN"

# Avoid opaque MSBuild failures when the validation machine is almost full.
check_disk_space() {
    local path="$1"
    local label="$2"
    local available_kb
    available_kb="$(df -Pk "$path" | awk 'NR==2 {print $4}')"
    [[ "$available_kb" =~ ^[0-9]+$ ]] || return 0
    local fail_kb=$((2 * 1024 * 1024))
    local warn_kb=$((3 * 1024 * 1024))
    if (( available_kb < fail_kb )); then
        fail "insufficient free disk space on $label: $((available_kb / 1024)) MiB available; free at least 2 GiB before validation"
    fi
    if (( available_kb < warn_kb )); then
        echo "WARNING: low free disk space on $label: $((available_kb / 1024)) MiB available; 3 GiB or more is recommended." >&2
    fi
}

check_disk_space "$ROOT" "build filesystem"
check_disk_space "$HOME" "home/NuGet filesystem"

echo
dotnet --version

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo
echo "=== RESTORE ==="
dotnet restore RHI-Linux.sln

echo
echo "=== BUILD RELEASE ==="
build_log="$(mktemp)"
trap 'rm -f -- "$build_log"' EXIT
if ! dotnet build RHI-Linux.sln -c Release --no-restore 2>&1 | tee "$build_log"; then
    fail "Release build failed"
fi

# Keep the public release free of warnings from RHI source/tests. CS9057 is intentionally
# allowed because Avalonia 12.1.2 ships analyzers built with a newer Roslyn than .NET 8.0.1xx.
internal_warnings="$(grep -E 'warning (CS[0-9]+|CA[0-9]+|xUnit[0-9]+):' "$build_log" | grep -v 'warning CS9057:' || true)"
if [[ -n "$internal_warnings" ]]; then
    echo "$internal_warnings" >&2
    fail "RHI source/test compiler warnings detected; only external Avalonia CS9057 warnings are allowed"
fi

echo "RHI source/test warnings: 0 (external Avalonia CS9057 ignored)"

echo
echo "=== XUNIT ==="
dotnet test tests/RHI.Core.Tests/RHI.Core.Tests.csproj -c Release --no-build --logger "console;verbosity=minimal"

echo
echo "=== RESULT ==="
echo "PASS — RHI Linux $expected_version public release tree"
echo "Expected xUnit result: $expected_total passed, 0 failed, 0 skipped."
