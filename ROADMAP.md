# Ostrasort roadmap

Planned features, roughly in order of intent. Correctness fixes don't live
here — they ship as soon as they're found. Shipped items move to the release
notes and get deleted from this file.

## Flagship: Mod profiles

Named load-order sets you can save and switch between — the signature feature
of every mature mod manager (MO2/Vortex), and the natural next step now that
apply/undo/backups exist:

- **Save** the current `aLoadOrder` (including `|edit`/`|disabled` markers and
  the ignore list) as a named profile: *"vanilla-plus"*, *"FFU run"*,
  *"testing ShipsWater"*.
- **Switch** profiles with one guarded write (same ritual: game-closed check,
  `.bak`, rolling backup, atomic write, undoable).
- **Diff** a profile against the current order before applying, reusing the
  Order-changes side-by-side view.
- Store profiles in `%LOCALAPPDATA%\Ostrasort\profiles\`, keyed per install
  like the backups. A profile referencing mods that no longer exist applies
  what it can and reports the rest — never a hard failure.
- Mind the interaction with the OstrasortPatch: switching profiles can change
  which conflicts exist, so a switch should re-run the staleness inspection
  and surface the patch tab if the patch no longer matches.

## Especially desired: Dark mode

.NET's WPF Fluent theming (`ThemeMode`) can supply the chrome, but the app
draws its own severity colours (hardcoded `Brushes.Black`/`Gray`/`Green`/
`Firebrick` etc. in `MainWindow.xaml.cs`, plus the banner backgrounds), so
the real work is a brush audit:

- Move all severity brushes into theme-aware resources (light + dark
  palettes), including the FFU banner backgrounds and the row dim/warn/bad
  colours.
- Respect the system theme by default; a manual toggle persisted in
  `GuiSettings` for people who want to pin it.
- Check every hand-built control (resolver rows, take-all buttons, restore
  menu) against both palettes.

## Two-way merge for mod-added objects

Two mods adding the *same new* object (no core ancestor) is currently
reported but left to load order. Treat an empty object as the base and reuse
the existing `ObjectMerge` engine + resolver: fields present in only one
version merge automatically, fields both set differently go to the resolver.
Schema-validate the result like every other merged object. Niche until two
popular content mods actually collide this way, which is why it sits below
profiles.

## Collision drill-down (side-by-side diff)

The Collisions tab summarises field notes but can't show the actual values.
Add a per-collision expander (or double-click) that renders each claimant's
JSON side by side with changed fields highlighted — the data already exists
in `FieldDiff`/`ObjectMerge`; this is presentation only.

## Friendly loot-pool names

`AAK1LootKioskOKLG` means nothing to a player. Cross-reference the loot pool
to the condowner(s) that use it (and their station/room context where
derivable) so the most common conflict class reads as *"OKLG kiosk
inventory"* instead of an internal id. Needs a reverse index over core
condowners' loot references; cache it alongside the core index.

## Player.log correlation

The Logs tab already tails Player.log. After a launch, parse it for mod-load
`ERROR`/`WARNING` lines, attribute each to the responsible mod (the loader
logs which folder it was reading), and surface them as row notes / warnings
on the next rescan — closing the loop between "Ostrasort says this is fine"
and "the game actually loaded it".

## Single-instance mutex

Two Ostrasort instances can both write `loading_order.json`. A named mutex at
startup: second instance either focuses the first window or opens read-only.
Trivial effort, rare in practice — batched with whatever release comes next.

---

## Not planned (accepted compromises)

- **In-app Workshop unsubscribe**: a real `ISteamUGC.UnsubscribeItem` call
  needs an authenticated Steamworks session under the game's own app id —
  fragile impersonation territory. The right-click *Unsubscribe on Steam…*
  action (opens the item's page in the Steam client, one click there) is the
  supported path.
- **Self-update** (download + swap the exe): SmartScreen re-warns on every
  unsigned update anyway; the in-app Update button → Releases page stays.
- **Distribution polish** (code signing, winget/Scoop manifests): costs money
  or upkeep disproportionate to the audience for now.
- **NativeAOT / console-only split**: WPF can't AOT-compile, and maintaining a
  second build to shave megabytes isn't worth it.
