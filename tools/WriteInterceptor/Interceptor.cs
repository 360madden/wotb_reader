using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WotBTreader.WriteInterceptor;

/// <summary>A single captured write to an armed address.</summary>
internal sealed record WriteHit(
    nuint Address,
    float Value,
    nuint Rip,
    string? Rva,
    uint ThreadId,
    DateTimeOffset Utc,
    IReadOnlyDictionary<string, uint>? Registers);

/// <summary>
/// The C#-native replacement for the (dead) x64dbg write-BP capture:
/// arm PAGE_GUARD on the pages holding the armed addresses, attach as the
/// process's only debugger, and on STATUS_GUARD_PAGE_VIOLATION record the
/// exception address (the write-site instruction pointer) plus the written
/// value and the faulting thread's i386 registers, then swallow, re-arm, and
/// continue. The debuggee keeps running the whole time (no breakin, no
/// freeze) — the WOW64 attach-freeze class is gone by construction.
/// </summary>
internal sealed class WriteInterceptor
{
    private static readonly nuint PageMask = ~(nuint)0xFFF;
    private const uint WaitTimeoutMs = 200;
    private const int ContinueSettleMs = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly uint _pid;
    private readonly nuint[] _addresses;
    private readonly double _seconds;
    private readonly string _outPath;

    private readonly List<WriteHit> _hits = [];
    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<nuint, uint> _originalProtects = [];
    private readonly Dictionary<nuint, float> _snapshots = [];
    private readonly List<nuint> _armedPages = [];
    private readonly List<ModuleEntry32> _modules = [];
    private int _exceptionEvents;
    private int _guardEvents;
    private int _armedPageEvents;

    public WriteInterceptor(uint pid, nuint[] addresses, double seconds, string outPath)
    {
        _pid = pid;
        _addresses = addresses;
        _seconds = seconds;
        _outPath = outPath;
    }

    public int Run()
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var deadline = startedUtc.AddSeconds(_seconds);
        int exitCode = 0;

        using (SafeProcessHandle process = NativeMethods.OpenProcess(
            NativeMethods.ProcessVmOperation | NativeMethods.ProcessVmRead | NativeMethods.ProcessVmWrite | NativeMethods.ProcessQueryInformation,
            bInheritHandle: false,
            _pid))
        {
            if (process.IsInvalid)
            {
                _diagnostics.Add($"open_process_failed win32={Marshal.GetLastWin32Error()}");
                WriteReport(startedUtc, 2);
                return 2;
            }

            // Attach FIRST, then arm: DebugActiveProcess freezes the target's
            // threads until the first ContinueDebugEvent, so arming after
            // attach has no race window. Arming first lets the still-running
            // target touch the guard page before the debugger is attached;
            // the OS clears the one-shot guard bit and the runtime's SEH
            // swallows the violation, silently disarming the trap (observed:
            // zero events for the armed page, only the runtime's own guard
            // event).
            if (!NativeMethods.DebugActiveProcess(_pid))
            {
                _diagnostics.Add($"debug_attach_failed win32={Marshal.GetLastWin32Error()} (already has a debugger?)");
                RestorePages(process);
                WriteReport(startedUtc, 4);
                return 4;
            }
            _diagnostics.Add("attached_debug_active");

            // Threads are frozen while we hold the CREATE_PROCESS event
            // pending; arm now with no race.
            if (!ArmPages(process))
            {
                _diagnostics.Add("arm_after_attach_failed");
            }

            try
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    if (!NativeMethods.WaitForDebugEvent(out DebugEvent de, WaitTimeoutMs))
                    {
                        int win32 = Marshal.GetLastWin32Error();
                        if (win32 is 258 or 121)
                        {
                            // 258 WAIT_TIMEOUT and 121 ERROR_SEM_TIMEOUT are
                            // both benign timeout paths observed on this OS;
                            // the event queue was simply empty.
                            continue;
                        }
                        _diagnostics.Add($"wait_for_debug_event_failed win32={win32}");
                        break;
                    }

                    if (de.DebugEventCode == NativeMethods.ExceptionDebugEvent)
                    {
                        _exceptionEvents++;
                        HandleExceptionEvent(process, de);
                    }
                    else
                    {
                        // CREATE/EXIT/LOAD/UNLOAD/OUTPUT events: just continue.
                        _ = NativeMethods.ContinueDebugEvent(de.ProcessId, de.ThreadId, NativeMethods.DbgContinue);
                    }
                }
            }
            finally
            {
                try
                {
                    _ = NativeMethods.DebugActiveProcessStop(_pid);
                }
                catch
                {
                    // Best-effort detach.
                }

                RestorePages(process);
            }
        }

        WriteReport(startedUtc, exitCode);
        return exitCode;
    }

    private bool ArmPages(SafeProcessHandle process)
    {
        // Two passes: (1) snapshot every address while its page is still
        // unprotected, (2) arm the page guards. ReadProcessMemory on a
        // PAGE_GUARD page fails with ERROR_PARTIAL_COPY (299) and/or consumes
        // the one-shot guard bit, silently disarming the trap - the snapshot
        // must always precede the arm.
        foreach (nuint address in _addresses)
        {
            if (TryReadFloat(process, address, out float value))
            {
                _snapshots[address] = value;
                _diagnostics.Add($"snapshot addr=0x{address:X8} value={value:F4}");
            }
            else
            {
                _diagnostics.Add($"snapshot_failed addr=0x{address:X8} win32={Marshal.GetLastWin32Error()}");
            }
        }

        foreach (nuint address in _addresses)
        {
            nuint page = address & PageMask;
            if (_originalProtects.ContainsKey(page))
            {
                continue;
            }

            // Query into a raw buffer and parse at hard-coded offsets.
            // Observed on this machine: the kernel writes the pre-1607
            // 28-byte MEMORY_BASIC_INFORMATION (no PartitionId field):
            //   0 Base, 4 AllocBase, 8 AllocProtect, 12 RegionSize,
            //   16 State, 20 Protect, 24 Type
            // (evidence: struct-marshal read state=0x4/protect=0x40000
            // which is the shifted Protect/Type). The raw hexdump stays in
            // diagnostics so any future layout change is visible.
            byte[] mbi = new byte[28];
            if (NativeMethods.VirtualQueryEx(process, (nint)page, mbi, (nuint)mbi.Length) == 0)
            {
                _diagnostics.Add($"virtual_query_failed page=0x{page:X8} win32={Marshal.GetLastWin32Error()}");
                continue;
            }

            uint state = BitConverter.ToUInt32(mbi, 16);
            uint protect = BitConverter.ToUInt32(mbi, 20);
            uint type = BitConverter.ToUInt32(mbi, 24);
            uint allocProtect = BitConverter.ToUInt32(mbi, 8);
            uint regionSize = BitConverter.ToUInt32(mbi, 12);

            _diagnostics.Add($"query page=0x{page:X8} raw={Convert.ToHexString(mbi)} allocProtect=0x{allocProtect:X} region=0x{regionSize:X} state=0x{state:X} protect=0x{protect:X} type=0x{type:X}");
            if (state != 0x1000) // MEM_COMMIT
            {
                _diagnostics.Add($"page_not_committed page=0x{page:X8} state=0x{state:X} protect=0x{protect:X}");
                continue;
            }

            // Mask to the low byte: VirtualQueryEx Protect can include CFG
            // bookkeeping bits (PAGE_TARGETS_INVALID 0x40000000) that
            // VirtualProtectEx rejects with ERROR_INVALID_PARAMETER (87).
            uint original = protect & 0xFF;
            if ((original & NativeMethods.PageNoAccess) != 0)
            {
                _diagnostics.Add($"page_no_access page=0x{page:X8} - cannot arm");
                continue;
            }

            uint newProtect = original | NativeMethods.PageGuard;
            if (!NativeMethods.VirtualProtectEx(process, (nint)page, 0x1000, newProtect, out _))
            {
                _diagnostics.Add($"arm_failed page=0x{page:X8} orig=0x{original:X} new=0x{newProtect:X} win32={Marshal.GetLastWin32Error()}");
                continue;
            }

            _originalProtects[page] = original;
            _armedPages.Add(page);
            _diagnostics.Add($"page_armed page=0x{page:X8} orig=0x{original:X}");
        }

        return _armedPages.Count > 0;
    }

    private void HandleExceptionEvent(SafeProcessHandle process, in DebugEvent de)
    {
        // x86 EXCEPTION_DEBUG_INFO offsets inside the 160-byte union:
        //   ExceptionCode ................ 0
        //   ExceptionAddress (RIP) ....... 12  (the faulting instruction)
        //   NumberParameters ............. 16
        //   ExceptionInformation[0] ...... 20  (the DATA address for
        //                                       access violations; observed
        //                                       EMPTY (0) for guard events)
        //   dwFirstChance ................ 80
        uint exceptionCode = BitConverter.ToUInt32(de.Union, 0);
        nuint rip = BitConverter.ToUInt32(de.Union, 12);
        uint numParams = BitConverter.ToUInt32(de.Union, 16);

        if (exceptionCode == NativeMethods.StatusGuardPageViolation)
        {
            _guardEvents++;
            // ExceptionInformation is not populated for guard events on this
            // OS (observed data=0x0), so the touched page cannot be identified
            // from the event; instead scan all armed addresses post-access and
            // re-arm every armed page.
            _armedPageEvents++;

            // The faulting thread is suspended while we hold the event: grab
            // its i386 registers NOW (the accurate at-exception snapshot).
            IReadOnlyDictionary<string, uint>? registers = TryReadRegisters(de.ThreadId, _diagnostics);

            // Swallow the exception: the OS auto-cleared the guard page, so
            // the access completes and the debuggee never notices. The OS then
            // re-executes the faulting instruction (guard now clear), so the
            // write lands a moment after we continue - wait for it before
            // reading the post-access value.
            _ = NativeMethods.ContinueDebugEvent(de.ProcessId, de.ThreadId, NativeMethods.DbgContinue);
            Thread.Sleep(ContinueSettleMs);

            // Post-access: reads leave the value unchanged, writes change it -
            // the read/write discriminator (PAGE_GUARD traps on ANY access).
            bool anyChanged = false;
            foreach (nuint address in _addresses)
            {
                if (!_snapshots.ContainsKey(address) || !TryReadFloat(process, address, out float value))
                {
                    continue;
                }

                float previous = _snapshots[address];
                if (MathF.Abs(value - previous) > 0.0001f)
                {
                    _snapshots[address] = value;
                    anyChanged = true;
                    _hits.Add(new WriteHit(
                        address,
                        value,
                        rip,
                        ResolveRva(rip),
                        de.ThreadId,
                        DateTimeOffset.UtcNow,
                        registers));
                    _diagnostics.Add($"write_captured addr=0x{address:X8} value={value:F4} rip=0x{rip:X8} numParams={numParams}");
                }
            }

            // Re-arm EVERY armed page: the one-shot guard bit was cleared by
            // the trap and cannot be located from the event.
            foreach (nuint page in _armedPages)
            {
                if (!NativeMethods.VirtualProtectEx(process, (nint)page, 0x1000, _originalProtects[page] | NativeMethods.PageGuard, out _))
                {
                    _diagnostics.Add($"rearm_failed page=0x{page:X8} win32={Marshal.GetLastWin32Error()}");
                }
            }
            _diagnostics.Add($"guard_handled rip=0x{rip:X8} changed={anyChanged}");
            return;
        }

        // Everything else goes to the target's own handling (SEH, the loader
        // breakpoint, real crashes) untouched.
        _ = NativeMethods.ContinueDebugEvent(de.ProcessId, de.ThreadId, NativeMethods.DbgExceptionNotHandled);
    }

    private void RestorePages(SafeProcessHandle process)
    {
        foreach ((nuint page, uint original) in _originalProtects)
        {
            _ = NativeMethods.VirtualProtectEx(process, (nint)page, 0x1000, original, out _);
        }
    }

    private static bool TryReadFloat(SafeProcessHandle process, nuint address, out float value)
    {
        byte[] buffer = new byte[4];
        if (NativeMethods.ReadProcessMemory(process, (nint)address, buffer, 4, out nuint read) && read == 4)
        {
            value = BitConverter.ToSingle(buffer, 0);
            return true;
        }

        value = 0f;
        return false;
    }

    private static Dictionary<string, uint>? TryReadRegisters(uint threadId, List<string> diagnostics)
    {
        try
        {
            // Empirically, GetThreadContext fails with ACCESS_DENIED (5)
            // unless the handle has broad rights (THREAD_ALL_ACCESS worked,
            // the minimal GET_CONTEXT mask failed) - the debugger owns the
            // thread, so all-access is legitimate.
            using SafeThreadHandle thread = NativeMethods.OpenThread(
                NativeMethods.ThreadAllAccess,
                bInheritHandle: false,
                threadId);
            if (thread.IsInvalid)
            {
                diagnostics.Add($"open_thread_failed tid={threadId} win32={Marshal.GetLastWin32Error()}");
                return null;
            }

            Context context = new()
            {
                ContextFlags = NativeMethods.ContextFull,
                FloatSave = new FloatSaveArea { RegisterArea = new byte[80] },
                ExtendedRegisters = new byte[512],
            };
            // GetThreadContext fails with ERROR_ACCESS_DENIED (5) on a thread
            // that is not explicitly suspended, even when the debugger has
            // frozen it. Suspend/resume around the read is the standard
            // pattern; the suspend count is balanced by the resume.
            // (THREAD_SUSPEND_RESUME access is required for SuspendThread.)
            uint suspendCount = NativeMethods.SuspendThread(thread);
            if (suspendCount == uint.MaxValue)
            {
                diagnostics.Add($"suspend_thread_failed tid={threadId} win32={Marshal.GetLastWin32Error()}");
                return null;
            }

            bool ok = NativeMethods.GetThreadContext(thread, ref context);
            int win32 = Marshal.GetLastWin32Error();
            _ = NativeMethods.ResumeThread(thread);
            if (!ok)
            {
                diagnostics.Add($"get_thread_context_failed tid={threadId} win32={win32}");
                return null;
            }

            if ((context.ContextFlags & NativeMethods.ContextFull) == 0)
            {
                diagnostics.Add($"context_flags_empty tid={threadId}");
                return null;
            }

            return new Dictionary<string, uint>
            {
                ["eax"] = context.Eax,
                ["ebx"] = context.Ebx,
                ["ecx"] = context.Ecx,
                ["edx"] = context.Edx,
                ["esi"] = context.Esi,
                ["edi"] = context.Edi,
                ["ebp"] = context.Ebp,
                ["esp"] = context.Esp,
                ["eip"] = context.Eip,
                ["eflags"] = context.EFlags,
            };
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveRva(nuint rip)
    {
        EnsureModules();
        foreach (ModuleEntry32 module in _modules)
        {
            nuint baseAddress = (nuint)module.ModBaseAddr;
            if (rip >= baseAddress && rip < baseAddress + module.ModBaseSize)
            {
                return $"{module.SzModule}+0x{rip - baseAddress:X}";
            }
        }

        return null; // JIT code or an unmapped region (the synthetic counter).
    }

    private void EnsureModules()
    {
        if (_modules.Count > 0)
        {
            return;
        }

        using SafeSnapshotHandle snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapshotModule | NativeMethods.Th32csSnapshotModule32,
            _pid);
        if (snapshot.IsInvalid)
        {
            _diagnostics.Add($"module_snapshot_failed win32={Marshal.GetLastWin32Error()}");
            return;
        }

        ModuleEntry32 entry = new() { DwSize = (uint)Marshal.SizeOf<ModuleEntry32>() };
        if (!NativeMethods.Module32First(snapshot, ref entry))
        {
            return;
        }

        do
        {
            _modules.Add(entry);
            entry.DwSize = (uint)Marshal.SizeOf<ModuleEntry32>();
        }
        while (NativeMethods.Module32Next(snapshot, ref entry));
    }

    private void WriteReport(DateTimeOffset startedUtc, int exitCode)
    {
        var report = new
        {
            mode = "interceptor",
            pid = _pid,
            addresses = _addresses.Select(a => $"0x{a:X8}").ToArray(),
            pagesArmed = _armedPages.Count,
            exceptionEvents = _exceptionEvents,
            guardEvents = _guardEvents,
            armedPageEvents = _armedPageEvents,
            durationSeconds = _seconds,
            startedUtc = startedUtc.ToString("o"),
            finishedUtc = DateTimeOffset.UtcNow.ToString("o"),
            hits = _hits.Select(h => new
            {
                address = $"0x{h.Address:X8}",
                value = h.Value,
                rip = $"0x{h.Rip:X8}",
                rva = h.Rva ?? "jit",
                threadId = h.ThreadId,
                utc = h.Utc.ToString("o"),
                registers = h.Registers,
            }).ToArray(),
            diagnostics = _diagnostics,
            exitCode,
        };

        File.WriteAllText(_outPath, JsonSerializer.Serialize(report, JsonOptions));
    }
}
