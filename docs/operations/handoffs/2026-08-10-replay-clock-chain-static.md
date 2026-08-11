# 2026-08-10 — replay-clock chain statically verified (TraceReplayClock v3)

## Summary

The `replayTime` discovery target (next in the preference order after the
published position family) got its full static ownership chain pinned for
the 11.19.0.10 build — hash-bound, 10/10 verifier checks, no product change.

## Finding

The replay clock is a **Double** (seconds, advances every frame) owned by
the replay-player sub-object under the server connection:

```
GameCore 0x04095c88 -> AppController +0xc -> SessionController +0x124
  -> AccountController +0x118 -> PlaybackController +0x128
  -> BWServerConnection +0x120 (vftable 0x34400d0)
  -> replay-player sub-object +0x58 -> clock +0x90 (Double)
```

### Independent anchors (all pass)

1. **Resolver provenance** — `BWEntities::handleEntityMoveWithError`
   (0x022fc850) reads the connection back-pointer (`MOV ECX,[ECX+0x4]`),
   calls a virtual (`MOV EAX,[EAX+0x24]; CALL EAX`), widens float→Double,
   and threads it into the entity-apply path that writes the movement-ring
   time field (record +0x0, stride 0x38).
2. **Direct getter** — connection vtable (0x34400d0) slot 10:
   `FUN_026f9140 = MOV EAX,[ECX+0x58]; FLD double [EAX+0x90]; RET`
   (bytes `8b 41 58 dd 80 90 00 00 00 c3`, verified).
3. **Semantics corroboration** — slot 18:
   `MOVSD XMM0,[EAX+0x1270]; SUBSD XMM0,[EAX+0x90]` — duration/end anchor
   (0x1270) minus current clock (0x90) = remaining time. Consistent pair.

### Caveats (recorded in the report)

- The resolver reads vtable slot 9 (+0x24), whose thunk JMP target lands in
  a Ghidra-mis-analyzed DAVA Any/TLS region; the slot-10 direct getter is
  unambiguous. Both paths first load `[this+0x58]` — same field either way.
- **Write site unpinned** — writer candidates (`FUN_0270b7d0` area) are DAVA
  Any/TLS machinery, not the clock write; the real write site is a
  live-interceptor capture artifact, exactly like FRESH36/43 for position.
- Static evidence only: `offset_table_promotion_ready=false`,
  `live_read_authorized=false`.

## Session implication

The live `replayTime` session no longer needs the ~120 s rolling campaign as
the only path: resolve the chain live via L0 region reads
(`GameCore→…→[Connection+0x58]+0x90`) and arm the interceptor directly on
the resolved address. Chain root 0x04095c88 must be re-checked against the
live module base at session start. The interceptor verdict contract (byte
exact 8-byte writes, module-RVA RIPs, 2-launch repeatability) is unchanged.

## Artifacts

- Verifier: `tools/ghidra-scripts/TraceReplayClock.java` (v3, final)
- Evidence: `.build/ghidra-evidence-clock34/trace-replay-clock.txt`
  (schema `wotbtreader.ghidra.trace-replay-clock.v3`, verdict
  `replay-clock-chain-verified`, sha256 `1cda5c31…1760307d`, 10 pass / 0 fail,
  fresh 2026-08-10 23:50 UTC, run log has zero SCRIPT ERROR / error lines)
- Plan updated: `docs/operations/replaytime-live-attempt-plan.md`

## Verification notes for reviewers

Run path (headless, `-noanalysis` on the already-analyzed WotBlitz project):

```
analyzeHeadless C:\work\tools\ghidra-projects WotBlitz -process wotblitz.exe \
  -noanalysis -postScript TraceReplayClock.java \
  -scriptPath C:\work\wotb_reader\tools\ghidra-scripts
```

with `WOTB_READER_GHIDRA_OUTPUT_DIR` pointed at a fresh evidence dir. Headless
rule respected: report must be newer than invocation start, verdict string
must be present, zero `SCRIPT ERROR`/`error:` in the log.
