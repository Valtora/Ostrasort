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
- An entry can carry a **`|disabled` marker** (`SomeMod|disabled`) — that's how
  the in-game MODS screen disables a mod: the entry stays in the list but is
  skipped at load. Ostrasort understands the marker: disabled mods show dimmed,
  keep their entry (they are never "dead paths"), and are excluded from
  collision and FFU analysis, since they don't load.
- The **BepInEx Workshop bridge reads `loading_order.json`** to locate
  subscribed code mods' plugins, so even code-only mods need to be there.
- `loading_order.json` can also carry **`aIgnorePatterns`** — global substring
  patterns matched against every data file's path, in core **and** every mod;
  matching files are skipped entirely (this is the vanilla way to *remove*
  data rather than override it). Ostrasort applies the same skips to its own
  index and warns about exactly which files the patterns remove.

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

Pools are compared **pairwise across all claimants**, not just neighbours —
with three or more mods on one pool, a partial overlap between the first and
last claimant is still caught even when each adjacent pair looks clean.

**Every other object type** (condowners, interactions, conditions, …) gets
**field-level analysis**: Ostrasort diffs each mod's version against the base
game and reports which fields each one changes. If the changed field sets are
**disjoint**, a hand-merged override could in principle keep both mods'
changes (only the last-loaded one survives today). If they **overlap**, it's
a genuine conflict the last-loaded mod wins. Ostrasort **reports** this — it
does not merge non-loot objects for you.

One namespace subtlety: **`conditions_simple` containers define conditions in
the same namespace as `conditions\`** — the game parses them into the
conditions dictionary *after* every mod loads, so a simple-defined condition
overrides a `conditions\` version of the same name **regardless of load
order**. Ostrasort expands each container's entries into individual claims so
those cross-folder collisions are detected, and explains the always-wins rule
in the collision notes.

**Flat-packed containers are never whole-object-merged.** `conditions_simple`
and its cousins (`strings`, `names_first|full|last|robots|ship|ship_adjectives|ship_nouns`,
`crewskins`, `manpages`, `traitscores`) each ship a *single* container object
whose `aValues` is a fixed-width record array the game **explodes into
individual records** after every mod loads — it never replaces the container
whole. So two mods that each add their own records lose nothing; only a record
**both** define collides (last-loaded wins). Ostrasort reports these as
additive-at-load and **never folds them into the patch** — unioning a
fixed-width `aValues` would scramble the packing and crash the game on load.

## Hygiene checks

Surfaced in the same pass:

- **dead `aLoadOrder` paths** — entries pointing at unsubscribed/removed items
- **unregistered local mods** — folders under `Mods\` missing from `aLoadOrder`
  (invisible to the MODS screen)
- **subscribed Workshop items** not yet in `aLoadOrder`
- **duplicate entries** (the game sometimes re-appends a subscription)
- **`strGameVersion` mismatch** vs the installed game version (worded by
  direction: a mod that predates the game vs one built for a newer game)
- **`aIgnorePatterns` removals** — which core/mod files the patterns skip
- **invalid/lenient JSON** — files with a trailing comma (or otherwise invalid
  JSON), which the game's own loader treats as an ERROR. Comments (`//` and
  `/* */`) are **not** flagged: the game accepts them, and ships them in its own
  core data (`tokens/verbs.json`, `conditions_simple/conditions_simple.json`)
- **image overrides** — two mods shipping the same `images\` path (last wins
  the whole file)
- **BepInEx sanity** — plugins that can never load because the loader isn't
  installed, and the same plugin DLL shipped by two different sources
  (double-patching)

## FFU support & the OstraAutoloader

**FFU (Fight for Universe: Beyond Reach)** patches the game with **MonoMod**
(`*.mm.dll` in `BepInEx\monomod\`) and extends the modding API: partial-object
entries merge **field-by-field** into existing objects at load,
`strReference` clones an existing entry, `removeIds`/`changesMap` in
`mod_info.json` delete/migrate entries, and `--ADD--`/`--INS--`/`--DEL--`/
`--MOD--` commands edit arrays in place. FFU mods declare their place in the
world in an `Autoload.Meta.toml`: a `LoadGroup` (`WithVanilla`, `FFUCore`,
`AfterFFU`) plus dependencies keyed by `strName`.

A **pure field-merge mod** (partial objects that overwrite only a few fields by
`strName`, with no `--ADD--`-style commands or `strReference`) carries no marker
Ostrasort can see — it is content-identical to a normal whole-object override,
so by default it would be sorted *up*, out of the FFU block. Two ways to keep it
in place: give it an `Autoload.Meta.toml` with `LoadGroup="AfterFFU"` (the
standard FFU declaration, which also tells FFU to load it after its target), or
add `"bFFU": true` to its `mod_info.json` — an **Ostrasort-only sorting hint**
(FFU's field-merge is automatic once FFU is installed; neither FFU nor the game
reads this key).

Ostrasort treats all of that as a **supported ordering contract**:

- a mod is classified **FFU** when its meta declares `FFUCore`/`AfterFFU`, it
  depends on an FFU mod, its data uses the FFU-only API features above, or its
  `mod_info.json` carries the `bFFU` hint;
- the sort keeps every FFU mod **after all non-FFU mods** (the game loads
  non-FFU content first — this is FFU's own rule), with the **Minor Fixes
  Plus** tier leading the FFU block and dependencies before dependents;
- FFU **"Patch" mods** are pinned immediately after their target and flagged
  "applies once — remove after one game launch";
- FFU data mods under `BepInEx\plugins\` are indexed and registered by
  absolute path;
- collision analysis models FFU's load-time field merge: a fragment touching
  only `nContainerWidth` no longer reads as "replaces the whole sink", and
  command-driven array edits are reported as merging, never folded into the
  Ostrasort Patch (which, on FFU installs, closes the **non-FFU** block
  instead of loading absolute-last);
- hygiene checks cover mixed FFU DLL versions (updates require removing all
  old FFU files), a missing MonoMod loader, a missing/unregistered Minor Fixes
  Plus, and FFU-style mods installed without the framework.

The one genuine **rival** is Robyn's **OstraAutoloader** plugin: it discovers
`Autoload.Meta.toml` mods and **regenerates `loading_order.json` from scratch
at every game launch** — local mods without a meta file, and every
`|edit`/`|disabled` marker, are silently dropped, and the game then re-appends
subscribed Workshop mods at the *end* of the list (after the FFU block).
Anything Ostrasort wrote would be undone at the next launch, so while the
autoloader DLL is present Ostrasort is **analysis-only**: the GUI opens
normally with a red banner and disabled write buttons, the console report
prints the same, and every write flag refuses (`--allow-rival-stack`
overrides). The banner recommends handing the load order to Ostrasort — it
understands everything the autoloader does, plus Workshop and local mods, and
unlike the autoloader it is actively maintained — and offers a one-click
**Disable OstraAutoloader** action (console: `--disable-autoloader`) that
renames the autoloader DLL(s) to `.disabled`, which is fully reversible. A
meta file **by itself is inert** and never triggers the read-only mode.

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
objects it merged, their content hashes, **the hash of the base-game object
each merge overlaid**, and every decision you made. A later refresh only
re-asks about things that genuinely changed — and if a **game update** changes
the vanilla version of a merged object, the patch is flagged stale even though
no mod changed, because the merge would keep overriding it with values built
on the old vanilla base. See [usage.md](usage.md) for the resolver workflow.

## Profiles

A profile is a saved copy of the `aLoadOrder` — each entry with its
`|edit`/`|disabled` marker — under a name, stored per install in
`%LOCALAPPDATA%\Ostrasort\profiles`. It captures **only the mod order**: not the
generated `OstrasortPatch` (Ostrasort's own overlay, re-derived per setup), not
`aIgnorePatterns`, and not the ignore list — all of which stay global to the
install, so switching a profile only ever rewrites the order.

Switching resolves each saved entry against what's installed now and drops any
mod that no longer exists (reported, never a hard failure), then writes the
result one of two ways, your choice:

- **Replace** — the profile's order becomes the whole `aLoadOrder`; currently
  registered mods the profile omits are removed from the order (their files are
  untouched, and any unregistered local mod is flagged as usual). The clean
  switch between distinct setups.
- **Merge-append** — the profile's order, then every currently registered mod it
  doesn't mention, appended in place. Layers a profile over the current order.

Either way, entries are de-duplicated by identity (a mod is never listed twice
with conflicting markers), core stays first, and the write goes through the same
guarded ritual as an apply (`.bak`, rolling backup, atomic, undoable). Because a
different set of mods can mean a different set of conflicts, a switch re-runs the
patch staleness inspection, so the Patch tab flags a now-stale or newly-needed
patch straight away.

## Safety model

- **Analysis never writes anything** and is safe to run while the game is
  open.
- **Every write path** (apply order, generate/refresh/remove patch, restore
  `.bak`) refuses while Ostranauts is running, saves the previous
  `loading_order.json` as `loading_order.json.bak` first, keeps the file a
  **top-level JSON array** (if it ever becomes a bare object the game silently
  drops all local mods and regenerates it), and strict-re-parses its own
  output before committing it. The live file is swapped in **atomically**
  (written to a scratch file first), so a crash or power cut mid-write can
  never leave a truncated `loading_order.json` behind. Local entries keep
  their `|edit`/`|disabled` markers, and exact-duplicate entries are dropped
  on write.
- **Rolling backups.** The `.bak` only survives until the next write, so every
  overwrite also snapshots the previous text into
  `%LOCALAPPDATA%\Ostrasort\backups` (the newest **3** are kept). The GUI's
  *Restore backup…* button lists all of them.
- **Workshop paths are written in the game's own on-disk case.** The Steam
  registry hands out a lowercase drive (`c:\program files…`), but Ostranauts
  writes `C:\Program Files…`. If Ostrasort wrote the lowercase form the game
  would not recognise its own subscription and would re-add it every launch,
  duplicating the mod — so every absolute path is canonicalised to its real
  filesystem case before writing.
- **The OstraAutoloader blocks writes.** If the autoloader plugin is present
  (see above), Ostrasort refuses to modify `loading_order.json` at all — the
  autoloader would overwrite it at the next launch — unless you explicitly
  override. FFU itself never blocks anything.
- **Everything Ostrasort writes is logged** to the Logs tab (and to
  `%LOCALAPPDATA%\Ostrasort\ostrasort.log`), so you can always see what it did.
