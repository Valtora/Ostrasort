# How Ostrasort works

Ostrasort reads your Ostranauts install and every mod in it, works out what
each mod actually contains, and reports two kinds of problem: **load-order
issues** (which it can sort for you) and **data collisions** (which it
detects for every data type, but can only automatically *resolve* for shop
and kiosk inventories). This page explains the model behind that.

## Why load order matters in Ostranauts

The game loads every folder listed in `loading_order.json`'s `aLoadOrder` in
sequence. When two loaded objects share the same `strName` **and** data type,
**the later one replaces the earlier one whole** — there is no field-by-field
merge. So load order decides which mod's version of a shared thing actually
takes effect.

Membership matters too:

- The in-game **MODS screen only lists entries present in `aLoadOrder`** — a
  mod folder sitting under `Mods\` that isn't registered is invisible to the
  game's mod UI.
- The **BepInEx Workshop bridge reads `loading_order.json`** to locate
  subscribed code mods' plugins, so even code-only mods need to be there.

## How mods are classified

Every mod is bucketed from its contents, which drives whether its position
matters:

| Class | Detected by | Ordering rule |
|---|---|---|
| `infrastructure` | ships `BepInEx\patchers\` (the Mod Loader) | pinned immediately after `core` |
| `code` | plugins/DLLs only, no data objects | position irrelevant — left alone |
| `shell` | metadata only | position irrelevant — left alone |
| `dataadditive` | adds new objects only | safe anywhere after `core` — left alone |
| `dataoverride` | replaces core objects | left alone unless it collides |
| `patch` | the folder Ostrasort itself generates | always last |

**Minimal churn:** the suggested order starts from your current order and
applies only the moves a rule actually demands, so nothing shuffles for no
reason. Optional **tidy grouping** additionally groups the list (core →
infrastructure → code → shells → additive → overrides → patch) for
readability; it's off by default.

## Collision detection (every data type)

Ostrasort cross-references every `(data type, strName)` claim across all
mods. Any object claimed by two or more mods is a collision, and the report
tells you who claims it and what the outcome is.

**Loot pools** (shop/kiosk inventories and similar `loot`-type objects) are
compared at **item granularity** — the token before `=` in each
`"Item=weight x count"` entry:

- later pool ⊇ earlier → **order correct** ✔
- later pool ⊂ earlier → **wrong order**: the superset is moved to load last
- **partial overlap** → **conflict**: no load order can keep both mods' items
  → this is what the patch generator fixes (see below)
- identical item sets → note (only quantities differ; last loaded wins)

**Every other object type** (condowners, interactions, conditions, …) gets
**field-level analysis**: Ostrasort diffs each mod's version against the base
game and reports which fields each one changes. If the changed field sets are
**disjoint**, a hand-merged override could in principle keep both mods'
changes (only the last-loaded one survives today). If they **overlap**, it's
a genuine conflict the last-loaded mod wins. Ostrasort **reports** this — it
does not merge non-loot objects for you.

## Hygiene checks

Surfaced in the same pass:

- **dead `aLoadOrder` paths** — entries pointing at unsubscribed/removed items
- **unregistered local mods** — folders under `Mods\` missing from `aLoadOrder`
  (invisible to the MODS screen)
- **subscribed Workshop items** not yet in `aLoadOrder`
- **duplicate entries** (the game sometimes re-appends a subscription)
- **`strGameVersion` lag** vs the installed game version
- **invalid/lenient JSON** — files that only parse with trailing commas etc.,
  which the game's own loader treats as an ERROR
- **image overrides** — two mods shipping the same `images\` path (last wins
  the whole file)
- **BepInEx sanity** — plugins that can never load because the loader isn't
  installed, and the same plugin DLL shipped by two different sources
  (double-patching)

## Rival load-order managers (FFU / Thunderstore)

Ostrasort is a **Steam-Workshop-first** tool and manages `loading_order.json`
for core, local, and Workshop mods. It deliberately does **not** support the
Thunderstore / **FFU** (Fight for Universe: Beyond Reach) stack, because that
stack ships its *own* load-order manager: Robyn's **OstraAutoloader** discovers
mods (by an `Autoload.Meta.toml` file, under `BepInEx\plugins\` or `Mods\`),
topologically sorts them by their declared `LoadGroup` and dependencies, and
**regenerates `loading_order.json` itself at every launch**. FFU also patches the
game with **MonoMod** (`*.mm.dll` in `BepInEx\monomod\`) rather than Harmony.

Two managers writing the same file fight each other, so Ostrasort detects the
stack — the autoloader plugin, any `Autoload.Meta.toml`, or MonoMod patches — and
steps aside rather than corrupting an FFU setup:

- the **GUI shows a blocking notice at startup**: quit (the default), or continue
  at your own risk;
- the **console report prints a banner**, and every **write path refuses**
  (`--apply` / `--patch` / `--unpatch` / `--normalize` exit with an error).
  `--allow-rival-stack` overrides the refusal for anyone who knows what they're
  doing.

Use one or the other on a given install, not both. Ostrasort has no plans to
support Thunderstore or FFU.

## The conflict patch

When two mods both change the same thing and the game would keep only one,
Ostrasort can build a **merged patch mod** (`OstrasortPatch`) that keeps both
and loads last. It merges two kinds of conflict:

### Shop/kiosk pools (per-item union)

A loot pool's `aLoots` is an additive list, so merging two versions is the
per-item union — every mod's wares survive, and where two mods stock the same
item with different quantities, you pick which wins (or exclude it).

### Game objects (3-way field merge)

For non-loot objects (conditions, condowners, interactions, …) Ostrasort does
a **three-way merge** using the base game as the common ancestor — the same
idea as a version-control merge:

- A field only **one** mod changed (vs the base game) merges automatically.
- A field **several** mods changed to the **same** value merges automatically.
- A field they changed to **different** values is a conflict you resolve: pick
  a mod's value, take the **union** (for array `a*` fields), or keep the
  **vanilla** value.

The game's Hungarian field prefixes make this reliable: `a*` fields are arrays
(a union is offered), everything else is a scalar (pick one).

Merged objects are then **validated against the game's own JSON schemas**
(`StreamingAssets\data\schemas`) and any that don't conform are flagged. This
is a **best-effort** merge, not a guaranteed-correct one: two mods can change
interdependent fields in ways no tool can reason about, so the result is
always presented for you to verify in game. Objects with no base-game version
(two mods adding the same brand-new object) are not auto-merged — there is no
common ancestor to merge against — so those are reported and left to load
order.

The patch is wholly owned by Ostrasort: a marker file records exactly which
objects it merged, their content hashes, and every decision you made, so a
later refresh only re-asks about things that genuinely changed. See
[usage.md](usage.md) for the resolver workflow.

## Safety model

- **Analysis never writes anything** and is safe to run while the game is
  open.
- **Every write path** (apply order, generate/refresh/remove patch, restore
  `.bak`) refuses while Ostranauts is running, saves the previous
  `loading_order.json` as `loading_order.json.bak` first, keeps the file a
  **top-level JSON array** (if it ever becomes a bare object the game silently
  drops all local mods and regenerates it), and strict-re-parses its own
  output before committing it. Local entries keep their `|edit` markers, and
  exact-duplicate entries are dropped on write.
- **Workshop paths are written in the game's own on-disk case.** The Steam
  registry hands out a lowercase drive (`c:\program files…`), but Ostranauts
  writes `C:\Program Files…`. If Ostrasort wrote the lowercase form the game
  would not recognise its own subscription and would re-add it every launch,
  duplicating the mod — so every absolute path is canonicalised to its real
  filesystem case before writing.
- **A rival load-order manager blocks writes.** If the FFU / OstraAutoloader
  stack is present (see above), Ostrasort refuses to modify `loading_order.json`
  at all — that stack owns the file — unless you explicitly override.
- **Everything Ostrasort writes is logged** to the Logs tab (and to
  `%LOCALAPPDATA%\Ostrasort\ostrasort.log`), so you can always see what it did.
