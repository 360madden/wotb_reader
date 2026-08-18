# Penetration v0.3 — shell identity-holder writer traced (identity1 = component id)

**Date:** 2026-08-18 (UTC)
**Status:** static, byte-verified against hash-bound `1cda5c31…` (wotblitz.exe).
Resolves the "identity-holder writer" open item left by
`2026-08-18-shell-descriptor-link.md` and **corrects** that handoff's
`identity0` claim. Nothing promoted.

## The identity holder is the `Shell` object itself

The prior handoffs described the terminal object of the shell-index chain as a
standalone "identity holder" reached at `[shell_slot + 0x1c]`. That is
structurally true but the *object* is the `Shell` itself, reached through a
`shared_ptr`:

- The gun's shell list is `vector<Shot*>` at `[[ammo+0x40] +0x20] +0x1b0`
  (Shot = 0x44 bytes, `Shot::vftable` `0x35a1a30`).
- `Shot` holds `shared_ptr<Shell>` at `+0x1c`/`+0x20` (object pointer +
  control block). The Shot ctor `FUN_00808430` zeroes `+0x1c`/`+0x20`; the
  `VehicleTypeReader::OnShotsElementStarted` path (`FUN_007c2240`) assigns the
  looked-up `Shell` into `Shot+0x1c` via `FUN_007a8750`.
- `ProcessCurrentShells` (`FUN_015ef402`) dereferences `[Shot+0x1c]` and
  compares **`Shell+0x20`** and **`Shell+0x24`** against the loaded shell.

`Shell` is a `VehicleComponent` subclass. Its ctor `FUN_007a3da0` (RVA
`0x3a3da0`) calls the base `VehicleComponent` ctor `FUN_008084e0` (RVA
`0x4084e0`) first — which sets `+0x4` = component type (`10` = shell),
`+0x20` = `10`, `+0x24` = `0x7fffffff` — then overwrites `+0x0` with
`Shell::vftable` and initialises the Shell-specific descriptor fields
(`kind +0x114`, `caliber +0x118`, damage, etc.).

## `+0x24` = the component **id** (identity1 = 71)

`ComponentsReader::OnReadComponents` (`FUN_00811070`, `ShellsReader` vtable
slot 8) is the writer. For every descriptor in the reader's list it stores:

```c
*(component + 0x20) = statusArg;        // the OnReadComponents batch argument
*(component + 0x24) = descriptor.id;    // piVar10[10]
*(component + 0x28) = icon;             // FUN_007b30c0(type, statusArg, id)
```

The descriptor field is labelled **`id`** by the function's own diagnostic
(`"Components::OnReadComponents (%s) id=%d already defined: status=%s"`, the
`%d` = `descriptor.id`). So:

- `Shell+0x24` = **id** — sentinel `0x7fffffff` = "no id" (the copy-ctor
  guard `FUN_00842a50` skips exactly that value).
- **`identity1` = `71` = the loaded shell's component id.**

The exact id source (the XML `<id>` attribute vs. a computed/global id) is not
yet pinned to a specific XML row — `uk/components/shells.xml` carries no
literal `71` as a value, so the id is either computed or defined in a shared
table; that is now the only remaining sub-detail and it does not change the
field semantics.

## `+0x20` is NOT the eShellKind (corrects the prior handoff)

`Shell+0x20` is written from `OnReadComponents`' **batch argument**
(`statusArg`, the same value for every component in the read batch), and it
feeds the icon key as a 4-bit component (`FUN_007b30c0`: `id << 8 | type |
statusArg << 4`). Its sentinel is `10` (`FUN_00842a90` skips exactly that
value). The live `identity0 = 5` is therefore a per-component **status/tier
discriminator**, **not** `eShellKind`.

The prior claim "`identity0` = `eShellKind` = `5` = APCR" was a
coincidence-driven inference: `+0x20` happens to equal `5`, and `5` also
happens to be `kArmorPiercingCr`, but the actual kind lives at **`Shell+0x114`**
(written by the `ShellsReader::shell-attribute-handler` `FUN_00840570`'s enum
lookup for the `kind` attribute), which the `shell-state` read surface never
reads. The loaded shell was *probably* APCR (the kind enum value `5` at
`+0x114` was never read live), but `identity0=5` alone no longer carries that
meaning.

## Definitive `Shell` layout (reconciled)

- `+0x04` component type (`10` = shell)
- `+0x08` name (FastName)
- `+0x20` **status/tier** discriminator (sentinel `10`)
- `+0x24` **id** (sentinel `0x7fffffff`)
- `+0x28` icon (string), `+0x2c` status name (FastName, default `"empty"`)
- `+0x114` **kind** (`eShellKind` 0–5, sentinel via enum)
- `+0x118` **caliber** (int32, sentinel `0x7fffffff`)
- `+0x11c` damage.armor, `+0x120` damage.devices, `+0x124/+0x128` factors,
  `+0x12c` isTracer, `+0x130` effects, `+0x148/+0x14c` normalization/ricochet,
  `+0x150` explosion radius, `+0x154` piercing falloff.

## Effect on the read surface

The `shell-state` anchor reads `Shell+0x20` and `Shell+0x24` as
`ShellStateIdentity0`/`ShellStateIdentity1`. With this trace those fields mean:

- `ShellStateIdentity1` = **id** = `71`.
- `ShellStateIdentity0` = **status/tier** = `5` (constant across stock
  shells in a batch, so the pair's discriminating member is the id).

The anchor itself needs no code change; only the documentation of the two
dwords changes. The G1 item 2 promotion gate is unaffected — it keys on an
index/fingerprint *flip* during a controlled swap, which the (status, id) pair
still witnesses.

## Evidence

- `.build/ghidra-evidence-shellwriter/functions-disasm.txt` — `FUN_007bd760`
  (Vehicles::OnReadInstallableComponents switch), `FUN_007c2240`
  (VehicleTypeReader::OnShotsElementStarted — Shot shared_ptr assignment),
  `FUN_0080a860` (VehicleComponent copy ctor).
- `.build/ghidra-evidence-setters2/functions-disasm.txt` — `FUN_00842a90`
  (sentinel 10) and `FUN_00842a50` (sentinel 0x7fffffff) conditional setters.
- `.build/ghidra-evidence-status/functions-disasm.txt` — `FUN_00811070`
  (`ComponentsReader::OnReadComponents`, the `+0x20`/`+0x24` writer).
- `.build/ghidra-evidence-icon/functions-disasm.txt` — `FUN_008008b0`
  (component-type prefix table) and `FUN_007b30c0` (icon key builder).
- `.build/ghidra-evidence-shellkind/functions-disasm.txt` — `FUN_007a3da0`
  (Shell ctor → base `FUN_008084e0` + `kind +0x114`/`caliber +0x118`).
