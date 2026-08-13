using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Storage;

/// <summary>
/// Builds renderable overlay frames from decoded replay data at an arbitrary
/// replay time. Source-agnostic by design: the overlay layer consumes only
/// <see cref="OverlayFrame"/>, so a future live source (memory reads behind
/// the gated surface) can implement the same interface without any overlay
/// rewrite. Purely offline — no game process access is involved.
/// </summary>
public interface IOverlayFrameSource
{
    /// <summary>
    /// Returns the overlay frame for <paramref name="sessionId"/> at
    /// <paramref name="replayTime"/>: the camera (the caller-supplied
    /// <paramref name="cameraOverride"/> when finite, else the viewpoint
    /// tank) plus every tank with a nearest position sample at or before
    /// that time. Tanks without position evidence at the frame time are
    /// omitted; the frame is never fabricated from endpoints outside the
    /// sample span. The override is the CAM-001 seam: a verified memory
    /// camera replaces the viewpoint approximation, and it is fail-closed
    /// (non-finite pose falls back to the viewpoint).
    /// </summary>
    ValueTask<OperationResult<OverlayFrame>> GetFrameAsync(
        BattleSessionId sessionId,
        TimeSpan replayTime,
        CancellationToken cancellationToken,
        OverlayCamera? cameraOverride = null,
        string? shellName = null);
}
