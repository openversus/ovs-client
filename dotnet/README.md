# OpenVersus MVS mod

## TL;DR: I just wanna play MVS again
[Go here and download the latest release.](https://github.com/openversus/ovs-client/releases)

### Current Features:
- Anti-Pak Signature Patch. Allows you to run `.pak` files with invalid `.sig` signatures (custom local skins ***replacing*** an existing skin or emote, etc).
- Anti-uToc Signature Patch. Allows you to use `uToc` files (and `.uCas`) with invalid Signature Headers (see above).
- Custom Server addresses: Allows you to change the server endpoint (whether it points to official OpenVersus servers or not. OpenVersus server code is *also* open source and [available here](https://github.com/openversus/))
- Bypass the game's built-in network sunset date/killswitch checks (necessary for online play)
- Supports using custom in-game notification dialogs
- Restores the original post-match behavior: after the final stock, everyone still in the match sees what the winners do again, instead of them standing frozen until the lobby
- Auto-update: at launch, checks for a newer version and installs it, then closes the game so the next launch runs the update (`AutoUpdate`, on by default)

### How to use:
Comprehensive install instructions are available in the `readme.txt` file included in the release zip file. An installer will be provided with a later build, but for right now, it's just a simple drag & drop.

### Settings
Settings live in `OpenVersus.toml` beside the plugin, which the mod creates with default values when it's missing. [`sample.toml`](sample.toml) in this repo is a copy of that default file, for reference. Text values go in double quotes (`ServerUrl = "https://prod.openversus.org/"`); `true`, `false` and numbers don't. Lines starting with `#` are comments, and a `#` after a value starts a comment too. If the file has a mistake, the mod runs on default settings and leaves the file alone, and a message in the game (and `logs/OpenVersus.log`) says which line and column to fix. If you still have an `OpenVersus.ini` from an older version, the first launch converts it to `OpenVersus.toml`, keeping your settings and comments, and deletes the ini. If an `OpenVersus.toml` is already there (say, after extracting a new release over an old install), the ini's settings are merged into it wherever it still has the default value, and the ini is deleted. Versions before the 2026.04.08.14 prerelease won't start without a settings file; since then, a missing one is created with default values.

---

### Info for nerds

The OpenVersus mod client is a C#/.NET 10 NativeAOT MultiVersus mod. Its native build environment
is a Linux machine using [xwin](https://github.com/Jake-Shadle/xwin) and `lld` to produce a native
Windows PE binary, but it should have no issue building on a Windows host. It is a port of the C++
client at the repository root: an `.asi` loaded by Ultimate ASI Loader. Its files are TOML:
a player's `OpenVersus.ini` becomes `OpenVersus.toml` on the first launch, `OVSState.ini` becomes
`OVSState.toml`, and `PatternsCache.cache` is replaced by `PatternsCache.toml` (see
[Settings](#settings-1)).

**Status (2026-09-25).** Verified in the game on Windows, Linux (Proton) and Steam Deck, including
matches with netstats on and the post-match freeze patch. Never tested on macOS.

## Layout

- `OpenVersus/`: the `.asi` itself, which is only the `InitializeASI` export (`Plugin.cs`).
- `OpenVersus.Core/`: everything else, so the tests and the Wine harness can share it.
  - `Client.cs`: startup, in the C++ client's order: settings, console, hooks, background work.
  - `Config/`: the TOML files (`TomlConfig`: `Settings`, `State`, `PatternCache`), the conversion
    from the old settings ini (`SettingsMigration`), and the reader for the old ini files (`IniFile`).
  - `Game/`: the game's structures and offsets (`Mvs`, `UE`), the function registry
    (`GameFunctions`), the object array and finder (`Objects`), reflection, dialogs and toasts
    (`GameUi`), the game-thread queue (`GameThread`) and the engine-ready gate (`Engine`).
  - `Hooks/`: one class per patch or hook; `Hooking/HookGuard` keeps exceptions out of the game.
  - `Memory/`: byte patterns, call-site redirects, trampolines, code writes, guarded reads.
  - `Net/`: HTTP, the notification poller, auto-update, JSON.
  - `Identity/`: the Steam/Epic identity and hardware fingerprint sent to the server.
  - `NetStats/`: the per-match rollback statistics log.
  - `Native/`: P/Invoke declarations, with Windows SDK names.
- `OpenVersus.HookTest/`: a plugin for the Wine harness that proves the hooking layer on a small
  host, with no game involved.
- `OpenVersus.Tests/`: xunit tests that run on the Linux host, including the object finder against
  a synthetic game (`FakeGame.cs`).
- `wine-host/`: the harness. `run.sh` cross-compiles `host.c`, publishes the test plugin and runs
  both under Wine in a private prefix.

## Build

Requires the .NET 10 SDK. Two scripts wrap the commands below and check the prerequisites:

```sh
dotnet/build.sh                # Linux/macOS (maybe?): build, test, publish OpenVersus_<version>.asi; --help for options
dotnet\build.ps1               # Windows (PowerShell; build.cmd runs it from cmd or a double-click)
```

| `build.sh` | `build.ps1` | |
| --- | --- | --- |
| (no command) | (no command) | build, run the tests, publish |
| `test` | `test` | build and run the tests only; needs no Windows toolchain |
| `publish` | `publish` | publish the plugin only |
| `harness` | | run the Wine harness |
| `clean` | `clean` | remove every `bin/` and `obj/` directory |
| `--install DIR` | `-Install DIR` | copy the plugin into `DIR` (the game's `plugins` folder), renaming any `OpenVersus*.asi` already there to `.bak` |
| `--rwx` | `-Rwx` | read-write-execute trampolines (see below) |
| `--skip-tests` | `-SkipTests` | skip the tests in the default command |
| `--accept-license` | | accept the Visual Studio Build Tools license for the Windows SDK sysroot |

On Windows the publish needs the "Desktop development with C++" workload of Visual Studio or its
Build Tools. On Linux it needs `lld-link` (package `lld`) and
[`xwin`](https://github.com/Jake-Shadle/xwin) on `PATH`. The first publish downloads the Windows
SDK sysroot (about 2.4 GB) into `~/.cache/xwin`, which needs its license accepted: use
`--accept-license`, `ACCEPT_VS_BUILD_TOOLS_LICENSE=true`, or answer `yes` when the script asks. To
reuse a sysroot you already have (xwin's own default is `.xwin-cache`), set `XWinCache` to that
directory, with a trailing slash.

By hand:

```sh
dotnet build dotnet/OpenVersus.slnx          # everything, for the host (tests, no AOT)
dotnet test  dotnet/OpenVersus.slnx
dotnet publish dotnet/OpenVersus/OpenVersus.csproj -c Release -r win-x64 -p:AcceptVSBuildToolsLicense=true
dotnet/wine-host/run.sh                      # the hooking layer, end to end under Wine
```

The published plugin is `dotnet/OpenVersus/bin/Release/net10.0/win-x64/publish/OpenVersus_<version>.asi`,
named from the `VERSION` file in the repo. [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader) loads every `.asi` in its current directory, and every `.asi` recursively from the `plugins` subdirectory, so a new version must
replace the old file, **not** sit beside it. The plugin imports only system DLLs (`KERNEL32`,
`ADVAPI32`, `bcrypt`, `ole32`) and the UCRT api-sets.

Trampoline pages are read-execute except while a stub is being written. `-p:RwxTrampolines=true`
on the publish (the scripts' `--rwx`) keeps them read-write-execute for their whole life, as the
C++ client did.

Release builds also write full XML documentation (`OpenVersus.Core.xml`, `OpenVersus.xml`). A public
member without a doc comment is a compile-time warning (CS1591), but not a build error.

## CI and releases

`.github/workflows/build.yml` runs on every branch push and pull request, and can be started by
hand. It runs `build.sh test` and `dotnet format --verify-no-changes`, then publishes the plugin
on a GitHub-hosted Ubuntu runner with the same toolchain as above (`lld` from apt, `xwin` pinned by
version and checksum, the SDK sysroot cached between runs). Its artifact is the install exactly
as it ships:

```
xinput1_3.dll                      Ultimate ASI Loader, pinned by digest
LICENSE_Ultimate_ASI_Loader.txt
plugins/OpenVersus_<version>.asi
plugins/OpenVersus.toml            from dotnet/sample.toml
plugins/LICENSE.txt
plugins/VERSION.txt
```

The loader's `xinput1_3` build is only published under upstream's moving `x64-latest` release, so
when upstream replaces it the build fails with a message naming `LOADER_SHA256`: check the new
loader, then update the digest (and `LOADER_LICENSE_URL`'s tag) in `build.yml`.

`.github/workflows/release.yml` publishes a release when a tag is pushed. It builds nothing: it
waits for the successful `build` run of the tagged commit and publishes that run's artifact, so
what ships is what was built and tested. To release:

1. Bump `VERSION` and push.
2. Tag that commit with the bare version and push the tag: `git tag 2026.09.25.01 && git push
   origin 2026.09.25.01`. There's no need to wait for the build; the release waits for it.

The tag must equal `VERSION` (a `v` prefix does not match). A suffix, as in `2026.09.25.01-test1`,
publishes a prerelease; remove a test release with `gh release delete <tag> --cleanup-tag`. The
release holds `OpenVersus_v<tag>.zip` (the install), `OpenVersus_<version>.asi` (the plugin alone,
which is what the server's `download_url` should point the auto-updater at), a `.sha256` beside
each, and `SHA256SUMS`.

## How it maps to the C++ client

| C++ | Here |
| --- | --- |
| `DllMain` → `OnInitializeHook` | `Plugin.InitializeASI` → `Client.Initialize` (NativeAOT cannot run managed code in `DllMain`; the loader calls the export right after `LoadLibrary`) |
| `PatternFinder`, `CachedPatternsMgr` | `PatternResolver`, `PatternCache`; the pattern parser has the C++ semantics (`?` is one byte) and a cached address is checked before use |
| `MakeProxyFromOpCode`, `InjectHook`, `Trampoline` | `CallSite.Redirect`, `CallSite.Inject`, `Trampoline` |
| `OVS::Hooks::*` | `Hooks/*`. The sig-check, sunset-date and post-match-freeze changes are byte patches (the sunset check runs thousands of times a minute); the endpoint and game-instance hooks are call redirects into `[UnmanagedCallersOnly]` methods guarded by `HookGuard`; the startup dialog and toast are a game-thread job |
| `MVSGame::*` function globals | `GameFunctions`: every resolved function by name, with where it came from and its signature; reflected functions through `Reflection.Find` |
| `NotificationPoller` heap scans | `ObjectFinder` over the engine's object array, falling back to the heap scan |
| `__try`/`__except` reads | `CodeWriter.TryRead` (a guarded read; NativeAOT cannot catch access violations) |
| WinInet / WinHTTP / raw socket | `IHttpTransport`, implemented on WinHTTP |

## Settings

The settings are `OpenVersus.toml`, read and written by `Config/TomlConfig.cs` through Tomlyn's
syntax tree (its serializer is not used: it drops comments, and its reflection path is not
NativeAOT-safe). Table and key names match regardless of case, as they did in the ini. Writing
only ever adds a missing key, directly under the last key of its table in that table's
`Key = Value` or `Key=Value` style, or a missing table at the end; comments, blank lines, spacing,
unknown keys, line endings and a byte-order mark stay as the player left them, and a complete file
is not rewritten at all. A boolean row takes a TOML boolean, or `on`/`off`, `1`/`0` or `"true"` in
any case. A value that does not parse is logged as a warning, read as its default and left on
disk. A file that is not valid TOML is logged with the line and column where it broke, every
setting takes its default, and the file is left alone. When the settings cannot be used (a TOML
mistake, an unreadable file, an ini that would not convert), a toast after "OpenVersus Loaded" says
so. A missing `OpenVersus.toml` is written from `Settings.Table` in its order, with CRLF line
endings, and `[Settings.Debug]` just above the servers. `sample.toml` is a checked-in copy of that
file: the plugin never writes it, and a test holds the two to the same bytes, so after changing a
row, regenerate it by copying the `OpenVersus.toml` the plugin writes into an empty folder.

A legacy `OpenVersus.ini` with no `OpenVersus.toml` beside it is converted once
(`Config/SettingsMigration.cs`), line for line: `;` comments become `#`, names take the spelling
of the row they match, values a boolean row reads as a boolean become TOML booleans, and
everything else becomes a string, deprecated and invalid values included. The one exception is both
`ServerUrl` keys, which become `https://prod.openversus.org/` whatever they held, since old files
can still point at the plain-http endpoint that only stays up for them; the log records the old
value. A repeated section or key, or a line that is neither, was never read by the ini reader, and
becomes a comment. The result is checked against the ini reader, key by key, before anything is
written. If it checks out, the TOML is written and the ini deleted; if it does not, the log keeps
the ini's text, the TOML starts from defaults, and the ini is still deleted. The ini is deleted only
once the TOML is on disk. When both files exist (a new release, which ships `OpenVersus.toml`,
extracted over an install that never converted), the ini is merged into the TOML
(`SettingsMigration.Merge`): a row's value is taken only where the TOML is missing it or still has
the default, so nothing changed in the TOML is overwritten; any other key only where the TOML
lacks it; `ServerUrl` never. Each value taken is logged, with a warning that sums them up, and the
ini is deleted once the TOML is saved. A TOML that does not parse takes nothing, and the ini stays
until it does.

The client's own files are TOML too. `OVSState.toml` holds `[FirstRun] PaidModWarned`; an
`OVSState.ini` is carried over once (the agreement, so the free-mod dialog is not shown again) and
deleted once that is on disk. `PatternsCache.toml` names the exe (`Exe`, the top 32 bits of the
.text hash) and client version (`Client`) it is for at its top, with pattern text to RVA under
`[Patterns]`; a file for another exe or client, or one that does not parse, starts over. The old
`PatternsCache.cache` is deleted: it was only a cache. `Config/IniFile.cs` remains only to read the
old ini files for these conversions.

What each key does is documented on its row in `Config/Settings.cs` (`Settings.Rows`). Compared
with the C++ client:

- Added: `[Patches] PostMatchFreeze`, on by default, with its pattern under `[Patterns.MVS]`; and,
  off by default, `[Patches] SunsetCallers`, `[Settings.Debug] CountSunsetCalls` and
  `[Features] NetStats`.
- `[Settings] LogLevel` takes a level name as well as a number (see [Logs](#logs)).
- No longer read: `LogSize`, `ModLoader`, `AntiCheatEngine`, `CurlSetOpt` and `CurlPerform` under
  `[Settings]` (`Settings.RetiredKeys`). They stay in files that have them, each logged at startup
  with why it is unused; nothing adds them to files that don't. Any other key the client does not
  know is logged as a warning.
- `[Settings.Keybinds] ToggleMenu` is kept so existing files mean what they did, but nothing reads
  it; the keyboard hook only ever logged F1.

### Sunset check switches

The game's sunset-date check is called thousands of times a minute. `[Patches] SunsetDate`
(default on) makes the function itself return false with two byte patches. Two more switches,
both off by default:

- `[Patches] SunsetCallers=true` finds every direct call and tail jump to the function through
  `.pdata` and a `.text` scan, and turns each into "return false" in place, so the function is
  never entered. The log reports the count found (146 in the final build).
- `[Settings.Debug] CountSunsetCalls=true` routes the function's comparison path through a
  counter, and the heartbeat line each minute reports how many calls the last minute saw. With
  both switches on, that number must be zero.

## Logs

`logs/OpenVersus.log` next to the plugin is the running log, truncated at each launch; the
previous run's is kept as `logs/OpenVersus_<launch time>.log`. Archives from the last week stay as
plain text; older ones are compressed to `.zst` (zstd level 11, through `ZstdSharp`) on a
background thread at launch, keeping their timestamps. With `[Settings.Debug] ShowConsole` on,
every line also goes to the debug console.

If the `logs` directory cannot be created or written, the log goes to the plugin's own directory
instead; if that fails too, the client still runs, `Log.FileError` records why, and every line
still reaches the console. `Log.Notice` says which happened and is the first warning logged.

### Levels

`Log` is a `Microsoft.Extensions.Logging.ILogger` with its own writer (file plus console). The
minimum level comes from `[Settings] LogLevel`: a name (`trace`, `debug`, `info`, `warn`, `error`,
`critical`, `none`, or the usual aliases such as `verbose`, `all`, `err`, `quiet`) or a number
(1 debug to 6 none; a number past 6 means 6). `0`, which existing files carry, means "decide from
`[Settings.Debug] DebugLogging`": Debug when it is on, Information when it is off. A name that is
not a level is logged and falls back the same way. Tags in the file are `TRC DBG NFO WRN ERR CRT`.
Per-attempt and game-thread queue lines are Trace; pattern and function resolution is Debug; hooks,
banners and netstats are Information.

### Netstats

With `[Features] NetStats=true` the per-second `NETSTATS` lines go to `logs/NetStats.log`, one file
per match: it opens when a session starts playing and closes at the summary, which archives it as
`NetStats_<match start time>_<match id>[_with_<teammates>]_vs_<opponents>.log` with the same
retention as the main log. The names come from the player data each fighter pawn carries and the
id from the gameplay-config subsystem (both confirmed on the live game), read at the first playing
sample and, while incomplete, again once a second for ten seconds. A log that never got a
description keeps the plain stamp, and at Debug level the main log lists what each reading saw
(every pointer followed, checked for being an object). A closed match log stays on disk as
`NetStats.log` and is not archived a second time by the next open.

The main log gets only the session start and end, the match log open and close, and the
`NETSTATS-SUMMARY` line; anything that used to grep the main log for `NETSTATS` reads the match
logs now. Between matches the session is looked up through the object array every five seconds,
and every sample asks the array whether the session object is still listed, so a freed session is
never sampled.

## Conventions

- The version is the `VERSION` file at the repository root and nothing else: the build generates
  `OvsVersion.Current` from it, stamps the assembly with it and names the plugin for it, and
  `release.yml` refuses a tag that differs from it.
- Formatting is `dotnet/.editorconfig`, applied with `dotnet format`: four spaces, Allman braces,
  one statement per line. Braces on every control-flow body (IDE0011) is an error in the build;
  a one-line auto-property or single-expression `=>` is fine.
- Every public member has an XML doc comment. Offsets and addresses say where they came from (a
  dump, a live read, a disassembly), since a dump's layout is a claim about this build, not a fact.
- Everything takes `ILogger`; the short verbs (`Info`, `Warn`, `Success`, ...) are extension
  methods in `LogExtensions.cs` that pass the text through verbatim, never as a template. Only
  `Client` and the plugin entry point hold the concrete `Log`, since they own its lifetime.
- A patch that cannot be made throws `PatchException` out of `Memory/` (`CodeWriter`, `CallSite`,
  `Trampoline`); `Client.ApplyHooks` catches per hook, logs it as that hook's failure and goes on.
  `CodeWriter.TryRead` never throws, since it runs on hot paths.
- Settings are rows (`Settings.Rows.*`), one static `SettingDef` per ini key, read through typed
  properties; `PatternResolver.Find("SigCheck")` reads the pattern text from the row of that key.
- JSON goes through the source-generated `Net/OvsJson.cs` context (NativeAOT has no reflection
  for System.Text.Json); add a `[JsonSerializable]` there for any new shape.
- Background loops stop through a `CancellationToken`, waited on rather than slept through.
- Nothing calls into the engine before `Engine.IsUp` (the game instance has existed for three
  seconds): every entry point in `UE` and `GameUi` refuses with an exception until then, and
  background work waits with `Engine.WaitUntilUp`. Calling the engine during UE initialization
  crashes the game with no log line to show for it.
- Reads of the game's memory go through `IMemory` (`ProcessMemory` in the plugin, a byte-backed
  fake in the tests) and engine name lookups through `IGameNames`, so the object array, the
  finder, and the reflection decoder can be tested on Linux against a synthetic game
  (`OpenVersus.Tests/FakeGame.cs`). Those tests prove the logic; the offsets they are built from
  are the code's own, and only the running game checks those.
- What stays C-shaped is what must: `[UnmanagedCallersOnly]` hooks and the static state they
  need, function-pointer casts, sequential-layout structs mirroring the game, `nint` arithmetic,
  and Win32 names in `Native/`.

## Testing against the game

`build.sh --install <game>/plugins` (or `-Install` on Windows) publishes and installs the plugin,
keeping the previous one as a `.bak`. With `DebugLogging=true` or `LogLevel=debug`,
`logs/OpenVersus.log` lists every pattern, the address of every function it resolved, and the
hooks that took; when something fails to resolve, try comparing those addresses with the C++ build's
console output.

Set `AutoUpdate=false` while testing. The update check works against https (the C++ one never
could) and installs whatever the server offers when it is newer than `VERSION`, which would replace
the build under test.

The Wine harness (`build.sh harness` or `wine-host/run.sh`) checks the hooking layer without the
game: the host calls three assembly sites before and after loading the test plugin, which
redirects a call and a jmp into managed code, patches a byte, and makes one hook throw to prove the
guard returns the fallback. It needs wine and mingw-w64 besides the publish toolchain, and uses its
own prefix under `local/wine-host`.

---

OpenVersus is not affiliated with, nor endorsed by, WB, PFG, its developers, or any other related entity.
OpenVersus is not affiliated with, nor endorsed by, any other modding project or effort, the developers of those projects, or any other related entity.
This mod is provided on an as-is, sans-warranty basis as detailed in the `LICENSE` file. Use at your own risk and discretion.
