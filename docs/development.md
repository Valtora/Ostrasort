# Building & developing Ostrasort

Ostrasort is a .NET 10 WPF application (Windows only). It targets
`net10.0-windows` with `UseWPF`.

## Build & run

```powershell
dotnet build -c Release        # dev build -> bin\Release\Ostrasort.exe (needs .NET 10 SDK)
.\publish.ps1                  # release artifact -> publish\Ostrasort.exe (self-contained single file)
```

`bin\Release\Ostrasort.exe` is framework-dependent — fine for development,
but **not** the thing you ship.

## Publishing (read this before releasing)

`publish.ps1` produces the single self-contained `publish\Ostrasort.exe` end
users run. Two things it must get right, both learned the hard way:

- **`IncludeNativeLibrariesForSelfExtract=true` is required.** WPF's native
  DLLs cannot be loaded from a bundled single-file exe; without this flag the
  app dies instantly on launch with a `DllNotFoundException` deep in
  `HwndSubclass`. The flag makes native libs extract to a temp folder on first
  run.
- **The published exe is smoke-tested, not just `bin\Release`.** `publish.ps1`
  runs `Ostrasort.exe --smoke-gui` against the *published* artifact and
  refuses to emit a build that fails it. A `bin\Release` GUI smoke test passes
  even when the single-file build is broken (native DLLs sit next to it
  there), so it proves nothing about the artifact you ship — always test the
  published exe.

Operational gotchas:

- **Close the running app before republishing** — it locks its own
  `publish\Ostrasort.exe`, and the build fails at the publish step.
- `publish.ps1` throws on failure. If you chain it with git commands
  (`publish.ps1; git commit && git push`), a publish failure aborts the whole
  line — the commit never happens. Run publishing as its own step and verify.

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
| `src\LoadOrderFile.cs` | guarded `loading_order.json` read/write ritual |
| `src\Report.cs` / `src\TextReport.cs` | console report / plain-text export |
| `src\Program.cs` | CLI parsing + GUI/console routing |
| `src\gui\` | WPF main window (incl. the FFU/autoloader banner), conflict resolver, persisted settings |

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
and autoloader detection:

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

The repo is public. To cut a release: tag the version, run `publish.ps1`, and
attach `publish\Ostrasort.exe` as the GitHub Release asset. The in-app update
check reads the GitHub releases API and shows a header link when a newer tag
exists.

## Roadmap

- Merge objects with no base-game version (two mods adding the same new
  object) — currently reported but not auto-merged, as there is no common
  ancestor for a 3-way merge.
- Machine-readable (`--json`) report output.
- NativeAOT packaging to shrink the exe (WPF isn't AOT-compatible, so this
  would likely pair with a console-only build).
