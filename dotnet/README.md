# OpenVersus client, .NET edition

The OpenVersus client as a C# NativeAOT plugin: the same `OpenVersus.asi`, loaded by Ultimate
ASI Loader, reading the same `OpenVersus.ini`, `OVSState.ini` and `PatternsCache.cache`. It is a
port of the C++ client at the repository root, built from Linux without Visual Studio.

- `OpenVersus.Core/` — everything: memory and hooking, game structures, the function registry,
  settings, HTTP, identity, auto-update, the notification poller, netstats.
- `OpenVersus/` — the `.asi` itself; only the `InitializeASI` export.
- `OpenVersus.HookTest/` — a plugin for the Wine harness that proves the hooking layer on a
  small host, with no game involved.
- `OpenVersus.Tests/` — xunit tests that run on the Linux host.
- `wine-host/` — the harness: `run.sh` builds `host.c`, publishes the test plugin and runs both
  under Wine in a private prefix.

## Build

Requires the .NET 10 SDK, and for a Windows binary `lld-link` (package `lld`) and `xwin` on `PATH`.

```sh
dotnet build dotnet/OpenVersus.slnx          # everything, for the host (tests, no AOT)
dotnet test  dotnet/OpenVersus.slnx
dotnet publish dotnet/OpenVersus/OpenVersus.csproj -c Release -r win-x64 -p:AcceptVSBuildToolsLicense=true
dotnet/wine-host/run.sh                      # the hooking layer, end to end under Wine
```

Trampoline pages are read-execute except while a stub is being written. `-p:RwxTrampolines=true`
on the publish keeps them read-write-execute for their whole life, as the C++ client did.

The published plugin is `dotnet/OpenVersus/bin/Release/net10.0/win-x64/publish/OpenVersus.asi`.
It imports only system DLLs and the UCRT api-sets. The first publish downloads the Windows SDK
sysroot (about 2.4 GB) into `~/.cache/xwin`; `AcceptVSBuildToolsLicense=true` accepts its license.

## How it maps to the C++ client

| C++ | Here |
| --- | --- |
| `DllMain` → `OnInitializeHook` | `Plugin.InitializeASI` → `Client.Initialize` (NativeAOT cannot run managed code in `DllMain`; the loader calls the export right after `LoadLibrary`) |
| `PatternFinder`, `CachedPatternsMgr` | `PatternResolver`, `PatternCache`; the pattern parser has the C++ semantics (`?` is one byte) and a cached address is checked before use |
| `MakeProxyFromOpCode`, `InjectHook`, `Trampoline` | `CallSite.Redirect`, `CallSite.Inject`, `Trampoline` |
| `OVS::Hooks::*` | `Hooks/*`; the sig-check, sunset-date and post-match-freeze changes are byte patches (the sunset check runs thousands of times a minute), the endpoint and game-instance hooks are call redirects into `[UnmanagedCallersOnly]` methods guarded by `HookGuard`; the startup dialog and toast are a game-thread job |
| `MVSGame::*` function globals | `GameFunctions`: every resolved function by name, with where it came from and its signature; reflected functions through `Reflection.Find` |
| `NotificationPoller` heap scans | `ObjectFinder` over the engine's object array, falling back to the heap scan |
| `__try`/`__except` reads | `CodeWriter.TryRead` (a guarded read; NativeAOT cannot catch access violations) |
| WinInet / WinHTTP / raw socket | `IHttpTransport`, implemented on WinHTTP |

## Log levels

`Log` is a `Microsoft.Extensions.Logging.ILogger` with its own writer (file plus console). The
minimum level comes from `[Settings] LogLevel`: a name (`trace`, `debug`, `info`, `warn`, `error`,
`critical`, `none`) or a number (1 debug to 6 none). `0`, which existing files carry, means
"decide from `[Settings.Debug] DebugLogging`": Debug when it is on, Information when it is off.
Tags in the file are `TRC DBG NFO WRN ERR CRT`. Per-attempt and game-thread queue lines are
Trace; pattern and function resolution is Debug; hooks, banners and netstats are Information.

## Settings files

`OpenVersus.ini`, `OVSState.ini` and `PatternsCache.cache` are read and written by
`Config/IniFile.cs`, which keeps the file line for line. Reading behaves like the Windows profile
API the C++ client used (names match regardless of case, values are trimmed and lose surrounding
quotes, `;` starts a comment), so every file players already have reads the same. Writing adds a
missing key at the end of its section in the file's own `Key = Value` or `Key=Value` style, adds a
missing section at the end, and touches nothing else: blank lines, comments, spacing, unknown
keys, line endings and encoding stay as the player left them, and a complete file is not
rewritten at all. Booleans are `true`/`false`, `on`/`off` or `1`/`0` in any case. A value that does not
parse is logged as a warning, read as its default and left on disk, where the C++ client would
have replaced it with the default. A file written from nothing comes out in the
shape of `sample.ini`, with CRLF line endings.

## Sunset check switches

The game's sunset-date check is called thousands of times a minute. `[Patches] SunsetDate`
(default on) makes the function itself return false with two byte patches. Two more switches,
both off by default:

- `[Patches] SunsetCallers=true` finds every direct call and tail jump to the function through
  `.pdata` and a `.text` scan, and turns each into "return false" in place, so the function is
  never entered. The log reports the count found (146 in the final build).
- `[Settings.Debug] CountSunsetCalls=true` routes the function's comparison path through a
  counter, and the heartbeat line each minute reports how many calls the last minute saw. With
  both switches on, that number must be zero.

## Testing against the game

Copy `OpenVersus.asi` over the one in `plugins/` next to the game (keep the old one as a `.bak`).
With `DebugLogging=true`, `logs/OpenVersus.log` next to the plugin (the running log, truncated
each launch; the previous run is copied to `logs/OpenVersus_<launch time>.log`) lists every pattern, the address
of every function it resolved, and the hooks that took; compare those addresses against the C++
build's console output. Set `AutoUpdate=false` while testing: this build's version check works
against https, which the C++ one did not.
