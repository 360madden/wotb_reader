namespace WotBTreader.Core.Discovery;

/// <summary>
/// One decoded HP-affecting event for a battle session: a direct-damage hit
/// or a destroy. The canonical-event columns are authoritative (replay time,
/// victim entity id); the parsed values are best-effort extractions from the
/// event's <c>values_json</c> — null means the value was absent or
/// unparseable, never guessed.
/// </summary>
public sealed record HpDamageEvent(
    ParticipantId? ParticipantId,
    long? EntityId,
    TimeSpan ReplayTime,
    CanonicalEventKind Kind,
    int? Damage,
    long? AttackerEntityId,
    string? ValuesJson);

/// <summary>
/// The decoded HP-affecting event timeline for one battle session, used as
/// ground truth by the record-diffing discovery playbook: a memory reader
/// dumps the entity record and correlates byte changes to these events
/// (victim entity id + replay time).
/// </summary>
public sealed record HpGroundTruth(
    TimeSpan Duration,
    IReadOnlyList<HpDamageEvent> Events);
