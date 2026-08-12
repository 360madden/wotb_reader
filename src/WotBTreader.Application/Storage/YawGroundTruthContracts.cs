using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Application.Storage;

/// <summary>
/// The rotation component a heading correlation targets. The decoded
/// <c>position_samples</c> row carries the full type-10 packet-tail
/// rotation triple (yaw/pitch/roll, migration 5, 2026-08-10); the facing
/// playbook's yaw-diff command selects which column is ground truth so the
/// SAME immutable ring-record dumps can be re-verdict for each member of
/// the rotation triple (roll <c>+0x28</c> / pitch <c>+0x2C</c> / yaw
/// <c>+0x30</c>).
/// </summary>
public enum HeadingField
{
    Yaw,
    Pitch,
    Roll,
}

/// <summary>
/// Provides the decoded packet-derived rotation timeline (radians, from the
/// type-10 position packet tail) used as ground truth by the facing
/// record-diffing discovery playbook. Purely offline data — no game process
/// access is involved.
/// </summary>
public interface IYawGroundTruthProvider
{
    /// <summary>
    /// Returns every persisted rotation sample for the requested
    /// <see cref="HeadingField"/> of a decoded battle session, ordered by
    /// replay time, including the session's replay clock span
    /// (<c>battle_sessions.duration_ticks</c>).
    /// </summary>
    ValueTask<OperationResult<YawGroundTruth>> GetAsync(
        BattleSessionId sessionId,
        HeadingField field,
        CancellationToken cancellationToken);
}
