# Community rules

`rules/community-rules.json` holds what no mod says about itself: which mods
must load after which, which are known to break each other or a game build,
which have been replaced, and FFU or final-say tags for mods that ship no
marker. Ostrasort ships a copy and fetches the live file from this repository
at most once a day, so a fix here reaches players without a release.

Every entry has to be checked against the mod itself, its Workshop page, or a
report you have reproduced. A wrong rule moves or warns about mods for every
player, so leave it out when in doubt.

## Format

Keys under `mods` are Steam Workshop item ids. A local copy of a Workshop mod
matches through the `strWorkshopID` in its `mod_info.json`.

```jsonc
{
  "version": 1,
  "updated": "2026-10-29",   // bump on every change: the newer of the shipped and fetched copies wins
  "mods": {
    "1234567890": {
      "name": "Example Mod",                 // for people reading the file
      "loadAfter": ["2345678901"],           // must load after these
      "incompatibleWith": [ { "id": "3456789012", "reason": "both replace the same method" } ],
      "brokenOn": ["1.0.1.*"],               // exact version, a prefix ending in .*, or *
      "brokenNote": "crashes when opening the PDA",
      "replacedBy": { "id": "4567890123", "name": "Example Mod Continued", "note": "same author" },
      "ffu": true,                           // loads in the FFU block
      "tier": "late",                        // early, normal or late (final say)
      "note": "shown in the mod's detail panel"
    }
  }
}
```

## How Ostrasort applies it

- A load priority the player pinned is never replaced by `tier`, and an FFU
  tag can only move a mod later.
- `loadAfter` moves a mod behind its dependency within the same load-priority
  band. When the bands keep them apart, Ostrasort says so instead of moving.
- `incompatibleWith`, `brokenOn` and `replacedBy` appear on the Warnings tab
  only when they apply to mods that load on this install.