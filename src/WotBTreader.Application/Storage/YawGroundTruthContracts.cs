using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Application.Storage;

/// <summary>
/// Provides the decoded packet-derived yaw timeline (radians, from the
/// type-10 position packet tail) used as ground truth by the facing
/// record-diffing discovery playbook. Purely offline data — no game process
/// access is involved.
/// </summary>
public interface IYawGroundTruthProvider
{
    /// <summary>
    /// Returns every persisted yaw sample for a decoded battle session,
    /// ordered by replay time, including the session's replay clock span
    /// (<c>battle_sessions.duration_ticks</c>).
    /// </summary>
    ValueTask<OperationResult<YawGroundTruth>> GetAsync(
        BattleSessionId sessionId,
        CancellationToken cancellationToken);
}
