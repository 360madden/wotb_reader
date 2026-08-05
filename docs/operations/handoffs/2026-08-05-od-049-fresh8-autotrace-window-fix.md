# OD-049 FRESH8: auto-write-trace arms the real x/z pair — window-drive blocker found & fixed

**Date:** 2026-08-05 · **Branch:** main · **Session:** FRESH8 (third full live M1→M2 run)

## Outcome

The M1→M2 same-launch choreography worked end to end *except the final
debugger window-drive*:

- `family_refined round=10 survivors=25 neighbors_added=149` (the M2
  neighbor-staging fix from the 10-round bug hunt held).
- Monitor completed all 70 rounds: **218,940 samples over 3,149 addresses**.
- Correlate: `verdict=evidence-strong strong_survivors=58 families=252
  complete_families=0` (x/z-pair layout confirmed again; no y member — the
  live struct is `[x][z]` adjacent, per the FRESH6 analysis).
- Family selection picked the **real x/z sibling pair** (the FRESH6
  selection-quality fix held): `family complete=false axes=x,z write_size=4`,
  `family_members_armed=2 unarmed=0 dr_limit=4`.
- Pre-arm attached x32dbg to the live game: `launched_x64dbg_attach
  pid=<game> process=14964`, marker OK, `debugger_armed`.
- **Blocker:** `FAILED_x64dbg_no_window` → exit 3. The script found the
  debugger process but its `MainWindowHandle` was zero, so the command-bar
  injection (scriptload/scriptrun) never ran and no hits were collected.

## Root cause analysis

- A controlled probe (attach x32dbg to a dummy target) showed the main window
  appears in **0.5 s** on a normal attach — so the failure was specific to the
  live-game attach, not a generic slowness.
- `Get-X64DbgProcess` matches the first x64dbg/x32dbg process with **no window
  filter**, so `Invoke-AutoPreArm`'s 15 s wait returned as soon as the process
  existed (≈ immediately after `Start-Process`), before the Qt main window was
  guaranteed to be up.
- Step 4 then sampled `MainWindowHandle` **exactly once** on a fresh Process
  object; .NET computes (and caches) that value on first access, so a sample
  taken mid-window-creation permanently reads 0 for that object.

## Fix (scripts/x64dbg-write-trace.ps1)

1. **`Wait-X64DbgWindow -TimeoutSeconds <n>`** — polls for a window-ready
   debugger process (fresh Process object per poll) up to `-WindowWaitSeconds`
   (new param, default 20 s).
2. **`Get-X64DbgWindowHandle`** — returns `{Id, Handle}`; falls back to
   **EnumWindows** (`WtX64Gui.WindowsForProcess`) for Qt windows
   `MainWindowHandle` can miss during creation states.
3. **`Invoke-AutoPreArm`** now waits for the *window* (15 s), not just the
   process.
4. **Failure diagnostics** — on timeout the script logs pid, `Responding`, and
   the debugger's top-level window titles (`WtX64Gui.WindowTitles`), so a
   repeat failure distinguishes a lag race from a windowless/hung attach.

## Validation (offline)

- Parse clean on PS 5.1 + pwsh 7; ASCII clean.
- PSSA gate passed (52 tracked files; 2 pre-existing advisory warnings).
- Dry-run against the FRESH8 report re-arms the real pair:
  `bph 0x29D957DC,w,4` + `bph 0x29D957E0,w,4` (the extended C# Add-Type
  compiles; selection logic unchanged and still correct).

## Notes

- `play_state=unknown` on this run (the HUD pixel probe). Non-fatal — no
  pause was needed for the trace window — but worth one look if we later need
  pause-awareness during a trace.
- Correlate observation cap: 3,149 observations truncated to the server cap
  2,000 (family neighbors kept). Results scored: 1,332.

## Next

FRESH9 — relaunch the fixed pipeline live. Success criteria: `wt_x64:
x64dbg_pid=…` (window found), `injected scriptload+scriptrun`, and an
`od-048-autotrace-*.json` hit report with captured addr→rip evidence for
`0x29D957DC` / `0x29D957E0`.
