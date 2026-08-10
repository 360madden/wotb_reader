using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Storage;

/// <summary>
/// Persists world-space POIs ("beacons") per battle session. Beacons are
/// placed against decoded replay coordinates (offline) and rendered by the
/// overlay HUD as labeled markers. A session's beacons survive restarts; they
/// are keyed by (session, name).
/// </summary>
public interface IBeaconStore
{
    /// <summary>All beacons defined for a session, in insertion order.</summary>
    Task<IReadOnlyList<OverlayBeacon>> GetBeaconsAsync(BattleSessionId sessionId, CancellationToken cancellationToken);

    /// <summary>Adds or replaces the beacon with the same name for the session.</summary>
    Task AddBeaconAsync(BattleSessionId sessionId, OverlayBeacon beacon, CancellationToken cancellationToken);

    /// <summary>Removes the named beacon; returns false when it did not exist.</summary>
    Task<bool> RemoveBeaconAsync(BattleSessionId sessionId, string name, CancellationToken cancellationToken);
}
