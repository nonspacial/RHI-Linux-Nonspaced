#!/usr/bin/env fish
set ROOT (dirname (status --current-filename))
cd "$ROOT"
or exit 1
dotnet run --project src/RHI.Desktop/RHI.Desktop.csproj $argv
