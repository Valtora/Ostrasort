# Ostrasort — load-order manager for Ostranauts

Ostrasort scans **core + every local and Workshop-subscribed mod** in your
Ostranauts install, understands what each one actually contains, finds data
collisions, suggests a `loading_order.json` that satisfies the rules — and
for the conflicts **no load order can fix**, generates a merged patch mod
with **you deciding the contested items** in a visual resolver. Run it before
launching the game.

## For players

Download `Ostrasort.exe` (single file, ~58 MB, nothing to install) and
**double-click it** — the app window opens. It finds your Ostranauts install
by itself via the Steam registry and `libraryfolders.vdf`, whatever drive
it's on.

The window shows every mod in load order with its class and any problems,
plus four detail tabs: **Collisions** (who claims the same objects and
whether the order handles it), **Order changes** (what a sort would do and
why, applied with one button), **Patch** (state of the generated patch mod),
and **Warnings** (dead entries, unregistered mods, version lag, broken JSON).
Nothing is written until you press a button, every `loading_order.json` write
keeps a `.bak`, and all writes are disabled while the game is running.

### The conflict resolver

When two mods stock the same shop pool and neither covers the other,
someone's wares vanish no matter the order. "Resolve conflicts & generate
patch" opens the resolver: every **contested item** (both mods stock it with
different values) is a row — pick which mod's entry wins, or use "Take all
from *<mod>*" for a whole column. Items only one mod stocks are always
carried over automatically. The result is written as a `Mods\OstrasortPatch`
mod that loads last.

- **Your decisions are remembered** (stored in the patch's marker file). When
  a merged mod updates, the patch is flagged **stale**; regenerating re-asks
  only the new or changed items.
- The folder is wholly owned by Ostrasort — never edit it by hand; remove it
  with the "Remove patch" button (or `--unpatch`).

### Command line (for scripts and automation)

```
Ostrasort.exe             open the GUI (same as double-clicking)
Ostrasort.exe --report    console analysis report; writes nothing (exit 2 = suggestions remain)
Ostrasort.exe --apply     write the suggested load order
Ostrasort.exe --patch     generate/refresh the patch; contested items open the
                          resolver window unless --no-gui
Ostrasort.exe --unpatch   remove the generated patch mod
Ostrasort.exe --no-gui    headless: contested items fall back to the later-loaded
                          mod's entry, marked for review in the GUI
Ostrasort.exe --game <p>  point at a non-standard install manually
Ostrasort.exe --no-pause  never wait for a key press
```

Console exit codes: `0` = nothing left to do, `2` = actionable suggestions
remain, `1` = error.

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
- partial overlap → **conflict**: no order fixes it → the resolver + patch
- identical item sets → note (only quantities differ; last loaded wins)

**Minimal churn**: the suggestion starts from the current order and applies
only the moves a rule demands.

## Safety

- Analysis never writes anything and is safe while the game runs.
- Every write path refuses while Ostranauts is running, saves the previous
  `loading_order.json` as `.bak`, keeps the file a **top-level JSON array**
  (the game silently drops all local mods and regenerates the file
  otherwise), and strict-re-parses its own output before writing. Workshop
  entries keep their absolute-path form; local entries keep their `|edit`
  markers.

## For developers

```powershell
dotnet build -c Release        # dev build -> bin\Release\Ostrasort.exe (needs .NET 10 SDK)
.\publish.ps1                  # release artifact -> publish\Ostrasort.exe (self-contained single file)
```

Layout: `src\GameEnv.cs` (install discovery), `src\Mods.cs` (model + scanner),
`src\Analysis.cs` (collisions + sorting rules), `src\Patcher.cs` (merge plans
+ the patch mod), `src\Engine.cs` (shared analyze pass), `src\LoadOrderFile.cs`
(guarded loading_order.json IO), `src\Report.cs` (console report),
`src\Program.cs` (CLI + GUI routing), `src\gui\` (WPF main window + resolver).

Releasing (repo owner): flip the repo public when ready, tag, and attach
`publish\Ostrasort.exe` as a GitHub Release asset.

## Roadmap

- Machine-readable (`--json`) report output.
- NativeAOT packaging once the SDK/vswhere hiccup is sorted (would shrink the
  exe considerably, though WPF is not AOT-compatible — likely paired with a
  console-only build).
