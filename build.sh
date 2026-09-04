#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
dotnet restore RHI-Linux.sln
dotnet test RHI-Linux.sln -c Release --no-restore
dotnet publish src/RHI.Desktop/RHI.Desktop.csproj -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
printf '\nBuilt: %s\n' "$PWD/artifacts/linux-x64/RHI"
