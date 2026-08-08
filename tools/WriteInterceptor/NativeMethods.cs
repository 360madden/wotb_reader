using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WotBTreader.WriteInterceptor;

/// <summary>
/// Win32 interop for the guard-page write interceptor. x86-only by design:
/// the helper is built with PlatformTarget x86 so it can attach to the
/// 32-bit game with the debug API (same-bitness requirement) and read full
/// i386 thread contexts without WOW64 translation.
/// </summary>
internal static partial class NativeMethods
{
    // Debug event codes.
    internal const uint ExceptionDebugEvent = 1;
    internal const uint CreateThreadDebugEvent = 2;
    internal const uint CreateProcessDebugEvent = 3;
    internal const uint ExitThreadDebugEvent = 4;
    internal const uint ExitProcessDebugEvent = 5;
    internal const uint LoadDllDebugEvent = 6;
    internal const uint UnloadDllDebugEvent = 7;
    internal const uint OutputDebugStringEvent = 8;
    internal const uint RipEvent = 9;

    // ContinueDebugEvent status values.
    internal const uint DbgContinue = 0x00010002;
    internal const uint DbgExceptionNotHandled = 0x00010001;

    // STATUS_GUARD_PAGE_VIOLATION.
    internal const uint StatusGuardPageViolation = 0x80000001;
    internal const uint StatusBreakpoint = 0x80000003;
    internal const uint StatusSingleStep = 0x80000004;

#if !INSTRUCTION_SNAPSHOT_HELPER
    // Legacy PAGE_GUARD memory protections and access rights. These symbols
    // are compiled out of the production instruction-snapshot helper.
    internal const uint PageGuard = 0x100;
    internal const uint PageNoAccess = 0x01;
#endif

    // Process access rights for the read/arm handle.
    internal const uint ProcessVmRead = 0x0010;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
#if !INSTRUCTION_SNAPSHOT_HELPER
    internal const uint ProcessVmOperation = 0x0008;
    internal const uint ProcessVmWrite = 0x0020;
#endif

    // Thread access rights for the context snapshot. THREAD_ALL_ACCESS is
    // required: minimal GET_CONTEXT+QUERY_INFO+SUSPEND_RESUME made
    // GetThreadContext fail with ACCESS_DENIED (5) empirically. Documented
    // x86 value is 0x1F03FF (STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | the
    // thread-specific rights).
    internal const uint ThreadGetContext = 0x0008;
    internal const uint ThreadSetContext = 0x0010;
    internal const uint ThreadContextAccess = ThreadGetContext | ThreadSetContext;
#if !INSTRUCTION_SNAPSHOT_HELPER
    internal const uint ThreadAllAccess = 0x1F03FF;
#endif

    // Toolhelp flags.
    internal const uint Th32csSnapshotModule = 0x00000008;
    internal const uint Th32csSnapshotModule32 = 0x00000010;
    internal const uint Th32csSnapshotThread = 0x00000004;
    internal const uint Th32csSnapshotProcess = 0x00000002;

    internal const uint ContextI386 = 0x00010000;
    internal const uint ContextControl = ContextI386 | 0x00000001;
    internal const uint ContextInteger = ContextI386 | 0x00000002;
    internal const uint ContextSegments = ContextI386 | 0x00000004;
    internal const uint ContextFloatingPoint = ContextI386 | 0x00000008;
    internal const uint ContextDebugRegisters = ContextI386 | 0x00000010;
    internal const uint ContextFull = ContextControl | ContextInteger | ContextSegments | ContextFloatingPoint;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugActiveProcess(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugActiveProcessStop(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DebugBreakProcess(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WaitForDebugEvent(out DebugEvent lpDebugEvent, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ContinueDebugEvent(
        uint dwProcessId,
        uint dwThreadId,
        uint dwContinueStatus);

#if !INSTRUCTION_SNAPSHOT_HELPER
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtectEx(
        SafeProcessHandle hProcess,
        nint lpAddress,
        nuint dwSize,
        uint flNewProtect,
        out uint lpflOldProtect);
#endif

    /// <summary>Raw-buffer variant: the native struct is parsed manually at
    /// hard-coded x86 offsets (this helper is PlatformTarget x86 by design, so
    /// the 28-byte layout is deterministic). The struct-marshal variant was
    /// observed to misread the tail fields (State/Protect/Type) on this OS,
    /// so raw parsing is the only supported path.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint VirtualQueryEx(
        SafeProcessHandle hProcess,
        nint lpAddress,
        [Out] byte[] lpBuffer,
        nuint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle hProcess,
        nint lpBaseAddress,
        [Out] byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeThreadHandle OpenThread(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadContext(
        SafeThreadHandle hThread,
        ref Context lpContext);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadContext(
        SafeThreadHandle hThread,
        ref Context lpContext);

#if !INSTRUCTION_SNAPSHOT_HELPER
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SuspendThread(SafeThreadHandle hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(SafeThreadHandle hThread);
#endif

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeSnapshotHandle CreateToolhelp32Snapshot(
        uint dwFlags,
        uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32First(
        SafeSnapshotHandle hSnapshot,
        ref ModuleEntry32 lpme);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Module32Next(
        SafeSnapshotHandle hSnapshot,
        ref ModuleEntry32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Thread32First(
        SafeSnapshotHandle hSnapshot,
        ref ThreadEntry32 lpte);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Thread32Next(
        SafeSnapshotHandle hSnapshot,
        ref ThreadEntry32 lpte);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(
        SafeSnapshotHandle hSnapshot,
        ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(
        SafeSnapshotHandle hSnapshot,
        ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle hProcess,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);
}

/// <summary>Raw debug event: 12 bytes of header + the 160-byte union kept as
/// raw bytes so the exception record can be read at known x86 offsets without
/// a fragile union layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DebugEvent
{
    public uint DebugEventCode;
    public uint ProcessId;
    public uint ThreadId;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)]
    public byte[] Union;
}

/// <summary>i386 thread context (the game is 32-bit; the helper is x86, so no
/// WOW64 translation is needed). Best-effort: callers validate ContextFlags
/// after the call and degrade gracefully when the snapshot fails.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Context
{
    public uint ContextFlags;
    public uint Dr0;
    public uint Dr1;
    public uint Dr2;
    public uint Dr3;
    public uint Dr6;
    public uint Dr7;
    public FloatSaveArea FloatSave;
    public uint SegGs;
    public uint SegFs;
    public uint SegEs;
    public uint SegDs;
    public uint Edi;
    public uint Esi;
    public uint Ebx;
    public uint Edx;
    public uint Ecx;
    public uint Eax;
    public uint Ebp;
    public uint Eip;
    public uint SegCs;
    public uint EFlags;
    public uint Esp;
    public uint SegSs;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
    public byte[] ExtendedRegisters;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FloatSaveArea
{
    public uint ControlWord;
    public uint StatusWord;
    public uint TagWord;
    public uint ErrorOffset;
    public uint ErrorSelector;
    public uint DataOffset;
    public uint DataSelector;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)]
    public byte[] RegisterArea;

    public uint Cr0NpxState;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ModuleEntry32
{
    public uint DwSize;
    public uint Th32ModuleId;
    public uint Th32ProcessId;
    public uint GlblcntUsage;
    public uint ProccntUsage;
    public nint ModBaseAddr;
    public uint ModBaseSize;
    public nint HModule;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string SzModule;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string SzExePath;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ThreadEntry32
{
    public uint DwSize;
    public uint CntUsage;
    public uint ThreadId;
    public uint OwnerProcessId;
    public int BasePriority;
    public int DeltaPriority;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ProcessEntry32
{
    public uint DwSize;
    public uint CntUsage;
    public uint ProcessId;
    public nuint DefaultHeapId;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int PriorityClassBase;
    public uint Flags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string ExeFile;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFileTime
{
    public uint LowDateTime;
    public uint HighDateTime;

    internal long ToInt64() => unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
}

internal sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeSnapshotHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() =>
        NativeMethods.CloseHandle(this.handle);

    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

/// <summary>Thread handle wrapper (portable TFM has no SafeThreadHandle).</summary>
internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeThreadHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() =>
        NativeMethods.CloseHandle(this.handle);

    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

internal static partial class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint hObject);
}
