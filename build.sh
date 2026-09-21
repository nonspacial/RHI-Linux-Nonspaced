#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

check_disk_space() {
    local path="$1"
    local label="$2"
    local available_kb
    available_kb="$(df -Pk "$path" | awk 'NR==2 {print $4}')"
    [[ "$available_kb" =~ ^[0-9]+$ ]] || return 0
    if (( available_kb < 2 * 1024 * 1024 )); then
        echo "ERROR: insufficient free disk space on $label: $((available_kb / 1024)) MiB available; free at least 2 GiB." >&2
        exit 1
    elif (( available_kb < 3 * 1024 * 1024 )); then
        echo "WARNING: low free disk space on $label: $((available_kb / 1024)) MiB available; 3 GiB or more is recommended." >&2
    fi
}

check_disk_space "$ROOT" "build filesystem"
check_disk_space "$HOME" "home/NuGet filesystem"

dotnet restore RHI-Linux.sln
dotnet test RHI-Linux.sln -c Release --no-restore
dotnet publish src/RHI.Desktop/RHI.Desktop.csproj -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
printf '\nBuilt: %s\n' "$ROOT/artifacts/linux-x64/RHI"
