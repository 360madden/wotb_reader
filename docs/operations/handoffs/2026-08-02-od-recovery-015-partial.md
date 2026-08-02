# Session handoff — 2026-08-02: OD-RECOVERY-015 process amend + Partial

**Author:** Codex Agent

**Branch:** `main`

## Outcome

`OD-RECOVERY-015` is **Partial** (process fixed; rolling completed to ≤10).

### Process amend (durable)

Flaw: treating file-association (`Invoke-Item` on
`%LOCALAPPDATA%\wotblitz\DAVAProject\replays\*.wotbreplay`) as the OD launch
path. Playback can succeed (replay HUD) while Host stays `Denied` /
`lifecycle_evidence_timeout`, so discover APIs refuse and Watch Offline
retries are wasted.

Canonical path now:

```text
powershell -File scripts/launch-offline-replay-for-od.ps1
```

Folder `.wotbreplay` → CLI import → managed `/launch` → settle →
`scripts/click-watch-offline.ps1` (capability re-read each poll; exit 6 on
`Denied`).

Docs: `docs/operations/offset-discovery-workflow.md`,
`docs/superpowers/specs/2026-08-02-watch-offline-color-blob.md`.

### Live pulse

- Game-folder Churchill `.wotbreplay` imported (content sha12 `0FAE5612491E`;
  CLI reported `duplicate=True` → not proven independent of OD-012/013).
- Gate reached `OfflineReplayVerified` via managed launch.
- First rolling pulse: **92793→…→260**, then `EvidenceStale`.
- Completion pulse (immediate post-verify): Double increased rolling
  **882617→151684→23719→3670→472→84→18→7** (`private-mapping`).
- Session discarded; no promotion; `independentReplays` still 0.

## Next move

1. Commit this closeout when asked.
2. `OD-RECOVERY-016`: interactive debugger / root on the ≤7 set; place a
   content-distinct second `.wotbreplay` in the game folder for BLK-0019.
