# Upstream baseline

- Project: RankFTW/RHI
- Upstream branch: `main`
- Upstream release observed at port start: **v2.5.6**
- Baseline commit: `b834e883aacb8c380c1fe203384694a64ee04b69`
- Baseline commit message: `Release v2.5.6 — fix NR stale addon removal`
- Baseline timestamp: 2026-09-03 18:28:15 UTC

The Linux port is intentionally maintained as a derivative project rather than pretending the WinUI UI can
be made cross-platform by changing the TargetFramework alone. Cross-platform code should be ported into
`RHI.Core`; OS integration belongs in platform projects.
