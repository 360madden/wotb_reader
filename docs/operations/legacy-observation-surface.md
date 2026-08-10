# Legacy observation surface — what the runtime memory path can actually read

Authoritative trace (2026-08-10) of the runtime **observation** read path —
the one that consumes `memory-offsets/<version>.json` through
`OffsetTableReader`. The **resolver** path
(`Type10EntityPositionResolver`, hash-bound layout) is separate and does
**not** read the offset table; it is untouched by everything below.

Source of truth: `GameSessionCoordinator.ReadMemoryAsync`
(`src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs`,
~line 788) + `OffsetTableReader`
(`src/WotBTreader.Application/Replay/OffsetTableReader.cs`) +
`IAuthorizedMemoryReader` (`ultimate-scanner/GuardedMemoryReader.cs`).

## 1. The path, step by step

1. **Gate:** `ObserveAsync` requires
   `GameSessionVerificationState.OfflineReplayVerified` plus a current
   authorization (liveness heartbeat). Otherwise → `Unknown`.
2. **Table load:** `LoadOffsetTable` requires an exact
   `gameVersion` + observed-`executableSha256` (64 hex) match.
   Load failure is non-fatal → `null` table.
3. **`null` table** → `Available` with all fields null (authorized but no
   offsets configured).
4. **Field filter (the guard):** `knownFields` = fields where
   `field.Offset != 0 && field.Status == Verified`.
   - `Offset == 0` ⇒ excluded **by construction** — this is the
     "chained ⇒ excluded" contract (a chained field's `offsets` value is 0
     by design; see `chains` in the table).
   - `Candidate` / `Unknown` / `Stale` ⇒ excluded (fail-closed). Only
     `Verified` crosses the observation path.
5. **No known fields** → `Available` with all fields null (NOT a refusal,
   NOT `Unsupported`). The HTTP endpoint still returns 200.
6. **Module base:** `WindowsGameProcessModuleBaseAddressResolver` →
   `process.MainModule.BaseAddress` (the main module, i.e. `wotblitz.exe`).
   Zero (module race / non-Windows) → `Unknown` (fail-closed, retryable).
7. **Reader:** `IAuthorizedMemoryReader` created per observation under the
   authorization read gate (`ReadAsync(address, length)`).
8. **Per known field:** size from `FieldType` (`DoubleField` 8,
   `FloatField` 4, `Int32Field` 4; `Unknown` → skipped); address =
   `moduleBase + field.Offset` (a **direct module-relative offset** — there
   is no chain concept in this path); `reader.ReadAsync(...)`. A failed
   read leaves that field null and continues.
9. **Field-name switch** maps to exactly eight observation slots:
   `replayTime` (Double), `playerHP` (Int32), `playerPositionX/Y/Z`
   (Float), `playerYaw` (Float), `cameraPitch` (Float),
   `aliveTankCount` (Int32). A name outside the switch is read but its
   value dropped (no default arm).
10. **Authorization re-check** before returning `Available` (a revocation
    racing the last read never turns into a bogus `Available`).

## 2. What this means for table publications

| Property | Consequence |
|---|---|
| Read address form | `moduleBase + offset` — **only direct module-relative fields can ever be read here**. A pointer chain is unrepresentable: chained fields must keep `offsets` 0, which excludes them automatically (pinned by `ChainedFields_AreExcludedFromObservationReads`). |
| Status gate | Only `Verified` fields cross; `Candidate`/`Unknown`/`Stale` are never read. Promotion to `Verified` is what arms a field for observation. |
| Field-type gate | `FieldType.Unknown` ⇒ size 0 ⇒ skipped even if offset ≠ 0. A publication must set the correct field type or the field is silently unread. |
| Observation slots | Only the eight names above are surfaced. A new field (e.g. velocity) needs a new slot + switch arm + contract change, not just a table edit. |
| No-known-fields | Returns `Available` all-nulls (200), not a refusal — the client sees `Availability: Available` with nulls. |
| Resolver fields | `playerPositionX/Y/Z` come from the resolver (hash-bound layout), not this path; the legacy observation emits them null for the chained publication. |

## 3. Contract invariants (do not regress)

1. Chained fields (offset 0) are NEVER read as `moduleBase + 0`.
2. Non-`Verified` fields are NEVER read, regardless of offset.
3. A failed per-field read nulls only that field.
4. Revocation during the loop ⇒ `Unknown`, never a stale `Available`.
5. The endpoint never refuses a verified session: `Available` (all-null or
   populated) or `Unknown` are the only observation outcomes today.

## 4. Related references

- Pack doc: `offline/memory-offsets.md` → "Runtime gating" +
  "Chains (pointer-chain verification)".
- Post-publication contract + regression test:
  `docs/operations/g0-post-publication-regression.md`.
- The resolver path (authoritative for position):
  `src/WotBTreader.Core/Discovery/Type10EntityPositionResolver.cs` +
  `Type10EntityPositionLayout`.
