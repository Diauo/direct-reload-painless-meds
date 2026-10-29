# Acknowledgements & provenance

Direct Reload & Painless Meds is MIT-licensed (see `LICENSE`). Some subsystems were built after studying other people's work; this file records **what came from where and where it lives in this tree**, so the credit is easy to check.

## SoftCoreMeds — Doug (MIT)

- Source: <https://github.com/QuietPillsHere/SPT-SoftCoreMeds>
- Referenced: the multi-limb ("batch") surgery approach — hook the game's body-part selection, then restore every blacked-out limb from the restore event.
- In this repo: `direct-reload-41/client/Patches/BatchSurgeryPatch.cs` (adapted and re-implemented for the 4.1 line).

## Fontaine's FOV Fix — space-commits (MIT)

- Source: <https://github.com/space-commits/SPT-FOV-Fix>
- Referenced: the FOV-unlock mechanism (re-binding the settings slider, replacing the value clamp).
- In this repo: `direct-reload-41/client/Patches/FovUnlockPatch.cs` (independently re-implemented for 4.1).

## txvicious — companion packet in issue #1 (MIT)

- Source: <https://github.com/Diauo/direct-reload-painless-meds/issues/1>
- Referenced:
  1. **Per-reload tickets** — one pending selection / created-ID store per reload instead of one per player, so two reloads in flight cannot mix or overwrite each other.
  2. **Zero-new-ID commits must still be sent** — bulk stack moves/merges create no new item IDs, and a "only send when IDs exist" gate silently skips mirroring those commits.
  3. Their `KNOWN-ISSUES.md` flagged the unload rollback order (must be newest-first, or firing order changes) and the failed-step client/host sync gap.
- In this repo: `direct-reload-41/client/Fika/ReloadTickets.cs`, `direct-reload-41/client/Fika/FikaAmmoSelectionBridge.cs`, `direct-reload-41/client/Patches/AmmoCommitShared.cs`.
- Scope note: the work here is a re-implementation inside this mod, not a copy of their companion DLL. Their packet keeps this project's MIT notice and states its additions are supplied under the same MIT terms; that is respected here.

## RZMedTiming — Kirisame dish / kirisamepositive (MIT)

- Source: <https://github.com/WYYQWQ1/SPT-RZMedTiming> · <https://sp-mod.com/mod/3219/rzmedtiming-surgery-medical-animation-sync>
- Referenced (ideas only, re-implemented here):
  1. Read the Surgery skill bonus from the **local player** at use time (`player.Skills.SurgerySpeed.Value`) instead of trusting a value cached in a shared field, and never fall back to a maxed 0.4 when the read fails.
  2. **Restore → read back → charge**: only consume the surgical-kit durability point when the limb is verified healed (`IsBodyPartDestroyed` after the restore), write durability as an absolute value, and never charge when the call throws. Optionally refund the point when a surgery is cancelled before healing anything.
- In this repo: `direct-reload-41/client/Patches/MedAnimatorSpeedPatch.cs`, `direct-reload-41/client/Patches/BatchSurgeryPatch.cs`.
- Their mod also carries **QuickMeds by BLACK HAWK** (MIT, <https://sp-mod.com/user/85915/black-hawk>). The conclusion that medical animation must not be driven through `FirearmsAnimator.SetUseTimeMultiplier` alone — some item prefabs silently ignore that animator parameter — came from reading the QuickMeds layer inside that mod; the animator-speed approach here is an independent implementation.

## Frameworks

SPT / SPT-AKI, BepInEx, HarmonyX and Fika — no mod without them.

---

If something in this tree is yours and is missing or mis-attributed, open an issue and it will be corrected (or removed, if you prefer).
