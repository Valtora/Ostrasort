# Using Ostrasort

**Windows only** (64-bit Windows 10/11). Download `Ostrasort.exe` from the
[latest release](https://github.com/Valtora/Ostrasort/releases/latest) — a
single self-contained file, nothing to install — and **double-click it**. It
finds your Ostranauts install by itself via the Steam registry and
`libraryfolders.vdf`, whatever drive it's on. (For an unusual install, point
it manually with `--game <path>`.)

On first run Windows SmartScreen may warn because the exe isn't code-signed —
choose *More info → Run anyway*, or unblock it in the file's Properties.

Ostrasort opens straight to its window (no console window appears). It also
checks GitHub for a newer release **each time it starts**, queried live, so a
release published after your build is picked up on the next launch. When one
exists, Ostrasort pops an update dialog with **Download Latest Version** (opens
the Releases page) and **Not Now**, and reveals an **Update available: vX.Y.Z**
button at the top-right that stays as a reminder after you dismiss the dialog.
You can also run the check yourself any time with the **Check for updates** link
(top-right), which reports the result either way. The check stays quiet when
you're already up to date, offline, or rate-limited, but always notes the
outcome in the Logs tab.

> Close Ostranauts first — Ostrasort disables all writes while the game is
> running.

## Typical workflow

1. **Close Ostranauts.**
2. **Double-click `Ostrasort.exe`.** It scans your install and lists every
   mod in load order.
3. **Read the tabs.** *Warnings* and *Collisions* highlight anything that
   needs attention; *Order changes* shows what a tidy-up would do and why.
4. **Apply Suggested Fixes** (one button) if you're happy with it — the
   old file is saved as `loading_order.json.bak` first.
5. **Resolve shop/kiosk conflicts** if the *Patch* tab reports any (see
   below).
6. **Undo anything** with Ctrl+Z.
7. **Launch the game** (there's a button) and check the in-game MODS screen.

If something ever looks wrong, **Restore backup…** offers the previous
`loading_order.json` (`.bak`) plus a rolling history of the last **3** writes
(kept in `%LOCALAPPDATA%\Ostrasort\backups`), and **Delete patch** removes the
generated patch mod entirely. **Make backup** adds the current file to that
history on demand — a handy checkpoint before hand-editing or experimenting.

## The main window

The table lists every mod in load order with its name, source
(game/Workshop/local/generated), class, data-object counts, Workshop ID, and
any problems. Below it, a row of tabs:

- **Collisions** — who claims the same objects and whether the order handles
  it, including field-level analysis of non-shop overrides. Only **active**
  conflicts appear here.
- **Resolved collisions** — conflicts the generated patch already merges away,
  kept on their own tab so the Collisions tab shows only what still needs
  attention.
- **Order changes** — the current order and the suggested order side by side,
  applied with one button.
- **Patch** — state of the generated conflict patch, with *Rebuild from
  scratch* and *Delete patch* buttons.
- **Warnings** — dead entries, unregistered mods, version lag, broken JSON,
  image overrides, BepInEx problems.
- **Logs** — a record of everything Ostrasort has done (applies, patches,
  restores, undo/redo), and a viewer for the game's own logs (Player.log and
  the BepInEx log) so you can see what happened after a launch. Copy or open
  any of them.
- **Profiles** — save named load orders and switch between them (see
  [Profiles](#profiles) below).

Above the table, the **Installation** picker chooses which game install
Ostrasort manages. **Auto-detect** finds your Steam install and its mods folder
(honouring the game's own `strPathMods`); **Manage…** saves named installs, each
with its own **game folder** and **mods folder** — which can live on different
disks — and switches between them in one click. Each install keeps its own load
order, profiles and backups.

Nothing is written until you press a button, every `loading_order.json` write
keeps a `.bak`, and all writes are disabled while the game is running.

### Working with the list

- **Drag rows** to reorder manually — rule violations are validated before
  anything is applied (core-first is enforced; other issues warn and let you
  confirm).
- **Filter** the table by typing in the filter box as your mod list grows.
- **Right-click** a mod for *Open folder*, *Open Steam Workshop page*, and
  *Copy name / Copy ID*; **double-click** a row to open its folder.
- **Disable / Enable** (right-click a registered mod) toggles the game's own
  `|disabled` marker — the same thing the in-game MODS screen does. The entry
  stays in the list (dimmed) but the mod does not load, and it is excluded
  from collision analysis while disabled.
- **Register mod (add to load order)** (right-click an unregistered mod) adds
  it to `loading_order.json` on its own — so the game loads it and the MODS
  screen lists it — without having to apply the whole suggested order. The
  entry lands where *Apply Suggested Fixes* would put it (just before any FFU
  block and the generated patch; local mods keep their `|edit` marker), any
  *Ignore* preference on it is cleared, and it's undoable with Ctrl+Z.
- **Ignore (leave unregistered)** (right-click an unregistered local mod)
  stops the permanent warning and the "add it" suggestion for a folder you
  deliberately keep parked. *Stop ignoring* brings both back. The preference
  is remembered per install.
- **Remove mod (park or delete)…** (right-click a local mod) parks its folder
  as `*.disabled` (reversible — rename it back) or deletes it permanently,
  and drops its load-order entry either way. Deleted files are **not**
  restored by Ctrl+Z; parking is the safe default.
- **Unsubscribe on Steam…** (right-click a Workshop mod) opens the item's
  page in the Steam client — subscriptions belong to Steam, so unsubscribing
  is its one-click button there; Steam then removes the files and Ostrasort's
  next rescan suggests pruning the dead entry.
- **Organise Mods by Class** toggles a cosmetic core → infrastructure → code →
  data grouping of the suggested order.
- **Theme** (toolbar) switches between **Light**, **Dark**, and **System**
  (follow the Windows theme, the default); the choice is remembered, and
  System-mode tracks the OS live.
- **Launch game** starts Ostranauts via Steam; the table **rescans itself**
  when you switch back to Ostrasort (e.g. after closing the game).
- **Copy report / Save report** export the full analysis as plain text for
  sharing when helping someone debug their setup.
- The window **remembers its size, position, and selected tab**, and when a
  newer release is on GitHub an update dialog appears on launch alongside a
  persistent **Update** button in the header.

### Undo / redo

Ctrl+Z / Ctrl+Y (or the toolbar buttons) cover every key operation for the
session: applied load orders, patch generation and refreshes — **including
your conflict-resolution decisions, which live inside the patch** — patch
removal, `.bak` restores, and individual drag moves before you apply them.

## Profiles

A **profile** is a named load order you can save and switch back to — one for a
vanilla-plus run, one for an FFU stack, one for testing a single mod. The
**Profiles** tab lists the profiles saved for this install:

- **Save current…** snapshots the current load order — each mod, its position,
  and its enabled/`|disabled` state — under a name you choose. It captures your
  mods only, not the generated Ostrasort Patch (that's rebuilt per setup) or the
  global ignore / `aIgnorePatterns` lists (those stay put across profiles).
- **Switch…** shows the current order and the profile side by side, with a
  choice of how to apply it:
  - **Replace** (default) — the profile becomes your whole load order; mods it
    doesn't list drop out of the order (their files stay on disk, and any
    unregistered local mod is flagged as usual). Use this to move cleanly
    between different setups.
  - **Merge-append** — apply the profile, then keep any currently registered
    mods it doesn't mention, added at the end. Use this to layer a profile on
    top of what you already have.

  Mods in the profile that are no longer installed are skipped and reported —
  switching never fails on a missing mod.
- **Rename** / **Delete** manage saved profiles; deleting one never touches any
  mod.

Switching writes through the same guarded ritual as everything else — a `.bak`
and a rolling backup are kept, it's undoable with **Ctrl+Z**, and it's disabled
while the game is running or the OstraAutoloader manages the install. After a
switch, Ostrasort re-checks whether the new setup needs a conflict patch and
lights up the **Patch** tab if so. Profiles are stored per install under
`%LOCALAPPDATA%\Ostrasort\profiles`.

## The conflict resolver

When two mods change the same thing and the game would keep only one,
**Resolve conflicts & generate patch** opens the resolver. Each conflict is a
titled card (the object's friendly type and name, and which mods edit it), and
every contested decision shows the choices **side by side** so you can compare
them at a glance rather than reading raw JSON. It handles two kinds of unit:

**Shop pools** — every **contested item** (both mods stock it with different
values) is a card of columns: each mod's entry (shown as a plain *count /
weight*) plus **Stock from nobody** to drop it. Use **Take all from *<mod>***
to claim a whole column at once. Items only one mod stocks carry over
automatically; expand the **carried-over** list to reject unwanted strays
(handy when a mod ships a stale copy of a whole kiosk and you only want its one
new item).

**Objects** — for a conflicting game object, each **contested field** is a card
with a plain-English label (the raw field name shown beneath it) and one column
per choice: the **Vanilla (unchanged)** baseline, each mod's value, and — for
array fields — the **Union of both**. Array values are shown as a **diff
against vanilla** (what each mod *adds* in green, *removes* in red), so the
actual disagreement is obvious. Fields only one mod changed merge
automatically; expand the **auto-merged** list to review or revert any of them.

The result is written as a `Mods\OstrasortPatch` mod that loads last, so no
mod's changes are lost. Merged objects are validated against the game's
schemas and flagged if they don't conform — they're best-effort, so verify
them in game.

### Managing the patch

- **Your decisions are remembered** in the patch's marker file. When a merged
  mod updates, the patch is flagged **stale**; regenerating re-asks only the
  new or changed items.
- **Rebuild patch from scratch** (Patch tab) discards every stored decision —
  source picks *and* exclusions — and resolves everything again from a blank
  slate.
- **Delete patch** removes the generated mod and its load-order entry. You can
  also **right-click the patch in the mod table → Remove generated patch**, the
  same way you remove any other mod — both routes do the same guarded delete.
- Both are undoable. The `OstrasortPatch` folder is wholly owned by Ostrasort
  — don't edit it by hand; it's safe to delete.

## FFU / Thunderstore mod stacks

**FFU (Fight for Universe: Beyond Reach) is supported.** When Ostrasort detects
FFU on your install (its MonoMod patches in `BepInEx\monomod\`, or FFU-style
mods) it shows a dismissable **banner** and applies FFU's own ordering rules to
the suggestion:

- all **non-FFU mods load first** (the Ostrasort Patch closes that block);
- **Minor Fixes Plus** — mandatory for FFU — leads the FFU block;
- FFU mods are **dependency-sorted** per their `Autoload.Meta.toml`;
- FFU **"Patch" mods** are placed immediately after the mod they patch, with a
  reminder that they apply once and should then be removed from the list;
- FFU data mods living under `BepInEx\plugins\` are registered by absolute
  path, exactly how the game expects them;
- conflict analysis knows that with FFU installed the game **merges same-name
  objects field-by-field at load**, so disjoint edits are reported as "nothing
  lost" instead of false conflicts, and `--ADD--`/`--DEL--` array edits are
  never folded into a patch.

The one exception is Robyn's **OstraAutoloader** plugin: it regenerates
`loading_order.json` from scratch at **every game launch**, dropping local mods
(and `|edit`/`|disabled` markers) it doesn't manage — anything Ostrasort wrote
would be undone. Running both is unsupported, and the autoloader has been
inactive for about a year while Ostrasort is actively maintained and covers
everything it does. While the autoloader DLL is installed Ostrasort runs
**analysis-only**: the banner explains and offers a one-click
**"Disable OstraAutoloader"** button (console: `--disable-autoloader`) that
renames the autoloader DLL(s) to `.disabled` — fully reversible, rename them
back to re-enable. r2modman users should disable it in their profile instead.
After disabling, Ostrasort manages the full order, FFU included. (Console
override for the write refusal without disabling: `--allow-rival-stack`, at
your own risk.)

One more FFU-specific safeguard: FFU's MonoMod DLLs are compiled against **one
specific game build**. If the installed FFU targets a different game version
than the one on disk (detected via Minor Fixes Plus's `strGameVersion`),
Ostrasort raises an **FFU VERSION MISMATCH** warning — a mismatched FFU
typically breaks the game outright (broken main menu, endless
NullReferenceExceptions) even though BepInEx's log looks clean. This also
means FFU **pins your game version**: you can't safely take game updates until
FFU updates too.

Because of that, **Ostrasort's recommendation is to use Steam Workshop mods
only** — the banner carries the full reasoning, and offers a **"Remove FFU"**
button (console: `--remove-ffu`) covering every FFU MonoMod DLL and the Minor
Fixes Plus mod. A small dialog asks how: **Park (reversible)** renames them to
`.disabled` (rename back to restore), **Delete files** removes them outright
for a clutter-free install. Either way they are unregistered from the load
order (a `.bak` is kept), and other FFU-dependent mods are left in place and
flagged in Warnings so you can review them yourself. The same park-or-delete
choice applies to the **Disable OstraAutoloader** button.

## Command-line reference

The GUI is the default (double-clicking = no arguments). These flags drive it
from a terminal or a script:

```
Ostrasort.exe             open the GUI (same as double-clicking)
Ostrasort.exe --report    console analysis report; writes nothing
Ostrasort.exe --headless  console only, never any window or key-press wait; alone it
                          acts like --report, combine with the flags below for automation
Ostrasort.exe --json      like --headless but prints ONE machine-readable JSON document
                          (mods, collisions, patch state, warnings, suggested order);
                          combines with --apply/--patch/--unpatch, same exit codes
Ostrasort.exe --apply     write the suggested load order (loading_order.json.bak kept)
Ostrasort.exe --patch     generate/refresh the patch; contested items open the
                          resolver window unless headless
Ostrasort.exe --fresh     with --patch: discard all stored decisions and rebuild
Ostrasort.exe --unpatch   remove the generated patch mod
Ostrasort.exe --profile-list          list saved load-order profiles for this install
Ostrasort.exe --profile-save <name>   save the current load order as a named profile
Ostrasort.exe --profile-load <name>   switch to a saved profile: Replace by default (mods it
                          doesn't list drop from the order; missing mods are skipped and
                          reported), or add --merge to keep current mods it omits, appended
                          at the end. Mutually exclusive with --apply
Ostrasort.exe --tidy      opt-in cosmetic grouping in the suggested order
Ostrasort.exe --no-gui    like --headless but only for the resolver: contested items
                          fall back to the later-loaded mod's entry, marked for review
Ostrasort.exe --game <p>  point at a non-standard install manually
Ostrasort.exe --mods <p>  point at the mods folder that holds loading_order.json — for a
                          mods folder on a different disk from the game; default is the
                          game's own strPathMods, else <game>\Ostranauts_Data\Mods
Ostrasort.exe --install <name>
                          use a saved installation's game + mods folders (manage saved
                          installs in the GUI); --game/--mods still override per slot
Ostrasort.exe --allow-rival-stack
                          write even while Robyn's OstraAutoloader is installed; by
                          default writes are refused there because the autoloader
                          regenerates loading_order.json at every game launch
                          (FFU itself is supported and never blocks)
Ostrasort.exe --disable-autoloader
                          park the OstraAutoloader DLL(s) as .disabled so Ostrasort
                          can manage the load order (reversible: rename them back)
Ostrasort.exe --remove-ffu
                          remove FFU Core (FFU MonoMod DLLs + Minor Fixes Plus parked
                          as .disabled and unregistered) — Ostrasort recommends
                          Steam Workshop mods only
Ostrasort.exe --delete    with --disable-autoloader / --remove-ffu: delete the
                          files outright instead of parking them as .disabled
Ostrasort.exe --version   print the version and exit
```

Console exit codes: `0` = nothing left to do, `2` = actionable suggestions
remain, `1` = error.
