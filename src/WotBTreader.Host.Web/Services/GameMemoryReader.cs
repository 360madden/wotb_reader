using System.Text.Json;
using WotBTreader.Host.Web.Infrastructure;

namespace WotBTreader.Host.Web.Services;

public sealed record GameMemorySnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }
    public int ProcessId { get; init; }
    public bool ProcessAccessible { get; init; }
    public double ReplayTimeSeconds { get; init; }
    public int PlayerHP { get; init; }
    public float PlayerPositionX { get; init; }
    public float PlayerPositionY { get; init; }
    public float PlayerPositionZ { get; init; }
    public float PlayerYaw { get; init; }
    public float CameraPitch { get; init; }
    public int AliveTankCount { get; init; }
    public bool AnyOffsetsValidated { get; init; }
}

/// <summary>
/// Reads WoT Blitz replay state from the running game process memory.
/// Offsets are loaded from JSON files in the memory-offsets/ directory.
/// Unknown offsets return default values without throwing.
/// All reads are read-only; this class never writes to the game process.
/// Offsets are relative to the game's base module address (assumed &lt; 2 GB).
/// </summary>
public sealed class GameMemoryReader : IDisposable
{
    private sealed record OffsetTable(
        string GameVersion,
        long ReplayTime,
        long PlayerHP,
        long PositionX,
        long PositionY,
        long PositionZ,
        long PlayerYaw,
        long CameraPitch,
        long AliveTankCount);

    // Hardcoded fallback — loaded offsets from disk take precedence.
    private static readonly OffsetTable[] KnownOffsets = LoadOffsetsFromDisk();

    private readonly TimeProvider _timeProvider;
    private SafeProcessHandle? _processHandle;
    private IntPtr _baseAddress;
    private OffsetTable? _activeTable;
    private bool _anyValidated;
    private bool _disposed;

    public GameMemoryReader(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsAttached =>
        _processHandle is { IsClosed: false, IsInvalid: false };

    public int AttachedProcessId { get; private set; }

    public bool Attach(int processId, string executableVersion)
    {
        Detach();
        AttachedProcessId = 0;

        _processHandle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFORMATION,
            bInheritHandle: false,
            (uint)processId);

        if (_processHandle.IsInvalid)
        {
            _processHandle.Dispose();
            _processHandle = null;
            return false;
        }

        IntPtr[] modules = new IntPtr[1024];
        if (!NativeMethods.EnumProcessModules(_processHandle, modules,
                (uint)(modules.Length * IntPtr.Size), out uint needed))
        {
            Detach();
            return false;
        }

        int moduleCount = (int)(needed / (uint)IntPtr.Size);
        if (moduleCount == 0)
        {
            Detach();
            return false;
        }

        _baseAddress = modules[0];
        _activeTable = KnownOffsets.FirstOrDefault(
            t => string.Equals(t.GameVersion, executableVersion, StringComparison.OrdinalIgnoreCase))
            ?? new OffsetTable(executableVersion, 0, 0, 0, 0, 0, 0, 0, 0);
        AttachedProcessId = processId;

        return true;
    }

    public GameMemorySnapshot Poll()
    {
        if (!IsAttached || _activeTable is null)
        {
            return new GameMemorySnapshot
            {
                CapturedAtUtc = _timeProvider.GetUtcNow(),
                ProcessAccessible = false,
                AnyOffsetsValidated = _anyValidated,
            };
        }

        var snapshot = new GameMemorySnapshot
        {
            CapturedAtUtc = _timeProvider.GetUtcNow(),
            ProcessId = AttachedProcessId,
            ProcessAccessible = true,
        };

        var table = _activeTable;
        bool anyRead = false;

        if (table.ReplayTime > 0)
        { snapshot = snapshot with { ReplayTimeSeconds = ReadDouble(table.ReplayTime) }; anyRead = true; }

        if (table.PlayerHP > 0)
        { snapshot = snapshot with { PlayerHP = ReadInt32(table.PlayerHP) }; anyRead = true; }

        if (table.PositionX > 0)
        { snapshot = snapshot with { PlayerPositionX = ReadFloat(table.PositionX) }; anyRead = true; }

        if (table.PositionY > 0)
        { snapshot = snapshot with { PlayerPositionY = ReadFloat(table.PositionY) }; anyRead = true; }

        if (table.PositionZ > 0)
        { snapshot = snapshot with { PlayerPositionZ = ReadFloat(table.PositionZ) }; anyRead = true; }

        if (table.PlayerYaw > 0)
        { snapshot = snapshot with { PlayerYaw = ReadFloat(table.PlayerYaw) }; anyRead = true; }

        if (table.CameraPitch > 0)
        { snapshot = snapshot with { CameraPitch = ReadFloat(table.CameraPitch) }; anyRead = true; }

        if (table.AliveTankCount > 0)
        { snapshot = snapshot with { AliveTankCount = ReadInt32(table.AliveTankCount) }; anyRead = true; }

        _anyValidated = _anyValidated || anyRead;

        return snapshot with { AnyOffsetsValidated = _anyValidated };
    }

    private int ReadInt32(long offset)
    {
        if (_processHandle is null) return 0;
        byte[] buffer = new byte[4];
        bool ok = NativeMethods.ReadProcessMemory(
            _processHandle, IntPtr.Add(_baseAddress, (int)offset), buffer, 4, out uint read);
        return ok && read == 4 ? BitConverter.ToInt32(buffer) : 0;
    }

    private float ReadFloat(long offset)
    {
        if (_processHandle is null) return 0f;
        byte[] buffer = new byte[4];
        bool ok = NativeMethods.ReadProcessMemory(
            _processHandle, IntPtr.Add(_baseAddress, (int)offset), buffer, 4, out uint read);
        return ok && read == 4 ? BitConverter.ToSingle(buffer) : 0f;
    }

    private double ReadDouble(long offset)
    {
        if (_processHandle is null) return 0.0;
        byte[] buffer = new byte[8];
        bool ok = NativeMethods.ReadProcessMemory(
            _processHandle, IntPtr.Add(_baseAddress, (int)offset), buffer, 8, out uint read);
        return ok && read == 8 ? BitConverter.ToDouble(buffer) : 0.0;
    }

    // ── Offset loading from disk ─────────────────────────────

    private static OffsetTable[] LoadOffsetsFromDisk()
    {
        string dir = FindOffsetsDirectory();
        if (!Directory.Exists(dir))
            return [new("11.8.0.7", 0, 0, 0, 0, 0, 0, 0, 0)];

        var tables = new List<OffsetTable>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("schema.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("scanner-state.json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;

                string version = root.GetProperty("gameVersion").GetString() ?? "unknown";
                JsonElement o = root.GetProperty("offsets");

                tables.Add(new OffsetTable(
                    version,
                    o.GetProperty("replayTime").GetInt64(),
                    o.GetProperty("playerHP").GetInt64(),
                    o.GetProperty("playerPositionX").GetInt64(),
                    o.GetProperty("playerPositionY").GetInt64(),
                    o.GetProperty("playerPositionZ").GetInt64(),
                    o.GetProperty("playerYaw").GetInt64(),
                    o.GetProperty("cameraPitch").GetInt64(),
                    o.GetProperty("aliveTankCount").GetInt64()));
            }
            catch
            {
                // Skip malformed files
            }
        }

        return tables.Count > 0
            ? tables.ToArray()
            : [new("11.8.0.7", 0, 0, 0, 0, 0, 0, 0, 0)];
    }

    private static string FindOffsetsDirectory()
    {
        // Walk up from the current directory looking for memory-offsets/
        string? current = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            string candidate = Path.Combine(current, "memory-offsets");
            if (Directory.Exists(candidate))
                return candidate;

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current)
                break;
            current = parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "memory-offsets");
    }

    private void Detach()
    {
        _processHandle?.Dispose();
        _processHandle = null;
        _baseAddress = IntPtr.Zero;
        _activeTable = null;
        AttachedProcessId = 0;
        _anyValidated = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }
}
