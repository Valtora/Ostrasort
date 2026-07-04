<p align="center"><img src="Ostrasort-logo.png" alt="Ostrasort" height="120"></p>

# Ostrasort

A load-order and conflict manager for **Ostranauts** (Blue Bottle Games).
Run it before you launch the game — it reads your whole mod setup and helps
you get it into a working state.

Ostrasort is built to do two things:

1. **Figure out the best mod load order.** It scans core plus every local and
   Workshop-subscribed mod, classifies each one, and suggests a
   `loading_order.json` that satisfies the rules (BepInEx after core, shop
   pools ordered correctly, dead entries pruned, unregistered mods surfaced)
   — with minimal churn to what you already have.
2. **Resolve mod conflicts.** When two mods change the same thing, the game
   keeps only one and silently drops the other's changes. Ostrasort merges
   them into a compatibility patch that keeps both — **shop/kiosk inventories**
   (per-item union) and **game objects** (field-by-field, with the base game
   as the common ancestor) — while **you** decide anything the mods genuinely
   disagree on.

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

1. **Close Ostranauts** (Ostrasort won't write while the game is running).
2. Download and **double-click `Ostrasort.exe`.** It finds your install
   automatically and lists every mod in load order.
3. Read the highlighted tabs, **Apply the suggested order** and **Resolve
   conflicts** as needed, then launch the game. Ctrl+Z undoes anything.

Full walkthrough: **[docs/usage.md](docs/usage.md)**.

## Documentation

- **[Using Ostrasort](docs/usage.md)** — the full player guide: the window
  tour, the conflict resolver, managing the patch, and the command-line
  reference.
- **[How it works](docs/how-it-works.md)** — load order in Ostranauts, how
  mods are classified, what collisions Ostrasort detects, and the safety
  model.
- **[Building & developing](docs/development.md)** — build/publish, code
  layout, and the roadmap.

## Licence / disclaimer

Provided as-is, with no warranty and no liability for damage to your game,
mods, or saves (see the notice above). Not affiliated with Blue Bottle Games.
