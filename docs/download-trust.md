# Download trust: host policy and digest rule per source

Every payload RHI Linux downloads goes through `RHI.Core.Deployment.TrustedDownload` with a named
`TrustedDownloadPolicy` (`RHI.Platform.Linux.Deployment.DownloadSources`, plus the core policies for RenoDX, ReShade
add-ons and DLSS). A policy allows HTTPS on the default port to its listed hosts only, and is checked on the link and
on the final link after redirects; it also caps the size. Cached payloads are reused only through `CacheStamp`
(source URL, version, published digest and the SHA-256 of what was stored, re-hashed on every reuse).

**Digest rule (#26).** Upstream RHI 2.8.5 verifies no digest anywhere. RHI Linux:

- verifies a digest whenever the publisher announces one; a mismatch is refused and nothing is placed or stamped;
- without one, follows upstream and accepts the payload on HTTPS + host policy (**fail-open**), but records its
  SHA-256 in the cache stamp and writes one `download-integrity` line to `rhi.log`;
- names every download a deployment record is made from in that record, the same way in every service
  (`VerifiedDownload.Record`): `Download.Sha256` and `Download.Integrity` (`published-digest` / `recorded-only`) for the
  main payload, `Download.<part>.Sha256` / `Download.<part>.Integrity` for each further one (Luma and the Feeder:
  `dgVoodoo`; the Feeder: `Consumer` (RenoDX DLSS5), `Lumenite`, `ReShadeHeaders`, `FeederShader` (raw fallback);
  RTX 20 FG Unlock: `Ini`; OptiScaler's FG stack: `DlssEnabler`; OptiScaler's OptiPatcher: `OptiPatcher`; RTX Encore: `NeuralRendering`; ReShade's Vulkan layer: `VulkanLoader`). The SHA-256 is that of the verified cache entry the
  deployment came from: the downloaded file, or, where RHI keeps only the file it extracted from the download
  (UAL, ShortFuse, DLSS Enabler, Universal MFG), that file. An update rewrites the keys; a local package (Luma, Deep
  Fried Chicken) is not a download and has none. Streamline and `nvngx_*` DLLs from the DLSS manifest are outside
  these policies and are not named;
- is **fail-closed** only where the fork already was and the publisher always ships an authority: Universal MFG and
  RTX Encore refuse a release with neither a GitHub digest nor a SHA256SUMS file, and check both when present.

| Source | Policy (`DownloadSources.*`) | Hosts | Digest authority | Without one |
|---|---|---|---|---|
| ReShade stable/legacy setup | `ReShadeSetup` | reshade.me | none published; upstream pins none | fail-open, SHA-256 stamped |
| ReShade nightly | `ReShadeNightly` | nightly.link, `productionresultssa<n>.blob.core.windows.net` | GitHub Actions artifact `digest` (API) for the newest successful `build.yml` run that crosire/reshade built from its own `main` on a `push` or `schedule` event (as nightly.link's branch link chooses; pull-request runs, including a fork's branch named `main`, are never used); the zip is fetched by that run's nightly.link URL | API unavailable: upstream's branch link, fail-open, never reused, identity = zip SHA-256 |
| GitHub API metadata | `GitHubApi` | api.github.com | n/a (16 MiB cap; ETag revalidation, #35) | n/a |
| ReLimiter, Display Commander, other GitHub add-ons | `GitHubAddonRelease` | GitHub release hosts | GitHub asset `digest` | fail-open |
| OptiScaler (stable, nightly, DLSS-NR) | `OptiScaler` | GitHub release hosts | GitHub asset `digest` | fail-open |
| OptiPatcher (rolling release, #98) | `OptiPatcher` | GitHub release hosts, 256 MiB | GitHub asset `digest`; cached per build commit, must be a 64-bit PE | fail-open |
| Luma Framework | `Luma` | GitHub release hosts (Filoppi namespace checked separately) | GitHub asset `digest` | fail-open; catalogue links without a version are fetched every time |
| REFramework | `REFramework` | GitHub release hosts | GitHub asset `digest` | fail-open |
| Ultimate ASI Loader | `UltimateAsiLoader` | GitHub release hosts | GitHub asset `digest` | fail-open |
| ShortFuse, DLSS Enabler, RenoDX DLSS5 consumer (RankFTW/rhi-repo) | `RhiRepository` | GitHub release hosts | GitHub asset `digest` | fail-open |
| Universal MFG (RTX40MFG-Unlock) | `UniversalMfg` | GitHub release hosts | GitHub asset `digest` and/or `SHA256SUMS.txt` | **fail-closed** (release refused) |
| RTX Encore (#158) | `RtxEncore` | GitHub release hosts, 512 MiB | GitHub asset `digest` and/or `SHA256SUMS`; the DLL must carry RTX Encore's version resource. Its optional `nvngx_dlssnr.dll` (record part `NeuralRendering`) comes from the DLSS manifest and must match a pinned SHA-256 (NVIDIA 310.8.0) | **fail-closed** (release refused) |
| Vulkan runtime (Khronos loader for ReShade's Vulkan layer, #92) | `LunarGVulkanRuntime` | sdk.lunarg.com (zip), vulkan.lunarg.com (version list), 128 MiB | none published; the extracted `vulkan-1.dll` must be a PE of the game's bitness whose version resource says "Vulkan Loader" (record part `VulkanLoader`) | fail-open, SHA-256 stamped |
| Nexus Mods game list (#174, card menu's Nexus page) | `NexusGameList` | data.nexusmods.com, 16 MiB | none (metadata, parsed before use; 24 h cache, last good copy kept) | n/a (nothing is installed from it) |
| DLSSG 20/30 unlock | `Dlssg2030` | raw.githubusercontent.com | git blob id from the contents API at the resolved commit (#41) | refused if GitHub reports no blob id |
| dgVoodoo2 | `DgVoodoo` | GitHub release hosts | GitHub asset `digest`; manifest links carry none | fail-open |
| DLSS5 Feeder (release zip, tagged shader, branch archives) | `Dlss5Feeder` | GitHub release hosts, raw.githubusercontent.com, codeload.github.com | GitHub asset `digest` for the release zip; branch archives have none | fail-open; branch archives are fetched per install |
| ReShade shader packs | `ShaderPacks` | GitHub release hosts, codeload.github.com, gitlab.com (upstream manifest's UltraShade) | none for branch archives | fail-open |
| Upstream Engine.ini templates (`engineIniFiles`, RenoDX installs) | `EngineIniTemplates` | raw.githubusercontent.com, 256 KiB | none (text template, parsed before use; last-known-good cache when unreachable) | fail-open |
| Universal UE DOF Fix | core `GitHub` | GitHub release hosts, raw.githubusercontent.com | GitHub asset `digest`; a manifest `ueDofFix` override names another file, so the digest does not apply | fail-open |
| RenoDX add-ons | core `RenoDxAddons` | GitHub, `<author>.github.io`, renodx.com | none (snapshots are rebuilt in place) | fail-open; ETag/Last-Modified/size stamp for update checks |
| ReShade Addon Manager | core `ReShadeAddons` | GitHub, `<author>.github.io`, raw.githubusercontent.com (Addons.ini) | none checked | fail-open |

**Stalled downloads (#233).** Every body `TrustedDownload` reads (payloads and metadata) gives up when no byte arrives
for `TrustedDownload.IdleLimit` (60 s; each read has its own timer, so a slow but steady download still finishes) with
`DownloadStalledException` ("The download stopped: no data arrived for 60 seconds. Source: <policy name>"), a network
failure, never a Cancel: the temp file is removed and nothing is placed or stamped. GitHub API answers that
`GitHubConditionalCacheHandler` (below) reads inside `SendAsync` are read under the same limit (it holds at most 8 MiB +
1 byte; a longer answer is passed on whole and not cached). The Luma wiki page, the GitHub token check and Nexus Mods
API answers read under the same limit too. Not covered: the wait for the response headers, bounded by the client's
`HttpClient.Timeout` (app: 5 min; token check and Nexus Mods: 30 s), which on `TrustedDownload` paths and the Nexus Mods
client reads "The server took too long to answer: RHI stopped waiting after 300 seconds. Source: <policy name>"
(`DownloadTimedOutException`, EN + PT-BR); bodies read under a whole-body timeout token or buffered by `HttpClient`
(reshade.me, RenoDX catalogue, MOTD: its 5-min total) are listed in `UPSTREAM.md`. The limit is per gap, never a total
deadline: a server sending a byte every 59 s is not stopped by it (the manual Cancel works; a throughput floor is #235).

**Release lookups (#35).** GitHub API GETs pass through `GitHubConditionalCacheHandler` on the app's `HttpClient`
(upstream's session `GitHubETagCache`): answers are revalidated with `If-None-Match`/`If-Modified-Since`; a 304
(which costs no rate limit) returns the cached body; when GitHub refuses (403/429 with no quota left, or 5xx) the
cached answer is served and, until the advertised reset, not asked again.

**Archives (#37).** ZIP members are taken out only through `ReShadeArchiveExtractor.ExtractEntryAsync` (declared-size
cap, fixed mode, length + CRC-32, atomic rename). 7z/encrypted archives go through `ExternalArchiveRunner` (one
runner: time limit, process-tree kill, password on stdin) and are refused when anything extracted is a symbolic link.
