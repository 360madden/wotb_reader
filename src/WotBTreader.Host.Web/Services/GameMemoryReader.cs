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
/// Represents the Host.Web memory-observation surface.
/// Process attachment is fail-closed until the evidence-backed offline replay
/// verification gate is implemented in GameIntegration. Polling while detached
/// returns an inaccessible snapshot without opening a process handle.
/// </summary>
public sealed class GameMemoryReader : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public GameMemoryReader(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsAttached => false;

    public int AttachedProcessId => 0;

    public bool Attach(int processId, string executableVersion)
    {
        // M0 containment: finding a game window, PID, or executable version is
        // not sufficient authorization to open a VM-read-capable handle.
        // Milestone 2 will replace this entry point with a GameIntegration
        // lease issued only for a positively verified offline replay session.
        _ = processId;
        _ = executableVersion;
        return false;
    }

    public GameMemorySnapshot Poll()
    {
        return new GameMemorySnapshot
        {
            CapturedAtUtc = _timeProvider.GetUtcNow(),
            ProcessAccessible = false,
            AnyOffsetsValidated = false,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
