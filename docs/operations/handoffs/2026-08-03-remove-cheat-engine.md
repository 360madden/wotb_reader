# Handoff — Cheat Engine removal from the pipeline (2026-08-03)

## Session summary

Per operator decision: **Cheat Engine is removed from the live pipeline and
deleted from the repo's tools folder.** Rationale (recorded in this session's
exchange): UltimateScanner is the sole memory reader (already wired
end-to-end through Host.Web → GameSessionCoordinator → MemoryScanEngine);
CE never scanned in this campaign. CE's only remaining role was the
operator-interactive write-trace step, and its automated write-BP path was
already ruled out by OD-020 (0 RIP hits in 3 live runs). x64dbg is the
replacement interactive debugger and was already the pre-arm preference.

## What was removed

- `tools/cheat-engine/` — whole folder deleted (6 files):
  `discover-offsets.lua`, `multiscan.lua`, `od-autorun-writebp.lua`,
  `prearm-attach.lua`, `pipeline-automation.md`, `README.md`.
- `tools/discover-offsets.ps1` — CE-output normalization orchestrator;
  its only purpose was consuming CE Lua output and its default
  `tools/cheat-engine/multiscan.lua` no longer exists.
- Cheat Engine entry + the AITools CE-plugin entry removed from
  `tools/external/tools.lock.json` (x64dbg, System Informer, Ghidra, ILSpy
  remain).

## What was updated (CE → x64dbg/scanner)

- `scripts/pre-arm-debugger.ps1` — rewritten x64dbg-only: dropped
  `Find-CheatEngine`, the CE launch branch, `-PreferX64Dbg`, and the
  `cheatEngineExe` marker field; keeps `-AutoAttach` and the
  `%TEMP%\od-prearmed-debugger.json` marker.
- `scripts/od-018-session.ps1` — operator-window text now instructs x64dbg
  `bphw` write breakpoints on `%TEMP%\od-survivors.txt` addresses instead of
  a CE right-click; address-file freshness comment reworded.
- `scripts/launch-offline-replay-for-od.ps1` — dropped `cheatengine*` from
  the `Stop-OdProcesses` kill list.
- `tools/src/WotBTreader.GameHarness/Program.cs` — help text:
  "Ghidra or Cheat Engine pipeline" → "Ghidra or x64dbg pipeline".
- `docs/operations/offset-discovery-guide.md` — tool table (removed CE rows,
  added rolling driver + delta extractor), pipeline phases, Phase 2a/2b/3/4,
  publication rules, preferred-approach block all re-pointed to x64dbg +
  rolling driver; `multiscan.lua`/`discover-offsets.ps1` sections replaced.
- `docs/operations/offset-discovery-workflow.md` — Phase 5 publication block
  (dropped `discover-offsets.ps1 -SelfTest`), operational notes, pre-arm
  instructions, controlled-transition wording.
- `docs/architecture/roadmap.md`, `docs/architecture/overview.md`,
  `docs/ROADMAP.md`, `knowledge.md`, `README.md`,
  `docs/operations/README.md` — "Cheat Engine-like" descriptors and policy
  mentions reworded to standalone/x64dbg.

## Historical records kept (by design)

- `docs/operations/offset-discovery-ledger.md` — all OD-009/010/011/020/023+
  CE evidence rows and rules-outs remain as the durable record.
- `docs/operations/handoffs/*` — prior session records with CE references
  are untouched (they describe what happened then).
- `docs/operations/blocker-log.md`, `offline/` pack — untouched.

## Validation

- PowerShell AST parse: OK for all three edited scripts.
- `tools/external/tools.lock.json`: valid JSON.
- `scripts/python/offline_check.py`: 22 files / 85 links / 0 broken; ledger
  consistent (25 result sections / 39 index rows).
- `scripts/python/offset_check.py --check-schema`: PASS.
- Final live sweep: zero remaining CE tooling references in live
  (non-historical) files; only the two intentional "removed 2026-08-03"
  notes remain.

## Next steps

- OD-RECOVERY-044 (live, operator present): replayTime delta pilot first,
  x64dbg pre-armed via `scripts/pre-arm-debugger.ps1 -AutoAttach`, operator
  arms `bphw` on `%TEMP%\od-survivors.txt` addresses.
- BLK-0019 (content-distinct second replay) remains the promotion blocker.
