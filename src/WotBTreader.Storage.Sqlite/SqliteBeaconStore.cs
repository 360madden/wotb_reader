using Microsoft.Data.Sqlite;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Storage.Sqlite;

/// <summary>
/// Persists overlay beacons (world-space POIs placed against decoded replay
/// coordinates) in the <c>beacons</c> table. Replacement is an upsert keyed by
/// (battle_session_id, name); a beacon never depends on a decode run.
/// </summary>
internal sealed class SqliteBeaconStore : IBeaconStore
{
    private readonly SqliteStorageContext _context;

    public SqliteBeaconStore(SqliteStorageContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<OverlayBeacon>> GetBeaconsAsync(
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteConnection connection =
            await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        List<OverlayBeacon> beacons = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name, x, y, z, color, visible_from_ticks, visible_until_ticks
            FROM beacons
            WHERE battle_session_id = $sessionId
            ORDER BY created_at_utc, name;
            """;
        command.Parameters.AddWithValue("$sessionId", SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            beacons.Add(new OverlayBeacon(
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : TimeSpan.FromTicks(reader.GetInt64(5)),
                reader.IsDBNull(6) ? null : TimeSpan.FromTicks(reader.GetInt64(6))));
        }

        return beacons;
    }

    public async Task AddBeaconAsync(
        BattleSessionId sessionId,
        OverlayBeacon beacon,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beacon);
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteConnection connection =
            await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO beacons (
                battle_session_id, name, x, y, z, color,
                visible_from_ticks, visible_until_ticks, created_at_utc)
            VALUES ($sessionId, $name, $x, $y, $z, $color,
                $visibleFrom, $visibleUntil, $createdAt)
            ON CONFLICT (battle_session_id, name) DO UPDATE SET
                x = excluded.x,
                y = excluded.y,
                z = excluded.z,
                color = excluded.color,
                visible_from_ticks = excluded.visible_from_ticks,
                visible_until_ticks = excluded.visible_until_ticks;
            """;
        command.Parameters.AddWithValue("$sessionId", SqliteValueConversions.Guid(sessionId.Value));
        command.Parameters.AddWithValue("$name", beacon.Name);
        command.Parameters.AddWithValue("$x", beacon.X);
        command.Parameters.AddWithValue("$y", beacon.Y);
        command.Parameters.AddWithValue("$z", beacon.Z);
        command.Parameters.AddWithValue("$color", beacon.Color);
        command.Parameters.AddWithValue(
            "$visibleFrom",
            beacon.VisibleFrom is null ? DBNull.Value : (object)beacon.VisibleFrom.Value.Ticks);
        command.Parameters.AddWithValue(
            "$visibleUntil",
            beacon.VisibleUntil is null ? DBNull.Value : (object)beacon.VisibleUntil.Value.Ticks);
        command.Parameters.AddWithValue(
            "$createdAt",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveBeaconAsync(
        BattleSessionId sessionId,
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using SqliteConnection connection =
            await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM beacons
            WHERE battle_session_id = $sessionId AND name = $name;
            """;
        command.Parameters.AddWithValue("$sessionId", SqliteValueConversions.Guid(sessionId.Value));
        command.Parameters.AddWithValue("$name", name);
        int deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0;
    }
}
