using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Application.Storage;

/// <summary>
/// Provides the decoded replay trajectory (per-entity position time-series)
/// used as ground truth by the replay-guided correlation campaign (strategy
/// v4). Purely offline data — no game process access is involved.
/// </summary>
public interface ITrajectoryGroundTruthProvider
{
    /// <summary>
    /// Returns downsampled per-entity trajectories for a decoded battle
    /// session, including the session's replay clock span
    /// (<c>battle_sessions.duration_ticks</c>) and the viewpoint (local
    /// player) participant.
    /// </summary>
    ValueTask<OperationResult<TrajectoryGroundTruth>> GetAsync(
        BattleSessionId sessionId,
        CancellationToken cancellationToken);
}
