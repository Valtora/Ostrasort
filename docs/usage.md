# Using Ostrasort

**Windows only** (64-bit Windows 10/11). Download `Ostrasort.exe` from the
[latest release](https://github.com/Valtora/Ostrasort/releases/latest) — a
single self-contained file, nothing to install — and **double-click it**. It
finds your Ostranauts install by itself via the Steam registry and
`libraryfolders.vdf`, whatever drive it's on. (For an unusual install, point
it manually with `--game <path>`.)

On first run Windows SmartScreen may warn because the exe isn't code-signed —
choose *More info → Run anyway*, or unblock it in the file's Properties.

> Close Ostranauts first — Ostrasort disables all writes while the game is
> running.

## Typical workflow

1. **Close Ostranauts.**
2. **Double-click `Ostrasort.exe`.** It scans your install and lists every
   mod in load order.
3. **Read the tabs.** *Warnings* and *Collisions* highlight anything that
   needs attention; *Order changes* shows what a tidy-up would do and why.
4. **Apply the suggested order** (one button) if you're happy with it — the
   old file is saved as `loading_order.json.bak` first.
5. **Resolve shop/kiosk conflicts** if the *Patch* tab reports any (see
   below).
6. **Undo anything** with Ctrl+Z.
7. **Launch the game** (there's a button) and check the in-game MODS screen.

If something ever looks wrong, **Restore .bak** swaps back to the previous
`loading_order.json`, and **Delete patch** removes the generated patch mod
entirely.

## The main window

The table lists every mod in load order with its name, source
(game/Workshop/local/generated), class, data-object counts, Workshop ID, and
any problems. Below it, four tabs:

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

Nothing is written until you press a button, every `loading_order.json` write
keeps a `.bak`, and all writes are disabled while the game is running.

### Working with the list

- **Drag rows** to reorder manually — rule violations are validated before
  anything is applied (core-first is enforced; other issues warn and let you
  confirm).
- **Filter** the table by typing in the filter box as your mod list grows.
- **Right-click** a mod for *Open folder*, *Open Steam Workshop page*, and
  *Copy name / Copy ID*; **double-click** a row to open its folder.
- **Tidy grouping** toggles a cosmetic core → infrastructure → code → data
  grouping of the suggested order.
- **Launch game** starts Ostranauts via Steam; the table **rescans itself**
  when you switch back to Ostrasort (e.g. after closing the game).
- **Copy report / Save report** export the full analysis as plain text for
  sharing when helping someone debug their setup.
- The window **remembers its size, position, and selected tab**, and a quiet
  link appears in the header when a newer release is on GitHub.

### Undo / redo

Ctrl+Z / Ctrl+Y (or the toolbar buttons) cover every key operation for the
session: applied load orders, patch generation and refreshes — **including
your conflict-resolution decisions, which live inside the patch** — patch
removal, `.bak` restores, and individual drag moves before you apply them.

## The conflict resolver

When two mods change the same thing and the game would keep only one,
**Resolve conflicts & generate patch** opens the resolver. It handles two
kinds of unit:

**Shop pools** — every **contested item** (both mods stock it with different
values) is a row: pick which mod's entry wins, use **Take all from *<mod>***
for a whole column, or **Exclude** to stock it from nobody. Items only one mod
stocks carry over automatically; expand the **carried-over** list to reject
unwanted strays (handy when a mod ships a stale copy of a whole kiosk and you
only want its one new item).

**Objects** — for a conflicting game object, each **contested field** (both
mods set it differently) is a row: pick a mod's value, the **union** (for
array fields), or **Vanilla** to keep the base game's value. Fields only one
mod changed merge automatically; expand the **auto-merged** list to review or
revert any of them.

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
- **Delete patch** removes the generated mod and its load-order entry.
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
NullReferenceExceptions) even though BepInEx's log looks clean.

## Command-line reference

The GUI is the default (double-clicking = no arguments). These flags drive it
from a terminal or a script:

```
Ostrasort.exe             open the GUI (same as double-clicking)
Ostrasort.exe --report    console analysis report; writes nothing
Ostrasort.exe --headless  console only, never any window or key-press wait; alone it
                          acts like --report, combine with the flags below for automation
Ostrasort.exe --apply     write the suggested load order (loading_order.json.bak kept)
Ostrasort.exe --patch     generate/refresh the patch; contested items open the
                          resolver window unless headless
Ostrasort.exe --fresh     with --patch: discard all stored decisions and rebuild
Ostrasort.exe --unpatch   remove the generated patch mod
Ostrasort.exe --tidy      opt-in cosmetic grouping in the suggested order
Ostrasort.exe --no-gui    like --headless but only for the resolver: contested items
                          fall back to the later-loaded mod's entry, marked for review
Ostrasort.exe --game <p>  point at a non-standard install manually
Ostrasort.exe --no-pause  never wait for a key press
Ostrasort.exe --allow-rival-stack
                          write even while Robyn's OstraAutoloader is installed; by
                          default writes are refused there because the autoloader
                          regenerates loading_order.json at every game launch
                          (FFU itself is supported and never blocks)
Ostrasort.exe --disable-autoloader
                          rename the OstraAutoloader DLL(s) to .disabled so Ostrasort
                          can manage the load order (reversible: rename them back)
Ostrasort.exe --version   print the version and exit
```

Console exit codes: `0` = nothing left to do, `2` = actionable suggestions
remain, `1` = error.
