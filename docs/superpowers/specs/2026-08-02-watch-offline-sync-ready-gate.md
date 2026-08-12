# Design: WATCH OFFLINE ready gate (sync dim / overlay)

Date: 2026-08-02  
Status: approved-to-implement (owner: dim+sync screenshots)  
Scope: Reliable click of **WATCH OFFLINE** only when the dialog is interactive.

## Problem

The orange button appears before the client finishes account sync. A few seconds
later the dialog **dims** and shows **“Synchronizing account…”**. Clicks during
that window often do not register (dialog may dismiss while Host stays
`awaiting_evidence`). Clicking on first orange sighting is too early; a long
blind settle is too late (dialog error).

## Visual evidence (owner screenshots)

| State | Dialog mean L | Orange px (existing heuristic) | Notes |
|-------|---------------:|-------------------------------:|-------|
| Ready (bright) | ~59 | ~8287 | Full brightness, no sync text |
| Syncing (dim) | ~31 | ~63 | Dim + “Synchronizing account…” |

Primary **do-not-click** signal: **dimming** (dialog mean luminance collapse).
Do **not** treat “low orange alone” as sync — high-luminance / low-orange frames
are splash or other UI and armed a false `SeenSyncing` that later clicked Profile
art. White-pixel counts alone are unreliable (button labels are also white).

## State machine

```text
LookingForDialog
  → (orange present) WaitingForReady
       → (lum low / orange collapsed) SeenSyncing
            → (lum high AND orange strong, stable N samples) Ready
                 → hold → click
       → (grace elapsed without dim, still bright+orange stable) Ready
                 → hold → click
```

**Ready** requires all of:
1. Orange blob ≥ strong threshold (default 2000 px in dialog ROI).
2. Dialog-ROI mean luminance ≥ ready floor (default 45).
3. Either `SeenSyncing` was observed (dim L≈18–40 with orange≈30–400) then
   first bright recovery, **or** bright grace: ≥`SyncGraceSeconds` (default 5)
   since first bright **and** ≥`MinDialogAgeSeconds` (default 5) since dialog
   appear. Live blitz-logs: Start replay ~8–9s after dialog; clicks at ~2–3s
   dismiss without Start; ErrorDialog ~11–13s → hard cap 11s.
4. **One** ready sample → click immediately (no hold). Blank frames (L≈0) are
   not sync.

Never click green **LOG IN AND WATCH**.

## Implementation

`scripts/click-watch-offline.ps1` — extend capture analysis with dialog mean
luminance; replace “stable orange then click” with the state machine above.
`scripts/launch-offline-replay-for-od.ps1` — keep calling the clicker; no blind
long settle.

## Timeouts

- AppearTimeoutSeconds: wait for first dialog (orange or dim dialog).
- ReadyTimeoutSeconds: wait for ready after dialog (covers sync duration).
- MaxDialogLifetimeSeconds: absolute ceiling from first dialog → click.
  (A stale "slow clicks → Error 126" attribution was superseded 2026-08-12:
  Error 126 is a replay client-version mismatch — the game refuses a replay
  whose version family differs from the installed game. The launcher's
  pre-flight probe guard catches that before the launch dance; see
  `launch-offline-replay-for-od.ps1` and the ledger.)
- Exit 5 if ready never reached; exit 3 if click rounds fail gate dual-check.

## Non-goals

OCR of “Synchronizing account…”, template matching, UIA.
