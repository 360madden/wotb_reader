# World-translation negative and type-10 pivot handoff (2026-08-08)

Outcome: OD-RECOVERY-066 completed the one authorized world-matrix-translation
capture. The capture mechanism and matrix displacement were valid, but the
sampled trajectory did not identify the decoded player location. The
render-transform branch is closed unless new semantic provenance changes the
hypothesis.

## Safe execution summary

- The instruction snapshot helper was freshly published and its synthetic x86
  capture/cleanup test passed before live access.
- One new coordinator-managed replay reached `OfflineReplayVerified`.
- Exactly one five-second snapshot ran at the fixed hash/byte-pinned
  `wotblitz.exe+0x7C39AB` instruction and EBX+`0x90/+0x94/+0x98` read.
- The result contained seven accepted finite samples from one privacy-safe
  opaque object key. The instruction fingerprint and cleanup/detach were proven.
- The game, Host, helper, legacy interceptor, and debugger processes were all
  stopped and independently observed absent after analysis.
- Private replay/runtime artifacts remain ignored and local. No heap addresses,
  raw values, replay identity, account/player data, full paths, tokens, or raw
  bytes are copied into tracked documentation.

## Aggregate comparison result

The capture and replay markers supplied a real UTC alignment. The decoded
session contained 26,822 positions from every participant. Offline comparison
tested every participant, all 48 axis/sign mappings, playback speeds from 0.5x
through 8x, and the bounded scene-marker uncertainty.

- Exact trajectory matches: 0.
- Best coherent absolute fit: mean 10.850 units, max 12.556 units.
- Samples within 1 unit in that fit: 0 of 7.
- The fixed 2.4x comparison was worse: mean 28.227, max 38.752 units.
- A free constant-offset motion-shape fit reached mean 1.260 units only by
  introducing a 250.832-unit origin shift and 6.26x playback. That is an
  over-parameterized shape resemblance, not coordinate or entity identity.

The world-translation hypothesis therefore returns `NoSignal` for player
location. Do not spend another replay on the same read to shave timing or widen
the displacement.

## What remains proven

Hash-verified disassembly still proves the arithmetic:

- `FUN_00d1a0f0` builds a local transform matrix.
- `FUN_00729570` composes it with a parent matrix.
- `FUN_00bc3940` copies the resulting 4x4 matrix to EBX+`0x60`.
- EBX+`0x90/+0x94/+0x98` is the translation row of that composed matrix.

The negative result concerns semantic identity: the object selected at this
per-frame transform-fill instruction, or its coordinate space, is not the
decoded player trajectory under the tested clock models. It does not refute
the execute-breakpoint helper, cleanup proof, or matrix layout.

No offset is promoted. Viewpoint identity, same decoded clock, stable root, and
a runtime-supported player-position field remain unproven.

## Durable pivot: OD-RECOVERY-067

The replay decoder already verifies the 49-byte type-10 position packet against
immutable decoded ground truth. The next work is offline/static-only and starts
from that semantic anchor instead of another render transform:

1. Locate the exact game code that consumes or applies type-10 XYZ to an entity
   or physics state.
2. Preserve executable identity, module RVA, instruction bytes, entity/register
   provenance, and the destination member or fixed contiguous read.
3. Synthetically validate a bounded capture plan with the existing offline
   authorization, cancellation, cleanup, privacy, and output-size requirements.
4. Only then request one fresh live capture and compare entity-bound samples to
   decoded type-10 ground truth at the aligned clock.

Do not fall back to broad scans, delayed tracing, raw-PID attachment, additional
transform-member guesses, or offset publication. The next milestone is a frozen
static target contract, not another live round.

## OD-RECOVERY-067 static triage result

Three new hash-bound Ghidra scripts tested the simplest consumer hypotheses
without touching live/private data:

- `FindType10PositionConsumers.java` scanned 526,935 executable functions. It
  found 3,457 local displacement-layout candidates and 190 framed-layout
  candidates, but manual decompilation refuted the top result as matrix/grid
  code. The result set is dominated by generic matrix, copy, serializer, and
  destructor shapes.
- `FindType10RecordDispatch.java` found no function that directly compares the
  same record base against both payload length `49` and type `10`. The 13 loose
  `0x31` comparisons were byte comparisons, not a dword record-length switch.
- `FindType10DispatchTable.java` returned eight nearby
  `{type=10,length=49,code-pointer}` data candidates. Their neighborhoods carry
  the MSVC EH magic `0x19930522`; all eight are exception metadata, not replay
  dispatch tables.

This is `Partial / no direct consumer anchor`. It rules out repeating the same
literal or displacement-only searches, not the verified type-10 data. The
output reports stay ignored under `.build/ghidra-evidence`; only aggregate
counts and classifications are durable here.

The active next method is data-flow-first: locate the generic replay event
reader/framer through replay/file entry points, then follow its type/payload
dispatch into an entity or physics setter. Only a hash-bound module/RVA/bytes
site with entity/register and destination-member provenance may reopen the
synthetic capture-design gate. No live run is currently authorized.

The Ghidra dump scripts no longer write into a disposable
`.freebuff/worktrees` path. They use `WOTB_READER_GHIDRA_OUTPUT_DIR`, with an
ignored `.build/ghidra-evidence` fallback, so worktree cleanup cannot silently
break durable analysis commands.
