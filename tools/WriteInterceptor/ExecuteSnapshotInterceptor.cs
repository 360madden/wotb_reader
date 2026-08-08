using System.Diagnostics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WotBTreader.WriteInterceptor;

internal sealed class ExecuteSnapshotInterceptor : IDisposable
{
    private const uint WaitTimeoutMilliseconds = 100;
    // A verified 11.19.0.10 offline-replay process was observed with 164
    // threads. Keep complete breakpoint coverage bounded, but above that
    // measured runtime requirement so a no-hit result remains meaningful.
    private const int MaximumThreads = 256;
    private const int SnapshotBytes = 12;
    private const uint ResumeFlag = 0x00010000;
    private const uint Dr0OwnedBit = 0x1;
    private const uint Dr0GlobalBit = 0x2;
    private const uint Dr0TypeLengthMask = 0x000F0000;
    private const uint MemCommit = 0x1000;
    private const uint MemImage = 0x01000000;
    private const string SupportedGameVersion = "11.19.0.10";
    private const string SupportedGameSha256 =
        "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";
    private const uint SupportedRva = 0x022FA78D;
    private const string SupportedInstructionHex = "F30F7E00";
    private const string SupportedCaptureKind = "type10-entity-position";
    private const int SupportedEntityIdDisplacement = 0x1c;
    private static readonly string ExpectedCoordinatorSha256 =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(static attribute => string.Equals(
                attribute.Key,
                "WotBTreader.ExpectedCoordinatorSha256",
                StringComparison.Ordinal))
            ?.Value ?? string.Empty;
    private static readonly string ExpectedCoordinatorAssemblySha256 =
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(static attribute => string.Equals(
                attribute.Key,
                "WotBTreader.ExpectedCoordinatorAssemblySha256",
                StringComparison.Ordinal))
            ?.Value ?? string.Empty;
    private readonly ExecuteSnapshotPlan _plan;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<uint, DebugRegisterState> _threadStates = [];
    private readonly List<ExecuteSnapshotHit> _hits = [];
    private readonly List<string> _diagnostics = [];
    private readonly Dictionary<uint, DateTimeOffset> _lastAcceptedByObject = [];
    private ExecuteSnapshotTarget? _target;
    private bool _attached;
    private bool _debuggerExitTerminatesTarget;
    private bool _bootstrapBreakpointObserved;
    private bool _cleanupProven;
    private bool _detached;
    private bool _targetExited;
    private bool _debugEventPending;
    private SafeProcessHandle? _debugEventProcessHandle;
    private int _threadsSeen;
    private int _threadsArmed;
    private int _threadsFailed;
    private int _threadsRestored;
    private int _matchingBreakpointEvents;

    internal ExecuteSnapshotInterceptor(
        ExecuteSnapshotPlan plan,
        CancellationToken cancellationToken)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _cancellationToken = cancellationToken;
    }

    internal (int ExitCode, ExecuteSnapshotReport Report) Run()
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        int exitCode;
        bool truncated = false;

        if (_cancellationToken.IsCancellationRequested)
        {
            AddDiagnostic("cancelled_before_validation");
            _cleanupProven = true;
            _detached = true;
            return Complete(startedUtc, 5, truncated);
        }

        exitCode = ValidatePlan() ? 0 : 2;

        if (exitCode != 0 || _cancellationToken.IsCancellationRequested)
        {
            if (exitCode == 0)
            {
                AddDiagnostic("cancelled_before_open");
                exitCode = 5;
            }

            _cleanupProven = true;
            _detached = true;
            return Complete(startedUtc, exitCode, truncated);
        }

        using SafeProcessHandle preflightProcess = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryInformation,
            bInheritHandle: false,
            checked((uint)Math.Max(_plan.ProcessId, 0)));
        if (exitCode == 0 && preflightProcess.IsInvalid)
        {
            AddDiagnostic("open_process_failed");
            exitCode = 3;
        }

        if (exitCode == 0
            && !_plan.SyntheticOwnedTarget
            && !ValidateProcessIdentity(preflightProcess))
        {
            exitCode = 3;
        }

        if (exitCode == 0 && _cancellationToken.IsCancellationRequested)
        {
            AddDiagnostic("cancelled_before_attach");
            exitCode = 5;
        }

        if (exitCode == 0)
        {
            if (!NativeMethods.DebugActiveProcess(checked((uint)_plan.ProcessId)))
            {
                AddDiagnostic("debug_attach_failed");
                exitCode = 4;
            }
            else
            {
                _attached = true;
                _debuggerExitTerminatesTarget = true;
            }
        }

        SafeProcessHandle? debugProcess = null;
        if (exitCode == 0 && !PrepareInitialAttachEvent(out debugProcess))
        {
            exitCode = _cancellationToken.IsCancellationRequested ? 5 : 3;
        }

        DateTimeOffset deadline = startedUtc.AddMilliseconds(_plan.DurationMilliseconds);
        if (exitCode == 0 && debugProcess is not null)
        {
            while (!_cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                if (!NativeMethods.WaitForDebugEvent(out DebugEvent debugEvent, WaitTimeoutMilliseconds))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is 121 or 258)
                    {
                        continue;
                    }

                    AddDiagnostic("wait_for_debug_event_failed");
                    exitCode = 5;
                    break;
                }

                _debugEventPending = true;
                EventDisposition disposition = HandleEvent(debugProcess, debugEvent);
                CloseSupplementalDebugEventHandles(debugEvent);
                if (!NativeMethods.ContinueDebugEvent(
                    debugEvent.ProcessId,
                    debugEvent.ThreadId,
                    disposition.ContinueStatus))
                {
                    AddDiagnostic("continue_debug_event_failed");
                    exitCode = 5;
                    break;
                }
                _debugEventPending = false;

                if (disposition.Fatal)
                {
                    exitCode = 5;
                    break;
                }

                if (disposition.Stop)
                {
                    truncated = _hits.Count >= _plan.MaxHits;
                    break;
                }
            }
        }

        if (_attached && !_targetExited && !_cleanupProven)
        {
            if (!RestoreViaDebuggerBreak())
            {
                AddDiagnostic("cleanup_restore_failed");
                exitCode = 5;
            }
        }
        else if (_targetExited)
        {
            _cleanupProven = true;
        }

        if (_attached && !_targetExited)
        {
            _detached = TryDetach();
            if (!_detached)
            {
                AddDiagnostic("debug_detach_failed");
                exitCode = 5;
            }
        }
        else if (_targetExited)
        {
            _detached = true;
        }

        if (!_attached)
        {
            _cleanupProven = true;
            _detached = true;
        }

        return Complete(startedUtc, exitCode, truncated);
    }

    private (int ExitCode, ExecuteSnapshotReport Report) Complete(
        DateTimeOffset startedUtc,
        int exitCode,
        bool truncated)
    {
        string status = exitCode switch
        {
            0 when _hits.Count == 0 => "completed-no-hit",
            0 => "completed",
            2 => "invalid-plan",
            3 => "not-armed",
            4 => "attach-failed",
            _ => "failed",
        };

        ExecuteSnapshotReport report = new()
        {
            Status = status,
            ExitCode = exitCode,
            StartedUtc = startedUtc,
            FinishedUtc = DateTimeOffset.UtcNow,
            DurationMilliseconds = _plan.DurationMilliseconds,
            MaxHits = _plan.MaxHits,
            MaxThreads = MaximumThreads,
            Target = _target,
            CaptureKind = _plan.CaptureKind,
            EntityIdDisplacement = _plan.EntityIdDisplacement,
            ThreadsSeen = _threadsSeen,
            ThreadsArmed = _threadsArmed,
            ThreadsFailed = _threadsFailed,
            ThreadsRestored = _threadsRestored,
            HitCount = _hits.Count,
            MatchingBreakpointEvents = _matchingBreakpointEvents,
            Truncated = truncated,
            Attached = _attached,
            CoordinatorIdentityPinned = IsSha256(ExpectedCoordinatorSha256)
                && IsSha256(ExpectedCoordinatorAssemblySha256),
            DebuggerExitTerminatesTarget = _debuggerExitTerminatesTarget,
            BootstrapBreakpointObserved = _bootstrapBreakpointObserved,
            CleanupProven = _cleanupProven,
            Detached = _detached,
            Hits = _hits,
            Diagnostics = _diagnostics,
        };
        _debugEventProcessHandle?.Dispose();
        return (exitCode, report);
    }

    private bool TryDetach()
    {
        uint processId = checked((uint)_plan.ProcessId);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (NativeMethods.DebugActiveProcessStop(processId))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private bool ValidatePlan()
    {
        if (_plan.ProcessId <= 0
            || _plan.DurationMilliseconds is < 1_000 or > 5_000
            || _plan.MaxHits is < 1 or > 64
            || !string.Equals(
                _plan.CaptureKind,
                SupportedCaptureKind,
                StringComparison.Ordinal)
            || _plan.EntityIdDisplacement != SupportedEntityIdDisplacement)
        {
            AddDiagnostic("plan_bounds_invalid");
            return false;
        }

        if (_plan.SyntheticOwnedTarget)
        {
            if (_plan.SyntheticTargetAddress == 0)
            {
                AddDiagnostic("synthetic_target_invalid");
                return false;
            }

            return true;
        }

        if (_plan.ProcessStartIdentity <= 0
            || string.IsNullOrWhiteSpace(_plan.CanonicalExecutablePath)
            || !string.Equals(_plan.ProductVersion, SupportedGameVersion, StringComparison.Ordinal)
            || !string.Equals(
                _plan.ExecutableSha256,
                SupportedGameSha256,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_plan.ModuleName, "wotblitz.exe", StringComparison.OrdinalIgnoreCase)
            || _plan.Rva != SupportedRva
            || _plan.MinimumObjectSampleIntervalMilliseconds != 750
            || !string.Equals(
                _plan.ExpectedInstructionHex,
                SupportedInstructionHex,
                StringComparison.OrdinalIgnoreCase)
            || !ValidateCoordinatorParent())
        {
            AddDiagnostic("production_plan_invalid");
            return false;
        }

        return true;
    }

    private bool ValidateCoordinatorParent()
    {
        if (_plan.CoordinatorProcessId <= 0
            || _plan.CoordinatorProcessStartIdentity <= 0
            || _plan.CoordinatorExecutableSha256.Length != 64
            || string.IsNullOrWhiteSpace(_plan.CoordinatorCanonicalExecutablePath)
            || !string.Equals(
                Path.GetFileName(_plan.CoordinatorCanonicalExecutablePath),
                "WotBTreader.Host.Web.exe",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(_plan.CoordinatorManagedAssemblyPath),
                "WotBTreader.Host.Web.dll",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(_plan.CoordinatorCanonicalExecutablePath)),
                Path.GetDirectoryName(Path.GetFullPath(_plan.CoordinatorManagedAssemblyPath)),
                StringComparison.OrdinalIgnoreCase)
            || !IsSha256(ExpectedCoordinatorSha256)
            || !IsSha256(ExpectedCoordinatorAssemblySha256))
        {
            AddDiagnostic("coordinator_identity_invalid");
            return false;
        }

        uint parentProcessId = GetParentProcessId();
        if (parentProcessId != checked((uint)_plan.CoordinatorProcessId))
        {
            AddDiagnostic("coordinator_parent_mismatch");
            return false;
        }

        using SafeProcessHandle parent = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation,
            bInheritHandle: false,
            parentProcessId);
        if (parent.IsInvalid
            || !NativeMethods.GetProcessTimes(
                parent,
                out NativeFileTime creation,
                out _,
                out _,
                out _)
            || creation.ToInt64() != _plan.CoordinatorProcessStartIdentity)
        {
            AddDiagnostic("coordinator_start_mismatch");
            return false;
        }

        char[] pathBuffer = new char[32_768];
        uint pathLength = checked((uint)pathBuffer.Length);
        if (!NativeMethods.QueryFullProcessImageNameW(
                parent,
                dwFlags: 0,
                pathBuffer,
                ref pathLength)
            || pathLength == 0)
        {
            AddDiagnostic("coordinator_path_unavailable");
            return false;
        }

        string observedPath = Path.GetFullPath(
            new string(pathBuffer, 0, checked((int)pathLength)));
        if (!string.Equals(
                observedPath,
                Path.GetFullPath(_plan.CoordinatorCanonicalExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic("coordinator_path_mismatch");
            return false;
        }

        try
        {
            using FileStream executable = new(
                observedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            string observedSha256 = Convert.ToHexString(SHA256.HashData(executable));
            if (!string.Equals(
                    observedSha256,
                    _plan.CoordinatorExecutableSha256,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    observedSha256,
                    ExpectedCoordinatorSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddDiagnostic("coordinator_hash_mismatch");
                return false;
            }

            using FileStream managedAssembly = new(
                _plan.CoordinatorManagedAssemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            string observedAssemblySha256 = Convert.ToHexString(SHA256.HashData(managedAssembly));
            if (!string.Equals(
                    observedAssemblySha256,
                    _plan.CoordinatorManagedAssemblySha256,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    observedAssemblySha256,
                    ExpectedCoordinatorAssemblySha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddDiagnostic("coordinator_assembly_hash_mismatch");
                return false;
            }
        }
        catch
        {
            AddDiagnostic("coordinator_hash_unavailable");
            return false;
        }

        return true;
    }

    internal static bool IsPinnedCoordinatorImage(string path, string managedAssemblyPath)
    {
        if (!IsSha256(ExpectedCoordinatorSha256)
            || !IsSha256(ExpectedCoordinatorAssemblySha256)
            || string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(managedAssemblyPath)
            || !string.Equals(
                Path.GetFileName(path),
                "WotBTreader.Host.Web.exe",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(managedAssemblyPath),
                "WotBTreader.Host.Web.dll",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(path)),
                Path.GetDirectoryName(Path.GetFullPath(managedAssemblyPath)),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using FileStream executable = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            bool executableMatches = string.Equals(
                Convert.ToHexString(SHA256.HashData(executable)),
                ExpectedCoordinatorSha256,
                StringComparison.OrdinalIgnoreCase);
            using FileStream managedAssembly = new(
                managedAssemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return executableMatches && string.Equals(
                Convert.ToHexString(SHA256.HashData(managedAssembly)),
                ExpectedCoordinatorAssemblySha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static uint GetParentProcessId()
    {
        using SafeSnapshotHandle snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapshotProcess,
            0);
        if (snapshot.IsInvalid)
        {
            return 0;
        }

        ProcessEntry32 entry = new()
        {
            DwSize = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
        };
        if (!NativeMethods.Process32First(snapshot, ref entry))
        {
            return 0;
        }

        uint currentProcessId = checked((uint)Environment.ProcessId);
        do
        {
            if (entry.ProcessId == currentProcessId)
            {
                return entry.ParentProcessId;
            }

            entry.DwSize = checked((uint)Marshal.SizeOf<ProcessEntry32>());
        }
        while (NativeMethods.Process32Next(snapshot, ref entry));
        return 0;
    }

    private bool PrepareInitialAttachEvent(out SafeProcessHandle? process)
    {
        process = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!NativeMethods.WaitForDebugEvent(out DebugEvent debugEvent, WaitTimeoutMilliseconds))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 121 or 258)
                {
                    continue;
                }

                AddDiagnostic("initial_debug_event_wait_failed");
                return false;
            }

            _debugEventPending = true;
            bool expectedEvent = debugEvent.DebugEventCode == NativeMethods.CreateProcessDebugEvent
                && debugEvent.ProcessId == checked((uint)_plan.ProcessId);
            bool ready = false;
            if (expectedEvent)
            {
                CaptureDebugEventProcessHandle(debugEvent);
                process = _debugEventProcessHandle;
                if (_cancellationToken.IsCancellationRequested)
                {
                    AddDiagnostic("cancelled_before_arm");
                    _cleanupProven = true;
                }
                else if (process is not null
                    && !process.IsInvalid
                    && ValidateTarget(process)
                    && ValidateInstructionWhileStopped(process)
                    && ArmExistingThreads())
                {
                    ready = true;
                }
                else if (_threadStates.Count == 0)
                {
                    _cleanupProven = true;
                }
                else
                {
                    _cleanupProven = RestoreAllThreads();
                }
            }
            else
            {
                AddDiagnostic("initial_debug_event_identity_mismatch");
            }

            CloseSupplementalDebugEventHandles(debugEvent);
            uint continueStatus = debugEvent.DebugEventCode == NativeMethods.ExceptionDebugEvent
                ? NativeMethods.DbgExceptionNotHandled
                : NativeMethods.DbgContinue;
            if (!NativeMethods.ContinueDebugEvent(
                    debugEvent.ProcessId,
                    debugEvent.ThreadId,
                    continueStatus))
            {
                AddDiagnostic("initial_debug_event_continue_failed");
                _cleanupProven = false;
                return false;
            }

            _debugEventPending = false;
            return expectedEvent && ready;
        }

        AddDiagnostic("initial_debug_event_timeout");
        return false;
    }

    private bool ValidateTarget(SafeProcessHandle process)
    {
        byte[] expectedBytes = Convert.FromHexString(_plan.ExpectedInstructionHex);
        if (_plan.SyntheticOwnedTarget)
        {
            byte[] actual = new byte[expectedBytes.Length];
            bool syntheticReadOk = NativeMethods.ReadProcessMemory(
                process,
                (nint)_plan.SyntheticTargetAddress,
                actual,
                (nuint)actual.Length,
                out nuint read) && read == (nuint)actual.Length;
            _target = new ExecuteSnapshotTarget(
                "synthetic-private",
                0,
                0,
                0,
                _plan.SyntheticTargetAddress,
                _plan.ExpectedInstructionHex,
                syntheticReadOk ? Convert.ToHexString(actual) : string.Empty,
                syntheticReadOk && actual.AsSpan().SequenceEqual(expectedBytes),
                ExecutableImageSectionProven: false);
            return _target.InstructionMatched;
        }

        if (!ValidateProcessIdentity(process))
        {
            return false;
        }

        List<ModuleEntry32> matches = GetModules()
            .Where(module => string.Equals(module.SzModule, _plan.ModuleName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
        {
            AddDiagnostic("module_identity_ambiguous");
            return false;
        }

        ModuleEntry32 targetModule = matches[0];
        uint moduleBase = unchecked((uint)targetModule.ModBaseAddr.ToInt32());
        uint resolved;
        try
        {
            if (_plan.Rva >= targetModule.ModBaseSize)
            {
                throw new OverflowException();
            }

            resolved = checked(moduleBase + _plan.Rva);
        }
        catch (OverflowException)
        {
            AddDiagnostic("target_rva_out_of_range");
            return false;
        }

        bool sectionProven = ValidateExecutableImageSection(
            _plan.CanonicalExecutablePath,
            _plan.Rva,
            targetModule.ModBaseSize);
        byte[] actualBytes = new byte[expectedBytes.Length];
        bool readOk = NativeMethods.ReadProcessMemory(
            process,
            (nint)resolved,
            actualBytes,
            (nuint)actualBytes.Length,
            out nuint bytesRead) && bytesRead == (nuint)actualBytes.Length;
        bool memoryImage = readOk && IsExecutableImageMemory(process, resolved);
        bool instructionMatched = readOk && actualBytes.AsSpan().SequenceEqual(expectedBytes);
        _target = new ExecuteSnapshotTarget(
            _plan.ModuleName,
            moduleBase,
            targetModule.ModBaseSize,
            _plan.Rva,
            resolved,
            _plan.ExpectedInstructionHex,
            readOk ? Convert.ToHexString(actualBytes) : string.Empty,
            instructionMatched,
            sectionProven && memoryImage);
        if (!sectionProven || !memoryImage || !instructionMatched)
        {
            AddDiagnostic("target_fingerprint_invalid");
            return false;
        }

        return true;
    }

    private bool ValidateProcessIdentity(SafeProcessHandle process)
    {
        if (!NativeMethods.GetProcessTimes(process, out NativeFileTime created, out _, out _, out _)
            || created.ToInt64() != _plan.ProcessStartIdentity)
        {
            AddDiagnostic("process_start_identity_mismatch");
            return false;
        }

        char[] pathBuffer = new char[32_768];
        uint pathLength = checked((uint)pathBuffer.Length);
        if (!NativeMethods.QueryFullProcessImageNameW(process, 0, pathBuffer, ref pathLength))
        {
            AddDiagnostic("process_path_query_failed");
            return false;
        }

        string observedPath = Path.GetFullPath(new string(pathBuffer, 0, checked((int)pathLength)));
        string expectedPath = Path.GetFullPath(_plan.CanonicalExecutablePath);
        if (!string.Equals(observedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic("process_path_mismatch");
            return false;
        }

        FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(expectedPath);
        string productVersion = string.IsNullOrWhiteSpace(versionInfo.ProductVersion)
            ? versionInfo.FileVersion ?? string.Empty
            : versionInfo.ProductVersion;
        productVersion = productVersion.Trim();
        if (!string.Equals(productVersion, _plan.ProductVersion, StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic("process_version_mismatch");
            return false;
        }

        using FileStream stream = new(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string sha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(sha256, _plan.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic("process_hash_mismatch");
            return false;
        }

        return true;
    }

    private static bool ValidateExecutableImageSection(string path, uint rva, uint moduleSize)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using PEReader pe = new(stream);
            if (pe.PEHeaders.PEHeader is null
                || pe.PEHeaders.CoffHeader.Machine != Machine.I386
                || pe.PEHeaders.PEHeader.SizeOfImage != moduleSize)
            {
                return false;
            }

            return pe.PEHeaders.SectionHeaders.Any(section =>
            {
                uint start = checked((uint)section.VirtualAddress);
                uint length = checked((uint)Math.Max(section.VirtualSize, section.SizeOfRawData));
                uint end = checked(start + length);
                return rva >= start
                    && rva < end
                    && (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExecutableImageMemory(SafeProcessHandle process, uint address)
    {
        byte[] mbi = new byte[28];
        if (NativeMethods.VirtualQueryEx(process, (nint)address, mbi, (nuint)mbi.Length) == 0)
        {
            return false;
        }

        uint state = BitConverter.ToUInt32(mbi, 16);
        uint protect = BitConverter.ToUInt32(mbi, 20) & 0xFF;
        uint type = BitConverter.ToUInt32(mbi, 24);
        return state == MemCommit
            && type == MemImage
            && protect is 0x10 or 0x20 or 0x40 or 0x80;
    }

    private bool ValidateInstructionWhileStopped(SafeProcessHandle process)
    {
        if (_target is null)
        {
            return false;
        }

        byte[] expected = Convert.FromHexString(_target.ExpectedInstructionHex);
        byte[] actual = new byte[expected.Length];
        bool ok = NativeMethods.ReadProcessMemory(
            process,
            (nint)_target.ResolvedAddress,
            actual,
            (nuint)actual.Length,
            out nuint read) && read == (nuint)actual.Length;
        if (!ok || !actual.AsSpan().SequenceEqual(expected))
        {
            AddDiagnostic("stopped_instruction_mismatch");
            return false;
        }

        return true;
    }

    private List<ModuleEntry32> GetModules()
    {
        List<ModuleEntry32> modules = [];
        using SafeSnapshotHandle snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapshotModule | NativeMethods.Th32csSnapshotModule32,
            checked((uint)_plan.ProcessId));
        if (snapshot.IsInvalid)
        {
            return modules;
        }

        ModuleEntry32 entry = new() { DwSize = checked((uint)Marshal.SizeOf<ModuleEntry32>()) };
        if (!NativeMethods.Module32First(snapshot, ref entry))
        {
            return modules;
        }

        do
        {
            modules.Add(entry);
            entry.DwSize = checked((uint)Marshal.SizeOf<ModuleEntry32>());
        }
        while (NativeMethods.Module32Next(snapshot, ref entry));
        return modules;
    }

    private bool ArmExistingThreads()
    {
        using SafeSnapshotHandle snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapshotThread,
            0);
        if (snapshot.IsInvalid)
        {
            AddDiagnostic("thread_snapshot_failed");
            return false;
        }

        ThreadEntry32 entry = new() { DwSize = checked((uint)Marshal.SizeOf<ThreadEntry32>()) };
        if (!NativeMethods.Thread32First(snapshot, ref entry))
        {
            AddDiagnostic("thread_snapshot_empty");
            return false;
        }

        bool found = false;
        do
        {
            if (entry.OwnerProcessId == checked((uint)_plan.ProcessId))
            {
                found = true;
                if (_cancellationToken.IsCancellationRequested
                    || !ArmThread(entry.ThreadId))
                {
                    if (_cancellationToken.IsCancellationRequested)
                    {
                        AddDiagnostic("cancelled_during_arm");
                    }

                    return false;
                }
            }

            entry.DwSize = checked((uint)Marshal.SizeOf<ThreadEntry32>());
        }
        while (NativeMethods.Thread32Next(snapshot, ref entry));
        return found;
    }

    private bool ArmThread(uint threadId)
    {
        if (_cancellationToken.IsCancellationRequested)
        {
            AddDiagnostic("cancelled_before_thread_arm");
            return false;
        }

        if (_threadStates.ContainsKey(threadId))
        {
            return true;
        }

        _threadsSeen++;
        if (_threadStates.Count >= MaximumThreads || _target is null)
        {
            _threadsFailed++;
            AddDiagnostic("thread_bound_or_target_invalid");
            return false;
        }

        using SafeThreadHandle thread = NativeMethods.OpenThread(
            NativeMethods.ThreadContextAccess,
            bInheritHandle: false,
            threadId);
        if (thread.IsInvalid)
        {
            _threadsFailed++;
            AddDiagnostic("thread_open_failed");
            return false;
        }

        Context context = new()
        {
            ContextFlags = NativeMethods.ContextControl
                | NativeMethods.ContextInteger
                | NativeMethods.ContextDebugRegisters,
        };
        if (!NativeMethods.GetThreadContext(thread, ref context))
        {
            _threadsFailed++;
            AddDiagnostic("thread_context_read_failed");
            return false;
        }

        if ((context.Dr7 & (Dr0OwnedBit | Dr0GlobalBit)) != 0)
        {
            _threadsFailed++;
            AddDiagnostic("thread_dr0_occupied");
            return false;
        }

        DebugRegisterState original = new(
            context.Dr0,
            context.Dr1,
            context.Dr2,
            context.Dr3,
            context.Dr6,
            context.Dr7);
        context.Dr0 = _target.ResolvedAddress;
        context.Dr6 &= ~Dr0OwnedBit;
        context.Dr7 = (context.Dr7 & ~Dr0TypeLengthMask) | Dr0OwnedBit;
        context.ContextFlags = NativeMethods.ContextDebugRegisters;
        if (_cancellationToken.IsCancellationRequested)
        {
            AddDiagnostic("cancelled_before_thread_context_write");
            return false;
        }

        if (!NativeMethods.SetThreadContext(thread, ref context))
        {
            _threadsFailed++;
            AddDiagnostic("thread_context_arm_failed");
            return false;
        }

        _threadStates.Add(threadId, original);
        _threadsArmed++;
        return true;
    }

    private EventDisposition HandleEvent(SafeProcessHandle process, in DebugEvent debugEvent)
    {
        switch (debugEvent.DebugEventCode)
        {
            case NativeMethods.CreateProcessDebugEvent:
                CaptureDebugEventProcessHandle(debugEvent);
                return ArmThread(debugEvent.ThreadId)
                    ? EventDisposition.Continue
                    : EventDisposition.FatalContinue;
            case NativeMethods.CreateThreadDebugEvent:
                return ArmThread(debugEvent.ThreadId)
                    ? EventDisposition.Continue
                    : EventDisposition.FatalContinue;
            case NativeMethods.ExitThreadDebugEvent:
                _threadStates.Remove(debugEvent.ThreadId);
                return EventDisposition.Continue;
            case NativeMethods.ExitProcessDebugEvent:
                _threadStates.Clear();
                _targetExited = true;
                return EventDisposition.StopContinue;
            case NativeMethods.ExceptionDebugEvent:
                return HandleException(process, debugEvent);
            default:
                return EventDisposition.Continue;
        }
    }

    private EventDisposition HandleException(SafeProcessHandle process, in DebugEvent debugEvent)
    {
        uint code = BitConverter.ToUInt32(debugEvent.Union, 0);
        uint address = BitConverter.ToUInt32(debugEvent.Union, 12);
        uint firstChance = BitConverter.ToUInt32(debugEvent.Union, 80);
        if (code == NativeMethods.StatusBreakpoint && !_bootstrapBreakpointObserved)
        {
            _bootstrapBreakpointObserved = true;
            return EventDisposition.Continue;
        }

        if (code != NativeMethods.StatusSingleStep || firstChance == 0 || _target is null)
        {
            return EventDisposition.NotHandled;
        }

        using SafeThreadHandle thread = NativeMethods.OpenThread(
            NativeMethods.ThreadContextAccess,
            bInheritHandle: false,
            debugEvent.ThreadId);
        if (thread.IsInvalid)
        {
            AddDiagnostic("hit_thread_open_failed");
            return EventDisposition.FatalNotHandled;
        }

        Context context = new()
        {
            ContextFlags = NativeMethods.ContextControl
                | NativeMethods.ContextInteger
                | NativeMethods.ContextDebugRegisters,
        };
        if (!NativeMethods.GetThreadContext(thread, ref context))
        {
            AddDiagnostic("hit_context_read_failed");
            return EventDisposition.FatalNotHandled;
        }

        bool owned = address == _target.ResolvedAddress
            && context.Eip == _target.ResolvedAddress
            && (context.Dr6 & Dr0OwnedBit) != 0
            && _threadStates.ContainsKey(debugEvent.ThreadId);
        if (!owned)
        {
            return EventDisposition.NotHandled;
        }

        _matchingBreakpointEvents++;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool acceptSample = !_lastAcceptedByObject.TryGetValue(context.Esi, out DateTimeOffset previous)
            || now - previous >= TimeSpan.FromMilliseconds(
                _plan.MinimumObjectSampleIntervalMilliseconds);
        if (acceptSample)
        {
            ExecuteSnapshotHit hit = CaptureHit(
                process,
                debugEvent.ThreadId,
                address,
                context,
                now);
            _hits.Add(hit);
            _lastAcceptedByObject[context.Esi] = now;
        }
        context.Dr6 &= ~Dr0OwnedBit;
        context.EFlags |= ResumeFlag;
        context.ContextFlags = NativeMethods.ContextControl | NativeMethods.ContextDebugRegisters;
        if (!NativeMethods.SetThreadContext(thread, ref context))
        {
            AddDiagnostic("hit_context_update_failed");
            return EventDisposition.FatalContinue;
        }

        if (acceptSample && _hits.Count >= _plan.MaxHits)
        {
            if (!RestoreAllThreads())
            {
                return EventDisposition.FatalContinue;
            }

            _cleanupProven = true;
            return EventDisposition.StopContinue;
        }

        return EventDisposition.Continue;
    }

    private ExecuteSnapshotHit CaptureHit(
        SafeProcessHandle process,
        uint threadId,
        uint exceptionAddress,
        Context context,
        DateTimeOffset capturedAtUtc)
    {
        uint entityIdAddress = 0;
        bool entityAddressOk = true;
        try
        {
            entityIdAddress = checked(
                context.Esi + checked((uint)_plan.EntityIdDisplacement));
        }
        catch (OverflowException)
        {
            entityAddressOk = false;
        }

        byte[] entityBytes = new byte[sizeof(int)];
        nuint entityBytesRead = 0;
        bool entityIdReadOk = entityAddressOk
            && NativeMethods.ReadProcessMemory(
                process,
                (nint)entityIdAddress,
                entityBytes,
                checked((nuint)entityBytes.Length),
                out entityBytesRead)
            && entityBytesRead == (nuint)entityBytes.Length;
        int? entityId = entityIdReadOk
            ? BitConverter.ToInt32(entityBytes, 0)
            : null;

        uint readAddress = context.Eax;
        byte[] bytes = new byte[SnapshotBytes];
        nuint bytesRead = 0;
        bool readOk = readAddress != 0
            && NativeMethods.ReadProcessMemory(
                process,
                (nint)readAddress,
                bytes,
                SnapshotBytes,
                out bytesRead)
            && bytesRead == SnapshotBytes;
        int actualBytes = bytesRead > (nuint)SnapshotBytes
            ? SnapshotBytes
            : checked((int)bytesRead);
        float? x = readOk ? BitConverter.ToSingle(bytes, 0) : null;
        float? y = readOk ? BitConverter.ToSingle(bytes, 4) : null;
        float? z = readOk ? BitConverter.ToSingle(bytes, 8) : null;
        bool finite = x.HasValue && y.HasValue && z.HasValue
            && float.IsFinite(x.Value)
            && float.IsFinite(y.Value)
            && float.IsFinite(z.Value);
        return new ExecuteSnapshotHit(
            _hits.Count + 1,
            capturedAtUtc,
            threadId,
            exceptionAddress,
            context.Eip,
            context.Dr6,
            context.Esi,
            readAddress,
            entityIdReadOk,
            entityId,
            new ExecuteSnapshotVector(readOk, actualBytes, x, y, z, finite),
            SameDebugEvent: true,
            DebugEventProcessSuspended: true,
            SingleRead12Bytes: readOk && bytesRead == SnapshotBytes,
            HardwareAtomicReadProven: false,
            SameDecodedClockProven: false,
            ViewpointIdentityProven: false,
            StableRootProven: false);
    }

    private bool RestoreViaDebuggerBreak()
    {
        if (_debugEventPending)
        {
            AddDiagnostic("cleanup_event_still_pending");
            return false;
        }

        if (_threadStates.Count == 0)
        {
            _cleanupProven = true;
            return true;
        }

        if (_debugEventProcessHandle is null
            || _debugEventProcessHandle.IsInvalid
            || !NativeMethods.DebugBreakProcess(_debugEventProcessHandle))
        {
            AddDiagnostic("cleanup_debug_break_failed");
            return false;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!NativeMethods.WaitForDebugEvent(out DebugEvent debugEvent, WaitTimeoutMilliseconds))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 121 or 258)
                {
                    continue;
                }

                return false;
            }

            _debugEventPending = true;

            uint continueStatus = NativeMethods.DbgContinue;
            bool cleanupEvent = false;
            if (debugEvent.DebugEventCode == NativeMethods.CreateProcessDebugEvent)
            {
                CaptureDebugEventProcessHandle(debugEvent);
            }
            else if (debugEvent.DebugEventCode == NativeMethods.CreateThreadDebugEvent)
            {
                // Cleanup is already in progress. This thread was never armed,
                // so it adds no debug-register state that must be restored.
            }
            else if (debugEvent.DebugEventCode == NativeMethods.ExitThreadDebugEvent)
            {
                _threadStates.Remove(debugEvent.ThreadId);
            }
            else if (debugEvent.DebugEventCode == NativeMethods.ExitProcessDebugEvent)
            {
                _threadStates.Clear();
                _targetExited = true;
                _cleanupProven = true;
                cleanupEvent = true;
            }
            else if (debugEvent.DebugEventCode == NativeMethods.ExceptionDebugEvent)
            {
                uint code = BitConverter.ToUInt32(debugEvent.Union, 0);
                if (code == NativeMethods.StatusBreakpoint)
                {
                    cleanupEvent = true;
                    if (!RestoreAllThreads())
                    {
                        AddDiagnostic("cleanup_thread_restore_failed");
                        return false;
                    }

                    _cleanupProven = true;
                }
                else
                {
                    continueStatus = NativeMethods.DbgExceptionNotHandled;
                }
            }

            CloseSupplementalDebugEventHandles(debugEvent);
            if (!NativeMethods.ContinueDebugEvent(
                debugEvent.ProcessId,
                debugEvent.ThreadId,
                continueStatus))
            {
                AddDiagnostic("cleanup_continue_failed");
                return false;
            }

            _debugEventPending = false;

            if (cleanupEvent)
            {
                return true;
            }
        }

        return false;
    }

    private bool RestoreAllThreads()
    {
        bool success = true;
        foreach ((uint threadId, DebugRegisterState original) in _threadStates.ToArray())
        {
            using SafeThreadHandle thread = NativeMethods.OpenThread(
                NativeMethods.ThreadContextAccess,
                bInheritHandle: false,
                threadId);
            if (thread.IsInvalid)
            {
                AddDiagnostic("restore_thread_open_failed");
                success = false;
                continue;
            }

            Context context = new()
            {
                ContextFlags = NativeMethods.ContextDebugRegisters,
                Dr0 = original.Dr0,
                Dr1 = original.Dr1,
                Dr2 = original.Dr2,
                Dr3 = original.Dr3,
                Dr6 = original.Dr6,
                Dr7 = original.Dr7,
            };
            if (!NativeMethods.SetThreadContext(thread, ref context))
            {
                AddDiagnostic("restore_thread_context_failed");
                success = false;
                continue;
            }

            Context observed = new()
            {
                ContextFlags = NativeMethods.ContextDebugRegisters,
            };
            if (!NativeMethods.GetThreadContext(thread, ref observed)
                || observed.Dr0 != original.Dr0
                || observed.Dr1 != original.Dr1
                || observed.Dr2 != original.Dr2
                || observed.Dr3 != original.Dr3
                || observed.Dr6 != original.Dr6
                || observed.Dr7 != original.Dr7)
            {
                AddDiagnostic("restore_thread_verify_failed");
                success = false;
                continue;
            }

            _threadsRestored++;
        }

        if (success)
        {
            _threadStates.Clear();
        }

        return success;
    }

    private void CaptureDebugEventProcessHandle(in DebugEvent debugEvent)
    {
        if (_debugEventProcessHandle is not null)
        {
            return;
        }

        // x86 CREATE_PROCESS_DEBUG_INFO: hFile=0, hProcess=4, hThread=8.
        int rawHandle = BitConverter.ToInt32(debugEvent.Union, 4);
        if (rawHandle != 0 && rawHandle != -1)
        {
            _debugEventProcessHandle = new SafeProcessHandle((nint)rawHandle, ownsHandle: true);
        }
    }

    private static void CloseSupplementalDebugEventHandles(in DebugEvent debugEvent)
    {
        if (debugEvent.DebugEventCode == NativeMethods.CreateProcessDebugEvent)
        {
            CloseRawHandle(BitConverter.ToInt32(debugEvent.Union, 0));
            CloseRawHandle(BitConverter.ToInt32(debugEvent.Union, 8));
        }
        else if (debugEvent.DebugEventCode is NativeMethods.CreateThreadDebugEvent
            or NativeMethods.LoadDllDebugEvent)
        {
            CloseRawHandle(BitConverter.ToInt32(debugEvent.Union, 0));
        }
    }

    private static void CloseRawHandle(int rawHandle)
    {
        if (rawHandle != 0 && rawHandle != -1)
        {
            _ = NativeMethods.CloseHandle((nint)rawHandle);
        }
    }

    private void AddDiagnostic(string code)
    {
        if (_diagnostics.Count < 64)
        {
            _diagnostics.Add(code);
        }
    }

    public void Dispose()
    {
        _debugEventProcessHandle?.Dispose();
        _debugEventProcessHandle = null;
    }

    private sealed record DebugRegisterState(
        uint Dr0,
        uint Dr1,
        uint Dr2,
        uint Dr3,
        uint Dr6,
        uint Dr7);

    private readonly record struct EventDisposition(uint ContinueStatus, bool Fatal, bool Stop)
    {
        internal static EventDisposition Continue => new(NativeMethods.DbgContinue, false, false);
        internal static EventDisposition NotHandled => new(NativeMethods.DbgExceptionNotHandled, false, false);
        internal static EventDisposition FatalContinue => new(NativeMethods.DbgContinue, true, false);
        internal static EventDisposition FatalNotHandled => new(NativeMethods.DbgExceptionNotHandled, true, false);
        internal static EventDisposition StopContinue => new(NativeMethods.DbgContinue, false, true);
    }
}
