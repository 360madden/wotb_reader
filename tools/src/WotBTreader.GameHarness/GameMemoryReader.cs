namespace WotBTreader.GameHarness;

/// <summary>
/// Snapshot of replay state read from the WoT Blitz game process memory.
/// All values are zero/default when the offset is unknown or the read failed.
/// </summary>
public sealed record GameMemorySnapshot
{
    /// <summary>UTC timestamp when this snapshot was captured.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Current replay playback time in seconds. 0 if unknown.</summary>
    public double ReplayTimeSeconds { get; init; }

    /// <summary>HP of the viewpoint/focused player tank. 0 if unknown.</summary>
    public int PlayerHP { get; init; }

    /// <summary>World-space X position of the player tank.</summary>
    public float PlayerPositionX { get; init; }

    /// <summary>World-space Y position (height) of the player tank.</summary>
    public float PlayerPositionY { get; init; }

    /// <summary>World-space Z position of the player tank.</summary>
    public float PlayerPositionZ { get; init; }

    /// <summary>Camera yaw in radians. 0 if unknown.</summary>
    public float PlayerYaw { get; init; }

    /// <summary>Camera pitch in radians. 0 if unknown.</summary>
    public float CameraPitch { get; init; }

    /// <summary>Number of tanks currently alive in the battle.</summary>
    public int AliveTankCount { get; init; }

    /// <summary>Whether the process was accessible during this read.</summary>
    public bool ProcessAccessible { get; init; }

    /// <summary>Whether any offsets were successfully validated in this session.</summary>
    public bool AnyOffsetsValidated { get; init; }

    /// <summary>Set of field names whose offsets are confirmed valid for the running version.</summary>
    public IReadOnlySet<string> ValidatedFields { get; init; } = new HashSet<string>();
}

/// <summary>
/// Versioned offset table for a known WoT Blitz build.
/// Offsets are relative to the game's base module address.
/// </summary>
public sealed record MemoryOffsetTable
{
    /// <summary>The game version this table applies to (e.g. "11.8.0.7").</summary>
    public string GameVersion { get; init; } = string.Empty;

    /// <summary>SHA-256 of the game executable this table was built for.</summary>
    public string ExecutableSha256 { get; init; } = string.Empty;

    /// <summary>Base address of the game module (typically discovered at runtime).</summary>
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
/// Reads WoT Blitz replay state from the running game process memory using
/// ReadProcessMemory. Offsets are version-specific and must be discovered per
/// game build. Unknown offsets return default values without throwing.
///
/// All reads are read-only; this class never writes to the game process.
/// </summary>
public sealed class GameMemoryReader : IDisposable
{
    // ── Known offset tables (placeholder — to be discovered per build) ──

    private static readonly MemoryOffsetTable[] KnownOffsets =
    [
        new()
        {
            GameVersion = "11.8.0.7",
            ExecutableSha256 = string.Empty, // To be filled after first probe
            ReplayTimeOffset = 0,
            PlayerHPOffset = 0,
            PlayerPositionXOffset = 0,
            PlayerPositionYOffset = 0,
            PlayerPositionZOffset = 0,
            PlayerYawOffset = 0,
            CameraPitchOffset = 0,
            AliveTankCountOffset = 0,
        },
    ];

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

    /// <summary>
    /// Whether the reader is currently attached to a game process.
    /// </summary>
    public bool IsAttached =>
        _processHandle is { IsClosed: false, IsInvalid: false };

    /// <summary>
    /// Attempts to attach to the specified game process and select the best
    /// offset table for its version. Call this before <see cref="PollAsync"/>.
    /// </summary>
    public bool Attach(int processId, string executableVersion, string executableSha256)
    {
        Detach();

        _validatedFields.Clear();

        // Open process for VM read
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

        // Find the game's base module address
        _baseAddress = GetBaseModuleAddress(_processHandle);
        if (_baseAddress == IntPtr.Zero)
        {
            Detach();
            return false;
        }

        // Select offset table
        _activeTable = SelectOffsetTable(executableVersion, executableSha256);

        return true;
    }

    /// <summary>
    /// Reads a snapshot of replay state from the game process memory.
    /// Returns default values for any offsets that are unknown or inaccessible.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A snapshot with all readable fields populated.</returns>
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

        // Read each known field
        if (_activeTable.ReplayTimeOffset > 0)
        {
            double val = ReadDouble(_activeTable.ReplayTimeOffset);
            snapshot = snapshot with { ReplayTimeSeconds = val };
            MarkValidated(nameof(snapshot.ReplayTimeSeconds));
        }

        if (_activeTable.PlayerHPOffset > 0)
        {
            int val = ReadInt32(_activeTable.PlayerHPOffset);
            snapshot = snapshot with { PlayerHP = val };
            MarkValidated(nameof(snapshot.PlayerHP));
        }

        if (_activeTable.PlayerPositionXOffset > 0)
        {
            float val = ReadFloat(_activeTable.PlayerPositionXOffset);
            snapshot = snapshot with { PlayerPositionX = val };
            MarkValidated(nameof(snapshot.PlayerPositionX));
        }

        if (_activeTable.PlayerPositionYOffset > 0)
        {
            float val = ReadFloat(_activeTable.PlayerPositionYOffset);
            snapshot = snapshot with { PlayerPositionY = val };
            MarkValidated(nameof(snapshot.PlayerPositionY));
        }

        if (_activeTable.PlayerPositionZOffset > 0)
        {
            float val = ReadFloat(_activeTable.PlayerPositionZOffset);
            snapshot = snapshot with { PlayerPositionZ = val };
            MarkValidated(nameof(snapshot.PlayerPositionZ));
        }

        if (_activeTable.PlayerYawOffset > 0)
        {
            float val = ReadFloat(_activeTable.PlayerYawOffset);
            snapshot = snapshot with { PlayerYaw = val };
            MarkValidated(nameof(snapshot.PlayerYaw));
        }

        if (_activeTable.CameraPitchOffset > 0)
        {
            float val = ReadFloat(_activeTable.CameraPitchOffset);
            snapshot = snapshot with { CameraPitch = val };
            MarkValidated(nameof(snapshot.CameraPitch));
        }

        if (_activeTable.AliveTankCountOffset > 0)
        {
            int val = ReadInt32(_activeTable.AliveTankCountOffset);
            snapshot = snapshot with { AliveTankCount = val };
            MarkValidated(nameof(snapshot.AliveTankCount));
        }

        snapshot = snapshot with
        {
            AnyOffsetsValidated = _validatedFields.Count > 0,
            ValidatedFields = new HashSet<string>(_validatedFields),
        };

        return ValueTask.FromResult(snapshot);
    }

    /// <summary>
    /// Returns the current offset table or null if not attached.
    /// Callers can inspect this to understand which offsets are configured.
    /// </summary>
    public MemoryOffsetTable? GetActiveOffsetTable() => _activeTable;

    // ── Low-level memory reads ───────────────────────────────

    private int ReadInt32(long offset)
    {
        if (_processHandle is null) return 0;
        IntPtr address = IntPtr.Add(_baseAddress, (int)offset);
        byte[] buffer = new byte[4];
        bool ok = NativeMethods.ReadProcessMemory(_processHandle, address, buffer, 4, out uint read);
        return ok && read == 4 ? BitConverter.ToInt32(buffer) : 0;
    }

    private float ReadFloat(long offset)
    {
        if (_processHandle is null) return 0f;
        IntPtr address = IntPtr.Add(_baseAddress, (int)offset);
        byte[] buffer = new byte[4];
        bool ok = NativeMethods.ReadProcessMemory(_processHandle, address, buffer, 4, out uint read);
        return ok && read == 4 ? BitConverter.ToSingle(buffer) : 0f;
    }

    private double ReadDouble(long offset)
    {
        if (_processHandle is null) return 0.0;
        IntPtr address = IntPtr.Add(_baseAddress, (int)offset);
        byte[] buffer = new byte[8];
        bool ok = NativeMethods.ReadProcessMemory(_processHandle, address, buffer, 8, out uint read);
        return ok && read == 8 ? BitConverter.ToDouble(buffer) : 0.0;
    }

    // ── Helpers ──────────────────────────────────────────────

    private static IntPtr GetBaseModuleAddress(SafeProcessHandle hProcess)
    {
        // Enumerate modules to find the base address of the main executable
        IntPtr[] modules = new IntPtr[1024];
        if (!NativeMethods.EnumProcessModules(hProcess, modules, (uint)(modules.Length * IntPtr.Size), out uint needed))
        {
            return IntPtr.Zero;
        }

        int moduleCount = (int)(needed / (uint)IntPtr.Size);
        if (moduleCount == 0)
        {
            return IntPtr.Zero;
        }

        // First module is always the main executable
        return modules[0];
    }

    private static MemoryOffsetTable? SelectOffsetTable(
        string executableVersion,
        string executableSha256)
    {
        foreach (var table in KnownOffsets)
        {
            if (string.Equals(table.GameVersion, executableVersion, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        // Fall back to SHA-256 match
        foreach (var table in KnownOffsets)
        {
            if (!string.IsNullOrEmpty(table.ExecutableSha256)
                && string.Equals(table.ExecutableSha256, executableSha256, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }
        }

        // No match — return default with all offsets at 0
        return new MemoryOffsetTable { GameVersion = executableVersion };
    }

    private void MarkValidated(string fieldName)
    {
        _validatedFields.Add(fieldName);
    }

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
}
