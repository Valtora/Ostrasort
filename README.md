<p align="center"><img src="Ostrasort-logo.png" alt="Ostrasort" height="120"></p>

# Ostrasort

Ostrasort is a mod manager for **Ostranauts** (Blue Bottle Games). You run it
before you launch the game. It reads your whole mod setup, tells you in plain
words whether it is healthy, and tries its best to help you fix things if there are any issues.

<img width="2560" height="1380" alt="Screenshot 2026-07-19 162715" src="https://github.com/user-attachments/assets/9659e490-5edf-4222-889e-dcb324fa96fe" />

## Why use Ostrasort?

Ostranauts loads mods in the order listed in a file called `loading_order.json`,
and when two mods change the same thing the game keeps only one and drops the
other without a word. Nothing tells you it happened. A mod you installed can
just not work properly, an item can vanish from a shop, and the only sign is
that the game does not behave the way the mod pages promised.

Sorting that out by hand means reading JSON, guessing the right load order, and
hoping. Ostrasort does it for you.

## What it does

**It tells you if you are OK.** A health card at the top of the window answers
the one question that matters before you play. Green means there is nothing to
do. Amber lists each issue in plain language with a one-click fix. Red means the
game itself reported a problem from a mod on its last launch, and Ostrasort
names the mod responsible and the next step to take.

**It sorts your load order.** It scans the base game plus every local, Steam
Workshop, and BepInEx mod, works out what each one actually contains, and
suggests a load order that follows the rules, with the smallest change to what
you already have. It also sorts by what each mod changes, not just its file
types, so a mod that owns the new-game and character-creation flow loads last and
its choices win over, say, a mod that only adds a starter ship. You can pin any
mod's load priority yourself if you want the final word.

**It merges conflicts.** When two mods change the
same thing, Ostrasort can build a small compatibility patch that keeps both.
For example, shop inventories are combined so no mod loses its wares, and other game objects
are merged field by field against the base game. Anything the two mods genuinely
disagree on is yours to decide, through a short guided wizard with one plain
question per choice and a suggested answer already selected. The merged results
are best-effort, so it is worth a quick look in game.

Around those three jobs it is a full mod manager. It can

- turn any mod on or off, remove it, or install one from a `.zip` that never
  came through the Workshop (just drag it onto the window),
- save your setup as a named **profile** and switch between setups later,
- manage more than one game install, even on different disks,
- show each mod's version and last-updated date, and flag a Workshop mod with a
  newer copy waiting,
- keep a backup of every change, with full undo and redo,
- read the game's own log after a launch and point each error at the mod that
  caused it,
- and produce a shareable report, or a pre-filled bug report, in one click.

Everything above is also driven from the command line for scripting. The full
walkthrough is in the [documentation](#documentation) below.

## A note on FFU and non-Workshop mods

Ostrasort supports **FFU (Fight for Universe, Beyond Reach)** and other mods
installed outside the Steam Workshop. It reads FFU's own ordering rules and
understands how FFU merges data as the game loads.

That said, Ostrasort recommends **Steam Workshop mods only**. FFU is built
against one exact game version, so installing it stops you taking Ostranauts
updates until FFU catches up, and a mismatch usually breaks the game outright.
Ostrasort supports FFU so it can spot that for you and offer a reversible,
one-click way out. The [documentation](#documentation) covers the details.

> ## Early development, use at your own risk
>
> Ostrasort is in active development and not yet stable. It edits your
> `loading_order.json` and can create a mod folder (`OstrasortPatch`) in your
> game install. It keeps a backup of every load-order write and refuses to run
> while the game is open, but it can still misbehave in ways that break your mod
> setup or a save, or leave you needing to verify or reinstall the game.
>
> The author accepts no responsibility for any damage to your game, mods, or
> saves. Back up your save folder first, and only use Ostrasort if you are
> comfortable recovering your setup yourself. By using it you accept that risk.

## Quick start

**Windows only** (64-bit), no admin rights needed.

1. Download **`Ostrasort-win-Setup.exe`** from the
   [latest release](https://github.com/Valtora/Ostrasort/releases/latest) and
   run it. It installs Ostrasort for your user, adds Desktop and Start Menu
   shortcuts, and keeps itself up to date. (Prefer no install. Download
   **`Ostrasort-win-Portable.zip`** instead, unzip it anywhere, and run
   `Ostrasort.exe`.)
2. **Close Ostranauts.** Ostrasort will not change anything while the game is
   running.
3. **Open Ostrasort.** It finds your install on its own and lists every mod in
   load order.
4. **Read the health card.** Apply the suggested order and resolve conflicts if
   it asks, then launch the game and check the in-game MODS screen.

Your settings, profiles, and backups live in `%APPDATA%\Ostrasort`, so they
survive updates and reinstalls.

> Windows SmartScreen may warn on first run because the installer is not
> code-signed. Choose **More info**, then **Run anyway**.

Full walkthrough in **[docs/usage.md](docs/usage.md)**.

## Documentation

- **[Using Ostrasort](docs/usage.md)** is the full player guide, covering the
  window tour, profiles, the conflict resolver, managing the patch, and the
  command-line reference.
- **[How it works](docs/how-it-works.md)** explains load order in Ostranauts,
  how mods are classified, what collisions Ostrasort detects, profiles, and the
  safety model.
- **[FFU support](docs/ffu.md)** is the deep dive on Fight for Universe: Beyond
  Reach — how it loads mods, how Ostrasort recognises and orders them, and the
  quirks (OstraAutoloader, version pinning, the manual FFU tag).
- **[Building and developing](docs/development.md)** covers the build and
  publish steps and the code layout.

## Licence and disclaimer

Released under the [MIT License](LICENSE). Do what you like with it, just keep
the copyright notice. Provided as is, with no warranty and no liability for
damage to your game, mods, or saves (see the notice above). Not affiliated with
Blue Bottle Games.
