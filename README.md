<p align="center"><img src="Ostrasort-logo.png" alt="Ostrasort" height="120"></p>

# Ostrasort

A load-order and conflict manager for **Ostranauts** (Blue Bottle Games).
Run it before you launch the game — it reads your whole mod setup, gets it into
a working state, and keeps it that way.

<img width="2556" height="1376" alt="1" src="https://github.com/user-attachments/assets/d3b4cd05-e7f6-4087-acf8-2ab7e0febe06" />


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
   disagree on, through a short **guided wizard** (one plain-language question
   per decision, a suggested pick pre-selected, and a *Choose for me* shortcut).

Around those two jobs it works as a complete mod manager:

- **A health card that answers "am I OK"** — green when everything is fine,
  amber with a plain-language issue list and a one-click **Fix automatically**
  (suggested order + compatibility patch in one go), red with **What went
  wrong?** when the game itself reported problems from a mod on its last
  launch — each problem matched to the responsible mod with the obvious next
  steps (disable and relaunch to confirm, open its Workshop page).

- **Profiles** — save the current load order as a named profile (a
  vanilla-plus run, an FFU stack, a single-mod test) and switch between setups
  with a diff preview, either **replacing** the whole order or **merging** the
  profile over what you already have. Mods a profile references that are no
  longer installed are skipped and reported, never a hard failure.
- **Installations** — manage more than one game install from one Ostrasort.
  Save named installs, each with its own **game folder** and **mods folder**
  (they can live on different disks), and switch which one Ostrasort manages
  from the header. Auto-detect still honours the game's own relocated mods
  folder. Each install keeps its own profiles and backups.
- **Install from file** — **Install from file…** (or drag a `.zip` onto the
  window) installs a mod that didn't come through the Workshop (a FFU build, a
  GitHub release, a Nexus/Discord download): it finds the mod inside the archive
  (stripping a GitHub `repo-main\` wrapper), installs every mod in a multi-mod
  zip, routes a **BepInEx** bundle into the game's BepInEx tree and a data mod
  into the Mods folder, then registers each data mod — no unzipping by hand.
  Path-traversal safe, with an overwrite prompt when a mod is already installed.
- **In-place mod management** — every row has a status glyph and an **On**
  checkbox; **double-click** opens the mod's detail panel (status, problems,
  attributed game-log lines, and its conflicts, with the actions right there);
  right-click to **enable / disable** (the game's own `|disabled` marker),
  **ignore** an unregistered folder you keep parked, **remove** a mod (park it
  as `.disabled` or delete it), or **unsubscribe** a Workshop item.
- **Manual control** — drag rows to reorder by hand with live rule-violation
  checks, or apply the suggested order in one click. **Group suggested order
  by category** optionally groups the suggestion for readability.
- **Version & freshness** — the mod table shows each mod's own **Version**
  (`strModVersion` from its `mod_info.json`) and its **Last Updated** date (the
  real Steam Workshop publish time, folder date for local mods), and flags any
  Workshop mod whose published version is newer than your local copy. The game
  pulls the newer files itself on next launch, so it's purely a heads-up.
- **Backups & undo** — every `loading_order.json` write keeps a `.bak` **and** a
  rolling backup history; **Make backup** snapshots on demand, **Restore
  backup** picks any restore point, and **Ctrl+Z / Ctrl+Y** undo/redo covers
  every operation. All writes are atomic and refused while the game is running.
- **Light / dark / system theming**, a **Logs** tab (Ostrasort's own log plus
  the game's `Player.log` and the BepInEx log), and an update checker that runs
  on launch and on demand (**Check for updates**).
- **Game-log correlation** — on each rescan Ostrasort reads the game's own
  `Player.log` / BepInEx log from the last launch, finds the load-time
  error/warning lines, and attributes each to the mod responsible (code mods by
  their BepInEx name, data mods by a JSON filename or a claimed object name it
  can match to exactly one mod). Attributed issues show as a warning and on the
  mod's row; anything it cannot pin to one mod is reported as an honest
  un-attributed summary rather than a wrong guess.
- **Markdown reports & one-click bug reports** — **Copy report** / **Save
  report…** produce a Markdown report of the whole analysis with each Workshop
  mod linked to its Steam page; **Report a bug** opens a pre-filled GitHub issue
  with that report and your environment (OS, game and tool version) filled in.
- **Scriptable** — a console mode drives every capability: `--report`, `--json`
  (machine-readable), `--apply`, `--patch` / `--unpatch`,
  `--install-zip` (install a mod from a `.zip`),
  `--profile-list` / `--profile-save` / `--profile-load`, and `--mods` /
  `--install` to point at a mods folder or saved install on another disk.

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

Objects with no base-game version (two mods adding the *same new* object with
no common ancestor) are **two-way merged** against an empty base: fields only
one mod sets are kept, fields both set differently are yours to resolve. Only a
source mod changing can make such a merge stale (there is no vanilla base for a
game update to invalidate).

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

**Windows only** (64-bit Windows 11). Nothing to install:

1. **Download `Ostrasort.exe`** from the
   [latest release](https://github.com/Valtora/Ostrasort/releases/latest).
2. **Close Ostranauts** (Ostrasort won't write while the game is running).
3. **Double-click `Ostrasort.exe`.** It opens straight to its window (no
   console), finds your install automatically, and lists every mod in load
   order. On first run it offers to **install itself** into
   `%LOCALAPPDATA%\Programs\Ostrasort` and create Desktop / Start Menu shortcuts,
   so you have one fixed place to keep and launch it (optional, dismissible, and
   repeatable any time from the **Install / shortcuts** link).
4. Read the highlighted tabs, **Apply the suggested order** and **Resolve
   conflicts** as needed, then launch the game. Save a **profile** if you want
   to come back to this setup, pick a **theme** to taste, and **Ctrl+Z** undoes
   anything.

Ostrasort checks GitHub for a newer release on startup and whenever you use the
**Check for updates** link. When a newer release exists it pops an update dialog
(**Download Latest Version** opens the Releases page, **Not Now** dismisses) and
reveals an **Update available** button at the top-right that stays as a
persistent reminder. The check stays quiet when you're already up to date,
offline, or rate-limited, always noting the outcome in the Logs tab.

If you've installed Ostrasort (below), just download the new `Ostrasort.exe` and
run it: it recognises it's newer than your installed copy, closes the running
one, replaces the installed binary, refreshes your shortcuts, and restarts. No
manual re-install. Ostrasort also runs as a **single instance** per Windows
session, so a second launch just brings the existing window to the front rather
than opening a rival that could fight it over `loading_order.json`.

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
