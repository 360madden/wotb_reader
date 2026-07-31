using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Session;

internal sealed class WindowsGameProcessQueryPlatform(
    IWindowsExecutableFingerprintReader fingerprintReader)
    : IGameProcessQueryPlatform
{
    private const string GameWindowClass = "SDL_app";
    private const int MaximumCandidateWindows = 128;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const int MaximumPathCharacters = 32_768;
    private const int MaximumWindowClassCharacters = 256;
    private readonly IWindowsExecutableFingerprintReader _fingerprintReader =
        fingerprintReader ?? throw new ArgumentNullException(nameof(fingerprintReader));

    public bool IsSupported => OperatingSystem.IsWindows();

    public GameWindowEnumerationResult EnumerateEligibleGameWindows()
    {
        if (!IsSupported)
        {
            return new GameWindowEnumerationResult([], IsComplete: true);
        }

        List<GameWindowCandidate> candidates = [];
        nint previous = nint.Zero;
        bool isComplete = false;
        for (int count = 0; count < MaximumCandidateWindows; count++)
        {
            nint window = NativeMethods.FindWindowExW(
                nint.Zero,
                previous,
                GameWindowClass,
                lpWindowName: null);
            if (window == nint.Zero)
            {
                isComplete = true;
                break;
            }

            previous = window;
            if (!TryGetEligibleCandidate(window, out GameWindowCandidate candidate))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        return new GameWindowEnumerationResult(candidates, isComplete);
    }

    public async ValueTask<IGameProcessQuerySession?> OpenQuerySessionAsync(
        GameWindowCandidate candidate,
        uint desiredAccess,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported
            || desiredAccess != GameProcessIdentityObserver.ProcessQueryLimitedInformation
            || candidate.ProcessId <= 0
            || candidate.WindowHandle == 0)
        {
            return null;
        }

        SafeProcessHandle processHandle = NativeMethods.OpenProcess(
            desiredAccess,
            bInheritHandle: false,
            checked((uint)candidate.ProcessId));
        if (processHandle.IsInvalid)
        {
            processHandle.Dispose();
            return null;
        }

        try
        {
            long processStartIdentity = QueryProcessStartIdentity(processHandle);
            string imagePath = QueryProcessImagePath(processHandle);
            if (processStartIdentity <= 0 || string.IsNullOrWhiteSpace(imagePath))
            {
                processHandle.Dispose();
                return null;
            }

            WindowsExecutableFingerprint? fingerprint = await _fingerprintReader
                .ReadAsync(
                    imagePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (fingerprint is null)
            {
                processHandle.Dispose();
                return null;
            }

            return new WindowsGameProcessQuerySession(
                processHandle,
                candidate.ProcessId,
                processStartIdentity,
                fingerprint.CanonicalPath,
                fingerprint.FileIdentity,
                fingerprint.ProductVersion,
                fingerprint.Sha256);
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or NotSupportedException)
        {
            processHandle.Dispose();
            return null;
        }
        catch
        {
            processHandle.Dispose();
            throw;
        }
    }

    public bool IsWindowStillEligible(GameWindowCandidate candidate)
    {
        if (!IsSupported || candidate.WindowHandle == 0 || candidate.ProcessId <= 0)
        {
            return false;
        }

        return TryGetEligibleCandidate(
                   new nint(candidate.WindowHandle),
                   out GameWindowCandidate current)
               && current == candidate;
    }

    private static bool TryGetEligibleCandidate(
        nint window,
        out GameWindowCandidate candidate)
    {
        candidate = null!;
        char[] className = new char[MaximumWindowClassCharacters];
        int classCharacters = NativeMethods.GetClassNameW(
            window,
            className,
            className.Length);
        if (classCharacters <= 0
            || !new string(className, 0, classCharacters).Equals(
                GameWindowClass,
                StringComparison.Ordinal)
            || !NativeMethods.IsWindowVisible(window)
            || NativeMethods.GetAncestor(window, GaRoot) != window
            || NativeMethods.GetWindow(window, GwOwner) != nint.Zero
            || !NativeMethods.GetClientRect(window, out NativeRect rect)
            || rect.Right <= rect.Left
            || rect.Bottom <= rect.Top)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId is 0 or > int.MaxValue)
        {
            return false;
        }

        candidate = new GameWindowCandidate(
            window.ToInt64(),
            checked((int)processId));
        return true;
    }

    private static long QueryProcessStartIdentity(SafeProcessHandle processHandle)
    {
        if (!NativeMethods.GetProcessTimes(
            processHandle,
            out NativeFileTime creationTime,
            out _,
            out _,
            out _))
        {
            return 0;
        }

        return creationTime.ToInt64();
    }

    private static string QueryProcessImagePath(SafeProcessHandle processHandle)
    {
        char[] buffer = new char[MaximumPathCharacters];
        uint characters = checked((uint)buffer.Length);
        return NativeMethods.QueryFullProcessImageNameW(
            processHandle,
            dwFlags: 0,
            buffer,
            ref characters)
            ? new string(buffer, 0, checked((int)characters))
            : string.Empty;
    }

}

internal sealed class WindowsGameProcessQuerySession(
    SafeProcessHandle processHandle,
    int processId,
    long processStartIdentity,
    string canonicalExecutablePath,
    ExecutableFileIdentity fileIdentity,
    string productVersion,
    ContentHash executableSha256)
    : IGameProcessQuerySession
{
    private const uint StillActive = 259;
    private readonly SafeProcessHandle _processHandle =
        processHandle ?? throw new ArgumentNullException(nameof(processHandle));

    public int ProcessId { get; } = processId;

    public long ProcessStartIdentity { get; } = processStartIdentity;

    public bool IsAlive =>
        !_processHandle.IsInvalid
        && !_processHandle.IsClosed
        && NativeMethods.GetExitCodeProcess(_processHandle, out uint exitCode)
        && exitCode == StillActive;

    public string CanonicalExecutablePath { get; } = canonicalExecutablePath;

    public ExecutableFileIdentity FileIdentity { get; } = fileIdentity;

    public string ProductVersion { get; } = productVersion;

    public ContentHash ExecutableSha256 { get; } = executableSha256;

    public void Dispose() => _processHandle.Dispose();
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeRect
{
    public readonly int Left;
    public readonly int Top;
    public readonly int Right;
    public readonly int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeFileTime
{
    private readonly uint _lowDateTime;
    private readonly uint _highDateTime;

    public long ToInt64() =>
        checked((long)(((ulong)_highDateTime << 32) | _lowDateTime));
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeFileInformation
{
    private readonly uint _fileAttributes;
    private readonly NativeFileTime _creationTime;
    private readonly NativeFileTime _lastAccessTime;
    private readonly NativeFileTime _lastWriteTime;
    public readonly uint VolumeSerialNumber;
    private readonly uint _fileSizeHigh;
    private readonly uint _fileSizeLow;
    private readonly uint _numberOfLinks;
    private readonly uint _fileIndexHigh;
    private readonly uint _fileIndexLow;

    public FileAttributes FileAttributes => (FileAttributes)_fileAttributes;

    public ulong FileIndex => ((ulong)_fileIndexHigh << 32) | _fileIndexLow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    public nint BaseAddress;
    public nint AllocationBase;
    public uint AllocationProtect;
    public ushort PartitionId;
    public nuint RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

internal static class NativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowExW(
        nint hWndParent,
        nint hWndChildAfter,
        string? lpszClass,
        string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetAncestor(nint hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(
        nint hWnd,
        [Out] char[] lpClassName,
        int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(
        nint hWnd,
        out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle hProcess,
        uint dwFlags,
        [Out] char[] lpExeName,
        ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        SafeProcessHandle hProcess,
        out NativeFileTime lpCreationTime,
        out NativeFileTime lpExitTime,
        out NativeFileTime lpKernelTime,
        out NativeFileTime lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(
        SafeProcessHandle hProcess,
        out uint lpExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out NativeFileInformation lpFileInformation);

    // Suspended process creation and verification
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessTerminate = 0x0001;
    internal const uint Synchronize = 0x0010_0000;
    internal const uint ThreadSuspendResume = 0x0002;
    internal const uint ThreadQueryLimitedInformation = 0x0040;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int GetProcessId(SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(SafeThreadHandle hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadProcessMemory(
        SafeProcessHandle hProcess,
        nint lpBaseAddress,
        nint lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int VirtualQueryEx(
        SafeProcessHandle hProcess,
        nint lpAddress,
        out MemoryBasicInformation lpBuffer,
        uint dwLength);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfoEx
{
    internal int cb;
    internal IntPtr lpReserved;
    internal IntPtr lpDesktop;
    internal IntPtr lpTitle;
    internal int dwX;
    internal int dwY;
    internal int dwXSize;
    internal int dwYSize;
    internal int dwXCountChars;
    internal int dwYCountChars;
    internal int dwFillAttribute;
    internal int dwFlags;
    internal short wShowWindow;
    internal short cbReserved2;
    internal IntPtr lpReserved2;
    internal IntPtr lpAttributeList;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    internal IntPtr hProcess;
    internal IntPtr hThread;
    internal int dwProcessId;
    internal int dwThreadId;
}

internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeThreadHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        NativeMethods.CloseHandle(handle);
        return true;
    }
}
