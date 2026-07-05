<p align="center"><img src="Ostrasort-logo.png" alt="Ostrasort" height="120"></p>

# Ostrasort

A load-order and conflict manager for **Ostranauts** (Blue Bottle Games).
Run it before you launch the game — it reads your whole mod setup, gets it into
a working state, and keeps it that way.

Two jobs are the heart of it:

1. **Figure out the best mod load order.** It scans core plus every local,
   Steam-Workshop, and `BepInEx\plugins` mod, classifies each one, and suggests
   a `loading_order.json` that satisfies the rules (BepInEx after core, shop
   pools ordered correctly, dead entries pruned, unregistered mods surfaced)
   — with minimal churn to what you already have.
2. **Resolve mod conflicts.** When two mods change the same thing, the game
   keeps only one and silently drops the other's changes. Ostrasort merges
   them into a compatibility patch that keeps both — **shop/kiosk inventories**
   (per-item union) and **game objects** (field-by-field, with the base game
   as the common ancestor) — while **you** decide anything the mods genuinely
   disagree on.

Around those two jobs it works as a complete mod manager:

- **Profiles** — save the current load order as a named profile (a
  vanilla-plus run, an FFU stack, a single-mod test) and switch between setups
  with a diff preview, either **replacing** the whole order or **merging** the
  profile over what you already have. Mods a profile references that are no
  longer installed are skipped and reported, never a hard failure.
- **In-place mod management** — from the mod table, **enable / disable** a mod
  (the game's own `|disabled` marker), **ignore** an unregistered folder you
  keep parked, **remove** a mod (park it as `.disabled` or delete it), or
  **unsubscribe** a Workshop item.
- **Manual control** — drag rows to reorder by hand with live rule-violation
  checks, or apply the suggested order in one click. **Organise Mods by Class**
  optionally groups the list for readability.
- **Backups & undo** — every `loading_order.json` write keeps a `.bak` **and** a
  rolling backup history; **Make backup** snapshots on demand, **Restore
  backup** picks any restore point, and **Ctrl+Z / Ctrl+Y** undo/redo covers
  every operation. All writes are atomic and refused while the game is running.
- **Light / dark / system theming**, a **Logs** tab (Ostrasort's own log plus
  the game's `Player.log` and the BepInEx log), and an update checker that runs
  on launch and on demand (**Check for updates**).
- **Scriptable** — a console mode drives every capability: `--report`, `--json`
  (machine-readable), `--apply`, `--patch` / `--unpatch`, and
  `--profile-list` / `--profile-save` / `--profile-load`.

## Scope — what it can and can't do

Ostrasort **detects** data collisions of *every* kind and analyses them. It
can **automatically merge**:

- **Shop/kiosk inventories** (loot pools) — the per-item union, so no mod's
  wares are lost.
- **Game objects** (conditions, condowners, interactions, …) — a **3-way
  field merge** against the base game: where two mods change *different*
  fields of one object, both changes are kept automatically; where they change
  the *same* field, you pick the winner (or the array union, or vanilla).
  Merged objects are **schema-validated** against the game's own schemas and
  presented as **best-effort** — verify them in game, since two mods can
  change interdependent fields in ways no tool can fully reason about.

It does **not** yet merge objects that have no base-game version (two mods
adding the *same new* object with no common ancestor) — those it reports and
leaves to load order.

**Steam Workshop, local, and FFU mods.** Ostrasort manages `loading_order.json`
for core, local, Steam-Workshop, **and FFU** (Fight for Universe: Beyond Reach)
mods. On an FFU install it reads each mod's `Autoload.Meta.toml` (LoadGroup +
dependencies), keeps every FFU-dependent mod **after** all non-FFU mods with the
mandatory **Minor Fixes Plus** leading the FFU block, pins FFU "Patch" mods
right after their targets (and reminds you they apply once), registers FFU data
mods living under `BepInEx\plugins\`, and accounts for FFU's field-by-field
merge semantics in its conflict analysis. The one thing it will not co-manage
is Robyn's **OstraAutoloader** plugin — it regenerates `loading_order.json`
from scratch at every game launch, so while it is installed Ostrasort runs
**analysis-only** (writes refused, override: `--allow-rival-stack`) and offers
a one-click, reversible **Disable OstraAutoloader** hand-over.

That said, **Ostrasort's recommendation is Steam Workshop mods only.** FFU's
MonoMod DLLs are compiled against one specific game build, so FFU **pins your
game version** — after any Ostranauts update the game breaks until FFU ships a
matching build (Ostrasort detects this and raises an **FFU VERSION MISMATCH**
warning), and FFU is distributed outside the Workshop. Ostrasort supports FFU
installs so it can diagnose them and offer the way out: a one-click, reversible
**Remove FFU** action (`--remove-ffu`).

> ## ⚠️ Early development — use at your own risk
>
> Ostrasort is **in active development and not yet stable.** It edits your
> `loading_order.json` and can create a mod folder (`OstrasortPatch`) in your
> game install. It keeps a `.bak` of every load-order write and refuses to run
> while the game is open, but **it can still misbehave in ways that break your
> mod setup, a save, or require you to verify/reinstall the game.**
>
> **The author accepts no responsibility for any damage to your game, mods, or
> saves.** Back up your save folder first, and only use it if you're
> comfortable recovering your setup yourself. By using Ostrasort you accept
> that risk.

## Quick start

**Windows only** (64-bit Windows 10/11). Nothing to install:

1. **Download `Ostrasort.exe`** from the
   [latest release](https://github.com/Valtora/Ostrasort/releases/latest).
2. **Close Ostranauts** (Ostrasort won't write while the game is running).
3. **Double-click `Ostrasort.exe`.** It opens straight to its window (no
   console), finds your install automatically, and lists every mod in load
   order.
4. Read the highlighted tabs, **Apply the suggested order** and **Resolve
   conflicts** as needed, then launch the game. Save a **profile** if you want
   to come back to this setup, pick a **theme** to taste, and **Ctrl+Z** undoes
   anything.

Ostrasort checks GitHub for a newer release on startup — showing an **Update
available** button at the top-right when one exists — and you can run the check
yourself any time with the **Check for updates** link (both silent-friendly:
quiet when you're up to date or offline, with the outcome noted in the Logs
tab).

> Windows SmartScreen may warn on first run because the exe isn't
> code-signed — choose *More info → Run anyway*, or unblock it in the file's
> Properties.

Full walkthrough: **[docs/usage.md](docs/usage.md)**.

## Documentation

- **[Using Ostrasort](docs/usage.md)** — the full player guide: the window
  tour, profiles, the conflict resolver, managing the patch, and the
  command-line reference.
- **[How it works](docs/how-it-works.md)** — load order in Ostranauts, how
  mods are classified, what collisions Ostrasort detects, profiles, and the
  safety model.
- **[Building & developing](docs/development.md)** — build/publish and the
  code layout.
- **[Roadmap](ROADMAP.md)** — what's planned next (two-way merge for
  mod-added objects, a collision drill-down view, friendlier loot-pool names,
  …) and what deliberately isn't.

## Licence / disclaimer

Released under the [MIT License](LICENSE) — do what you like with it, just keep
the copyright notice. Provided as-is, with no warranty and no liability for
damage to your game, mods, or saves (see the notice above). Not affiliated with
Blue Bottle Games.
