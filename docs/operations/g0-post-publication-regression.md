# G0 — Post-publication regression plan (chained-field table change)

Prepared 2026-08-09 alongside `docs/operations/g0-offset-table-draft.md`.
Applies the moment the operator approves and applies the publication
(11.19.0.10.json `fieldValidation` → Verified + `chains` section; offsets
stay 0). The goal: prove the runtime behavior is EXACTLY as before for the
legacy observation path and unchanged for the resolver path.

## 1. The runtime contract (from code, verified 2026-08-09)

- `GameSessionCoordinator.ReadMemoryAsync` collects known fields as
  `field.Offset != 0 && field.Status == Verified` and reads
  `moduleBase + field.Offset` (`GameSessionCoordinator.cs`).
- Chained position fields keep `offsets` value **0** by design → they are
  EXCLUDED from the legacy observation reads → the observation emits
  position **null** (exactly as today, where all offsets are 0).
- The authoritative position reader is the resolver path
  (`POST /api/v1/game/discover/entity-position`, `GET .../position-page`),
  which uses the C# hash-bound layout (`Type10EntityPositionLayout`), NOT
  the table. The table change does not touch that path.

## 2. What MUST hold after the change

| Check | Expected | Why |
|---|---|---|
| `OffsetTableReader.Load` succeeds | no `offset.read_failed` | `schemaVersion` stays 1; System.Text.Json ignores the new `chains` key |
| Legacy observation: position fields | **null** (excluded) | offset 0 ⇒ not a known field ⇒ no bogus `moduleBase + value` read |
| Legacy observation: non-position fields (replayTime/playerHP...) | unchanged (still excluded today — all offsets 0) | no behavior change |
| Resolver endpoints | still resolve (24/24 in a bounded poll) | resolver uses the C# layout, not the table |
| `python scripts/python/offset_check.py --check-schema` | PASS | chains validated (offsets 0 for chained fields, hop kinds/values) |
| `tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10` | runs clean | read-only reporter |
| `scripts/validate.ps1` | green | full gate; no product-code change expected |

## 3. Required verification (executed after the edit, before the commit)

1. **Unit/regression test (spec):** add a coordinator test that constructs an
   `OffsetTable` with (a) `playerPositionX` `Status=Verified, Offset=0`
   (chained) and (b) `replayTime` `Status=Verified, Offset=0x1000`, passes it
   through `ObserveAsync` with a recording `IAuthorizedMemoryReader`, and
   asserts the reader is called for `moduleBase + 0x1000` (replayTime) and
   NEVER for `moduleBase + 0` / the chained field — i.e., the observation's
   position values stay null. Use the existing `StubOffsetTableReader`
   (override it to return the fixture table) and the coordinator test
   harness. This pins the "chained ⇒ excluded" contract against regression.
2. **Live smoke (optional, next approved session):** one bounded resolver
   poll (or a position-page call) returns `Resolved` while the legacy
   `/api/v1/game/state` observation reports position nulls — proving both
   paths coexist correctly.
3. **Gate sequence:** `offset_check.py --check-schema` → `report-offset-evidence.ps1`
   → `offline_check.py --refresh` (file-tree) → `scripts/validate.ps1`.
4. Commit the table + schema.json + pack doc + validator + the regression
   test + ledger (`numericOffsetPublication: true`) + handoff as ONE change.

## 4. What must NOT happen (fail-closed triggers)

- Any code path computing `moduleBase + offset` for a chained field
  (position) — the coordinator's knownFields filter is the guard; the test
  in §3 pins it.
- Any non-zero `offsets` value for `playerPositionX/Y/Z` in the table
  (validator fails it: "chained fields must stay 0").
- Any absolute/heap address anywhere in the table, `chains`, or ledger.
- `playerYaw`, velocity `+0x28`, `replayTime`, `playerHP`, `cameraPitch`,
  `aliveTankCount` statuses changing in the same change.

## 5. Rollback

If any check fails: revert the single publication commit (the table is the
only consumer-facing artifact changed; the runtime needs no code change to
return to the pre-publication state — all offsets 0 / Unknown again). The
`chains` validator remains harmless without a `chains` section (no-op).
