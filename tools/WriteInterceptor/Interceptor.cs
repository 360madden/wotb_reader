using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WotBTreader.WriteInterceptor;

/// <summary>
/// A single captured write to an armed address. <c>Kind</c> is <c>member</c>
/// for an originally-armed family address, or <c>source</c> for a
/// dynamically-armed copy-source address (FRESH38+: the coordinate is written
/// by a VCRUNTIME memcpy from a per-battle heap buffer held in esi, so arming
/// the source page can catch the game's own fill write site one level up).
/// </summary>
internal sealed record WriteHit(
    nuint Address,
    float Value,
    nuint Rip,
    string? Rva,
    string? InstructionHex,
    uint ThreadId,
    DateTimeOffset Utc,
    IReadOnlyDictionary<string, uint>? Registers,
    string Kind,
    string? ValueHex = null);

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
    private const int PostWriteReadRetries = 10;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly uint _pid;
    private readonly nuint[] _addresses;
    private readonly double _seconds;
    private readonly string _outPath;
    private readonly int _valueSize;
    private readonly bool _armSourceOnFirstHit;

    private readonly List<WriteHit> _hits = [];
    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<nuint, uint> _originalProtects = [];
    private readonly Dictionary<nuint, byte[]> _snapshots = [];
    private readonly List<nuint> _armedPages = [];
    private readonly List<ModuleEntry32> _modules = [];
    private readonly Dictionary<nuint, string?> _instructionHexByRip = [];

    // FRESH38+ dynamic source-arm: addresses copied FROM (esi at hit time),
    // discovered live and armed mid-trace so the game's own fill write site
    // (one level above the VCRUNTIME memcpy) can be caught in the same window.
    private readonly List<nuint> _sourceAddresses = [];
    private int _sourcePagesArmed;
    private const int MaxSourcePages = 8;
    private const int SourceSnapshotFloats = 4;

    private int _exceptionEvents;
    private int _guardEvents;
    private int _armedPageEvents;
    private int _foreignGuardEvents;
    private const int MaxDiagnostics = 400;
    private const int InstructionBytesToRead = 16;

    public WriteInterceptor(
        uint pid,
        nuint[] addresses,
        double seconds,
        string outPath,
        int valueSize = 4,
        bool armSourceOnFirstHit = false)
    {
        _pid = pid;
        _addresses = addresses;
        _seconds = seconds;
        _outPath = outPath;
        _valueSize = valueSize is 4 or 8 ? valueSize : 4;
        _armSourceOnFirstHit = armSourceOnFirstHit;
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
                AddDiagnostic($"open_process_failed win32={Marshal.GetLastWin32Error()}");
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
                AddDiagnostic($"debug_attach_failed win32={Marshal.GetLastWin32Error()} (already has a debugger?)");
                RestorePages(process);
                WriteReport(startedUtc, 4);
                return 4;
            }
            AddDiagnostic("attached_debug_active");

            // Snapshot modules while threads are still frozen so write-site
            // RIPs can be resolved to module-relative RVAs for durable evidence.
            EnsureModules();
            AddDiagnostic($"module_snapshot count={_modules.Count}");

            // Threads are frozen while we hold the CREATE_PROCESS event
            // pending; arm now with no race. Fail CLOSED if no page could be
            // armed: a capture loop on zero armed pages can never capture and
            // must not report success.
            if (!ArmPages(process))
            {
                AddDiagnostic("arm_after_attach_failed - no page armed");
                _ = NativeMethods.DebugActiveProcessStop(_pid);
                RestorePages(process);
                WriteReport(startedUtc, 3);
                return 3;
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
                        AddDiagnostic($"wait_for_debug_event_failed win32={win32}");
                        exitCode = 5; // unexpected debug-event failure is not success
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
        // Byte-exact snapshot (4 or 8 bytes per -ValueSize). NEVER a float
        // epsilon compare: a monotonic Double's low dword reinterpreted as
        // float is a tiny denormal (60.0 -> 60.016 changes 0x00000000 ->
        // 0x9374BC6A, a ~3e-38 float delta far below any epsilon), so the
        // write discriminator must compare the tracked bytes, not values.
        foreach (nuint address in _addresses)
        {
            if (TryReadBytes(process, address, _valueSize, out byte[] snapshot))
            {
                _snapshots[address] = snapshot;
                AddDiagnostic($"snapshot addr=0x{address:X8} size={_valueSize} bytes={Convert.ToHexString(snapshot)}");
            }
            else
            {
                AddDiagnostic($"snapshot_failed addr=0x{address:X8} win32={Marshal.GetLastWin32Error()}");
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
                AddDiagnostic($"virtual_query_failed page=0x{page:X8} win32={Marshal.GetLastWin32Error()}");
                continue;
            }

            uint state = BitConverter.ToUInt32(mbi, 16);
            uint protect = BitConverter.ToUInt32(mbi, 20);
            uint type = BitConverter.ToUInt32(mbi, 24);
            uint allocProtect = BitConverter.ToUInt32(mbi, 8);
            uint regionSize = BitConverter.ToUInt32(mbi, 12);

            AddDiagnostic($"query page=0x{page:X8} raw={Convert.ToHexString(mbi)} allocProtect=0x{allocProtect:X} region=0x{regionSize:X} state=0x{state:X} protect=0x{protect:X} type=0x{type:X}");
            if (state != 0x1000) // MEM_COMMIT
            {
                AddDiagnostic($"page_not_committed page=0x{page:X8} state=0x{state:X} protect=0x{protect:X}");
                continue;
            }

            // Mask to the low byte: VirtualQueryEx Protect can include CFG
            // bookkeeping bits (PAGE_TARGETS_INVALID 0x40000000) that
            // VirtualProtectEx rejects with ERROR_INVALID_PARAMETER (87).
            uint original = protect & 0xFF;
            if (original == 0 || (original & NativeMethods.PageNoAccess) != 0)
            {
                // original == 0 additionally guards the layout-misparse case
                // (a 32-byte MBI with PartitionId would read State into the
                // Protect slot and mask to 0x00, which the NOACCESS bit test
                // alone would miss and would arm a zero-access guard page).
                AddDiagnostic($"page_no_access page=0x{page:X8} orig=0x{original:X} - cannot arm");
                continue;
            }

            uint newProtect = original | NativeMethods.PageGuard;
            if (!NativeMethods.VirtualProtectEx(process, (nint)page, 0x1000, newProtect, out _))
            {
                AddDiagnostic($"arm_failed page=0x{page:X8} orig=0x{original:X} new=0x{newProtect:X} win32={Marshal.GetLastWin32Error()}");
                continue;
            }

            _originalProtects[page] = original;
            _armedPages.Add(page);
            AddDiagnostic($"page_armed page=0x{page:X8} orig=0x{original:X}");
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
            // re-arm every armed page. Count events that touched no armed
            // address (foreign guard users, e.g. stack growth) so a live run
            // can distinguish our traps from the target's own guard usage.
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

            // Post-access: reads leave the tracked bytes unchanged, writes
            // change them - the read/write discriminator (PAGE_GUARD traps on
            // ANY access). The write lands asynchronously after the thread
            // resumes, so retry the read briefly rather than trusting a single
            // settle sleep. Scan the original member addresses FIRST
            // (kind=member), then any dynamically-armed source addresses
            // (kind=source).
            bool anyChanged = false;
            foreach ((nuint address, string kind) in EnumerateTrackedAddresses())
            {
                if (!_snapshots.TryGetValue(address, out byte[]? previousBytes))
                {
                    continue;
                }

                bool changed = false;
                byte[]? current = null;
                for (int attempt = 0; attempt < PostWriteReadRetries; attempt++)
                {
                    if (TryReadBytes(process, address, previousBytes.Length, out byte[] read))
                    {
                        current = read;
                        if (!read.AsSpan().SequenceEqual(previousBytes))
                        {
                            changed = true;
                            break;
                        }
                    }

                    Thread.Sleep(1);
                }

                if (changed && current is not null)
                {
                    _snapshots[address] = current;
                    anyChanged = true;
                    string? instructionHex = TryReadInstructionHex(process, rip);
                    float value = current.Length >= 4
                        ? BitConverter.ToSingle(current, 0)
                        : 0f;
                    _hits.Add(new WriteHit(
                        address,
                        value,
                        rip,
                        ResolveRva(rip),
                        instructionHex,
                        de.ThreadId,
                        DateTimeOffset.UtcNow,
                        registers,
                        kind,
                        Convert.ToHexString(current)));
                    AddDiagnostic($"write_captured kind={kind} addr=0x{address:X8} bytes={Convert.ToHexString(current)} rip=0x{rip:X8} numParams={numParams}");
                }
            }

            // FRESH38+ source-arm: the first captured write to a member is a
            // memcpy store (esi = the per-battle heap source buffer). Arm the
            // source page NOW, in the same trace window, so the game's own
            // fill write to that buffer (one level above VCRUNTIME) can trap
            // before the window ends. Snapshots precede the arm (the two-pass
            // rule: reads on a guarded page fail with ERROR_PARTIAL_COPY).
            if (_armSourceOnFirstHit && registers is not null && anyChanged)
            {
                TryArmSourcePages(process, registers);
            }

            // Re-arm EVERY armed page: the one-shot guard bit was cleared by
            // the trap and cannot be located from the event. Includes any
            // source pages armed above.
            foreach (nuint page in _armedPages)
            {
                if (!NativeMethods.VirtualProtectEx(process, (nint)page, 0x1000, _originalProtects[page] | NativeMethods.PageGuard, out _))
                {
                    AddDiagnostic($"rearm_failed page=0x{page:X8} win32={Marshal.GetLastWin32Error()}");
                }
            }
            if (!anyChanged)
            {
                _foreignGuardEvents++;
            }

            AddDiagnostic($"guard_handled rip=0x{rip:X8} changed={anyChanged}");
            return;
        }

        // Everything else goes to the target's own handling (SEH, the loader
        // breakpoint, real crashes) untouched.
        _ = NativeMethods.ContinueDebugEvent(de.ProcessId, de.ThreadId, NativeMethods.DbgExceptionNotHandled);
    }

    /// <summary>
    /// Arms up to <see cref="MaxSourcePages"/> distinct pages containing the
    /// copy-source pointer (esi) captured at hit time, snapshotting the floats
    /// at that address first. The source page then traps on the game's own
    /// writes to the buffer (the fill site), which the post-access scan picks
    /// up as a <c>source</c>-kind hit. Fail-open on unreadable/committed/access
    /// failures: the member capture is already armed, a failed dynamic arm must
    /// not fail the whole trace.
    /// </summary>
    private void TryArmSourcePages(SafeProcessHandle process, IReadOnlyDictionary<string, uint> registers)
    {
        if (!registers.TryGetValue("esi", out uint esi) || esi == 0)
        {
            AddDiagnostic("source_arm skipped (no esi in registers)");
            return;
        }

        nuint source = esi;
        nuint page = source & PageMask;
        if (_originalProtects.ContainsKey(page))
        {
            AddDiagnostic($"source_arm skipped page_already_armed page=0x{page:X8}");
            return;
        }

        if (_sourcePagesArmed >= MaxSourcePages)
        {
            AddDiagnostic($"source_arm capped at {MaxSourcePages} pages");
            return;
        }

        // Snapshot BEFORE arming (reads on a guarded page fail/consume the
        // guard bit). Snapshot a few floats: the copy moves 16 bytes, so the
        // sibling slots are plausibly the same struct's other axes. The
        // snapshots are held in a LOCAL list and only committed to tracking
        // AFTER the arm succeeds: an unarmed page must never be scanned (the
        // game's source buffer drifts every frame, so a tracked-but-unarmed
        // address would emit false source-kind hits attributed to whatever
        // member RIP fired the current guard event).
        // Source slots are copied as 4-byte floats (the memcpy moves 16
        // bytes = 4 floats), so snapshot each 4-byte slot byte-exactly - the
        // same discriminator the member scan uses.
        var pending = new List<nuint>();
        for (int i = 0; i < SourceSnapshotFloats; i++)
        {
            nuint addr = source + (nuint)(i * 4);
            if (TryReadBytes(process, addr, sizeof(float), out byte[] snapshot))
            {
                pending.Add(addr);
                _snapshots[addr] = snapshot;
                AddDiagnostic($"source_snapshot addr=0x{addr:X8} bytes={Convert.ToHexString(snapshot)}");
            }
            else
            {
                AddDiagnostic($"source_snapshot_failed addr=0x{addr:X8} win32={Marshal.GetLastWin32Error()}");
            }
        }

        byte[] mbi = new byte[28];
        if (NativeMethods.VirtualQueryEx(process, (nint)page, mbi, (nuint)mbi.Length) == 0)
        {
            AddDiagnostic($"source_query_failed page=0x{page:X8} win32={Marshal.GetLastWin32Error()}");
            return;
        }

        uint state = BitConverter.ToUInt32(mbi, 16);
        uint protect = BitConverter.ToUInt32(mbi, 20);
        if (state != 0x1000)
        {
            AddDiagnostic($"source_page_not_committed page=0x{page:X8} state=0x{state:X}");
            return;
        }

        uint original = protect & 0xFF;
        if (original == 0 || (original & NativeMethods.PageNoAccess) != 0)
        {
            AddDiagnostic($"source_page_no_access page=0x{page:X8} orig=0x{original:X}");
            return;
        }

        if (!NativeMethods.VirtualProtectEx(process, (nint)page, 0x1000, original | NativeMethods.PageGuard, out _))
        {
            AddDiagnostic($"source_arm_failed page=0x{page:X8} orig=0x{original:X} win32={Marshal.GetLastWin32Error()}");
            return;
        }

        // Arm succeeded: NOW commit the snapshots to the tracked set (the
        // guard event that just fired cleared the one-shot bit, so the values
        // read above are pre-arm baselines and stay valid until a write traps).
        foreach (nuint addr in pending)
        {
            _sourceAddresses.Add(addr);
        }

        _originalProtects[page] = original;
        _armedPages.Add(page);
        _sourcePagesArmed++;
        AddDiagnostic($"source_page_armed page=0x{page:X8} esi=0x{source:X8} tracked={pending.Count} armed_count={_sourcePagesArmed}");
    }

    private void RestorePages(SafeProcessHandle process)
    {
        foreach ((nuint page, uint original) in _originalProtects)
        {
            _ = NativeMethods.VirtualProtectEx(process, (nint)page, 0x1000, original, out _);
        }
    }

    /// <summary>Member addresses first (kind=member), then dynamically-armed
    /// source addresses (kind=source). The member scan dominates the post-access
    /// discriminator; source slots catch the fill write one level up.</summary>
    private IEnumerable<(nuint Address, string Kind)> EnumerateTrackedAddresses()
    {
        foreach (nuint address in _addresses)
        {
            yield return (address, "member");
        }

        foreach (nuint address in _sourceAddresses)
        {
            yield return (address, "source");
        }
    }

    private static bool TryReadBytes(
        SafeProcessHandle process,
        nuint address,
        int length,
        out byte[] bytes)
    {
        byte[] buffer = new byte[length];
        if (NativeMethods.ReadProcessMemory(process, (nint)address, buffer, (nuint)length, out nuint read)
            && read == (nuint)length)
        {
            bytes = buffer;
            return true;
        }

        bytes = [];
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

    private string? TryReadInstructionHex(SafeProcessHandle process, nuint rip)
    {
        if (_instructionHexByRip.TryGetValue(rip, out string? cached))
        {
            return cached;
        }

        byte[] buffer = new byte[InstructionBytesToRead];
        if (!NativeMethods.ReadProcessMemory(process, (nint)rip, buffer, (nuint)buffer.Length, out nuint read)
            || read == 0)
        {
            AddDiagnostic($"instruction_read_failed rip=0x{rip:X8} win32={Marshal.GetLastWin32Error()}");
            _instructionHexByRip[rip] = null;
            return null;
        }

        int count = (int)read;
        var hex = string.Create(count * 2, (buffer, count), static (span, state) =>
        {
            ReadOnlySpan<byte> bytes = state.buffer.AsSpan(0, state.count);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                span[i * 2] = ToHexNibble(b >> 4);
                span[(i * 2) + 1] = ToHexNibble(b & 0xF);
            }
        });
        _instructionHexByRip[rip] = hex;
        return hex;
    }

    private static char ToHexNibble(int value) =>
        (char)(value < 10 ? ('0' + value) : ('A' + (value - 10)));

    private static string PathBasename(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return string.Empty;
        }
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
            AddDiagnostic($"module_snapshot_failed win32={Marshal.GetLastWin32Error()}");
            return;
        }

        ModuleEntry32 entry = new() { DwSize = (uint)Marshal.SizeOf<ModuleEntry32>() };
        if (!NativeMethods.Module32First(snapshot, ref entry))
        {
            AddDiagnostic($"module32_first_failed win32={Marshal.GetLastWin32Error()}");
            return;
        }

        do
        {
            _modules.Add(entry);
            entry.DwSize = (uint)Marshal.SizeOf<ModuleEntry32>();
        }
        while (NativeMethods.Module32Next(snapshot, ref entry));
    }

    private void AddDiagnostic(string line)
    {
        _diagnostics.Add(line); // direct list append (not a recursive call)
        if (_diagnostics.Count > MaxDiagnostics)
        {
            // Keep the head (setup/arm evidence) and the tail (last events)
            // rather than an unbounded list.
            _diagnostics.RemoveRange(MaxDiagnostics / 2, _diagnostics.Count - MaxDiagnostics);
        }
    }

    private void WriteReport(DateTimeOffset startedUtc, int exitCode)
    {
        // Basename-only module paths: never write full install paths into
        // durable evidence that may later be grepped or summarized.
        var modules = _modules.Select(m => new
        {
            name = m.SzModule ?? string.Empty,
            baseAddress = $"0x{(nuint)m.ModBaseAddr:X8}",
            size = m.ModBaseSize,
            pathBasename = PathBasename(m.SzExePath),
        }).ToArray();

        var report = new
        {
            mode = "interceptor",
            pid = _pid,
            addresses = _addresses.Select(a => $"0x{a:X8}").ToArray(),
            pagesArmed = _armedPages.Count,
            exceptionEvents = _exceptionEvents,
            guardEvents = _guardEvents,
            armedPageEvents = _armedPageEvents,
            foreignGuardEvents = _foreignGuardEvents,
            durationSeconds = _seconds,
            startedUtc = startedUtc.ToString("o"),
            finishedUtc = DateTimeOffset.UtcNow.ToString("o"),
            modules,
            sourcePagesArmed = _sourcePagesArmed,
            hits = _hits.Select(h => new
            {
                address = $"0x{h.Address:X8}",
                // value stays the float32 read of the low 4 bytes for the
                // legacy float consumers; valueHex is the exact tracked bytes
                // (authoritative for 8-byte Double fields like replayTime -
                // the float interpretation of a Double's low dword is a
                // meaningless denormal, and can even be NaN/Infinity, which
                // JsonSerializer refuses - emit null then, valueHex is exact).
                value = float.IsFinite(h.Value) ? h.Value : (float?)null,
                valueHex = h.ValueHex,
                rip = $"0x{h.Rip:X8}",
                rva = h.Rva ?? "jit",
                instructionHex = h.InstructionHex,
                threadId = h.ThreadId,
                utc = h.Utc.ToString("o"),
                registers = h.Registers,
                kind = h.Kind,
            }).ToArray(),
            diagnostics = _diagnostics,
            exitCode,
        };

        File.WriteAllText(_outPath, JsonSerializer.Serialize(report, JsonOptions));
    }
}
