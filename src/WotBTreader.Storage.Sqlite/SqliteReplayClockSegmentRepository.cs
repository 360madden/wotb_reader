using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal sealed class SqliteReplayClockSegmentRepository : IReplayClockSegmentRepository
{
    private readonly SqliteStorageContext _context;

    public SqliteReplayClockSegmentRepository(SqliteStorageContext context)
    {
        _context = context;
    }

    public async ValueTask<OperationResult<ReplayClockSegment>> AppendAsync(
        ReplayClockSegment segment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Sequence < 0 ||
            segment.ReplayAnchor < TimeSpan.Zero ||
            segment.Uncertainty < TimeSpan.Zero ||
            !double.IsFinite(segment.Speed) ||
            segment.Speed <= 0 ||
            !Enum.IsDefined(segment.Source))
        {
            return OperationResult.Failure<ReplayClockSegment>(
                StorageErrors.Invalid("The replay-clock segment has invalid monotonic values."));
        }

        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // IMMEDIATE serializes the read-last/append decision across processes;
            // a deferred transaction could admit two writers that observed the
            // same predecessor and then race during lock promotion.
            await using SqliteTransaction transaction =
                connection.BeginTransaction(deferred: false);
            try
            {
                ReplayClockSegment? byId = await ReadByIdAsync(
                    connection,
                    transaction,
                    segment.Id,
                    cancellationToken).ConfigureAwait(false);
                ReplayClockSegment normalized = segment with
                {
                    SourceAnchorUtc = segment.SourceAnchorUtc.ToUniversalTime(),
                    CreatedAtUtc = segment.CreatedAtUtc.ToUniversalTime(),
                };
                if (byId is not null)
                {
                    return byId == normalized
                        ? OperationResult.Success(byId)
                        : OperationResult.Failure<ReplayClockSegment>(
                            StorageErrors.Conflict("The replay-clock segment identifier is immutable."));
                }

                ReplayClockSegment? latest = await ReadLatestAsync(
                    connection,
                    transaction,
                    segment.BattleSessionId,
                    cancellationToken).ConfigureAwait(false);
                if (latest is not null &&
                    (segment.Sequence <= latest.Sequence ||
                     segment.SourceAnchorUtc <= latest.SourceAnchorUtc ||
                     segment.ReplayAnchor < latest.ReplayAnchor))
                {
                    return OperationResult.Failure<ReplayClockSegment>(
                        new ApplicationError(
                            "storage.clock_not_monotonic",
                            "Replay-clock sequence and anchors must be monotonic."));
                }

                await using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO replay_clock_segments(
                        id, battle_session_id, sequence, source_anchor_utc,
                        replay_anchor_ticks, speed, source, uncertainty_ticks,
                        created_at_utc)
                    VALUES(
                        $id, $battleSessionId, $sequence, $sourceAnchorUtc,
                        $replayAnchorTicks, $speed, $source, $uncertaintyTicks,
                        $createdAtUtc);
                    """;
                insert.Parameters.AddWithValue(
                    "$id",
                    SqliteValueConversions.Guid(segment.Id.Value));
                insert.Parameters.AddWithValue(
                    "$battleSessionId",
                    SqliteValueConversions.Guid(segment.BattleSessionId.Value));
                insert.Parameters.AddWithValue("$sequence", segment.Sequence);
                insert.Parameters.AddWithValue(
                    "$sourceAnchorUtc",
                    SqliteValueConversions.Utc(segment.SourceAnchorUtc));
                insert.Parameters.AddWithValue("$replayAnchorTicks", segment.ReplayAnchor.Ticks);
                insert.Parameters.AddWithValue("$speed", segment.Speed);
                insert.Parameters.AddWithValue("$source", (int)segment.Source);
                insert.Parameters.AddWithValue("$uncertaintyTicks", segment.Uncertainty.Ticks);
                insert.Parameters.AddWithValue(
                    "$createdAtUtc",
                    SqliteValueConversions.Utc(segment.CreatedAtUtc));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult.Success(normalized);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return OperationResult.Failure<ReplayClockSegment>(
                StorageErrors.Conflict("The replay-clock segment conflicts with stored evidence."));
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<ReplayClockSegment>(StorageErrors.From(exception));
        }
    }

    public async ValueTask<OperationResult<IReadOnlyList<ReplayClockSegment>>> ListAsync(
        BattleSessionId battleSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!await SessionExistsAsync(connection, battleSessionId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return OperationResult.Failure<IReadOnlyList<ReplayClockSegment>>(
                    StorageErrors.NotFound("Battle session"));
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, battle_session_id, sequence, source_anchor_utc,
                       replay_anchor_ticks, speed, source, uncertainty_ticks,
                       created_at_utc
                FROM replay_clock_segments
                WHERE battle_session_id = $battleSessionId
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue(
                "$battleSessionId",
                SqliteValueConversions.Guid(battleSessionId.Value));
            List<ReplayClockSegment> segments = [];
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                segments.Add(ReadSegment(reader));
            }

            return OperationResult.Success<IReadOnlyList<ReplayClockSegment>>(segments);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<IReadOnlyList<ReplayClockSegment>>(
                StorageErrors.From(exception));
        }
    }

    private static async ValueTask<ReplayClockSegment?> ReadByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReplayClockSegmentId id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, battle_session_id, sequence, source_anchor_utc,
                   replay_anchor_ticks, speed, source, uncertainty_ticks,
                   created_at_utc
            FROM replay_clock_segments
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(id.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSegment(reader)
            : null;
    }

    private static async ValueTask<ReplayClockSegment?> ReadLatestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BattleSessionId battleSessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, battle_session_id, sequence, source_anchor_utc,
                   replay_anchor_ticks, speed, source, uncertainty_ticks,
                   created_at_utc
            FROM replay_clock_segments
            WHERE battle_session_id = $battleSessionId
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "$battleSessionId",
            SqliteValueConversions.Guid(battleSessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSegment(reader)
            : null;
    }

    private static async ValueTask<bool> SessionExistsAsync(
        SqliteConnection connection,
        BattleSessionId battleSessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM battle_sessions WHERE id = $id);";
        command.Parameters.AddWithValue(
            "$id",
            SqliteValueConversions.Guid(battleSessionId.Value));
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static ReplayClockSegment ReadSegment(SqliteDataReader reader) =>
        new(
            new ReplayClockSegmentId(Guid.Parse(reader.GetString(0))),
            new BattleSessionId(Guid.Parse(reader.GetString(1))),
            reader.GetInt64(2),
            SqliteValueConversions.ReadUtc(reader, 3),
            TimeSpan.FromTicks(reader.GetInt64(4)),
            reader.GetDouble(5),
            (TelemetrySourceKind)reader.GetInt32(6),
            TimeSpan.FromTicks(reader.GetInt64(7)),
            SqliteValueConversions.ReadUtc(reader, 8));
}
