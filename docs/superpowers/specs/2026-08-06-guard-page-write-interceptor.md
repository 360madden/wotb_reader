# Spec: C# guard-page write interceptor (M2 successor)

**Date:** 2026-08-06
**Status:** Design + scaffold (buildable offline); live integration follows.
**Supersedes:** x64dbg write-BP capture (CLOSED — see
[`handoffs/2026-08-06-fresh32-33-x64dbg-write-bp-route-dead.md`](../../operations/handoffs/2026-08-06-fresh32-33-x64dbg-write-bp-route-dead.md)).

## Why

The x64dbg write-BP route is conclusively dead in this environment (in-script
`bpm` errors; `bpm`/`bph` never fire even on a constantly-writing synthetic
target; worker-thread writes escape main-thread DR BPs; every UIA log read was
reading chrome). The replacement must be something we fully control and can
prove offline before any live session.

## Core mechanism

OS-level guard pages work (they are hardware-enforced via page protections);
what was broken was x64dbg's handling of them in this environment. We
implement the standard memory-BP loop ourselves:

1. `VirtualProtectEx(pid, pageOf(addr), originalProtect | PAGE_GUARD)` — arm
   the page containing each armed address.
2. `DebugActiveProcess(pid)` — attach as the process's ONLY debugger.
3. Debug-event loop on a background thread: `WaitForDebugEvent` →
   `ContinueDebugEvent(DBG_CONTINUE)` for everything, EXCEPT
   `STATUS_GUARD_PAGE_VIOLATION (0x80000001)` whose
   `ExceptionRecord.ExceptionAddress` lands in an armed page:
   - **record the hit**: `ExceptionAddress` IS the write-site instruction
     pointer (no GetThreadContext needed for the core evidence),
   - read the written value via `ReadProcessMemory` (4 bytes),
   - resolve the module-relative RVA from the module list,
   - `ContinueDebugEvent(DBG_CONTINUE)` → the guard page was auto-cleared by
     the OS, so the write completes,
   - **re-arm** the page (`VirtualProtectEx` again) for the next access.
4. On window end: restore original protections, `DebugActiveProcessStop`,
   write the hit report (address, RIP, RVA, value, count, timestamps).

The process keeps running the whole time (no breakin, no freeze) — this
removes the WOW64 attach-freeze class entirely.

## Bitness (the decisive architecture fact)

`DebugActiveProcess` requires **same-bitness**. The game is 32-bit; the host
is 64-bit. The interceptor therefore runs as a **32-bit helper process** —
the same reason x64dbg ships a separate `x32dbg.exe`. As a 32→32 debugger the
helper also gets full `CONTEXT_i386` registers via `GetThreadContext` (the
base-register + field-displacement evidence) with no WOW64 gymnastics.

## Placement (no architecture-test changes)

- New project **`tools/WriteInterceptor/`** — `net10.0` TFM (passes the
  `TargetFrameworkTests` allowlist unchanged), `PlatformTarget x86`,
  `OutputType Exe`, no package refs.
- `tools/` tools are not scanned by `NativeAccessBoundaryTests` (which covers
  Host.Web / GameHarness / GameIntegration / UltimateScanner), so the helper
  needs **no allowlist amendment** and **no reference-graph change**
  (UltimateScanner's "referenced only by GameIntegration" rule stays intact —
  the helper is standalone).
- Two modes in one exe:
  - `--interceptor -Pid <n> -Addresses <csv> -Seconds <n> -Out <json>` — the
    capture loop.
  - `--counter -AddrFile <path> -ProgressFile <path>` — the synthetic target
    (writes a float to a static field in a loop, publishes the field address,
    writes a progress counter to a file). This gives a fully offline,
    self-contained mechanism test with no external compiler.

## Evidence captured per hit (matches the campaign's writer-evidence goal)

- armed address + written value (4 bytes, float)
- **RIP** (ExceptionAddress) + module-relative **RVA** (best-effort; JIT code
  reported as `jit`)
- thread id + wall timestamp
- full register dump (32-bit context) — scaffolded; consumed in the live
  integration step for base-register/displacement classification.

## Offline proof (before any live session)

`tmpwotb-e2e/test-guard-interceptor.ps1`:
1. `dotnet build tools/WriteInterceptor -c Release`
2. start `--counter`, read its published address + pid
3. run `--interceptor -Pid <pid> -Addresses <addr> -Seconds 5 -Out report.json`
4. assert `hits >= 1` with a plausible RIP (inside the counter's JIT region),
   the written value present, and the counter's progress advancing during the
   window (liveness), plus a control run with a NON-written address that must
   yield 0 hits (selectivity).

If the offline proof passes, the interceptor mechanism is PROVEN in C# — the
thing x64dbg could never do here.

## Live integration (next milestone step, no live session yet)

- od-048's trace invocation switches from `x64dbg-write-trace.ps1` to
  launching the interceptor helper against the armed family (attach-only, no
  second-debugger conflict since the smoke no longer attaches x64dbg).
- The smoke's pause/resume CPU verification is replaced by the existing
  `GetProcessTimes` liveness check (no debugger needed).
- Report shape stays `od-048-autotrace-*.json`-compatible where possible.
