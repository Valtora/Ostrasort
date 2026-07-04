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

## The conflict patch (shop/kiosk pools only)

When two mods both stock the same shop pool and neither pool covers the
other, someone's wares vanish no matter the load order. Ostrasort can build a
**merged patch mod** (`OstrasortPatch`) that takes the per-item union of the
conflicting pools and loads last, so nothing is lost — with **you** deciding
any item both mods define differently.

This automatic resolution applies to **`loot`-type collisions only** (shops,
kiosks, and other loot pools). Ostrasort deliberately does not auto-merge
other data types, because merging arbitrary game objects field-by-field can't
be done safely without understanding each field's meaning — for those, the
report tells you what conflicts and you decide the load order (or hand-patch).

The patch is wholly owned by Ostrasort: a marker file records exactly which
pools it merged, their content hashes, and every decision you made, so a
later refresh only re-asks about items that genuinely changed. See
[usage.md](usage.md) for the resolver workflow.

## Safety model

- **Analysis never writes anything** and is safe to run while the game is
  open.
- **Every write path** (apply order, generate/refresh/remove patch, restore
  `.bak`) refuses while Ostranauts is running, saves the previous
  `loading_order.json` as `loading_order.json.bak` first, keeps the file a
  **top-level JSON array** (if it ever becomes a bare object the game silently
  drops all local mods and regenerates it), and strict-re-parses its own
  output before committing it. Workshop entries keep their absolute-path form;
  local entries keep their `|edit` markers; exact-duplicate entries are
  dropped on write.
