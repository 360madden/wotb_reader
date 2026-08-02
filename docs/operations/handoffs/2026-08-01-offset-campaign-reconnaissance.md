# Session handoff — 2026-08-01: privacy-safe offset campaign reconnaissance

**Author:** Codex Agent

**Branch:** `main`

**Baseline:** `8bf21a4` (`fix(scanner): prove managed offline replay reads`)

**Commit unit:** GameHarness orchestration, focused tests, offline/operations
documentation, and redacted live evidence; no offset-table change and no push

## Outcome

The verified offline scanner now has a repeatable aggregate-only campaign
command. `discover-campaign` privately derives a bounded main-module range,
creates an aligned Float32 snapshot, performs rolling comparisons, renders only
aggregate counts, and attempts scanner-session discard on every exit path. It
composes the existing loopback endpoints and does not change any shared HTTP,
application, or scanner contract.

Two fresh managed launches of the same private replay reached
`OfflineReplayVerified` with exactly one attributable `wotblitz.exe` each. Both
real campaigns completed with identical negative reconnaissance: the first
16 MiB contained 2,265,195 Float32 values in `-500..500`, none changed during
the first two-second interval, and the rolling changed set remained empty in
the second interval. Both scanner sessions were discarded. Evidence expiry
terminated each exact game process without a forced stop; the lead-started web
host was then stopped explicitly. Post-trial game and host counts were zero.

This is not offset evidence. No address, value, memory bytes, session identifier,
RVA, member displacement, pointer chain, heap route, address kind, replay path,
artifact identifier, content hash, or player data was published or committed.
No field changed status, and `memory-offsets/11.19.0.10.json` was untouched.

## Command boundary

`discover-campaign` defaults to:

- two rolling `changed` comparisons at two-second intervals;
- a 16 MiB cap on the trusted main-module address range;
- aligned Float32 values from `-500` through `500`;
- one candidate requested from each loopback comparison response, ignored and
  never rendered;
- eight seconds maximum configured wait across all comparisons;
- unconditional retained-session discard attempt.

Options permit one through four comparisons, one through five seconds per
interval, one through 64 MiB, ordered finite float bounds, and the existing four
comparison modes. The wait-product guard preserves time inside the coordinator's
15-second evidence lifetime for native reads and cleanup. Invalid arguments are
rejected before rendezvous or process access; missing/malformed host capability
records fail closed.

## Defects exposed during live setup

An initial orchestration rehearsal polled the lifecycle gate for only four
seconds, shorter than the documented 45-second launch-evidence window. The
script stopped its own host before capturing the managed PID; a read-only
singleton check then proved the sole game process belonged to the zero-process
preflight and managed launch, and that exact PID was stopped. No scanner request
ran. The corrected trials capture the singleton immediately and use the full
lifecycle window.

The first positively verified campaign attempt then exposed `BLK-0020`: a
64-byte neighborhood probe at relative offset zero was centered below the
trusted module base and failed closed before snapshot creation. The campaign
now probes one minimum radius into the module, so the lower bound lands exactly
on the trusted base. A request-shape regression pins the displacement, and both
subsequent live campaigns passed.

## Evidence interpretation

`OD-RECOVERY-003` is recorded as `Partial` in the append-only ledger. It rules
out only this bounded main-module natural-change protocol as a useful dynamic
anchor under the recorded filters and timing. It does not rule out private or
heap state and does not identify `playerPositionX`, `playerPositionZ`,
`replayTime`, HP, or any other field.

`BLK-0019` remains open: the local replay inventory contains only one distinct
payload. Two fresh launches establish process-launch independence, not replay
independence. No candidate can enter promotion review until it survives a
controlled transition protocol and a second independent replay, along with the
existing structural/static evidence and approvals.

## Validation

- Focused GameHarness suite after the live-probe correction — 34 passed,
  0 failed, 0 skipped.
- `dotnet format WotBTreader.sln --verify-no-changes --no-restore` — passed.
- Two final managed offline campaigns — passed with identical aggregate counts,
  explicit scanner-session discard, natural evidence-expiry game termination,
  and zero post-trial game/host processes.
- `scripts/validate.ps1` before handoff creation — passed: locked restore,
  formatting, Release build with 0 warnings/errors, 494 passed tests,
  2 expected local opt-in skips, repository scan, and offline freshness/link
  checks.

## Files in this unit

- `tools/src/WotBTreader.GameHarness/OffsetCampaign.cs`
- `tools/src/WotBTreader.GameHarness/Properties/AssemblyInfo.cs`
- `tools/src/WotBTreader.GameHarness/Program.cs`
- `tools/tests/WotBTreader.GameHarness.Tests/OffsetCampaignTests.cs`
- `tools/tests/WotBTreader.GameHarness.Tests/GameHarnessCommandContainmentTests.cs`
- `docs/operations/offset-discovery-guide.md`
- `docs/operations/offset-discovery-ledger.md`
- `docs/operations/blocker-log.md`
- `offline/offset-discovery.md`
- this handoff

## Next move

Start `OD-RECOVERY-004` with a changed hypothesis: use a controlled tank-movement
transition to search readable private/heap state for `playerPositionX` or
`playerPositionZ`, then classify any survivor as module-relative, member
displacement, pointer-chain reachable, or heap-dynamic. Keep raw evidence local
and ignored. Obtain a second independently sourced replay before any promotion
review. Do not repeat the 16 MiB main-module natural-change protocol unchanged.
