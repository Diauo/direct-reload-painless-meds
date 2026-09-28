# Direct Reload & Painless Meds

**A lightweight QoL duo for SPT — Stalker-style reloading plus faster, penalty-free medical care.**

Tired of magazine Tetris mid-fight? **Direct Reload** lets you feed the magazine on your gun straight from the loose rounds you carry — no spare magazines required. And when things go wrong, **Painless Meds** speeds up medical care, removes the max-HP surgery penalty, and keeps animations in sync.

---

## Features

### Reload
- **Direct reload (Stalker-style):** press R with compatible loose ammo in reach (pockets / rig) — one reload animation plays and the magazine on your gun is filled from your loose rounds. **No spare magazine needed.**
- **Covers standard magazine weapons** *and* **detachable-mag weapons with direct-feed support** (e.g. SKS-A style setups).
- **Pure internal-magazine weapons (Mosin, fixed-mag SKS) intentionally keep the vanilla one-by-one loading** — a deliberate realism/softcore balance point, not a bug.
- **Instant magazine ops:** filling or emptying magazines from your inventory completes instantly — great for stripping ammo off dead enemies.
- **Safe by design:** if there's no reachable ammo or no free slot to stow the current magazine, the reload is refused (with a toggleable native-style warning). Nothing is cheated in.

### Medical
- **Faster medical use** (server-side): all meds are faster; multiplier configurable.
- **Painless surgery:** surgical kits (CMS / Surv12 / Sanitar) restore blacked-out limbs **without the max-HP penalty**.
- **Batch surgery:** one surgical-kit use treats **all** blacked-out limbs (1 durability per limb).
- **Synchronized animations:** med/surgery animation speed matches the faster use time.
- **Drug buff durations (optional toggle):** positive effect durations ×2.

### Fika
Fully multiplayer-safe. In Fika sessions the **host mirrors the reload fill/chambering on its own copy of your character**, keeping client and host in sync (verified on a dedicated headless setup).

---

## Requirements
- **SPT 4.0.13** (EFT 0.16.9.40087)
- BepInEx (bundled with SPT)

## Installation
Extract the release archive into your **SPT root** (it merges `BepInEx/` and `SPT/`):
- Client plugin: `BepInEx/plugins/RZDirectReload/RZDirectReload.dll`
- Server mod: `SPT/user/mods/RZDirectReload/` (dll + config.json)

## Configuration
**Client (F12 menu → "Direct Reload & Painless Meds"):**

| Option | Default | Notes |
|---|---|---|
| Enable Direct Reload | on | the core feature |
| Show Reload Failure Notification | on | native warning on transaction failure |
| Sync Med Animation Speed | on | match animation to server use time |
| Animation Speed Scale | 2.0 | = 1 / server `medUseTime.multiplier` |
| Enable Batch Surgery | on | all limbs in one use |
| Instant Magazine Load / Unload | on | instant mag ops |
| Debug Logging | off | troubleshooting |

**Server (`SPT/user/mods/RZDirectReload/config.json`):**
- `surgery.keepMaxHealthPercent` — 100 = no penalty (default)
- `medUseTime.multiplier` — 0.5 = half time (default), `minSeconds` floor
- `drugBuffs.multiplier` — positive buff duration ×2 (default)

## Notes
- Weapon reload behavior at a glance:

| Weapon type | Behavior |
|---|---|
| Standard magazine weapons | direct reload (one animation, no spare mag) |
| Detachable mag + direct-feed support (EXWIRS) | direct reload |
| Pure internal magazine (Mosin / fixed-mag SKS) | vanilla one-by-one loading |

---

## Building from source

Requirements: **.NET SDK** (server targets `net9.0`, client targets `netstandard2.1`) and an **SPT 4.0.13 installation** to copy reference assemblies from.

1. Copy reference DLLs (not committed to this repository — game/SPT binaries are not redistributed here):
   - **Client** → `direct-reload/client/libs/`:
     `0Harmony.dll`, `BepInEx.dll` (from `BepInEx/core`), `spt-reflection.dll` (from `BepInEx/plugins/spt`), `Assembly-CSharp.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `Comfort.dll` (from `EscapeFromTarkov_Data/Managed`)
   - **Server** → `libs/` (repository root):
     `SPTarkov.Server.Core.dll`, `SPTarkov.DI.dll`, `SPTarkov.Common.dll`, `SemanticVersioning.dll` (from your SPT server installation)
2. Build:

   ```bash
   ./build.sh
   # or: dotnet build -c Release inside direct-reload/client and direct-reload/server
   ```

Outputs:
- Client: `direct-reload/client/bin/Release/netstandard2.1/RZDirectReload.dll`
- Server: `direct-reload/server/bin/Release/net9.0/RZDirectReload.Server.dll`

## Project layout
- `direct-reload/client/` — BepInEx client plugin (reload flow, instant mag ops, med animation sync, batch surgery)
- `direct-reload/server/` — SPT server mod (surgery / med time / drug buff config)

## License
MIT — see [LICENSE](LICENSE).
