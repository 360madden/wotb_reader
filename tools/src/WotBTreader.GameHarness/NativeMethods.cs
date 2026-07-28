#pragma warning disable CA2101 // P/Invoke declarations are trusted and reviewed

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WotBTreader.GameHarness;

// ────────────────────────────────────────────────────────────
//  Safe handles
// ────────────────────────────────────────────────────────────

internal sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeProcessHandle() : base(ownsHandle: true) { }

    public SafeProcessHandle(IntPtr existingHandle, bool ownsHandle)
        : base(ownsHandle) =>
        SetHandle(existingHandle);

    protected override bool ReleaseHandle() =>
        NativeMethods.CloseHandle(handle);
}

// ────────────────────────────────────────────────────────────
//  Structs
// ────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

internal enum InputType : uint
{
    Mouse = 0,
    Keyboard = 1,
    Hardware = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort VirtualKeyCode;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public IntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Explicit)]
internal struct INPUT_UNION
{
    [FieldOffset(0)] public MOUSEINPUT Mouse;
    [FieldOffset(0)] public KEYBDINPUT Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public InputType Type;
    public INPUT_UNION Union;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORY_BASIC_INFORMATION
{
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public uint AllocationProtect;
    public UIntPtr RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

// ────────────────────────────────────────────────────────────
//  Constants
// ────────────────────────────────────────────────────────────

internal static class Win32Constants
{
    // Process access rights
    public const uint PROCESS_VM_READ = 0x0010;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_DUP_HANDLE = 0x0040;

    // Window states
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_RESTORE = 9;

    // Memory states
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_FREE = 0x10000;
    public const uint MEM_RESERVE = 0x2000;

    // Page protections
    public const uint PAGE_READWRITE = 0x04;
    public const uint PAGE_READONLY = 0x02;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    // Virtual-key codes (subset used for replay controls)
    public const ushort VK_SPACE = 0x20;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_1 = 0x31;
    public const ushort VK_2 = 0x32;
    public const ushort VK_3 = 0x33;
    public const ushort VK_4 = 0x34;
    public const ushort VK_5 = 0x35;

    // Input flags
    public const uint KEYEVENTF_KEYDOWN = 0x0000;
    public const uint KEYEVENTF_KEYUP = 0x0002;
}

// ────────────────────────────────────────────────────────────
//  Native methods (internal — only GameHarness calls these)
// ────────────────────────────────────────────────────────────

internal static partial class NativeMethods
{
    // ── kernel32.dll ────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        uint nSize,
        out uint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle hProcess,
        IntPtr lpBaseAddress,
        IntPtr lpBuffer,
        uint nSize,
        out uint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint VirtualQueryEx(
        SafeProcessHandle hProcess,
        IntPtr lpAddress,
        out MEMORY_BASIC_INFORMATION lpBuffer,
        uint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsWow64Process(
        SafeProcessHandle hProcess,
        out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime);

    // ── user32.dll ──────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowW(
        string? lpClassName,
        string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowRect(
        IntPtr hWnd,
        out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(
        uint cInputs,
        [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs,
        int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetSystemMetrics(int nIndex);

    // ── psapi.dll ───────────────────────────────────────────

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetModuleFileNameEx(
        SafeProcessHandle hProcess,
        IntPtr hModule,
        [Out] char[] lpFilename,
        uint nSize);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumProcessModules(
        SafeProcessHandle hProcess,
        [Out] IntPtr[] lphModule,
        uint cb,
        out uint lpcbNeeded);
}

// ────────────────────────────────────────────────────────────
//  Supplementary structs
// ────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_INFO
{
    public ushort ProcessorArchitecture;
    private readonly ushort Reserved;
    public uint PageSize;
    public IntPtr MinimumApplicationAddress;
    public IntPtr MaximumApplicationAddress;
    public IntPtr ActiveProcessorMask;
    public uint NumberOfProcessors;
    public uint ProcessorType;
    public uint AllocationGranularity;
    public ushort ProcessorLevel;
    public ushort ProcessorRevision;
}
