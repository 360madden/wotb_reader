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

- `identity0` (`ShellStateIdentity0`) = `eShellKind` = `5` = APCR. This is now
  a *decoded* semantic, not a bare fingerprint — the enum is explicit, the
  field is an int32, and `5` is a valid in-range value.
- `identity1` (`ShellStateIdentity1`) = `71` remains **undecoded**. Most likely
  a caliber component or a shell resource id, but the identity-holder writer
  (the function that populates `+0x20`/`+0x24`) has not been traced to its
  source. This is the only remaining bounded static item.

## Remaining bounded item

Trace the **identity-holder writer** — the function that stores the two dwords
at `identity_holder +0x20`/`+0x24`. The holder is `[shell_slot +0x1c]`; the
shell list is the vector at `[[ammo+0x40] +0x20] +0x1b0`. Confirming the writer
names both dwords (and settles `identity1=71`). This is static, cheap, and does
not block the G1 item 2 promotion gate (which only needs a controlled
shell-swap flip of the index + fingerprint).

## Evidence

- `.build/ghidra-evidence-shellkind/raw-bytes.txt` — `s_eShellKind` table +
  the five `*_DESCRIPTION` localization keys.
- `.build/ghidra-evidence-shellkind/functions-disasm.txt` — `FUN_007a3da0`
  (Shell ctor) and `FUN_0083b650` (shared_ptr Shell factory) disassembly +
  decompile.
- `.build/ghidra-evidence-gun-fields/shell-gun-producers.txt` — `FUN_007c6780`
  (eShellKind registration), `FUN_00840480` (HE radius fix-up),
  `FUN_00840570` (`ShellsReader::shell-attribute-handler`).
