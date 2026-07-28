using System.Text.Json;

namespace WotBTreader.GameHarness;

public sealed record GameMemorySnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }
    public double ReplayTimeSeconds { get; init; }
    public int PlayerHP { get; init; }
    public float PlayerPositionX { get; init; }
    public float PlayerPositionY { get; init; }
    public float PlayerPositionZ { get; init; }
    public float PlayerYaw { get; init; }
    public float CameraPitch { get; init; }
    public int AliveTankCount { get; init; }
    public bool ProcessAccessible { get; init; }
    public bool AnyOffsetsValidated { get; init; }
    public IReadOnlySet<string> ValidatedFields { get; init; } = new HashSet<string>();
}

public sealed record MemoryOffsetTable
{
    public string GameVersion { get; init; } = string.Empty;
    public string ExecutableSha256 { get; init; } = string.Empty;
    public long ReplayTimeOffset { get; init; }
    public long PlayerHPOffset { get; init; }
    public long PlayerPositionXOffset { get; init; }
    public long PlayerPositionYOffset { get; init; }
    public long PlayerPositionZOffset { get; init; }
    public long PlayerYawOffset { get; init; }
    public long CameraPitchOffset { get; init; }
    public long AliveTankCountOffset { get; init; }
}

/// <summary>
/// Reads WoT Blitz replay state from the running game process memory.
/// Offsets are loaded from JSON files in the memory-offsets/ directory.
/// </summary>
public sealed class GameMemoryReader : IDisposable
{
    private static readonly MemoryOffsetTable[] KnownOffsets = LoadOffsetsFromDisk();

    private readonly TimeProvider _timeProvider;
    private SafeProcessHandle? _processHandle;
    private IntPtr _baseAddress;
    private MemoryOffsetTable? _activeTable;
    private readonly HashSet<string> _validatedFields = [];
    private bool _disposed;

    public GameMemoryReader(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsAttached =>
        _processHandle is { IsClosed: false, IsInvalid: false };

    public bool Attach(int processId, string executableVersion, string executableSha256)
    {
        Detach();
        _validatedFields.Clear();

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

        _baseAddress = GetBaseModuleAddress(_processHandle);
        if (_baseAddress == IntPtr.Zero)
        {
            Detach();
            return false;
        }

        _activeTable = SelectOffsetTable(executableVersion, executableSha256);
        return true;
    }

    public ValueTask<GameMemorySnapshot> PollAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsAttached || _activeTable is null)
        {
            return ValueTask.FromResult(new GameMemorySnapshot
            {
                CapturedAtUtc = _timeProvider.GetUtcNow(),
                ProcessAccessible = false,
                AnyOffsetsValidated = _validatedFields.Count > 0,
                ValidatedFields = _validatedFields,
            });
        }

        var snapshot = new GameMemorySnapshot
        {
            CapturedAtUtc = _timeProvider.GetUtcNow(),
            ProcessAccessible = true,
        };

        var table = _activeTable;

        if (table.ReplayTimeOffset > 0)
        { snapshot = snapshot with { ReplayTimeSeconds = ReadDouble(table.ReplayTimeOffset) }; MarkValidated(nameof(snapshot.ReplayTimeSeconds)); }

        if (table.PlayerHPOffset > 0)
        { snapshot = snapshot with { PlayerHP = ReadInt32(table.PlayerHPOffset) }; MarkValidated(nameof(snapshot.PlayerHP)); }

        if (table.PlayerPositionXOffset > 0)
        { snapshot = snapshot with { PlayerPositionX = ReadFloat(table.PlayerPositionXOffset) }; MarkValidated(nameof(snapshot.PlayerPositionX)); }

        if (table.PlayerPositionYOffset > 0)
        { snapshot = snapshot with { PlayerPositionY = ReadFloat(table.PlayerPositionYOffset) }; MarkValidated(nameof(snapshot.PlayerPositionY)); }

        if (table.PlayerPositionZOffset > 0)
        { snapshot = snapshot with { PlayerPositionZ = ReadFloat(table.PlayerPositionZOffset) }; MarkValidated(nameof(snapshot.PlayerPositionZ)); }

        if (table.PlayerYawOffset > 0)
        { snapshot = snapshot with { PlayerYaw = ReadFloat(table.PlayerYawOffset) }; MarkValidated(nameof(snapshot.PlayerYaw)); }

        if (table.CameraPitchOffset > 0)
        { snapshot = snapshot with { CameraPitch = ReadFloat(table.CameraPitchOffset) }; MarkValidated(nameof(snapshot.CameraPitch)); }

        if (table.AliveTankCountOffset > 0)
        { snapshot = snapshot with { AliveTankCount = ReadInt32(table.AliveTankCountOffset) }; MarkValidated(nameof(snapshot.AliveTankCount)); }

        snapshot = snapshot with
        {
            AnyOffsetsValidated = _validatedFields.Count > 0,
            ValidatedFields = new HashSet<string>(_validatedFields),
        };

        return ValueTask.FromResult(snapshot);
    }

    public MemoryOffsetTable? GetActiveOffsetTable() => _activeTable;

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

    private static IntPtr GetBaseModuleAddress(SafeProcessHandle hProcess)
    {
        IntPtr[] modules = new IntPtr[1024];
        if (!NativeMethods.EnumProcessModules(hProcess, modules,
                (uint)(modules.Length * IntPtr.Size), out uint needed))
            return IntPtr.Zero;

        int count = (int)(needed / (uint)IntPtr.Size);
        return count > 0 ? modules[0] : IntPtr.Zero;
    }

    private static MemoryOffsetTable? SelectOffsetTable(
        string executableVersion, string executableSha256)
    {
        foreach (var table in KnownOffsets)
        {
            if (string.Equals(table.GameVersion, executableVersion, StringComparison.OrdinalIgnoreCase))
                return table;
        }

        foreach (var table in KnownOffsets)
        {
            if (!string.IsNullOrEmpty(table.ExecutableSha256)
                && string.Equals(table.ExecutableSha256, executableSha256, StringComparison.OrdinalIgnoreCase))
                return table;
        }

        return new MemoryOffsetTable { GameVersion = executableVersion };
    }

    private void MarkValidated(string fieldName) => _validatedFields.Add(fieldName);

    private void Detach()
    {
        _processHandle?.Dispose();
        _processHandle = null;
        _baseAddress = IntPtr.Zero;
        _activeTable = null;
        _validatedFields.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }

    // ── Offset loading from disk ─────────────────────────────

    private static MemoryOffsetTable[] LoadOffsetsFromDisk()
    {
        string dir = FindOffsetsDirectory();
        if (!Directory.Exists(dir))
            return [new() { GameVersion = "11.8.0.7" }];

        var tables = new List<MemoryOffsetTable>();
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

                tables.Add(new MemoryOffsetTable
                {
                    GameVersion = version,
                    ExecutableSha256 = root.TryGetProperty("executableSha256", out JsonElement sha)
                        ? sha.GetString() ?? string.Empty : string.Empty,
                    ReplayTimeOffset = o.GetProperty("replayTime").GetInt64(),
                    PlayerHPOffset = o.GetProperty("playerHP").GetInt64(),
                    PlayerPositionXOffset = o.GetProperty("playerPositionX").GetInt64(),
                    PlayerPositionYOffset = o.GetProperty("playerPositionY").GetInt64(),
                    PlayerPositionZOffset = o.GetProperty("playerPositionZ").GetInt64(),
                    PlayerYawOffset = o.GetProperty("playerYaw").GetInt64(),
                    CameraPitchOffset = o.GetProperty("cameraPitch").GetInt64(),
                    AliveTankCountOffset = o.GetProperty("aliveTankCount").GetInt64(),
                });
            }
            catch
            {
                // Skip malformed files
            }
        }

        return tables.Count > 0
            ? tables.ToArray()
            : [new() { GameVersion = "11.8.0.7" }];
    }

    private static string FindOffsetsDirectory()
    {
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
}
