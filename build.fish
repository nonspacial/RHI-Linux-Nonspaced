#!/usr/bin/env fish
set ROOT (dirname (status --current-filename))
cd "$ROOT"
or exit 1
dotnet restore RHI-Linux.sln
or exit 1
dotnet test RHI-Linux.sln -c Release --no-restore
or exit 1
dotnet publish src/RHI.Desktop/RHI.Desktop.csproj -c Release -r linux-x64 --self-contained false -o artifacts/linux-x64
or exit 1
echo "Built: $ROOT/artifacts/linux-x64/RHI"
