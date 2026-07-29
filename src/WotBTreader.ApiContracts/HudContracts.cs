namespace WotBTreader.ApiContracts;

/// <summary>
/// Capability-neutral wire shapes retained for the bounded HUD command/status
/// protocol. The overlay does not host a control plane.
/// </summary>
public sealed record OverlayStatusResponse
{
    /// <summary>Whether the overlay has connected to a web host.</summary>
    public bool Connected { get; init; }

    /// <summary>The loopback base URI of the connected web host, or null.</summary>
    public string? BaseUri { get; init; }

    /// <summary>Number of sessions loaded from the web host.</summary>
    public int SessionsCount { get; init; }

    /// <summary>Map name of the currently selected session, or null.</summary>
    public string? SelectedMap { get; init; }

    /// <summary>Whether the timeline is currently auto-advancing.</summary>
    public bool IsPlaying { get; init; }

    /// <summary>Current scrubber position in seconds.</summary>
    public double CurrentTimeSeconds { get; init; }

    /// <summary>Total battle duration in seconds, or 0 if unknown.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Current playback speed multiplier.</summary>
    public double PlaybackSpeed { get; init; }

    /// <summary>Whether the game window is currently being tracked.</summary>
    public bool GameWindowFound { get; init; }

    /// <summary>Human-readable status message from the overlay.</summary>
    public string Status { get; init; } = string.Empty;
}

/// <summary>Request to launch a replay into the game.</summary>
public sealed record LaunchRequest
{
    /// <summary>Full path to the .wotbreplay file to launch.</summary>
    public string ReplayPath { get; init; } = string.Empty;
}

/// <summary>Request to seek the timeline scrubber.</summary>
public sealed record SeekRequest
{
    /// <summary>Target time in seconds from the start of the replay.</summary>
    public double Seconds { get; init; }
}

/// <summary>Request to set the playback speed.</summary>
public sealed record SpeedRequest
{
    /// <summary>Playback speed multiplier: 0.5, 1, 2, 4, or 8.</summary>
    public double Speed { get; init; }
}

/// <summary>Request to select a session by its battle session ID.</summary>
public sealed record SelectSessionRequest
{
    /// <summary>The battle session ID to select.</summary>
    public Guid BattleSessionId { get; init; }
}

/// <summary>Response returned after a launch attempt.</summary>
public sealed record LaunchResponse
{
    /// <summary>True if the launch was initiated successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable message describing the outcome.</summary>
    public string Message { get; init; } = string.Empty;
}
