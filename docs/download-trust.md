# Download trust: host policy and digest rule per source

Every payload RHI Linux downloads goes through `RHI.Core.Deployment.TrustedDownload` with a named
`TrustedDownloadPolicy` (`RHI.Platform.Linux.Deployment.DownloadSources`, plus the core policies for RenoDX, ReShade
add-ons and DLSS). A policy allows HTTPS on the default port to its listed hosts only, and is checked on the link and
on the final link after redirects; it also caps the size. Cached payloads are reused only through `CacheStamp`
(source URL, version, published digest and the SHA-256 of what was stored, re-hashed on every reuse).

**Digest rule (#26).** Upstream RHI 2.8.5 verifies no digest anywhere. RHI Linux:

- verifies a digest whenever the publisher announces one; a mismatch is refused and nothing is placed or stamped;
- without one, follows upstream and accepts the payload on HTTPS + host policy (**fail-open**), but records its
  SHA-256 in the cache stamp, writes one `download-integrity` line to `rhi.log`, and, where the service writes a
  deployment record from that download, stores `Download.Sha256` and `Download.Integrity`
  (`published-digest` / `recorded-only`) in the record's metadata;
- is **fail-closed** only where the fork already was and the publisher always ships an authority: Universal MFG
  refuses a release with neither a GitHub digest nor `SHA256SUMS.txt`, and checks both when present.

| Source | Policy (`DownloadSources.*`) | Hosts | Digest authority | Without one |
|---|---|---|---|---|
| ReShade stable/legacy setup | `ReShadeSetup` | reshade.me | none published; upstream pins none | fail-open, SHA-256 stamped |
| ReShade nightly | `ReShadeNightly` | nightly.link, `productionresultssa<n>.blob.core.windows.net` | GitHub Actions artifact `digest` (API) for the newest successful `build.yml` run on main; the zip is fetched by that run's nightly.link URL | API unavailable: upstream's branch link, fail-open, never reused, identity = zip SHA-256 |
| GitHub API metadata | `GitHubApi` | api.github.com | n/a (16 MiB cap; ETag revalidation, #35) | n/a |
| ReLimiter, Display Commander, other GitHub add-ons | `GitHubAddonRelease` | GitHub release hosts | GitHub asset `digest` | fail-open |
| OptiScaler (stable, nightly, DLSS-NR) | `OptiScaler` | GitHub release hosts | GitHub asset `digest` | fail-open |
| Luma Framework | `Luma` | GitHub release hosts (Filoppi namespace checked separately) | GitHub asset `digest` | fail-open; catalogue links without a version are fetched every time |
| REFramework | `REFramework` | GitHub release hosts | GitHub asset `digest` | fail-open |
| Ultimate ASI Loader | `UltimateAsiLoader` | GitHub release hosts | GitHub asset `digest` | fail-open |
| ShortFuse, DLSS Enabler, RenoDX DLSS5 consumer (RankFTW/rhi-repo) | `RhiRepository` | GitHub release hosts | GitHub asset `digest` | fail-open |
| Universal MFG (RTX40MFG-Unlock) | `UniversalMfg` | GitHub release hosts | GitHub asset `digest` and/or `SHA256SUMS.txt` | **fail-closed** (release refused) |
| DLSSG 20/30 unlock | `Dlssg2030` | raw.githubusercontent.com | git blob id from the contents API at the resolved commit (#41) | refused if GitHub reports no blob id |
| dgVoodoo2 | `DgVoodoo` | GitHub release hosts | GitHub asset `digest`; manifest links carry none | fail-open |
| DLSS5 Feeder (release zip, tagged shader, branch archives) | `Dlss5Feeder` | GitHub release hosts, raw.githubusercontent.com, codeload.github.com | GitHub asset `digest` for the release zip; branch archives have none | fail-open; branch archives are fetched per install |
| ReShade shader packs | `ShaderPacks` | GitHub release hosts, codeload.github.com, gitlab.com (upstream manifest's UltraShade) | none for branch archives | fail-open |
| Universal UE DOF Fix | core `GitHub` | GitHub release hosts, raw.githubusercontent.com | GitHub asset `digest`; a manifest `ueDofFix` override names another file, so the digest does not apply | fail-open |
| RenoDX add-ons | core `RenoDxAddons` | GitHub, `<author>.github.io`, renodx.com | none (snapshots are rebuilt in place) | fail-open; ETag/Last-Modified/size stamp for update checks |
| ReShade Addon Manager | core `ReShadeAddons` | GitHub, `<author>.github.io`, raw.githubusercontent.com (Addons.ini) | none checked | fail-open |

**Release lookups (#35).** GitHub API GETs pass through `GitHubConditionalCacheHandler` on the app's `HttpClient`
(upstream's session `GitHubETagCache`): answers are revalidated with `If-None-Match`/`If-Modified-Since`; a 304
(which costs no rate limit) returns the cached body; when GitHub refuses (403/429 with no quota left, or 5xx) the
cached answer is served and, until the advertised reset, not asked again.

**Archives (#37).** ZIP members are taken out only through `ReShadeArchiveExtractor.ExtractEntryAsync` (declared-size
cap, fixed mode, length + CRC-32, atomic rename). 7z/encrypted archives go through `ExternalArchiveRunner` (one
runner: time limit, process-tree kill, password on stdin) and are refused when anything extracted is a symbolic link.
