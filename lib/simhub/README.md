# SimHub SDK DLLs (vendored for CI builds)

This folder vendors three DLLs from a SimHub install so that
`F1SimHubLive.csproj` can compile on machines that **don't** have SimHub
installed — primarily GitHub Actions runners during the release build.

| File | Source | Why we need it |
|---|---|---|
| `SimHub.Plugins.dll` | `C:\Program Files (x86)\SimHub\SimHub.Plugins.dll` | `IDataPlugin`, `PluginNameAttribute`, `PluginAuthorAttribute`, `PluginDescriptionAttribute` — the SimHub plugin contract types |
| `GameReaderCommon.dll` | `C:\Program Files (x86)\SimHub\GameReaderCommon.dll` | `GameData` and related game-state DTOs |
| `log4net.dll` | `C:\Program Files (x86)\SimHub\log4net.dll` | SimHub's logging framework — we get a `Logger` instance via SimHub at runtime, but the type ref needs to resolve at compile time |

These are **compile-time references only.** The plugin csproj sets
`<Private>false</Private>` on all three so they never end up in the
plugin build output — SimHub already has its own copies at runtime, we
must not shadow or overwrite them.

## Why vendor instead of download-during-build?

Earlier approach was `<HintPath>$(SimHubPath)\...</HintPath>` with
`$(SimHubPath)` defaulting to `$(MSBuildProgramFiles32)\SimHub`. That
works on every dev machine that has SimHub installed (zero config). It
silently broke on CI runners where SimHub is not installed — `csc`
reported "type or namespace IDataPlugin could not be found" and the
release workflow failed.

Alternatives considered:
1. **Download SimHub installer at CI time** — adds ~30 sec, fragile
   (URL/version drift), still has redistribution concerns since SimHub
   doesn't expose a "SDK only" download.
2. **Build the plugin DLL locally and commit `installer/Assets/F1SimHubLive.dll`** — exactly the bug v1.1.2 fixed in the
   first place (stale-binary-in-repo lottery).
3. **NuGet package for SimHub.Plugins** — does not exist as of 2026-06.
4. **Vendor the three DLLs (what we chose)** — adds ~10 MB to the repo
   one time, makes CI deterministic, requires no per-build network IO,
   easy to refresh if SimHub publishes a contract change (just copy the
   newer DLLs into here and commit).

## License / redistribution

SimHub is freely distributable for personal use per its [download page]
(https://www.simhubdash.com/download-2). The DLLs here are unmodified
copies shipped as part of the SimHub installer. We redistribute them
here purely as a build-time dependency for an open-source plugin that
extends SimHub for the SimHub user community — same pattern many other
public SimHub plugin repos use. If the SimHub maintainers ever object
we'll move to approach (1) above.

## Refreshing these DLLs

If SimHub publishes a contract change that the plugin needs to pick up,
update these files from a local install:

```powershell
$simhub = "C:\Program Files (x86)\SimHub"
@("SimHub.Plugins.dll", "GameReaderCommon.dll", "log4net.dll") | ForEach-Object {
    Copy-Item (Join-Path $simhub $_) "lib\simhub\$_" -Force
}
git add lib/simhub
git commit -m "chore(lib): refresh SimHub SDK DLLs"
```
