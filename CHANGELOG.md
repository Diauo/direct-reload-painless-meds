# Changelog

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
