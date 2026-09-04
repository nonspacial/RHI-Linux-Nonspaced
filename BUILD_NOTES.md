# Build / validation notes — v0.1.0

## Validation completed in the generation environment

- All `.axaml` and `.csproj` files parse as XML.
- C# source passed an independent lexical/bracket scan.
- Bash helper scripts pass `bash -n`.
- `src/` contains no direct WinUI, Windows App SDK, Windows Registry, NvAPIWrapper, Task Scheduler, or `net8.0-windows` dependency.
- The project includes a Linux GitHub Actions workflow that restores, tests and publishes `linux-x64`.

## Validation not completed here

A real `dotnet restore`, `dotnet test` and `dotnet publish` could not be executed in the generation container because the .NET SDK is not installed there. Therefore this snapshot is **not claimed as compile-validated**. Run `./build.fish` (or the included GitHub Actions workflow) before treating it as a release build.

## Upstream reference

The snapshot was designed against RankFTW/RHI v2.5.6, upstream commit `b834e883aacb8c380c1fe203384694a64ee04b69` observed on 2026-09-03.
