namespace WotBTreader.Host.Web.Contracts;

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
/// POST /api/v1/game/launch
/// </summary>
public sealed record GameLaunchRequest
{
    /// <summary>Full path to the .wotbreplay file.</summary>
    public string ReplayPath { get; init; } = string.Empty;
}

/// <summary>
/// Result of a game launch attempt.
/// </summary>
public sealed record GameLaunchResponse
{
    /// <summary>True if the game was launched successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable status message.</summary>
    public string Message { get; init; } = string.Empty;
}
