# Ostrasort — load-order analyzer & conflict patcher for Ostranauts

A LOOT-style (Skyrim's Load Order Optimisation Tool) console tool. Run it
before launching the game: it scans **core + every local and
Workshop-subscribed mod**, understands what each one actually contains, finds
data collisions, suggests a `loading_order.json` that satisfies the rules —
and for the conflicts **no load order can fix**, it can generate a merged
patch mod. Read-only unless you pass a write flag.

## For players

Download `Ostrasort.exe` (single file, ~36 MB, no .NET install needed) and
either **double-click it** (the window stays open so you can read the report)
or run it from a terminal. It finds your Ostranauts install by itself via the
Steam registry and `libraryfolders.vdf`, whatever drive it's on.

```
Ostrasort.exe             analyze and report; writes nothing
Ostrasort.exe --apply     write the suggested load order (keeps loading_order.json.bak)
Ostrasort.exe --patch     generate/refresh the "Ostrasort Patch" mod (see below)
Ostrasort.exe --unpatch   remove the generated patch mod again
Ostrasort.exe --game <p>  point at a non-Steam / unusual install manually
Ostrasort.exe --no-pause  for scripts: never wait for a key press
```

Exit codes: `0` = nothing left to do, `2` = actionable suggestions remain,
`1` = error.

## Why load order matters in Ostranauts

The game loads every folder in `aLoadOrder` in sequence, and a later object
with the same `strName` and data type **replaces the earlier one whole** — no
merging. Shop inventories are single objects (e.g. the furnishings kiosk is
one `loot` object), so when two mods stock the same kiosk, whichever loads
last silently deletes the other's wares.

Membership matters too: the in-game MODS screen only lists entries present in
`aLoadOrder` (an unregistered folder under `Mods\` is invisible), and the
BepInEx Workshop bridge reads `loading_order.json` to locate subscribed code
mods' plugins.

## What it checks

Every mod is classified from its contents:

| Class | Detected by | Ordering rule |
|---|---|---|
| `infrastructure` | ships `BepInEx\patchers\` (the Mod Loader) | pinned immediately after `core` |
| `code` | plugins/DLLs only, no data objects | position irrelevant — left alone |
| `shell` | metadata only | position irrelevant — left alone |
| `dataadditive` | adds new objects only | safe anywhere after `core` — left alone |
| `dataoverride` | replaces core objects | left alone unless it collides |
| `patch` | the folder Ostrasort itself generates | always last |

Then it cross-references every `(data type, strName)` claim. For `loot` pools
the comparison is at item granularity (the token before `=` in each
`"Item=weight x count"` entry):

- later pool ⊇ earlier → **order correct** ✔
- later pool ⊂ earlier → **wrong order**: the superset is moved to load after
  the subset
- partial overlap → **conflict**: no order fixes it → `--patch`
- identical item sets → note (only quantities differ; last loaded wins)

**Hygiene** in the same report: dead `aLoadOrder` paths (unsubscribed items),
local mod folders missing from `aLoadOrder`, subscribed Workshop items not
yet registered, duplicate entries, `strGameVersion` lagging the installed
game (read from `Ostranauts_Data\globalgamemanagers`), and mod data files
that only parse leniently (trailing commas — the game's loader ERRORs on
those).

**Minimal churn**: the suggestion starts from the current order and applies
only the moves a rule demands.

## The Ostrasort Patch (`--patch`)

When two mods both override the same shop pool and neither covers the other,
someone's wares vanish no matter the order. `--patch` writes a
`Mods\OstrasortPatch` mod containing the **per-item union** of each
conflicting pool and registers it last:

- Items only one mod stocks are all kept.
- Items both stock: the **later-loaded mod's quantity wins** (provisional
  rule — the same last-wins the game applies to whole objects, at item
  grain).
- The folder is wholly owned by Ostrasort: a marker file
  (`ostrasort_patch.json`) records exactly which pools it merged and their
  content hashes. Never edit it by hand; it is safe to delete.
- When a merged mod later updates, the analyzer flags the patch **STALE** —
  re-run `--patch`. When the underlying conflict disappears, it suggests
  `--unpatch`.

## Safety

- Analysis never writes anything and is safe while the game runs.
- `--apply`, `--patch`, and `--unpatch` refuse while Ostranauts is running,
  save the previous `loading_order.json` as `.bak` before writing it, keep
  the file a **top-level JSON array** (the game silently drops all local mods
  and regenerates the file otherwise), and strict-re-parse their own output
  before writing. Workshop entries keep their absolute-path form; local
  entries keep their `|edit` markers.

## For developers

```powershell
dotnet build -c Release        # dev build -> bin\Release\Ostrasort.exe (needs .NET 10 SDK)
.\publish.ps1                  # release artifact -> publish\Ostrasort.exe (self-contained single file)
```

Layout: `src\GameEnv.cs` (install discovery), `src\Mods.cs` (model + scanner),
`src\Analysis.cs` (collisions + sorting rules), `src\Patcher.cs` (the patch
mod), `src\LoadOrderFile.cs` (guarded loading_order.json IO), `src\Report.cs`,
`src\Program.cs`.

Releasing (repo owner): flip the repo public when ready, tag, and attach
`publish\Ostrasort.exe` as a GitHub Release asset.

## Roadmap

- Settle the shared-item quantity rule for `--patch` (later-wins is
  provisional).
- Machine-readable (`--json`) report output.
- NativeAOT packaging (~4–8 MB, faster start) once the SDK/vswhere hiccup is
  sorted.
