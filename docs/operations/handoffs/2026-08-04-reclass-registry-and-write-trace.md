# Handoff: ReClass.NET registry entry + Phase 2c guide + x64dbg write-trace automation

**Date:** 2026-08-04
**Status:** Tooling + docs land; both the automated write-trace and the ReClass
class-layout phase are staged for the next live run
**Campaign:** WoT Blitz PC offset discovery (`playerYaw` hypothesis quarantined; offset remains 0)

## Outcome

Two tooling capabilities landed, closing the "third-party tools" thread opened
after the Cheat Engine removal (2026-08-03):

1. **ReClass.NET registered** in `tools/external/tools.lock.json` — canonical
   MIT tool for turning a staged survivor heap pointer into a concrete class
   layout (the "make it much easier" add evaluated in the CE-removal follow-up).
2. **`scripts/x64dbg-write-trace.ps1`** (from the prior session, now
   validated) — drives the already-installed x64dbg so the operator
   Find-what-writes step becomes optional, plus **Phase 2c** in the guide
   documenting the ReClass bridge between survivor staging and
   `tools/find-static-roots.py`.

| Piece | State |
|---|---|
| ReClass.NET entry (`tools.lock.json`) | v1.2, SHA-256 `3822bf89…9f46` (computed from the canonical GitHub asset), MIT, `["win32","win64"]` — **pending-install/pending-approval** |
| Phase 2c guide section | Inserted between Phase 2b and Phase 3; pipeline diagram + Preferred Approach updated; fences balanced |
| `scripts/x64dbg-write-trace.ps1` | New, `PARSE_OK`, DryRun smoke **EXIT=0** — generates `bph`/`bphwlog`/`savedata`-with-`{rip}`/fast-resume/run |
| `od-018-session.ps1` wiring | `-AutoWriteTrace` / `-WriteTraceSeconds` added; `PARSE_OK` |
| Stale-CE cleanup | 3 missed references re-pointed at x64dbg/write-trace staging (workflow ×2, strategy ×1) |

## Key facts verified this session

- **ReClass.NET canonical repo is `ReClassNET/ReClass.NET`** (MIT, 2,175★, not
  archived). Its last *published GitHub release* is **v1.2 (2019-04-15)**;
  development continues on `master`/`UnsafeMemoryScanner` with newer nightlies
  distributed via reclass.net (unpinned — recorded, not approved).
- **SHA-256 was computed from the actual canonical asset** (2,748,658-byte
  `ReClass.NET.rar`), not copied from an unverified source.
- **Target game is x86** (WOW64-observed 32-bit; the scanner's
  `GuardedMemoryReader` resolves `ImageFileMachineI386` with 32-bit pointers;
  ledger offsets are all 32-bit-range). ReClass platform field is therefore
  `["win32","win64"]` with a platform note — an initial win64-only draft was
  caught by review and corrected.
- **x64dbg automation constraints confirmed from docs/source:** CLI only
  supports `-p PID` attach (no script flag) → command-bar injection;
  `bph <addr>,w,8` hardware write BPs capped at 4 (DR0–DR3);
  `savedata` string-formats its filename (`{rip}` in the evidence filename =
  automatable capture, no GUI scraping); `SetHardwareBreakpointFastResume`
  keeps the replay playing through the capture window.

## Review fixes applied

1. ReClass `platforms` corrected `["win64"]` → `["win32","win64"]` + platform
   note (game is x86).
2. Pipeline-diagram note reworded — survivors are staged in **Phase 2b** (not
   Phase 3), and Phase 2c precedes Phase 3 in the document.
3. Workflow rule (8) keeps the OD-RECOVERY-031 attempt-4 CE-autorun lesson
   attribution honest while generalizing the default-path rule to the
   x64dbg/write-trace staging.

## Assumptions and unknowns

- ReClass.NET is **pending-install/pending-approval** — no local install yet;
  the entry carries the verified archive hash but `launcher_present: false`.
- The x64dbg-write-trace automation has been validated by DryRun only
  (`-DryRun` writes the x64dbg script + prints the plan). It has **not** been
  exercised against a live game session yet — first real use is the next
  operator window.
- `independentReplays` still 0 (BLK-0019 open); no RIP/root recorded; offset
  remains 0.

## Changed files (this handoff's commit)

- `tools/external/tools.lock.json` — ReClass.NET entry
- `docs/operations/offset-discovery-guide.md` — Phase 2c + diagram + Preferred Approach
- `docs/operations/offset-discovery-strategy-v2.md` — tooling table (ReClass-only)
- `docs/operations/offset-discovery-workflow.md` — staging rules + Phase-5 re-point
- `scripts/x64dbg-write-trace.ps1` — new automated write-trace (prior session, validated here)
- `scripts/od-018-session.ps1` — `-AutoWriteTrace` wiring (prior session)

The pre-existing `docs/operations/handoffs/2026-08-02-od-recovery-014-partial.md`
edit is intentionally left unstaged.

## Validation

```text
tools.lock.json: JSON_OK (8 tools); em-dashes normalized to ASCII
offset-discovery-guide.md: 56 fences (even = balanced); Phase 2c present
offline_check.py: 22 files / 85 links / 0 broken; ledger consistency OK
offset_check.py --check-schema: PASS
x64dbg-write-trace.ps1: PARSE_OK; DryRun EXIT=0 (generates correct bph/bphwlog/savedata commands)
od-018-session.ps1: PARSE_OK
```

## Recommended next steps

1. **First live write-trace run:** next operator window, run
   `scripts/od-018-session.ps1 -AutoWriteTrace -WriteTraceSeconds 120` (or the
   write-trace script directly with the pre-armed x64dbg). The `{rip}`-named
   `savedata` evidence files are the capture channel — no GUI scraping.
2. **ReClass.NET install:** download `ReClass.NET.rar` v1.2 from the canonical
   GitHub releases page, extract to `C:\tools\ReClass.NET\`, verify the
   launcher + hash, flip the entry to verified-local, then use Phase 2c on the
   next staged survivor set.
3. **Verify x64dbg build vs x86 target:** the game is 32-bit (WOW64); confirm
   whether `release\x64\x64dbg.exe` can debug the 32-bit target or whether the
   x32 build must be launched for the write-trace window.
4. Import a content-distinct second `.wotbreplay` to close BLK-0019
   (`independentReplays`).
