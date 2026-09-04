#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run --project src/RHI.Desktop/RHI.Desktop.csproj "$@"
