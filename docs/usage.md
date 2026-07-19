# Using Ostrasort

**Windows only** (64-bit Windows 10/11). Two ways to get it, both per-user with
no admin rights:

- **Installer (recommended).** Download `Ostrasort-win-Setup.exe` from the
  [latest release](https://github.com/Valtora/Ostrasort/releases/latest) and run
  it. It installs Ostrasort for your user, creates **Desktop** and **Start Menu**
  shortcuts, and opens the window. An installed copy keeps itself up to date (see
  below).
- **Portable.** Download `Ostrasort-win-Portable.zip`, unzip it anywhere, and run
  `Ostrasort.exe` from the folder. No install, and it still self-updates in place.

Ostrasort finds your Ostranauts install by itself via the Steam registry and
`libraryfolders.vdf`, whatever drive it's on. (For an unusual install, point it
manually with `--game <path>`.)

On first run Windows SmartScreen may warn because the installer isn't
code-signed — choose *More info → Run anyway*, or unblock it in the file's
Properties.

Ostrasort opens straight to its window (no console window appears). It also
checks GitHub for a newer release **each time it starts**, queried live, so a
release published after your build is picked up on the next launch. When one
exists Ostrasort quietly **downloads it in the background** and reveals a
**Restart to update to vX.Y.Z** button at the top-right. You can also run the
check yourself any time with the **Check for updates** link (top-right), which
reports the result either way. The check stays quiet when you're already up to
date, offline, or rate-limited, but always notes the outcome in the Logs tab.

**Applying an update is one click.** Click the **Restart to update** button (or
run **Check for updates** and confirm): Ostrasort closes, applies the downloaded
update, and reopens on the new version. Nothing to download or run by hand. The
whole install/update mechanism is [Velopack](https://velopack.io); a portable
copy updates the same way.

Ostrasort runs as a **single instance** per Windows session. Two windows could
both write `loading_order.json` and race each other, so a second launch simply
brings the existing window to the front and exits. If another tool edits
`loading_order.json` while Ostrasort is open (for example Ostraplan registering
a ship mod), Ostrasort notices and reloads it rather than overwriting that
change, so nothing you didn't intend gets lost.

> Close Ostranauts first — Ostrasort disables all writes while the game is
> running.

## Where your data lives

Your settings, saved profiles, load-order backups and logs are kept in
`%APPDATA%\Ostrasort` (the roaming profile), separate from the app itself, so
they survive updates, reinstalls, and even an uninstall. Upgrading from a
pre-0.23 build moves this data there automatically the first time the new
version runs. The installed app lives under `%LOCALAPPDATA%\Ostrasort`; to remove
Ostrasort, uninstall it from **Apps & features** (or delete that folder).

## Typical workflow

1. **Close Ostranauts.**
2. **Double-click `Ostrasort.exe`.** It scans your install and lists every
   mod in load order.
3. **Read the health card** at the top. Green means "Your mods are set up
   correctly. Nothing to do." Amber lists the issues in plain language.
4. **Fix automatically** (the button on the card) applies the suggested load
   order and creates or refreshes the compatibility patch in one go. You are
   only asked when a genuine decision is needed; every write keeps a backup
   (`loading_order.json.bak`).
5. **What went wrong?** appears on the card when the game itself reported
   problems from a mod on its last launch — it names the mod and offers the
   obvious next steps.
6. **Undo anything** with Ctrl+Z.
7. **Launch the game** (there's a button) and check the in-game MODS screen.

If something ever looks wrong, the **More…** menu has **Restore backup…**
(the previous `loading_order.json` plus a rolling history of the last **3**
writes, kept in `%APPDATA%\Ostrasort\backups`), **Make backup now** (a
checkpoint on demand), and **Remove compatibility patch…** to take the
generated patch mod out entirely.

## The main window

The **health card** at the top answers "am I OK": green when everything is
fine, amber with a plain-language issue list otherwise, red when the game
itself reported problems from a mod on its last launch. Its buttons — **Fix
automatically**, **What went wrong?**, and **Show me** — are the fastest route
through anything it reports.

The table lists every mod in load order with a **status glyph** (✓ fine,
! needs a look, ✕ problems, ⏸ disabled), an **On** checkbox that toggles the
game's own enable/disable marker, its name, its own **Version**
(`strModVersion`), a **Last Updated** date, and any problems in plain words.
The diagnostic columns (source, class, data-object counts, Workshop ID) are
hidden behind the **Technical columns** toggle. **Double-click any row** for
the mod's detail panel: everything about that mod — status, file problems, the
game-log lines attributed to it, and every conflict it participates in — with
the actions right there. Below the table, a row of tabs:

- **Conflicts** — who claims the same objects and whether the order handles
  it, including field-level analysis of non-shop overrides. It shows **only
  conflicts that need action**, so it reads clean when there is nothing to do.
  Conflicts the load order, FFU, or the game already handle losslessly (for
  example two shops stocking the same items with different quantities) move to
  the **Handled automatically** tab instead of cluttering this one.
  **Double-click any conflict** (the `•` line) to open a read-only,
  side-by-side view of the base game's version and each mod's version, with
  changed fields highlighted and disagreements flagged in red — *Copy JSON*
  puts every version on the clipboard.
- **Handled automatically** — every conflict that needs no action: merged by
  the generated patch, merged at load by FFU or the game, or handled
  losslessly by the load order. Kept on its own tab so the Conflicts tab stays
  focused on real problems.
- **Load order** — the current order and the suggested order side by side.
- **Compatibility patch** — state of the generated conflict patch, with
  *Resolve conflicts & generate patch*, *Rebuild from scratch* and *Remove
  patch* buttons.
- **Warnings** — dead entries, unregistered mods, version lag, broken JSON,
  image overrides, BepInEx problems, and any load errors the **game itself**
  logged at the last launch, attributed to the mod responsible where Ostrasort
  can pin them down (see the Logs tab for the rest).
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

### Installing a mod from a file

**Install from file…** (toolbar) — or just **drag a `.zip` onto the window** —
installs a mod that did not come through the Steam Workshop (a FFU build, a
GitHub release, a Nexus or Discord download). Ostrasort looks inside the
archive, extracts each mod it finds to the right place, and adds it to the load
order for you, so there is no unzipping into `Ostranauts_Data\Mods\` by hand.

- It finds the mod even when the archive wraps it in a folder (GitHub's
  `repo-main\…`), installs **every** mod when the zip holds several, and routes
  a **BepInEx** bundle (Thunderstore/FFU style) into the game's BepInEx tree
  while a plain data mod goes to the Mods folder.
- A confirmation lists exactly what will be installed and where. If a mod is
  **already installed**, it is skipped unless you tick **Overwrite** (which
  cleanly replaces it).
- Each installed **data** mod is registered in the load order automatically;
  code-only plugins are copied (BepInEx auto-loads them, so they need no entry).
- Extraction is path-traversal safe and the game must be closed, like every
  other write. To undo an install, use **Remove mod** on the table row —
  Ctrl+Z reverses only the load-order entry, not the extracted files.

### Working with the list

- **Drag rows** to reorder manually — rule violations are validated before
  anything is applied (core-first is enforced; other issues warn and let you
  confirm).
- **Filter** the table by typing in the filter box as your mod list grows.
- **Right-click** a mod for *Mod details…*, *Open folder*, *Open Steam
  Workshop page*, and *Copy name / Copy ID*; **double-click** a row for the
  detail panel (the folder is one click from there).
- **Disable / Enable** (right-click a registered mod) toggles the game's own
  `|disabled` marker — the same thing the in-game MODS screen does. The entry
  stays in the list (dimmed) but the mod does not load, and it is excluded
  from collision analysis while disabled.
- **Register mod (add to load order)** (right-click an unregistered mod) adds
  it to `loading_order.json` on its own — so the game loads it and the MODS
  screen lists it — without having to apply the whole suggested order. The
  entry lands where the suggestion would put it (just before any FFU block and
  the generated patch; local mods keep their `|edit` marker), any *Ignore*
  preference on it is cleared, and it's undoable with Ctrl+Z.
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
- **Group suggested order by category** toggles a cosmetic core →
  infrastructure → code → data grouping of the suggested order (it does not
  regroup the table itself).
- **Theme** (toolbar) switches between **Light**, **Dark**, and **System**
  (follow the Windows theme, the default); the choice is remembered, and
  System-mode tracks the OS live.
- **Launch game** starts Ostranauts via Steam; the table **rescans itself**
  when you switch back to Ostrasort (e.g. after closing the game).
- **Copy report / Save report** export the full analysis as plain text for
  sharing when helping someone debug their setup.
- The window **remembers its size, position, and selected tab**, and when a
  newer release is on GitHub it is downloaded in the background and a
  **Restart to update** button appears in the header.

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
`%APPDATA%\Ostrasort\profiles`.

## The conflict resolver

When two mods change the same thing and the game would keep only one,
**Resolve conflicts & generate patch** (or **Fix automatically**) opens the
resolver — a short guided wizard:

1. **Summary** — how many things conflict, how many merge automatically with
   nothing lost, and how many need you to choose, listed with friendly names.
2. **One decision per page** ("Decision 2 of 4"), each phrased as a question
   with the choices **side by side**: the **Vanilla (unchanged)** baseline,
   each mod's value, and — for array fields — the **Union of both**. Array
   values are shown as a **diff against vanilla** (what each mod *adds* in
   green, *removes* in red), so the actual disagreement is obvious. Every page
   has a pre-selected **suggested** pick (the later-loaded mod's value, the
   same outcome the game itself would produce), so *Continue* is never
   blocked — and **Choose for me** accepts the suggestion for everything
   remaining. Shop-pool items read as plain *stocks N (chance weight w)*, with
   a *Don't stock this item* option to drop one.
3. **Review** — every outcome listed, the automatically merged items available
   as a collapsed list to review or revert, then **Create compatibility
   patch**.

The result is written as a `Mods\OstrasortPatch` mod that loads after
everything it merges, so no mod's changes are lost. Merged objects are
validated against the game's schemas and flagged if they don't conform —
they're best-effort, so verify them in game. Picks accepted via *Choose for
me* stay marked for review the next time the resolver opens.

### Managing the patch

- **Your decisions are remembered** in the patch's marker file. When a merged
  mod updates, the patch is flagged **stale**; regenerating re-asks only the
  new or changed items.
- **Rebuild patch from scratch** (Compatibility patch tab) discards every
  stored decision — source picks *and* exclusions — and resolves everything
  again from a blank slate.
- **Remove patch** (same tab, or the More… menu) removes the generated mod and
  its load-order entry. You can also **right-click the patch in the mod table
  → Remove generated patch**, the same way you remove any other mod — every
  route does the same guarded delete.
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
Ostrasort.exe --install-zip <p>
                          install a mod from a .zip: extract it into the game (data mods to
                          the Mods folder, BepInEx bundles to the BepInEx tree; zip-slip safe)
                          and register each data mod. Strips a GitHub-style wrapper folder and
                          installs every mod in a multi-mod archive
Ostrasort.exe --overwrite with --install-zip: replace a mod already installed (default: skip)
Ostrasort.exe --profile-list          list saved load-order profiles for this install
Ostrasort.exe --profile-save <name>   save the current load order as a named profile
Ostrasort.exe --profile-load <name>   switch to a saved profile: Replace by default (mods it
                          doesn't list drop from the order; missing mods are skipped and
                          reported), or add --merge to keep current mods it omits, appended
                          at the end. Mutually exclusive with --apply
Ostrasort.exe --tidy      opt-in cosmetic grouping in the suggested order
Ostrasort.exe --no-gui    never open a window: alone it acts like --report; with --patch,
                          contested items fall back to the later-loaded mod's entry,
                          marked for review
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
