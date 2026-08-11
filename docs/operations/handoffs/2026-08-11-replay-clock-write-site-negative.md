# 2026-08-11 — replay-clock write-site negative: copy-path expectation

## Summary

Follow-up to the verified replay-clock chain (2026-08-10). This phase tried
to pin the **write site** of the clock field `[BWServerConnection+0x58]+0x90`
statically and proved, exhaustively, that **no direct 8-byte store to that
offset exists anywhere in wotblitz.exe**. The field is written by a copy
path — the same synchronized-multi-copy reality FRESH37/38/43 proved for
position. The consequence is a concrete session expectation, not a dead end.

## What was done (all offline, hash-bound, no product change)

### 1. Sub-object class identified

`TraceReplayPlayerObject` → `TracePlaybackTickWrite` traced the connection
ctor: `[Connection+0x58]` is constructed by `FUN_0270ecf0` (RVA 0x230ecf0 —
a **Ghidra function-boundary artifact** had placed it mid-body in DAVA
Any-cast machinery; raw-bytes check recovered the real entry:
`55 8b ec 6a ff 68 …` classic MSVC prologue). The ctor installs
**`BW::SmartServerConnection::vftable`** — the replay-player sub-object is a
`BW::SmartServerConnection` (vtable 0x344260c). Its vtable-slot scan found
zero `+0x90` Double stores.

### 2. Exhaustive write-site scan — the negative

Two independent scans, both zero:

- **Instruction-iterator scan** (`ScanAllClockOffsetStores.java`, v2):
  every decoded instruction referencing `0x90]` → 27 store-shaped hits, all
  integer adds (`ADD dword [reg+0x90],imm`), 4-byte float stores
  (`FSTP float ptr`), or stack temps (`MOVLPD [ESP+0x90]`) — **no qword
  (8-byte) Double store**.
- **Byte-pattern scan** (`ScanClockStoreBytes.java`, v2): exact encodings
  for every direct 8-byte store form with displacement 0x90 — FSTP/FST m64fp
  (disp8 + disp32), MOVSD store (mod-10/mod-01/SIB/absolute), MOVQ
  (66 0F D6), MOVLPD (66 0F 13), and split-double `MOV dword` pairs →
  **0 hits across the whole executable**, including regions Ghidra did not
  disassemble.

### 3. Why this is the right answer, not a scan bug

The getter is byte-verified (`8b 41 58 dd 80 90 00 00 00 c3` = `MOV
EAX,[ECX+0x58]; FLD double [EAX+0x90]; RET`), so the field exists and is
read as a Double. A Double that is never directly stored must be written by
one of:

1. a CRT `memcpy`/`rep movsd` landing on it (the FRESH37/38/43 position
   shape: first hit is the copy site, real write one level up), or
2. a DAVA `Any` store through a computed address (type-erased buffer
   machinery — consistent with the mis-analyzed Any regions already on
   record).

Both are copy paths; neither is a statically pinnable direct store. This
matches the pre-existing verifier caveat (`write_site_rva=unpinned, live
interceptor capture, like FRESH36/43`) — now upgraded from assumption to
exhaustive negative.

## Session consequence (plan updated)

The `replayTime` live session should **expect the first interceptor hit to be
a copy site** (CRT/VCRUNTIME RIP shape), so:

- `-ArmSourceOnFirstHit` is **load-bearing, not optional** — capture the
  first hit, arm the copy-source page in the same window, resolve the real
  write one level up (mirror FRESH43).
- The chain-resolve path is unchanged: land the interceptor on
  `[subobj+0x90]` via L0 region reads (~10 s) instead of the ~120 s rolling
  campaign.
- Verdict contract unchanged: byte-exact 8-byte writes, module-RVA RIPs,
  2-launch × 2-replay repeatability after first HIT.

## Artifacts

- `tools/ghidra-scripts/ScanAllClockOffsetStores.java` (v2) — iterator scan
- `tools/ghidra-scripts/ScanClockStoreBytes.java` (v2) — byte-pattern scan
- Evidence: `.build/ghidra-evidence-player18/scan-all-clock-offset-stores.txt`
  (27 stores / 3088 loads, 0 qword stores)
- Evidence: `.build/ghidra-evidence-player21/scan-clock-store-bytes.txt`
  (v2, 0 hits)
- Verifier caveat updated: `tools/ghidra-scripts/TraceReplayClock.java`
- Plan updated: `docs/operations/replaytime-live-attempt-plan.md`
  (2026-08-11 section + State)

## Verification notes for reviewers

Both scans run headless against the hash-verified WotBlitz project with
`-noanalysis`. The byte scan covers un-decoded regions by construction (raw
bytes + ModRM decode), closing the gap the iterator scan cannot. Reviewers
can re-run with:

```
analyzeHeadless C:\work\tools\ghidra-projects WotBlitz -process wotblitz.exe \
  -noanalysis -postScript ScanClockStoreBytes.java \
  -scriptPath C:\work\wotb_reader\tools\ghidra-scripts
```
