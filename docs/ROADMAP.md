# Ostrasort roadmap

Planned features, roughly in order of intent. Correctness fixes don't live
here — they ship as soon as they're found. Shipped items move to the release
notes and get deleted from this file.

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
