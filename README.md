# Direct Reload & Painless Meds

**A lightweight QoL duo for SPT — Stalker-style reloading plus faster, penalty-free medical care.**

Tired of magazine Tetris mid-fight? **Direct Reload** lets you feed the magazine on your gun straight from the loose rounds you carry — no spare magazines required. And when things go wrong, **Painless Meds** speeds up medical care, removes the max-HP surgery penalty, and keeps animations in sync.

---

## Versions & compatibility

| SPT version | Mod version | Client artifact | Server artifact |
|---|---|---|---|
| **SPT 4.1.x** (EFT 0.16.9.5) | **v1.2.0** | `direct-reload-41/client` | `direct-reload-41/server` |
| **SPT 4.0.x** (EFT 0.16.9.40087) | **v1.2.0** | `direct-reload/client` | `direct-reload/server` |

Both lines are maintained in this repository — pick the source directory matching your SPT version.

## Features

### Reload
- **Direct reload (Stalker-style):** press R with compatible loose ammo in reach (pockets / rig) — one reload animation plays and the magazine on your gun is filled from your loose rounds. **No spare magazine needed.**
- **Covers standard magazine weapons** *and* **detachable-mag weapons with direct-feed support** (e.g. SKS-A style setups).
- **Pure internal-magazine weapons (Mosin, fixed-mag SKS) intentionally keep the vanilla one-by-one loading** — a deliberate realism/softcore balance point, not a bug.
- **Instant magazine ops:** filling or emptying magazines from your inventory completes instantly — great for stripping ammo off dead enemies.
- **Safe by design:** if there's no reachable ammo or no free slot to stow the current magazine, the reload is refused (with a toggleable native-style warning). Nothing is cheated in.

### FOV
- **Unlock the FOV cap:** the in-game base-FOV setting is no longer capped at 75 — the slider and the value clamp use a configurable range instead (default 50–110). Toggleable in the F12 menu.

### Medical
- **Faster medical use** (server-side): all meds are faster; multiplier configurable.
- **Painless surgery:** surgical kits (CMS / Surv12 / Sanitar) restore blacked-out limbs **without the max-HP penalty**.
- **Batch surgery:** one surgical-kit use treats **all** blacked-out limbs (1 durability per limb).
- **Synchronized animations:** med/surgery animation speed matches the faster use time.
- **Drug buff durations (optional toggle):** positive effect durations ×2.

### Fika
Fully multiplayer-safe. In Fika sessions the **host mirrors the reload fill/chambering on its own copy of your character**, keeping client and host in sync (verified with a dedicated headless setup on 4.0 and a local host session on 4.1).

---

## Requirements
- **SPT 4.1.x** (EFT 0.16.9.5) → use v1.1.0 artifacts
- **SPT 4.0.x** (EFT 0.16.9.40087) → use v1.0.2 artifacts
- BepInEx (bundled with SPT)

## Installation
Extract the release archive into your **SPT root** (it merges the directory layout):

**SPT 4.1.x:**
- Client plugin: `BepInEx/plugins/RZDirectReload/RZDirectReload.dll`
- Server mod: `SPT_Runtime/user/mods/RZDirectReload/` (dll + config.json)

**SPT 4.0.x:**
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
| Unlock FOV Range | on | base FOV selectable beyond the vanilla 75 cap |
| Min / Max FOV | 50 / 110 | selectable base-FOV range |
| Debug Logging | off | troubleshooting |

**Server (`user/mods/RZDirectReload/config.json`):**
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

- **SPT 4.1 note:** the 4.1 softcore reload flags the stowed magazine as temporarily known during the reload transaction, mirroring the game's own drag operations — required by the 4.1 inventory observer system (`UnknownItemError` otherwise).

---

## Building from source

Requirements: **.NET SDK** and an **SPT installation matching your target line** to copy reference assemblies from (game/SPT binaries are not redistributed in this repository).

### SPT 4.0.x — `direct-reload/`

1. Copy reference DLLs:
   - **Client** → `direct-reload/client/libs/`:
     `0Harmony.dll`, `BepInEx.dll` (from `BepInEx/core`), `spt-reflection.dll` (from `BepInEx/plugins/spt`), `Assembly-CSharp.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `Comfort.dll`, `Sirenix.Serialization.dll`, `Sirenix.Utilities.dll`, `Sirenix.Serialization.Config.dll` (from `EscapeFromTarkov_Data/Managed`)
   - **Server** → `libs/` (repository root):
     `SPTarkov.Server.Core.dll`, `SPTarkov.DI.dll`, `SPTarkov.Common.dll`, `SemanticVersioning.dll` (from your SPT server installation)
2. Build: `./build.sh` (or `dotnet build -c Release` inside each project).

### SPT 4.1.x — `direct-reload-41/`

1. Copy reference DLLs:
   - **Client** → `direct-reload-41/client/libs/`:
     `0Harmony.dll`, `BepInEx.dll` (from `BepInEx/core`), `spt-reflection.dll` (from `BepInEx/plugins/spt`), `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `Comfort.dll`, `Sirenix.Serialization.dll`, `Sirenix.Utilities.dll`, `Sirenix.Serialization.Config.dll` (from `EscapeFromTarkov_Data/Managed`), and **`hollowed.dll`** — the deobfuscated `Assembly-CSharp` contract used by all SPT 4.1 client mods:
     ```bash
     curl -L -o direct-reload-41/client/libs/hollowed.dll \
       https://raw.githubusercontent.com/sp-tarkov/modules/main/project/Shared/Hollowed/hollowed.dll
     ```
   - **Server:** the 4.1 server project restores `SPTarkov.Server.Core` from NuGet — no manual DLL copies needed.

2. Build: `./build.sh` (or `dotnet build -c Release` inside each project).

Outputs:
- 4.0 client: `direct-reload/client/bin/Release/netstandard2.1/RZDirectReload.dll`
- 4.0 server: `direct-reload/server/bin/Release/net9.0/RZDirectReload.Server.dll`
- 4.1 client: `direct-reload-41/client/bin/Release/netstandard2.1/RZDirectReload.dll`
- 4.1 server: `direct-reload-41/server/bin/Release/RZDirectReload.Server/RZDirectReload.Server.dll`

## Project layout
- `direct-reload/` — SPT 4.0 line (client + server)
- `direct-reload-41/` — SPT 4.1 line (client + server)
- `build.sh` — builds both lines

## Credits & Acknowledgements

- **[SoftCoreMeds](https://github.com/QuietPillsHere/SPT-SoftCoreMeds)** by Doug (MIT License) — the batch-surgery feature in this mod was adapted from their surgical-kit implementation. Thanks for the open-source work!
- **Fontaine's FOV Fix** ([space-commits/SPT-FOV-Fix](https://github.com/space-commits/SPT-FOV-Fix)) — the FOV-unlock mechanism (re-binding the settings slider and replacing the value clamp) was implemented after studying their approach; independently re-implemented and ported to 4.1. Thanks for the reference points!

## License
MIT — see [LICENSE](LICENSE).
