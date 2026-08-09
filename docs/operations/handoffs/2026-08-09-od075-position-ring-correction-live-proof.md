# OD-RECOVERY-075 position-ring correction and live proof

Date: 2026-08-09
Status: milestone ready after validation
Scope: exact-build offline/static proof, bounded synthetic verification, and
positively verified offline-replay polling only

## Outcome

The module-rooted resolver now reads the current movement-ring **position**.
Static inspection proved that the shared helper store at RVA `0x0230DF40`
lays out each eight-entry record as follows:

- record base: `helper + 0x08 + (index * 0x38)`;
- position XYZ: record `+0x10/+0x14/+0x18`, therefore helper-relative
  `+0x18/+0x1C/+0x20` for record zero;
- velocity XYZ: record `+0x28/+0x2C/+0x30`, therefore helper-relative
  `+0x30/+0x34/+0x38` for record zero;
- current index: helper `+0x1C8`.

OD-073 had combined a helper-relative position displacement (`+0x18`) with a
record-relative position displacement (`+0x18`). That double-count selected
record `+0x28`, the adjacent velocity triple. The first live OD-075 diagnostic
therefore returned 24/24 readable samples and 21 distinct values, but its
minimum retained-trajectory distance was 115.686 units. This is now classified
as a useful implementation diagnostic, not a negative position result.

The corrected resolver uses ring base `+0x08` and record position `+0x10`. It
also admits only the three statically proved filter/helper subtype pairs and
rejects a cross-type pairing before any ring read.

## Static and synthetic evidence

The exact executable remains version `11.19.0.10`, SHA-256
`1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d`.

- `TraceEntityRegistryPosition.java`: 82/82 hash-bound checks, verdict
  `replay-resolver-layout-proven`.
- `TraceType10MovementPosition.java`: 40/40 hash-bound checks, verdict
  `semantic-chain-proven`.
- The vehicle path is explicit: WGVehicleFilter2 vtable RVA `0x032565AC`,
  helper vtable RVA `0x0325658C`, helper factory RVA `0x01069BE0`, helper
  constructor RVA `0x010139B0`, vehicle store wrapper RVA `0x01069C80`, and
  common store RVA `0x0230DF40`.
- Focused Core coverage distinguishes position from the adjacent velocity
  fields and exercises every exact subtype pair plus mismatched-pair denial.

## Corrected live result

One fresh managed process reached `OfflineReplayVerified`. Ground truth was
bound to the exact canonical launch artifact through an owner-only marker,
then selected from that artifact's newest immutable decode. The corrected
bounded poll produced this aggregate:

- verdict: `stable-resolver-positive`;
- requested/resolved: 24/24;
- distinct position triples: 24;
- exact retained-trajectory matches: 5;
- within one world unit: 8;
- within three world units: 21;
- minimum/maximum retained-trajectory distance: 0 / 3.57889998332587;
- module-rooted, entity revalidation, and consistent double-read flags: true;
- hardware atomicity, same-decoded-clock proof, stable-root cross-replay
  repeatability, and offset-table promotion readiness: false.

The ignored private result is retained locally. No entity ID, coordinates,
process address, replay path, raw byte, capability, player/account data, or
other private value is copied into tracked documentation.

## Artifact binding policy

The canonical managed launcher now writes only the imported artifact UUID to
an owner-only, non-reparse location under local application data. The polling
runner fails closed if the marker is absent, stale, malformed, or not
owner-only. Automatic selection filters decoded sessions by exact
`sourceArtifactId`; an explicit session is rejected if its artifact differs.
Future aggregates use schema v3 and record only that artifact binding was
proved and how the decode was selected.

## Cross-replay status and blocker

A content-distinct replay was selected for the unchanged repeat, but its
managed launch attempts exited before the positive offline-evidence gate. The
Host remained `Unknown` / `session.initial`; no position request or other
memory operation ran, and no evidence result was created. All exact managed
game/Host/helper processes were stopped afterward.

This is BLK-0026. It blocks the second continuous-polling proof, not the
already established cross-replay event-based player-position result. Do not
spend another discovery run on unchanged launch retries. Diagnose the launch
failure separately, then run exactly one unchanged poll on the other replay.

## Decision

- Continuous module-rooted player-position polling has a strong positive in
  one replay/fresh process.
- Cross-replay continuous-polling repeatability remains unproved.
- Double-read consistency is not hardware atomicity or same-clock proof.
- Do not edit `memory-offsets/11.19.0.10.json` or promote a numeric offset.
- The effective read is a server-owned module-rooted pointer chain and member
  layout, not one caller-supplied address.

## Validation

Focused resolver coverage passed 18/18. The full `scripts/validate.ps1` gate
passed: locked restore, formatting, Release build with zero warnings/errors,
654 tests passed with two expected local opt-in skips, repository/privacy scan,
PowerShell hygiene, fresh offline pack, link checks, blocker numbering, and
ledger consistency.
