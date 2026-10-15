# Changelog

## 1.3.0
- **New — ammo selection wheel:** hold the reload key (default R) to open a radial menu of every caliber-compatible round you carry — icons, damage, penetration and available counts included. Scroll to flip pages (7 slots per page), release to select. Pick a priority round and every reload refills with it first, automatically falling back to the rest (highest penetration first) once it runs out. Default **AUTO** = highest penetration first.
- **Reloads now return the magazine's old rounds first:** every reload dumps the magazine back to your rig (merging into same-type stacks) before refilling — magazines end up with exactly the mix you expect, and the priority round sits on top (it is the first one fired).
- **Magazine + chamber compatibility enforced:** the wheel and direct reload only accept rounds compatible with both the magazine and the weapon's chamber.
- **Fika-safe as before:** the host mirror executes the same dump/refill sequence.
- **SPT 4.1.x only from this version:** the SPT 4.0.x line is frozen at v1.2.0 and no longer receives updates.

## 1.2.0
- **New — FOV unlock:** the in-game base-FOV setting is no longer capped at 75. The settings slider and the value clamp now use a configurable range (default 50–110), toggleable in the F12 menu. Shipped on **both** SPT lines (4.0.x + 4.1.x).
- Versions unified: both lines now carry the same version number.
- (4.1 line carries everything from the 1.1.0 port below.)

## 1.1.0
- **SPT 4.1 support:** full client + server re-port for SPT 4.1.x (EFT 0.16.9.5), built against the deobfuscated 4.1 client contract (`hollowed.dll`). See `direct-reload-41/`.
- **Fixed:** the 4.1 inventory observer system rejected the softcore reload's temporary magazine stow (`UnknownItemError` — "Cannot manipulate unknown or not found item"). The magazine is now flagged as *temporarily known* during the reload transaction, mirroring the game's own drag operations, and cleared afterwards.
- **Fixed:** the batch-surgery patch now targets the method's declaring base class — cleaner patch application, no more Harmony advisory on startup.
- **Fixed:** the temporary magazine drop-off search now matches vanilla behaviour exactly (rig + pockets only — no backpack/secure container).
- Verified end-to-end on SPT 4.1.6: direct reload (full + partial magazines), batch surgery, faster meds, animation sync — including a Fika local host session.

## 1.0.2
- **Fika compatibility finalized:** in multiplayer sessions the host now mirrors reload fill/chambering on its own copy of your character, keeping client and host state in sync (no more desync-related errors after direct reloads). Verified on a dedicated headless setup.
- Instant magazine operations sync through the standard inventory-operation channel.
- Bilingual config UI (中文 / English) and language-localized notifications.

## 1.0.1
- Reload failure notifications localize by game language (Chinese UI → Chinese, otherwise English).
- English client config texts.

## 1.0.0
- First public release: Stalker-style direct reload (no spare magazines), instant magazine load/unload, faster medical use, painless surgery, batch surgery, med animation sync, optional drug buff multiplier.
