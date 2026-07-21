# FFU support

**FFU (Fight for Universe: Beyond Reach)** is a large overhaul framework for
Ostranauts. It is not a normal data mod: it patches the game itself and changes
how mods load. Ostrasort treats FFU as a **supported** framework — it applies
FFU's ordering rules and models its load-time merge semantics — rather than
something to work around. This page explains what FFU does, how Ostrasort
recognises and orders FFU mods, and the quirks worth knowing.

The one true rival is a *different* project, Robyn's **OstraAutoloader** — see
[Quirks and gotchas](#quirks-and-gotchas).

## What FFU is

FFU ships as **MonoMod patches** — `Assembly-CSharp.FFU_BR*.mm.dll` files that
live in `BepInEx\monomod\` and are woven into the game's `Assembly-CSharp.dll` at
launch by the MonoMod patcher. They rewrite the game's data layer (`DataHandler`
and the whole `Json*` family of data classes) and extend the modding API. The
framework comes in modules, each of which registers a named "modding API" that
mods can declare a dependency on:

| Module DLL | Registered API |
|---|---|
| `Assembly-CSharp.FFU_BR.mm.dll` | `FFU_BR_Core` |
| `Assembly-CSharp.FFU_BR_Console.mm.dll` | `FFU_BR_Console` |
| `Assembly-CSharp.FFU_BR_Extended.mm.dll` | `FFU_BR_Extended` |
| `Assembly-CSharp.FFU_BR_Fixes.mm.dll` | `FFU_BR_Fixes` |
| `Assembly-CSharp.FFU_BR_Quality.mm.dll` | `FFU_BR_Quality` |
| `Assembly-CSharp.FFU_BR_Super.mm.dll` | `FFU_BR_Super` |

The base game itself is exposed as the `Base_Game` API. On top of the DLLs, FFU
ships a mandatory data mod, **Minor Fixes Plus** (id `Minor_Fixes_Plus`): using
the FFU DLLs without it breaks parts of the vanilla game, and it must load as the
**first** FFU mod.

## How FFU loads mods

FFU replaces the game's `DataHandler.LoadMods`. The important facts (verified
against the decompiled `Assembly-CSharp.FFU_BR.mm.dll`):

### It loads in `loading_order.json` order

FFU:BR walks `loading_order.json`'s `aLoadOrder` **in the order written** and
loads each mod in turn. It does **not** re-sort by any metadata. So the order
Ostrasort writes is exactly the order FFU honours — getting that order right is
the whole job.

### Objects merge field-by-field

Vanilla replaces a same-`strName` object **whole** (a later mod's partial object
drops every field it omits). With FFU installed, a partial object **merges
field-by-field** into the existing one — an entry that sets only `fMass` changes
only `fMass`. FFU also adds precision editing:

- **`strReference`** clones an existing entry as the base for a new one.
- **`removeIds`** / **`changesMap`** in `mod_info.json` delete or migrate entries.
- **`--ADD--`, `--INS--`, `--DEL--`, `--MOD--`** commands edit arrays in place.

### The FFU markers live in `mod_info.json`

FFU:BR reads two arrays from each mod's `mod_info.json` and enforces ordering
from them:

- **`requiredAPIs`** — the modding APIs this mod needs, e.g.
  `["FFU_BR_Core>=0.6.0", "Base_Game>=0.15"]`. **A non-empty `requiredAPIs` means
  the mod is FFU-based**, and FFU requires it to load **after** Minor Fixes Plus
  (it logs *"FFU-based mod 'X' should be listed after 'Minor_Fixes_Plus'"* if
  not). Mods with an **empty** `requiredAPIs` are treated as non-FFU and should
  load **before** Minor Fixes Plus. If a required API is missing, FFU **ignores
  (drops) the mod**.
- **`requiredMods`** — hard "load-after" dependencies on other mods, e.g.
  `["Minor_Fixes_Plus", "Some Other Mod>=1.2"]`. Each named mod must be installed
  **and load earlier**; if one is missing or ordered after the dependent, FFU
  **drops the dependent mod entirely**.

Both accept an optional version constraint (`>`, `>=`, `<`, `<=`, `=`, `!=`)
after the name.

### Names normalise with `GetID`

FFU compares mod names by an id: **spaces become underscores, then every
character outside `[A-Za-z0-9_]` is stripped** (case preserved). So a
`requiredMods` entry `Minor_Fixes_Plus` matches a mod whose `strName` is
`Minor Fixes Plus`, and `Glass_Only_EVA` matches `Glass Only EVA`.

### `Autoload.Meta.toml` is *not* FFU's

A mod may also ship an `Autoload.Meta.toml` — a `LoadGroup`
(`WithVanilla` / `FFUCore` / `AfterFFU`) plus `[dependencies]`. This file belongs
to **OstraAutoloader**, not FFU:BR — FFU:BR never reads it. Without OstraAutoloader
installed it has no effect on the game at all; it is advisory ordering metadata
that mirrors what `requiredAPIs`/`requiredMods` already imply. Ostrasort still
reads it (see below), because it is a clear statement of intent.

## How Ostrasort supports FFU

### Install detection

Ostrasort scans `BepInEx` for FFU's fingerprint: FFU MonoMod patches in
`BepInEx\monomod\` (the framework), whether the MonoMod loader is present, and —
separately — the OstraAutoloader plugin DLL. FFU on its own **never** blocks
writes; only OstraAutoloader does.

### Classifying a mod as FFU

A mod is classified FFU when **any** of these hold:

1. its `mod_info.json` declares a non-empty **`requiredAPIs`** (the authoritative
   FFU marker — this is what FFU:BR itself checks);
2. it depends on an FFU mod, via **`requiredMods`** or an `Autoload.Meta.toml`
   `[dependencies]` entry (a dependency on `Minor_Fixes_Plus` counts);
3. its `Autoload.Meta.toml` declares `LoadGroup="FFUCore"` or `"AfterFFU"`;
4. its data uses FFU-only API features (`strReference`, `--ADD--`-style commands,
   `removeIds`, `changesMap`);
5. its `mod_info.json` carries the Ostrasort-only **`bFFU": true`** hint; or
6. you **marked it FFU-dependent by hand** (see below).

Dependency names resolve with FFU's own `GetID` rule, so the underscore and
spaced forms of a name are the same mod. An explicit `LoadGroup="WithVanilla"` is
the one opt-out — it keeps a mod in the non-FFU block — unless the mod also
declares a hard `requiredMods` edge, which wins.

### Ordering rules

Ostrasort produces the order FFU wants:

- **Non-FFU mods load first**, then the **FFU block** — matching FFU's own
  "non-FFU before Minor Fixes Plus, FFU-based after" rule.
- Inside the FFU block, the **Minor Fixes Plus tier leads**, then FFU mods
  **dependency-sorted** (a mod loads after everything in its `requiredMods` /
  meta dependencies).
- **FFU "Patch" mods** are pinned immediately after the mod they patch, with a
  one-use reminder to remove them after the next launch.
- The generated **Ostrasort Patch** closes the **non-FFU** block (FFU mods
  field-merge on top of it) rather than loading absolute-last.

### Conflict analysis with FFU installed

Because FFU merges field-by-field, Ostrasort's collision analysis switches to the
same model: a fragment that touches only `nContainerWidth` reads as "changes one
field", not "replaces the whole object", and command-driven array edits
(`--ADD--` …) are reported as merging rather than being folded into the Ostrasort
Patch. (Plain loot/shop pools still replace wholesale unless a mod uses the array
commands.)

### Hygiene checks

Ostrasort warns about the FFU mistakes that silently break a save:

- Minor Fixes Plus missing or unregistered while the framework is installed.
- Mixed FFU DLL versions in `BepInEx\monomod` (an FFU update requires deleting
  **all** old FFU files first).
- The MonoMod loader missing (`BepInEx\core\MonoMod.dll`), so FFU never loads.
- **FFU version mismatch** — the FFU build targets a different game version than
  the one installed (this usually breaks the game outright; see quirks).
- A `requiredMods` dependency that is missing or disabled — flagged loudly,
  because FFU will **drop** the dependent mod.
- FFU-style mods installed with no framework at all.

### Data mods under `BepInEx\plugins\`

FFU / Thunderstore data mods sometimes live under `BepInEx\plugins\<Name>\` with
a `mod_info.json` + `data\`. Ostrasort indexes those and registers them in
`loading_order.json` by **absolute path** (the game loads only what `aLoadOrder`
lists).

### The manual FFU tag (for markerless Workshop mods)

If a Steam Workshop FFU mod ships **none** of the markers above — no
`requiredAPIs`, no `Autoload.Meta.toml`, no `bFFU` — Ostrasort genuinely cannot
see that it is FFU, and its files are read-only so you cannot add a marker. Tag
it yourself:

- **GUI:** right-click the mod → *"Mark as FFU-dependent (load after Minor Fixes
  Plus)"* (and *"Stop treating as FFU-dependent"* to clear it).
- **CLI:** `Ostrasort.exe --mark-ffu <name>` / `--unmark-ffu <name>`, where
  `<name>` is the MODS-screen name or the folder/Workshop id.

It is a **per-install sorting preference only** — no game files change, it is
remembered in `%APPDATA%\Ostrasort`, and it can only ever move a mod **later**
(into the FFU block), so it can never mis-sort a plain Workshop mod on its own.

## Quirks and gotchas

- **OstraAutoloader is the real rival.** Robyn's OstraAutoloader is a BepInEx
  *plugin* (not part of FFU:BR) that regenerates `loading_order.json` from
  scratch at **every** game launch, keeping only `Autoload.Meta.toml`-tagged mods
  and re-appending Workshop subscriptions at the end (after the FFU block,
  violating FFU's own rule). Anything Ostrasort writes would be undone, so with
  OstraAutoloader installed **Ostrasort is analysis-only**. Recommended: disable
  it (GUI button / `--disable-autoloader`) and let Ostrasort manage the order —
  it covers everything the autoloader does. Override the write-block, at your own
  risk, with `--allow-rival-stack`.
- **FFU pins your game version.** The MonoMod DLLs are compiled against **one
  exact game build**. After an Ostranauts update the game typically breaks
  (broken main menu, `NullReferenceException` spam) until a matching FFU build
  ships — and FFU lives outside the Steam Workshop, so nothing updates
  automatically. Ostrasort's standing recommendation is Steam Workshop mods only;
  the *Remove FFU* button / `--remove-ffu` parks the FFU files as `.disabled`
  (reversible) or deletes them.
- **`Autoload.Meta.toml` does nothing without OstraAutoloader.** It is not read by
  FFU:BR. A mod relying on it for ordering, on an install without OstraAutoloader,
  is ordered purely by `loading_order.json` — i.e. by whatever Ostrasort (or the
  game's default append) wrote.
- **Pure field-merge mods are invisible without a marker.** A mod that ships
  partial objects with no `requiredAPIs`, no commands, and no `strReference` is
  byte-for-byte indistinguishable from a normal whole-object override. Ostrasort
  cannot detect it as FFU — the author should add `requiredAPIs`/`requiredMods` or
  `bFFU`, or you can use the manual tag.
- **A missing `requiredMods` target silently drops the mod.** FFU removes the
  dependent mod from the load entirely if a required mod is absent, disabled, or
  ordered after it — so the mod appears installed but contributes nothing.
  Ostrasort flags this.
- **FFU "Patch" mods apply once.** They are meant to be removed from the load
  order after a single game launch; Ostrasort pins them after their target and
  reminds you.
- **`removeIds` is order-sensitive.** If one mod removes an object that another
  mod also modifies, the outcome depends on load order; Ostrasort surfaces the
  conflict and suggests loading the modifier after the remover to keep its
  version.
