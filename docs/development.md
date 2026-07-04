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
| `src\FieldDiff.cs` | field-level diff for non-shop same-object collisions |
| `src\Patcher.cs` | merge plans + generating/maintaining the `OstrasortPatch` mod |
| `src\Engine.cs` | the shared analyze pass + hygiene checks (image, BepInEx) |
| `src\LoadOrderFile.cs` | guarded `loading_order.json` read/write ritual |
| `src\Report.cs` | console report rendering |
| `src\TextReport.cs` | plain-text report for clipboard/file export |
| `src\Program.cs` | CLI parsing + GUI/console routing |
| `src\gui\` | WPF main window, conflict resolver, persisted settings |

`--smoke-gui` (hidden flag) constructs the WPF windows without showing them —
used for headless verification. `--smoke-undo` exercises the snapshot
undo/redo against a fixture install.

## Testing against a fixture

The analysis and patch paths can be exercised against a fake install with
`--game <path>`: a directory containing `Ostranauts_Data\StreamingAssets\data`
(core), `Ostranauts_Data\Mods\` with mod folders and a `loading_order.json`,
and a `globalgamemanagers` file with a version string. This isolates tests
from the real install (and `strPathMods` is ignored when `--game` is passed,
so fixtures stay self-contained).

## Releasing

The repo is public. To cut a release: tag the version, run `publish.ps1`, and
attach `publish\Ostrasort.exe` as the GitHub Release asset. The in-app update
check reads the GitHub releases API and shows a header link when a newer tag
exists.

## Roadmap

- Machine-readable (`--json`) report output.
- NativeAOT packaging to shrink the exe (WPF isn't AOT-compatible, so this
  would likely pair with a console-only build).
