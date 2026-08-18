# Penetration v0.3 — shell descriptor link resolved (identity0 = eShellKind)

**Date:** 2026-08-18 (UTC)
**Status:** static, byte-verified against hash-bound `1cda5c31…` (wotblitz.exe).
Resolves the open descriptor link left by
`2026-08-18-shell-state-read-surface.md`: the live fingerprint
`identity0=5, identity1=71` is now decoded — **identity0 is `eShellKind`, and
`5` = `kArmorPiercingCr` (APCR)**. Nothing promoted. The identity-holder
*writer* (the byte-level source of `+0x20`/`+0x24`) is still a bounded open
item; see below.

## eShellKind enum (definitive)

The DAVA reflection registration `FUN_007c6780` (RVA `0x3c6780`) registers the
enum with explicit value/name pairs (`FUN_00908a50(value, "name")`), and the
data table `s_eShellKind` (RVA `0x31a1aa8`) lists the names in declaration
order:

| value | name | gameplay meaning |
|------:|------|------------------|
| 0 | `kUnknown` | unset |
| 1 | `kHollowCharge` | HEAT |
| 2 | `kHighExplosive` | HE |
| 3 | `kArmorPiercing` | AP |
| 4 | `kArmorPiercingHe` | APHE / HESH |
| 5 | `kArmorPiercingCr` | **APCR** |

## Shell descriptor layout (definitive)

The two `Shell::vftable` (`0x35a1e14`) installers — in-place ctor
`FUN_007a3da0` (RVA `0x3a3da0`) and the `std::_Ref_count_obj2<Shell>` factory
`FUN_0083b650` (RVA `0x43b650`, `operator_new(0x164)`) — initialize the same
fields:

- `+0x000` `Shell::vftable`
- `+0x114` **kind** (int32, init `0` = `kUnknown`)
- `+0x118` **caliber** (int32, init `0x7fffffff` sentinel)
- `+0x11c` `damage.armor` (float, init `-100000.0f`)
- `+0x120` `damage.devices` (float, init `-100000.0f`)
- `+0x124` / `+0x128` two `0.25f` / `0.05f` factors
- `+0x12c` `isTracer` (byte), `+0x130` `effects` (string)
- `+0x148` normalization, `+0x14c` ricochet, `+0x150` explosion radius
  (init `-100000.0f`), `+0x154` piercing-power-loss-by-distance (init `0`)

Cross-confirmed by `FUN_00840480` (RVA `0x440480`), the post-load fix-up:

```
if ([shell+0x114] == 2 /* kHighExplosive */ && [shell+0x150] <= 0.0f)
    [shell+0x150] = ([shell+0x118] * [shell+0x118]) / 5555.0f;   // radius ∝ caliber²
```

`kind@+0x114` compared as int to `2` (HE) and `caliber@+0x118` squared into
the explosion radius — the classic HE radius formula — so the field order is
`kind` then `caliber`, both int32.

## What this means for the read surface

`ProcessCurrentShells` compares the loaded-shell map lookup against the gun's
shell list via the **identity holder** (two dwords `+0x20`/`+0x24`). The live
capture returned `identity0=5`. Given `eShellKind` value `5` =
`kArmorPiercingCr`, **the loaded shell was APCR** (Churchill I / Oasis).

- ~~`identity0` = `eShellKind` = `5` = APCR~~ — **CORRECTED (2026-08-18, see
  `2026-08-18-shell-identity-holder-writer.md`).** `Shell+0x20` is a
  per-component **status/tier** discriminator (sentinel `10`), not the kind.
  The actual kind lives at `Shell+0x114` and was never read live. The
  `identity0=5` value coincidentally equals `kArmorPiercingCr` but does not
  decode to it.
- `identity1` (`ShellStateIdentity1`) = `71` — **RESOLVED (2026-08-18, see
  `2026-08-18-shell-identity-holder-writer.md`): it is the component `id`**
  (`Shell+0x24`, sentinel `0x7fffffff` = "no id"). The identity-holder writer
  is `ComponentsReader::OnReadComponents` (`FUN_00811070`).

## Additional static findings (same session)

- **`Shot` ctor** `FUN_00808430` (RVA `0x408430`, 0x44 bytes, `Shot::vftable`
  `0x35a1a30`) byte-confirms the gun-descriptor `vector<Shot>` ballistic
  layout: `defaultPortion +0x24` (init `-100000.0f`), `speed +0x28`,
  `gravity +0x2c`, `maxDistance +0x30`, `isATGM +0x40`, name string at `+0x4`,
  and a **pointer at `+0x1c`** (init `0`).
- The shell list that `ProcessCurrentShells` walks is the vector at
  `[[ammo+0x40] +0x20] +0x1b0`; its element `P` dereferences `[P+0x1c]` to the
  identity holder `Q` whose two dwords are compared (`+0x20`/`+0x24`). A
  shell-adjacent struct (constructed by `FUN_008084e0`) carries the **same
  `0x7fffffff` sentinel at `+0x24`** that `Shell.caliber@+0x118` uses, so
  `identity1` is most plausibly a caliber component — still unproven until the
  writer is traced.

## Remaining bounded item

~~Trace the identity-holder writer.~~ **DONE (2026-08-18, see
`2026-08-18-shell-identity-holder-writer.md`):** the writer is
`ComponentsReader::OnReadComponents` (`FUN_00811070`), which stores the status
argument at `+0x20` and the descriptor `id` at `+0x24`. Only the exact source
of the id value `71` (XML `<id>` attribute vs. a computed/global id) remains
unpinned; it does not change the field semantics.

## Evidence

- `.build/ghidra-evidence-shellkind/raw-bytes.txt` — `s_eShellKind` table +
  the five `*_DESCRIPTION` localization keys.
- `.build/ghidra-evidence-shellkind/functions-disasm.txt` — `FUN_007a3da0`
  (Shell ctor) and `FUN_0083b650` (shared_ptr Shell factory) disassembly +
  decompile.
- `.build/ghidra-evidence-gun-fields/shell-gun-producers.txt` — `FUN_007c6780`
  (eShellKind registration), `FUN_00840480` (HE radius fix-up),
  `FUN_00840570` (`ShellsReader::shell-attribute-handler`).
