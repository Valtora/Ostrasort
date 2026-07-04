# Ostrasort — a load-order analyzer for Ostranauts

A LOOT-style (Skyrim's Load Order Optimisation Tool) console tool. Run it
before launching the game: it scans **core + every local and
Workshop-subscribed mod**, understands what each one actually contains, finds
data collisions, and suggests a `loading_order.json` that satisfies the rules
— explaining every change. Read-only by default.

```
ostrasort                 analyze and report; writes nothing
ostrasort --apply         write the suggested order (keeps loading_order.json.bak)
ostrasort --game <path>   non-default Ostranauts install folder
```

Exit codes: `0` = current order satisfies every rule (or `--apply` succeeded),
`2` = changes suggested (analysis mode), `1` = error.

## Why load order matters in Ostranauts

The game loads every folder in `aLoadOrder` in sequence, and a later object
with the same `strName` and data type **replaces the earlier one whole** — no
merging. Shop inventories are single objects (e.g. the furnishings kiosk is
one `loot` object, `ItmOKLGFurnishingsKioskInv`), so when two mods stock the
same kiosk, whichever loads last silently deletes the other's wares. The
community convention: shop mods ship a pool that is the **union** of vanilla +
the other known shop mods + their own items, and the union must load last.
Ostrasort verifies exactly that, at item granularity.

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

Then it cross-references every `(data type, strName)` claim:

- **Collisions** (two+ mods claim the same object): for `loot` pools the
  comparison is at item granularity (the token before `=` in each
  `"Item=weight x count"` entry) —
  - later pool ⊇ earlier → **order correct** ✔
  - later pool ⊂ earlier → **wrong order**: the superset is moved to load
    after the subset
  - partial overlap → **conflict**: no order fixes it; the report lists
    exactly which items each side drops so a union pool can be regenerated
  - identical item sets → note (only quantities differ; last loaded wins)
- **Hygiene**: dead `aLoadOrder` paths (unsubscribed items), local mod folders
  missing from `aLoadOrder` (invisible to the MODS screen), subscribed
  Workshop items not yet registered, duplicate entries, `strGameVersion`
  lagging the installed game (read from `Ostranauts_Data\globalgamemanagers`,
  the same string the main menu shows), and mod data files that only parse
  leniently (trailing commas — the game's loader ERRORs on those).

**Minimal churn**: the suggestion starts from the current order and applies
only the moves a rule demands. Mods that don't collide keep their positions.

## Safety

- Analysis never writes anything and is safe while the game runs.
- `--apply` refuses while Ostranauts is running, saves the previous file as
  `loading_order.json.bak`, keeps the file a **top-level JSON array** (the
  game silently drops all local mods and regenerates the file otherwise),
  and strict-re-parses its own output before writing. Workshop entries keep
  their absolute-path form; local entries keep their `|edit` markers.

## Build & run

```powershell
dotnet build -c Release        # -> bin\Release\Ostrasort.exe (.NET 10)
.\bin\Release\Ostrasort.exe
```

## Relationship to ModTools

The workspace's central deploy tool (`..\ModTools\deploy.ps1`) owns per-mod
correctness — build, validate, sync, register (always appending new entries
at the end). Ostrasort owns whole-list intelligence and is the only thing
that reorders. Deploy prints a reminder to run it.

## Roadmap

- Generate a "merged patch" mod for partial-overlap conflicts (union pool,
  loads last) instead of only reporting them.
- Steam library auto-discovery (`libraryfolders.vdf`) for non-default
  installs, then single-file publishing for end users.
- Machine-readable (`--json`) report output.
