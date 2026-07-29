namespace WotBTreader.ApiContracts;

/// <summary>
/// Read-only snapshot of the game process and replay lifecycle state.
/// Returned by GET /api/v1/game/state.
/// </summary>
public sealed record GameStateResponse
{
    /// <summary>Whether wotblitz.exe is currently running.</summary>
    public bool GameRunning { get; init; }

    /// <summary>Process ID of the running game, or null if not running.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Window handle of the game window, or null if not found.</summary>
    public long? WindowHandle { get; init; }

    /// <summary>Current replay lifecycle state (e.g. NotRunning, OfflineReplayActive).</summary>
    public string ReplayState { get; init; } = "Unknown";

    /// <summary>UTC timestamp when the replay state was last observed.</summary>
    public DateTimeOffset? ReplayStateObservedAtUtc { get; init; }

    /// <summary>Log watermark at the time of observation, for correlation.</summary>
    public string? LogWatermark { get; init; }
}

/// <summary>
/// Request to launch a replay through the installed game.
/// POST /api/v1/game/launch.
/// </summary>
public sealed record GameLaunchRequest
{
    /// <summary>Full path to the .wotbreplay file.</summary>
    public string ReplayPath { get; init; } = string.Empty;
}

/// <summary>Result of a game launch attempt.</summary>
public sealed record GameLaunchResponse
{
    /// <summary>True if the game was launched successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable status message.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Snapshot of replay memory read from the running game process.
/// Returned by GET /api/v1/game/memory. All fields are 0/default when
/// offsets are unknown or the process is not accessible.
/// </summary>
public sealed record GameMemoryResponse
{
    /// <summary>UTC timestamp when this snapshot was captured.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Whether the game process was accessible for memory reading.</summary>
    public bool ProcessAccessible { get; init; }

    /// <summary>Current replay playback time in seconds.</summary>
    public double ReplayTimeSeconds { get; init; }

    /// <summary>HP of the viewpoint player tank.</summary>
    public int PlayerHP { get; init; }

    /// <summary>World-space X position of the player tank.</summary>
    public float PlayerPositionX { get; init; }

    /// <summary>World-space Y position (height) of the player tank.</summary>
    public float PlayerPositionY { get; init; }

    /// <summary>World-space Z position of the player tank.</summary>
    public float PlayerPositionZ { get; init; }

    /// <summary>Camera yaw in radians.</summary>
    public float PlayerYaw { get; init; }

    /// <summary>Camera pitch in radians.</summary>
    public float CameraPitch { get; init; }

    /// <summary>Number of tanks alive in the battle.</summary>
    public int AliveTankCount { get; init; }

    /// <summary>Whether any memory offsets were validated in this session.</summary>
    public bool AnyOffsetsValidated { get; init; }
}
