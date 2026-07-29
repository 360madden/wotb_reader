namespace WotBTreader.ApiContracts;

/// <summary>
/// Read-only snapshot of the game process and replay lifecycle state.
/// Returned by GET /api/v1/game/state.
/// </summary>
public sealed record GameStateResponse
{
    /// <summary>Whether a game process is currently present.</summary>
    public bool GamePresent { get; init; }

    /// <summary>Current evidence-backed verification state.</summary>
    public string VerificationState { get; init; } = "Unknown";

    /// <summary>UTC timestamp when this state was observed.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>UTC expiry of positive evidence, when applicable.</summary>
    public DateTimeOffset? EvidenceExpiresAtUtc { get; init; }

    /// <summary>Stable, path-free reason code for the current state.</summary>
    public string ReasonCode { get; init; } = "session.unknown";
}

/// <summary>
/// Request to launch a replay through the installed game.
/// POST /api/v1/game/launch.
/// </summary>
public sealed record GameLaunchRequest
{
    /// <summary>Identifier of a replay artifact managed by the application.</summary>
    public string SourceArtifactId { get; init; } = string.Empty;
}

/// <summary>Result of a game launch attempt.</summary>
public sealed record GameLaunchResponse
{
    /// <summary>True if the game was launched successfully.</summary>
    public bool Success { get; init; }

    /// <summary>Stable, path-free launch status code.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Snapshot of replay memory read from the running game process.
/// Returned by GET /api/v1/game/memory. Telemetry fields are null when
/// unsupported or unknown so legitimate zero values remain distinguishable.
/// </summary>
public sealed record GameMemoryResponse
{
    /// <summary>UTC timestamp when this snapshot was captured.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Unknown, Unsupported, or Available.</summary>
    public string Availability { get; init; } = "Unknown";

    /// <summary>Current replay playback time in seconds.</summary>
    public double? ReplayTimeSeconds { get; init; }

    /// <summary>HP of the viewpoint player tank.</summary>
    public int? PlayerHP { get; init; }

    /// <summary>World-space X position of the player tank.</summary>
    public float? PlayerPositionX { get; init; }

    /// <summary>World-space Y position (height) of the player tank.</summary>
    public float? PlayerPositionY { get; init; }

    /// <summary>World-space Z position of the player tank.</summary>
    public float? PlayerPositionZ { get; init; }

    /// <summary>Camera yaw in radians.</summary>
    public float? PlayerYaw { get; init; }

    /// <summary>Camera pitch in radians.</summary>
    public float? CameraPitch { get; init; }

    /// <summary>Number of tanks alive in the battle.</summary>
    public int? AliveTankCount { get; init; }
}
