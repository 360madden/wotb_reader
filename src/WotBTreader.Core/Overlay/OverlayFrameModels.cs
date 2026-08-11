namespace WotBTreader.Core.Overlay;

/// <summary>
/// The rendered camera for one overlay frame: the viewpoint entity's world
/// position and rotation at a replay time. The yaw/pitch come from the
/// type-10 packet tail (migration 5); null means the sample predates the
/// persisted rotation or the viewpoint entity has no position sample.
/// </summary>
public sealed record OverlayCamera(
    double X,
    double Y,
    double Z,
    double? YawRadians,
    double? PitchRadians,
    double? RollRadians);

/// <summary>
/// One tank rendered in an overlay frame. World position and facing come
/// from the NEAREST decoded position sample at the frame's replay time
/// (fail-closed: no sample at or before the frame time means the tank is
/// omitted from that frame). HP is expressed as a 0..1 fraction of the
/// tank's observed damage arc: cumulative damage received up to the frame
/// time over the total damage the tank ever received — 1.0 when the tank
/// took no damage (exact max HP is not in the decoded data). Alive is false
/// once a Destroyed event lands for the entity.
/// </summary>
public sealed record OverlayTankState(
    long EntityId,
    double X,
    double Y,
    double Z,
    double? YawRadians,
    double HpFraction,
    bool Alive,
    int? TeamNumber,
    string? PlayerName,
    string? ClanTag,
    string? TankName,
    string? TankClass,
    double DistanceMeters);

/// <summary>
/// A persistent point of interest anchored in world space, rendered as a
/// labeled marker over the game window. Color is an HTML-style hex string
/// ("#FFD700"). The replay-time tag makes a beacon visible only inside an
/// optional window — null bounds mean always visible. Pure offline data:
/// beacons are placed against decoded replay coordinates and persisted per
/// session.
/// </summary>
public sealed record OverlayBeacon(
    string Name,
    double X,
    double Y,
    double Z,
    string Color,
    TimeSpan? VisibleFrom,
    TimeSpan? VisibleUntil);

/// <summary>
/// A transient event-feed marker for one overlay frame: a damage hit or a
/// destruction that landed within the frame's recent window. Pure offline
/// data — decoded canonical events, never a game process. <see cref="Kind"/>
/// is <see cref="CanonicalEventKind.Damage"/> (with the amount) or
/// <see cref="CanonicalEventKind.Destroyed"/>.
/// </summary>
public sealed record OverlayEventPip(
    long EntityId,
    CanonicalEventKind Kind,
    int Damage,
    TimeSpan ReplayTime);

/// <summary>
/// A complete renderable instant of a replay battle: the camera plus every
/// tank state that has position evidence at the frame's replay time, the
/// recent event-feed pips (damage/death), and the persistent kill feed for
/// the HUD. Pure offline data — built entirely from the decoded replay
/// projection, never from a game process.
/// </summary>
public sealed record OverlayFrame(
    TimeSpan ReplayTime,
    OverlayCamera Camera,
    IReadOnlyList<OverlayTankState> Tanks,
    IReadOnlyList<OverlayEventPip> Pips,
    IReadOnlyList<OverlayKill> Kills)
{
    /// <summary>Frame with an empty kill feed — for fixtures that only
    /// exercise the nameplate/pip layers.</summary>
    public OverlayFrame(
        TimeSpan replayTime,
        OverlayCamera camera,
        IReadOnlyList<OverlayTankState> tanks,
        IReadOnlyList<OverlayEventPip> pips)
        : this(replayTime, camera, tanks, pips, []) { }
}

/// <summary>
/// One kill for the HUD kill feed: the destroyed tank's entity and the
/// killer's entity when attribution is possible (the attacker of the last
/// damage event received before the destroy marker, allowing the small
/// posthumous window observed on real replays). Killer is null for
/// environmental kills (no damage evidence). Pure offline data — decoded
/// canonical Destroyed + Damage events, never a game process.
/// </summary>
public sealed record OverlayKill(
    long VictimEntityId,
    long? KillerEntityId,
    TimeSpan ReplayTime);
