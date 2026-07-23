# How Ostrasort works

Ostrasort reads your Ostranauts install and every mod in it, works out what each
mod actually contains, and reports two kinds of problem. **Load-order issues** it
can sort for you, and **data collisions** it detects for every data type and can
merge into a compatibility patch (shop and kiosk inventories as a per-item union,
other game objects field-by-field against the base game). This page explains the
model behind that.

## Why load order matters in Ostranauts

The game loads every folder listed in `loading_order.json`'s `aLoadOrder` in
sequence. When two loaded objects share the same `strName` **and** data type,
**the later one replaces the earlier one whole**. There is no field-by-field
merge. So load order decides which mod's version of a shared thing actually takes
effect.

Membership matters too.

- The in-game **MODS screen only lists entries present in `aLoadOrder`**. A mod
  folder sitting under `Mods\` that is not registered is invisible to the game's
  mod UI.
- An entry can carry a **`|disabled` marker** (`SomeMod|disabled`). That is how
  the in-game MODS screen disables a mod, the entry stays in the list but is
  skipped at load. Ostrasort understands the marker, so disabled mods show
  dimmed, keep their entry (they are never "dead paths"), and are left out of
  collision and FFU analysis, since they do not load.
- The **BepInEx Workshop bridge reads `loading_order.json`** to locate subscribed
  code mods' plugins, so even code-only mods need to be there.
- `loading_order.json` can also carry **`aIgnorePatterns`**, global substring
  patterns matched against every data file's path, in core **and** every mod.
  Matching files are skipped entirely (this is the vanilla way to *remove* data
  rather than override it). Ostrasort applies the same skips to its own index and
  warns about exactly which files the patterns remove.

## How mods are classified

Every mod is bucketed from its contents, which drives whether its position
matters.

| Class | Detected by | Ordering rule |
|---|---|---|
| `infrastructure` | ships `BepInEx\patchers\` (the Mod Loader) | pinned immediately after `core` |
| `code` | plugins or DLLs only, no data objects | position irrelevant, left alone |
| `shell` | metadata only | position irrelevant, left alone |
| `dataadditive` | adds new objects only | safe anywhere after `core`, left alone |
| `dataoverride` | replaces core objects | left alone unless it collides |
| `patch` | the folder Ostrasort itself generates | always last |

**Minimal churn.** The suggested order starts from your current order and applies
only the moves a rule actually demands, so nothing shuffles for no reason.
Optional **tidy grouping** additionally groups the list (core, then
infrastructure, then code, then shells, then additive, then overrides, then
patch) for readability. It is off by default.

## Game-system categories and load priority

Class answers "what kind of files does this mod ship". A second, independent axis
answers "what part of the game does it change", and that is what decides which
mod should win when two touch the same system. Ostrasort buckets every mod into a
**category** from its data types and object namespaces (ships and stations, items
and economy, characters and behaviour, interactions and rules, narrative,
cosmetic, and **new game / start**), shown in the mod detail and the exported
report.

Most categories carry no ordering weight: their mods stay put (minimal churn).
One does. A **new game / start** mod owns character generation, the choices a
player makes before the game begins, and it must have the **final say**, so it
loads **late** (after ordinary content, before the Ostrasort patch and the FFU
block). The signal is reliable: in the base game the whole `lifeevents` data type
is character generation, and every character-generation object across other types
carries the `CGEnc` (Character Generation Encounter) name prefix. A mod that
touches either is detected as new game / start.

This is why, for example, **Vanilla Plus Character Generation** sorts to the
bottom. It replaces the vanilla starting-ship dice roll with a deterministic
choice by overriding the `CGEncShipbreaker*` pools. Because the game keeps the
**last-loaded** version of a shared object, its deterministic version only wins if
it loads after every other mod that touches those pools, including a ship mod that
merely adds a starter ship to the selection. The old purely-quantity loot rule
(the mod stocking *more* items should load last) fought that: a curation mod
deliberately stocks *fewer*, so it read as the one to move up. The category rule
wins over that heuristic, so a final-say mod is never pushed up by item count.

**Curation is respected, not merged.** When a final-say mod loads last over a pool
it owns, that collision is handled by load order: its version wins by design and
the other mod's is intentionally replaced. Ostrasort does **not** fold such a pool
into the compatibility patch, because a per-item union would re-add exactly the
entries the author removed (the vanilla ships a deterministic start is meant to
exclude). The collision shows on the **Handled automatically** tab, not as a
conflict.

**Load priority (aCOs).** Character-generation pools route their choices through
the `aCOs` field (a parallel weighted list) rather than `aLoots`, so the collision
analysis compares both, unioning their referenced entries. A pool whose only
difference is in `aCOs` is no longer read as an identical empty pool.

**Pinning it yourself.** Detection is data-driven, but you have the final word.
Right-click any mod and pick **Load priority** to pin it Late (final say), Early
(yields to other mods), Normal, or back to Auto. From a terminal the same is
`--mark-late`, `--mark-early`, `--mark-normal`, and `--unpin-priority`. It is a
per-install sorting preference only (no game files change, remembered in
`%APPDATA%\Ostrasort`) and the suggestion honours it on every rescan instead of
re-proposing a move.

## Collision detection (every data type)

Ostrasort cross-references every `(data type, strName)` claim across all mods.
Any object claimed by two or more mods is a collision, and the report tells you
who claims it and what the outcome is.

**Loot pools** (shop and kiosk inventories and similar `loot`-type objects) are
compared at **item granularity**, the token before `=` in each
`"Item=weight x count"` entry.

- The later pool contains the earlier one, so the **order is correct** ✔.
- The later pool is a subset of the earlier one, so the **order is wrong** and
  the superset is moved to load last.
- **Partial overlap** is a **conflict** where no load order can keep both mods'
  items. This is what the patch generator fixes (see below).
- Identical item sets are just a note (only the quantities differ, and the last
  loaded wins).

Pools are compared **pairwise across all claimants**, not just neighbours. With
three or more mods on one pool, a partial overlap between the first and last
claimant is still caught even when each adjacent pair looks clean.

Not every collision is a problem. Outcomes that lose nothing (identical item
sets, identical overrides, a last-loaded version that already includes every
change, a correct-order superset, or an object type the game or FFU merges
field-by-field at load) move to the **Handled automatically** tab, alongside
anything the generated patch merges. The **Conflicts** tab (and its badge, the
"needs attention" total, and the headless exit code, all driven by the same
predicate) shows only collisions where something is actually lost or is fixable
(partial-overlap loot, wrong-order drops, or a mergeable object override), so it
reads clean when there is nothing to do.

**Every other object type** (condowners, interactions, conditions, …) gets
**field-level analysis**. Ostrasort diffs each mod's version against the base
game and reports which fields each one changes. If the changed field sets are
**disjoint** (each mod touches different fields), the patch can keep both mods'
changes automatically. Where they change the **same** field to different values,
the mods genuinely disagree, so you pick the winner in the resolver (a mod's
value, the array union, or vanilla). The merge itself lands in the compatibility
patch, described under [The conflict patch](#the-conflict-patch) below.

One namespace subtlety. **`conditions_simple` containers define conditions in the
same namespace as `conditions\`**. The game parses them into the conditions
dictionary *after* every mod loads, so a simple-defined condition overrides a
`conditions\` version of the same name **regardless of load order**. Ostrasort
expands each container's entries into individual claims so those cross-folder
collisions are detected, and explains the always-wins rule in the collision
notes.

**Flat-packed containers are never whole-object-merged.** `conditions_simple` and
its cousins (`strings`, `names_first|full|last|robots|ship|ship_adjectives|ship_nouns`,
`crewskins`, `manpages`, `traitscores`) each ship a *single* container object
whose `aValues` is a fixed-width record array the game **explodes into individual
records** after every mod loads. It never replaces the container whole. So two
mods that each add their own records lose nothing, and only a record **both**
define collides (last-loaded wins). Ostrasort reports these as additive-at-load
and **never folds them into the patch**, because unioning a fixed-width `aValues`
would scramble the packing and crash the game on load.

## Hygiene checks

Surfaced in the same pass.

- **dead `aLoadOrder` paths**, entries pointing at unsubscribed or removed items
- **unregistered local mods**, folders under `Mods\` missing from `aLoadOrder`
  (invisible to the MODS screen)
- **subscribed Workshop items** not yet in `aLoadOrder`
- **duplicate entries** (the game sometimes re-appends a subscription)
- **`strGameVersion` mismatch** against the installed game version (worded by
  direction, a mod that predates the game against one built for a newer game)
- **`aIgnorePatterns` removals**, which core or mod files the patterns skip
- **invalid or lenient JSON**, files with a trailing comma (or otherwise invalid
  JSON), which the game's own loader treats as an ERROR. Comments (`//` and
  `/* */`) are **not** flagged, since the game accepts them and ships them in its
  own core data (`tokens/verbs.json`, `conditions_simple/conditions_simple.json`)
- **image overrides**, two mods shipping the same `images\` path (last wins the
  whole file)
- **BepInEx sanity**, plugins that can never load because the loader is not
  installed, and the same plugin DLL shipped by two different sources
  (double-patching)

## FFU support and the OstraAutoloader

**FFU (Fight for Universe, Beyond Reach)** patches the game with **MonoMod**
(`*.mm.dll` in `BepInEx\monomod\`) and extends the modding API. Partial-object
entries merge **field-by-field** into existing objects at load, `strReference`
clones an existing entry, `removeIds` and `changesMap` in `mod_info.json` delete
or migrate entries, and `--ADD--`, `--INS--`, `--DEL--`, and `--MOD--` commands
edit arrays in place. The most reliable marker is in a mod's own
`mod_info.json`: FFU:BR reads a **`requiredAPIs`** array (a non-empty one means
the mod needs the FFU framework, so FFU itself requires it to load *after* Minor
Fixes Plus) and a **`requiredMods`** array (hard "load-after" dependencies FFU
enforces by *dropping* the mod if one is missing or ordered wrong). A mod may
also carry an `Autoload.Meta.toml` — a `LoadGroup` (`WithVanilla`, `FFUCore`,
`AfterFFU`) plus dependencies — but that file is read by Robyn's OstraAutoloader,
**not** by FFU:BR itself (which just loads in `loading_order.json` order).

A **pure field-merge mod** (partial objects that overwrite only a few fields by
`strName`, with no `requiredAPIs`, `--ADD--`-style commands, or `strReference`)
carries no marker Ostrasort can see. It is content-identical to a normal
whole-object override, so by default it would be sorted *up*, out of the FFU
block. The fix is any marker the mod can ship: a `requiredAPIs`/`requiredMods`
entry or an `Autoload.Meta.toml` `LoadGroup="AfterFFU"` (the real FFU
declarations), or `"bFFU": true` in `mod_info.json` — an **Ostrasort-only sorting
hint** for a pure field-merge mod that legitimately declares none of the above.

When a **Steam Workshop** FFU mod ships one of those markers, Ostrasort detects
it automatically — no action needed. Only when the mod ships *nothing* (no
`requiredAPIs`, no meta, no `bFFU`) and its files are read-only can Ostrasort not
see it. For that case, **mark the mod FFU-dependent yourself**: right-click it and
choose "Mark as FFU-dependent (load after Minor Fixes Plus)", or run
`--mark-ffu <name>` from a terminal. It is a per-install sorting preference only
(no game files change), remembered in `%APPDATA%\Ostrasort`, and can only ever
move a mod later in the order, so it can never mis-sort a plain Workshop mod on
its own.

Ostrasort treats all of that as a **supported ordering contract**.

- A mod is classified **FFU** when its `mod_info.json` declares `requiredAPIs`, it
  depends on an FFU mod (`requiredMods` or an `Autoload.Meta.toml` dependency), its
  meta declares `FFUCore`/`AfterFFU`, its data uses the FFU-only API features
  above, its `mod_info.json` carries the `bFFU` hint, or you marked it
  FFU-dependent by hand. Mod names resolve by FFU's own id rule (spaces become
  underscores, so `Minor Fixes Plus` and `Minor_Fixes_Plus` are the same mod).
- The sort keeps every FFU mod **after all non-FFU mods** (the game loads non-FFU
  content first, which is FFU's own rule), with the **Minor Fixes Plus** tier
  leading the FFU block and dependencies before dependents.
- FFU **"Patch" mods** are pinned immediately after their target and flagged
  "applies once, remove after one game launch".
- FFU data mods under `BepInEx\plugins\` are indexed and registered by absolute
  path.
- Collision analysis models FFU's load-time field merge, so a fragment touching
  only `nContainerWidth` no longer reads as "replaces the whole sink", and
  command-driven array edits are reported as merging, never folded into the
  Ostrasort Patch (which, on FFU installs, closes the **non-FFU** block instead
  of loading absolute-last).
- Hygiene checks cover mixed FFU DLL versions (updates require removing all old
  FFU files), a missing MonoMod loader, a missing or unregistered Minor Fixes
  Plus, and FFU-style mods installed without the framework.

The one genuine **rival** is Robyn's **OstraAutoloader** plugin. It discovers
`Autoload.Meta.toml` mods and **regenerates `loading_order.json` from scratch at
every game launch**. Local mods without a meta file, and every `|edit` or
`|disabled` marker, are dropped without warning, and the game then re-appends
subscribed Workshop mods at the *end* of the list (after the FFU block).
Anything Ostrasort wrote would be undone at the next launch, so while the
autoloader DLL is present Ostrasort is **analysis-only**. The GUI opens normally
with a red banner and disabled write buttons, the console report prints the same,
and every write flag refuses (`--allow-rival-stack` overrides). The banner
recommends handing the load order to Ostrasort, which understands everything the
autoloader does, plus Workshop and local mods, and unlike the autoloader is
actively maintained. It offers a one-click **Disable OstraAutoloader** action
(console `--disable-autoloader`) that renames the autoloader DLL(s) to
`.disabled`, fully reversible. A meta file **by itself is inert** and never
triggers the read-only mode.

## The conflict patch

When two mods both change the same thing and the game would keep only one,
Ostrasort can build a **merged patch mod** (`OstrasortPatch`) that keeps both and
loads last. It merges two kinds of conflict.

### Shop and kiosk pools (per-item union)

A loot pool's `aLoots` is an additive list, so merging two versions is the
per-item union. Every mod's wares survive, and where two mods stock the same item
with different quantities, you pick which wins (or exclude it).

A pool's internal id (`CNDOLKioskEmbassyOKLG`) means little on its own, so
Ostrasort annotates it with a **friendly name**. It reverse-indexes the core game
so a pool is described by the kiosk or shop that uses it (its `strNameFriendly`,
for example "Embassy Services, K-Leg, 1036-Ganymed"), falling back to a decode of
the id itself for shop-like pools ("OKLG furnishings kiosk inventory"). This
index is cached alongside the core index, so it costs nothing after the first
scan following a game update.

### Game objects (3-way field merge)

For non-loot objects (conditions, condowners, interactions, …) Ostrasort does a
**three-way merge** using the base game as the common ancestor, the same idea as
a version-control merge.

- A field only **one** mod changed (against the base game) merges automatically.
- A field **several** mods changed to the **same** value merges automatically.
- A field they changed to **different** values is a conflict you resolve. Pick a
  mod's value, take the **union** (for array `a*` fields), or keep the **vanilla**
  value.

The game's Hungarian field prefixes make this reliable. `a*` fields are arrays (a
union is offered), and everything else is a scalar (pick one). The union is the
union of the **mods'** entries only, so an entry that exists in vanilla but was
removed by every mod stays removed, honouring their shared intent. Headless runs
(`--no-gui` and `--json`) auto-resolve a contested field to the **later-loaded
mod's value** (the same outcome the game itself would produce), never to a
computed union, and mark it for review in the GUI.

Merged objects are then **validated against the game's own JSON schemas**
(`StreamingAssets\data\schemas`) and any that do not conform are flagged. This is
a **best-effort** merge, not a guaranteed-correct one. Two mods can change
interdependent fields in ways no tool can reason about, so the result is always
presented for you to verify in game. Objects with **no base-game version** (two
mods adding the *same* brand-new object) have no common ancestor, so they degrade
to a **two-way merge** against an empty base. A field only one mod sets is kept
automatically, and a field both mods set differently is a conflict you resolve,
the same resolver and the same schema validation. Because there is no vanilla
base, only a source mod changing (not a game update) can make such a merge stale.

The patch is wholly owned by Ostrasort. A marker file records exactly which
objects it merged, their content hashes, **the hash of the base-game object each
merge overlaid**, and every decision you made. A later refresh only re-asks about
things that genuinely changed. And if a **game update** changes the vanilla
version of a merged object, the patch is flagged stale even though no mod changed,
because the merge would keep overriding it with values built on the old vanilla
base. Every generation **rebuilds the patch's `data\` tree from scratch**, so a
conflict that disappeared (a mod uninstalled or updated) can never leave a ghost
override behind. The staleness inspection also verifies the patch actually
**loads after every mod it merges**, so if the order puts a merged mod later, the
patch is flagged instead of falsely claiming "nothing lost". See
[usage.md](usage.md) for the resolver workflow.

## Profiles

A profile is a saved copy of the `aLoadOrder` (each entry with its `|edit` or
`|disabled` marker) under a name, stored per install in
`%APPDATA%\Ostrasort\profiles`. It captures **only the mod order**, not the
generated `OstrasortPatch` (Ostrasort's own overlay, re-derived per setup), not
`aIgnorePatterns`, and not the ignore list, all of which stay global to the
install, so switching a profile only ever rewrites the order.

Switching resolves each saved entry against what is installed now and drops any
mod that no longer exists (reported, never a hard failure), then writes the
result one of two ways, your choice.

- **Replace** makes the profile's order the whole `aLoadOrder`. Currently
  registered mods the profile omits are removed from the order (their files are
  untouched, and any unregistered local mod is flagged as usual). This is the
  clean switch between distinct setups.
- **Merge-append** writes the profile's order, then every currently registered
  mod it does not mention, appended in place. This layers a profile over the
  current order.

Either way, entries are de-duplicated by identity (a mod is never listed twice
with conflicting markers), core stays first, and the write goes through the same
guarded ritual as an apply (`.bak`, rolling backup, atomic, undoable). Because a
different set of mods can mean a different set of conflicts, a switch re-runs the
patch staleness inspection, so the Patch tab flags a now-stale or newly-needed
patch straight away.

## Safety model

- **Analysis never writes anything** and is safe to run while the game is open.
- **Every write path** (apply order, generate or refresh or remove patch, restore
  `.bak`) refuses while Ostranauts is running, saves the previous
  `loading_order.json` as `loading_order.json.bak` first, keeps the file a
  **top-level JSON array** (if it ever becomes a bare object the game drops all
  local mods and regenerates it), and strict-re-parses its own output before
  committing it. The live file is swapped in **atomically** (written to a scratch
  file first), so a crash or power cut mid-write can never leave a truncated
  `loading_order.json` behind. Local entries keep their `|edit` and `|disabled`
  markers, and exact-duplicate entries are dropped on write.
- **One writer at a time.** Every write takes a short-lived, per-file lock around
  the ritual above, so the GUI and a headless run (for example Ostraplan
  registering a ship mod through Ostrasort) cannot interleave writes to the same
  `loading_order.json`. On top of that the GUI notices when the file changed on
  disk since it last scanned (a background registration, another tool), and
  reloads instead of overwriting that change with a stale view.
- **Rolling backups.** The `.bak` only survives until the next write, so every
  overwrite also snapshots the previous text into `%APPDATA%\Ostrasort\backups`
  (the newest **3** are kept). The GUI's **Restore backup…** button lists all of
  them.
- **Workshop paths are written in the game's own on-disk case.** The Steam
  registry hands out a lowercase drive (`c:\program files…`), but Ostranauts
  writes `C:\Program Files…`. If Ostrasort wrote the lowercase form the game
  would not recognise its own subscription and would re-add it every launch,
  duplicating the mod, so every absolute path is canonicalised to its real
  filesystem case before writing.
- **The OstraAutoloader blocks writes.** If the autoloader plugin is present (see
  above), Ostrasort refuses to modify `loading_order.json` at all, since the
  autoloader would overwrite it at the next launch, unless you explicitly
  override. FFU itself never blocks anything.
- **Everything Ostrasort writes is logged** to the Logs tab (and to
  `%APPDATA%\Ostrasort\ostrasort.log`), so you can always see what it did.
