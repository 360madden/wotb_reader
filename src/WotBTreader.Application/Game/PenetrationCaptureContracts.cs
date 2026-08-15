using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Game;

/// <summary>
/// The only capture phase currently admitted by the managed-offline contract.
/// The coordinator owns phase order and all memory locations; callers cannot
/// select a scan, address, or read shape.
/// </summary>
public enum PenetrationCapturePhaseIntent
{
    FullExactInputVerdict = 0,
}

/// <summary>
/// Opaque operator intent for one decoded battle session. The coordinator
/// binds the decode run to its own managed launch and never accepts process,
/// module, address, or raw-memory inputs.
/// </summary>
public sealed record PenetrationCaptureRequest(
    DecodeRunId DecodeRunId,
    PenetrationCapturePhaseIntent PhaseIntent =
        PenetrationCapturePhaseIntent.FullExactInputVerdict);

/// <summary>
/// Executes one serialized, coordinator-owned managed-offline capture and
/// returns only the privacy-safe aggregate evaluation.
/// </summary>
public interface IPenetrationCapture
{
    ValueTask<OperationResult<PenetrationCaptureEvaluation>> CaptureAsync(
        PenetrationCaptureRequest request,
        CancellationToken cancellationToken);
}
