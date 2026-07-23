# Building & developing Ostrasort

Ostrasort is a .NET 10 WPF application (Windows only). It targets
`net10.0-windows` with `UseWPF`.

## Build & run

```powershell
dotnet build -c Release        # dev build -> bin\Release\Ostrasort.exe (needs .NET 10 SDK)
.\publish.ps1                  # release artifacts -> publish\releases\ (Velopack installer + portable)
```

`bin\Release\Ostrasort.exe` is framework-dependent — fine for development,
but **not** the thing you ship. A dev build is not a Velopack install, so it
never offers self-updates (by design).

## Install & update: Velopack

Install, shortcuts, and auto-update are handled by
[Velopack](https://velopack.io) (`Velopack` NuGet package + the `vpk` CLI):

- `Program.Main` calls `VelopackApp.Build().Run()` as its **first** statement.
  This handles Velopack's install/update/uninstall hooks and returns for a
  normal launch. It must stay first, and it no-ops on ordinary args, so the
  headless/CLI paths are unaffected.
- `src\gui\VeloUpdate.cs` wraps `UpdateManager` against a `GithubSource` for
  `Valtora/Ostrasort`. On launch (and from **Check for updates**) it checks,
  downloads a newer release in the background, and on the user's click applies
  it and restarts. `Create()` returns null for a copy Velopack doesn't manage
  (a dev build), so the affordance simply never appears there.
- The installed app lives in `%LOCALAPPDATA%\Ostrasort` (Velopack replaces the
  `current\` folder wholesale on each update). User data is kept **out** of
  there, in `%APPDATA%\Ostrasort` — see `src\AppPaths.cs`.

## Publishing (read this before releasing)

`publish.ps1` produces the release artifacts in `publish\releases\`. `vpk pack`
emits a few files there, but **a release ships only three of them**:
`Ostrasort-v<ver>-Setup.exe` (the installer, renamed to carry its version),
`Ostrasort-<ver>-full.nupkg` (the update payload), and `releases.win.json` (the
manifest the in-app updater reads). Those last two are **required** for
self-update: the updater reads `releases.win.json` off the latest release, then
downloads the `full.nupkg` it names. The other files `vpk` leaves in the folder
(`Ostrasort-win-Portable.zip`, the legacy Squirrel `RELEASES`, `assets.win.json`)
are **not** uploaded — the portable zip is not offered, and Velopack's own updater
does not read `RELEASES`. `publish.ps1`:

1. `dotnet publish`es a **plain self-contained** build to `publish\raw` (not
   single-file — Velopack does its own bundling, and a normal layout keeps WPF's
   native DLLs beside the exe, so the old `IncludeNativeLibrariesForSelfExtract`
   single-file dance is gone).
2. **Smoke-tests the published exe** (`publish\raw\Ostrasort.exe --smoke-gui`)
   and refuses to pack a build that fails it. Always test the published exe, not
   `bin\Release` — a `bin\Release` smoke pass proves nothing about the shipped
   layout.
3. `vpk pack`s `publish\raw` into `publish\releases`, then renames the installer
   to `Ostrasort-v<ver>-Setup.exe`.

It needs the `vpk` CLI once: `dotnet tool install -g vpk`.

Operational gotchas:

- **Close the running app before republishing** — it locks its own exe, and the
  build fails at the publish step.
- `publish.ps1` throws on failure. If you chain it with git commands, a publish
  failure aborts the whole line — the commit never happens. Run publishing as
  its own step and verify.

## Code layout

| File | Responsibility |
|---|---|
| `src\GameEnv.cs` | install discovery (Steam registry → `libraryfolders.vdf`), game version |
| `src\Mods.cs` | mod model + the scanner that classifies and indexes each mod |
| `src\Analysis.cs` | collision detection, sorting rules, manual-order validation |
| `src\FieldDiff.cs` | non-loot collision analysis (drives mergeability via `ObjectMerge`) |
| `src\ObjectMerge.cs` | the 3-way field-merge engine for non-loot objects |
| `src\SchemaValidator.cs` | compact draft-07-subset validator for the game's schema files |
| `src\Patcher.cs` | merge plans (loot + objects) + generating/maintaining `OstrasortPatch` |
| `src\CollisionView.cs` | the grouped, humanized collision rendering (GUI + console share it) |
| `src\Engine.cs` | the shared analyze pass + hygiene checks (image, BepInEx, autoloader) |
| `src\Ffu.cs` | FFU support: `Autoload.Meta.toml` parser, install detection (`FfuContext`), FFU classification + hygiene (`FfuAnalysis`) |
| `src\LoadOrderFile.cs` | guarded `loading_order.json` read/write ritual, incl. the per-file cross-process write lock (GUI + headless share the choke point) |
| `src\Report.cs` / `src\TextReport.cs` | console report / plain-text export |
| `src\Program.cs` | CLI parsing + GUI/console routing |
| `src\gui\` | WPF main window (incl. the FFU/autoloader banner), conflict resolver, persisted settings |
| `src\AppPaths.cs` | the roaming data root (`%APPDATA%\Ostrasort`) + one-time migration from the old `%LOCALAPPDATA%\Ostrasort` location |
| `src\gui\VeloUpdate.cs` | Velopack update wrapper: check + background download + apply-and-restart (null for an unmanaged/dev copy) |
| `src\gui\LegacyInstall.cs` | one-time cleanup of the pre-0.23 self-install (`%LOCALAPPDATA%\Programs\Ostrasort`) and its stale shortcuts |
| `src\gui\SingleInstance.cs` | one instance per session (named mutex + activate signal event); a second launch focuses the first window |

`--smoke-gui` (hidden flag) constructs the WPF windows without showing them —
used for headless verification. `--smoke-undo` exercises the snapshot
undo/redo against a fixture install.

**Console model.** The project is a **WinExe** (GUI subsystem), so a
double-click opens the window with no console flashing up behind it. The
console paths (`--report`, `--headless`, `--version`, `--help`, error text)
still print: `Program.Main` calls `AttachConsole(ATTACH_PARENT_PROCESS)` when
launched from a terminal and reopens `Console.Out/Error` onto it. It skips the
attach when stdout is already redirected to a pipe/file (automation, shell
redirection, the smoke tests), so captured output is never stolen. A pure
double-click has no parent console, so the attach simply fails and the app
stays silent — exactly what a GUI wants.

## Tests

Unit tests live in `tests\Ostrasort.Tests.csproj` (xUnit) and cover the pure
logic — the field-merge engine (including FFU fragment semantics), the schema
validator, the guarded `loading_order.json` read/write, the sort rules
(including the FFU block), `Autoload.Meta.toml` parsing, FFU classification,
autoloader detection, the roaming-data migration (`AppPathsTests`, including the
partial-destination merge that must not strand data), and the update guard
(`UpdaterTests`: `VeloUpdate.Create` declines for an unmanaged copy). The
Velopack check/download/apply steps are integration-level and verified
manually:

```powershell
dotnet test tests\Ostrasort.Tests.csproj
```

The test project references the app project; the app project excludes
`tests\**` from its own compilation. GUI code isn't unit-tested directly, but
`--smoke-gui` constructs the real windows and **asserts the resolver renders
selectors** for a contested plan (a regression that shipped once), and
`--smoke-undo` exercises the snapshot undo/redo.

## Testing against a fixture

The analysis and patch paths can be exercised against a fake install with
`--game <path>`: a directory containing `Ostranauts_Data\StreamingAssets\data`
(core, including a `schemas\` folder to exercise schema validation),
`Ostranauts_Data\Mods\` with mod folders and a `loading_order.json`, and a
`globalgamemanagers` file with a version string. This isolates tests from the
real install (and `strPathMods` is ignored when `--game` is passed, so
fixtures stay self-contained).

## Releasing

The repo is public. To cut a release:

1. Bump `<Version>` in `Ostrasort.csproj` (this is the single source of truth —
   it becomes the packed version and what the in-app updater compares).
2. Run `.\publish.ps1` (with the app closed) to build and validate the artifacts
   in `publish\releases\`.
3. Create the GitHub release and upload **exactly the three shipped assets**: the
   versioned installer, the update package, and the manifest. `releases.win.json`
   and the `full.nupkg` are what make self-update work — do not omit either.

   ```powershell
   gh release create vX.Y.Z --title vX.Y.Z --notes-file notes.md `
       publish\releases\Ostrasort-vX.Y.Z-Setup.exe `
       publish\releases\Ostrasort-X.Y.Z-full.nupkg `
       publish\releases\releases.win.json
   ```

   The release title is the bare version (`vX.Y.Z`). Draft the notes from
   `git log <last-tag>..HEAD`. Do **not** upload the portable zip or the legacy
   `RELEASES` file — the release ships the installer only, and Velopack's updater
   reads `releases.win.json`, not `RELEASES`. (`vpk upload github` is the Velopack
   uploader, but it publishes the portable zip and `RELEASES` as well and names
   the installer `Ostrasort-win-Setup.exe`, so the plain `gh` upload above is used
   instead to ship the trimmed, version-named set.)

Installed copies pick the update up on their next launch: `UpdateManager` reads
`releases.win.json`, compares its own version against the newest release, and
downloads the `full.nupkg` it names. A release whose `<Version>` didn't move will
not be offered.
