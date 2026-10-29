# Changelog

## 1.4.0
- **New — medical animations now really accelerate (all items):** the animation speed is driven directly through `Animator.speed` instead of the `UseTimeMultiplier` animator parameter. Splints, tourniquets, injectors and pills play through completely and finish together with the treatment — previously their animation was cut off halfway and snapped back into the "put away" pose ("unfold half way, then folds back").
- **New — surgery skill compensation:** the game shortens surgical treatment on a blacked-out limb by `1 + Surgery skill` but only passes `1 + skill / 100` to the animation. The animation multiplier is now corrected by that ratio, so a maxed Surgery skill no longer leaves the kit closing for a second after the limb is healed.
- **Fixed — batch surgery never charges durability for a limb that was not actually healed:** each limb is restored, then **read back** (`IsBodyPartDestroyed`); the durability point is only consumed when the limb is verified healed, and a throwing step never charges. Durability is written as an absolute value so parallel changes cannot double-charge.
- **New — optional "refund durability on cancel" (off by default):** if a surgery is interrupted (move / cancel) before healing a single limb, the point the game already took is returned. Toggle in F12 → *Batch Surgery → Refund Durability On Cancel*.
- **New — empty-magazine direct reload keeps the vanilla bolt-pull:** when both the chamber and the magazine are empty, one round of the selected ammo is pre-seeded into the magazine before the reload starts, so the game plans its own chambering step and the reload animation shows the bolt being worked.
- **Fixed — overlapping reloads in Fika:** every reload now carries its own ticket through the selection / commit envelopes, so two reloads in flight can no longer mix or overwrite each other's ammo selection and created-item IDs.
- **Fixed — host mirror completeness:** the commit envelope is sent on every successful reload, including the empty created-ID list a whole-stack transfer produces (previously that commit was not mirrored).

## 1.3.3
- **Fixed — belt (BeltSlot) ammunition was invisible to direct loading:** the direct-load, ammo-wheel and unload queries only searched the game's default reachable slots (pockets + tactical vest). The belt added by **BeltSlot** occupies the **ArmBand** slot, so loose rounds and magazines stored on the belt could not be used. The ammo query now builds its slot list explicitly — the game's `FastAccessSlots` (so a PackNStrap-provided replacement is honoured) **plus `ArmBand`**, de-duplicated and filtered to slots that exist on the equipment — and calls `GetAcceptableItemsNonAlloc`. Applies to the client and the Fika host mirror alike.
- No other behaviour changes; server version bumped for release consistency.

## 1.3.2
- **Fixed — internal-magazine & cylinder weapons:** the Marlin M1894 lever-action, the RSH-12 revolver and any other weapon whose magazine cannot be detached in raid no longer attempt the direct-reload magazine dance — they keep the **vanilla one-by-one loading** instead of ending in a failed reload transaction. The direct-load applicability check now mirrors the game's own in-raid movement rules (magazine slot is not a vital part, magazine is raid-moddable, no "cannot remove from slots during raid" component).

## 1.3.1
- **Fixed — Fika inventory identity:** after a direct reload in a Fika session, later inventory actions on the refilled rounds (unloading magazines, moving or dropping rounds/magazines) could be rejected by the host with `Could not find item with id`. The client now records the item IDs it creates while refilling, and the host mirror recreates them with the **same IDs** — both sides stay identical.
- **Fixed — no more phantom host commits:** the host mirror now only runs when the client's commit signal (created-ID list) arrives. Vanilla spare-magazine reloads no longer trigger a host-side mirror commit.
- **Fixed — inventory key collision:** pressing the reload key with the inventory open (e.g. rotating an item with R) no longer triggers a direct reload or the ammo wheel.
- **Fixed — wheel rendering across raids:** the ammo wheel's labels and icons no longer disappear in later raids of the same game session (stale UI objects from the previous raid are now cleaned up).

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
