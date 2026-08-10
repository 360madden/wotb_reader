using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Application.Storage;

/// <summary>
/// Provides the decoded HP-affecting event timeline (direct-damage hits and
/// destroys, per canonical event) used as ground truth by the record-diffing
/// discovery playbook. Purely offline data — no game process access is
/// involved.
/// </summary>
public interface IHpGroundTruthProvider
{
    /// <summary>
    /// Returns the damage/destroyed canonical events for a decoded battle
    /// session, ordered by replay time, including the session's replay clock
    /// span (<c>battle_sessions.duration_ticks</c>).
    /// </summary>
    ValueTask<OperationResult<HpGroundTruth>> GetAsync(
        BattleSessionId sessionId,
        CancellationToken cancellationToken);
}
