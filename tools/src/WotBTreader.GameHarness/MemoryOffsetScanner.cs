using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotBTreader.GameHarness;

/// <summary>
/// Cheat Engine-style memory scanner for discovering WoT Blitz replay
/// memory offsets. Attaches to the game process, enumerates readable
/// memory regions, and scans for known values (HP, position, replay time).
/// Supports iterative narrowing — run multiple scans with different values
/// to converge on the correct offset.
/// </summary>
public sealed class MemoryOffsetScanner : IDisposable
{
    private const int ScanBufferSize = 64 * 1024; // 64 KB read chunks
    private const string StateFileName = "scanner-state.json";

    private SafeProcessHandle? _processHandle;
    private IntPtr _baseAddress;
    private int _processId;
    private string? _executableVersion;
    private bool _disposed;

    /// <summary>Set of candidate base-relative offsets from the last scan.</summary>
    public HashSet<long> Candidates { get; } = [];

    // ── Attach / Detach ──────────────────────────────────────

    public bool Attach(int processId)
    {
        Detach();
        _processId = processId;

        _processHandle = NativeMethods.OpenProcess(
            Win32Constants.PROCESS_VM_READ | Win32Constants.PROCESS_QUERY_INFORMATION,
            bInheritHandle: false,
            (uint)processId);

        if (_processHandle.IsInvalid)
        {
            _processHandle.Dispose();
            _processHandle = null;
            return false;
        }

        // Get base address and version
        _baseAddress = GetBaseModuleAddress(_processHandle);
        if (_baseAddress == IntPtr.Zero)
        {
            Detach();
            return false;
        }

        try
        {
            using Process? proc = Process.GetProcessById(processId);
            _executableVersion = proc?.MainModule?.FileVersionInfo?.FileVersion;
        }
        catch
        {
            _executableVersion = null;
        }

        return true;
    }

    public void Detach()
    {
        _processHandle?.Dispose();
        _processHandle = null;
        _baseAddress = IntPtr.Zero;
        _processId = 0;
        _executableVersion = null;
        Candidates.Clear();
    }

    // ── Scanning ─────────────────────────────────────────────

    /// <summary>
    /// Scans all readable committed memory regions for the specified int32 value.
    /// Returns the number of candidate addresses found.
    /// If <paramref name="previousCandidates"/> is provided, only addresses in
    /// that set are scanned (narrowing mode).
    /// </summary>
    public int ScanInt32(int targetValue, HashSet<long>? previousCandidates = null)
    {
        byte[] target = BitConverter.GetBytes(targetValue);
        return ScanMemory(target, 4, previousCandidates);
    }

    /// <summary>
    /// Scans all readable committed memory regions for the specified float value.
    /// </summary>
    public int ScanFloat(float targetValue, HashSet<long>? previousCandidates = null)
    {
        byte[] target = BitConverter.GetBytes(targetValue);
        return ScanMemory(target, 4, previousCandidates);
    }

    /// <summary>
    /// Scans all readable committed memory regions for the specified double value.
    /// </summary>
    public int ScanDouble(double targetValue, HashSet<long>? previousCandidates = null)
    {
        byte[] target = BitConverter.GetBytes(targetValue);
        return ScanMemory(target, 8, previousCandidates);
    }

    private static readonly uint MbiSize = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

    private int ScanMemory(byte[] target, int valueSize, HashSet<long>? previousCandidates)
    {
        if (_processHandle is null) return 0;

        var newCandidates = new HashSet<long>();
        bool narrowing = previousCandidates is { Count: > 0 };

        // Scan the ENTIRE process address space, not just the main module.
        // Replay state (HP, positions, timeline) lives on the heap, not in .data.
        IntPtr current = checked((IntPtr)0x10000); // Skip null page
        // CA2020 suppressed: unchecked cast is intentional — 64-bit constant
        // is guarded by Environment.Is64BitProcess check above.
#pragma warning disable CA2020
        IntPtr maxUserAddress = Environment.Is64BitProcess
            ? unchecked((IntPtr)0x00007FFFFFFFFFFF)
            : unchecked((IntPtr)0x7FFEFFFF);
#pragma warning restore CA2020

        while (current.ToInt64() < maxUserAddress.ToInt64() && current.ToInt64() > 0)
        {
            var mbi = default(MEMORY_BASIC_INFORMATION);
            uint result = NativeMethods.VirtualQueryEx(
                _processHandle, current, out mbi, MbiSize);

            if (result == 0) break;

            bool isReadable = (mbi.State == Win32Constants.MEM_COMMIT)
                && (mbi.Protect & (Win32Constants.PAGE_READONLY
                    | Win32Constants.PAGE_READWRITE
                    | Win32Constants.PAGE_EXECUTE_READWRITE)) != 0
                && (mbi.Protect & 0x100) == 0; // Not PAGE_GUARD

            if (isReadable)
            {
                long regionStart = mbi.BaseAddress.ToInt64();
                long regionSize = (long)mbi.RegionSize;
                long regionEnd = regionStart + regionSize;

                // If narrowing, skip regions with no candidates
                if (narrowing)
                {
                    bool hasCandidates = previousCandidates!.Any(
                        c => c >= regionStart - _baseAddress.ToInt64()
                             && c < regionEnd - _baseAddress.ToInt64());
                    if (!hasCandidates)
                    {
                        current = IntPtr.Add(mbi.BaseAddress, checked((int)regionSize));
                        continue;
                    }
                }

                ScanRegion(regionStart, regionSize, target, valueSize,
                    narrowing ? previousCandidates! : null, newCandidates);
            }

            current = IntPtr.Add(mbi.BaseAddress, checked((int)(long)mbi.RegionSize));
        }

        Candidates.Clear();
        foreach (long c in newCandidates)
            Candidates.Add(c);

        return Candidates.Count;
    }

    private void ScanRegion(
        long regionStart, long regionSize,
        byte[] target, int valueSize,
        HashSet<long>? previousCandidates,
        HashSet<long> newCandidates)
    {
        if (_processHandle is null) return;

        long baseAddr = _baseAddress.ToInt64();
        long regionEnd = regionStart + regionSize;
        byte[] buffer = new byte[ScanBufferSize];

        for (long offset = regionStart; offset < regionEnd; offset += ScanBufferSize - valueSize + 1)
        {
            int readSize = (int)Math.Min(ScanBufferSize, regionEnd - offset);
            if (readSize < valueSize) break;

            IntPtr addr = checked((IntPtr)offset);
            bool ok = NativeMethods.ReadProcessMemory(
                _processHandle, addr, buffer, (uint)readSize, out uint bytesRead);

            if (!ok || bytesRead < valueSize) continue;

            int scanEnd = (int)bytesRead - valueSize + 1;

            for (int i = 0; i < scanEnd; i++)
            {
                long fileOffset = (offset + i) - baseAddr;

                // If narrowing, only check addresses in previous candidates
                if (previousCandidates is not null && !previousCandidates.Contains(fileOffset))
                    continue;

                bool match = true;
                for (int j = 0; j < valueSize; j++)
                {
                    if (buffer[i + j] != target[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    newCandidates.Add(fileOffset);
            }
        }
    }

    // ── State persistence ────────────────────────────────────

    public void SaveState(string? dir = null)
    {
        dir ??= Environment.CurrentDirectory;
        string path = Path.Combine(dir, StateFileName);
        var state = new ScannerState
        {
            ProcessId = _processId,
            ExecutableVersion = _executableVersion ?? "unknown",
            BaseAddress = _baseAddress.ToInt64(),
            CandidateCount = Candidates.Count,
            TopCandidates = Candidates.Take(20).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    public static ScannerState? LoadState(string? dir = null)
    {
        dir ??= Environment.CurrentDirectory;
        string path = Path.Combine(dir, StateFileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<ScannerState>(
            File.ReadAllText(path), JsonOptions);
    }

    // ── Helpers ──────────────────────────────────────────────

    private static IntPtr GetBaseModuleAddress(SafeProcessHandle hProcess)
    {
        IntPtr[] modules = new IntPtr[1024];
        if (!NativeMethods.EnumProcessModules(hProcess, modules,
                (uint)(modules.Length * IntPtr.Size), out uint needed))
            return IntPtr.Zero;

        int count = (int)(needed / (uint)IntPtr.Size);
        return count > 0 ? modules[0] : IntPtr.Zero;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── IDisposable ──────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }
}

public sealed record ScannerState
{
    public int ProcessId { get; init; }
    public string ExecutableVersion { get; init; } = string.Empty;
    public long BaseAddress { get; init; }
    public int CandidateCount { get; init; }
    public List<long> TopCandidates { get; init; } = [];
}
