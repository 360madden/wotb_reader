using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Storage.Sqlite;

/// <summary>
/// Reads the decoded packet-derived yaw timeline (radians) for the facing
/// record-diffing playbook. The yaw field is authoritative replay ground
/// truth: the type-10 position packet tail carries the entity's rotation,
/// persisted as <c>position_samples.yaw</c> (migration 5, 2026-08-10) and
/// validated 1:1 against the position-derived heading on both 11.19 replays.
/// Rows without a yaw value (pre-migration decodes) are simply absent.
/// </summary>
internal sealed class SqliteYawGroundTruthProvider : IYawGroundTruthProvider
{
    private readonly SqliteStorageContext _context;

    public SqliteYawGroundTruthProvider(SqliteStorageContext context)
    {
        _context = context;
    }

    public async ValueTask<OperationResult<YawGroundTruth>> GetAsync(
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            long durationTicks = await ReadSessionDurationAsync(
                connection,
                sessionId,
                cancellationToken).ConfigureAwait(false);
            if (durationTicks <= 0)
            {
                // Missing row OR a NULL/zero duration: the replay clock span is
                // unknown, so the yaw timeline would be meaningless. Fail
                // closed instead of returning a degenerate ground truth.
                return OperationResult.Failure<YawGroundTruth>(
                    StorageErrors.NotFound("Battle session"));
            }

            List<YawSample> samples = await ReadYawSamplesAsync(
                connection,
                sessionId,
                cancellationToken).ConfigureAwait(false);

            return OperationResult.Success(new YawGroundTruth(
                TimeSpan.FromTicks(durationTicks),
                samples));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<YawGroundTruth>(
                StorageErrors.From(exception));
        }
    }

    private static async ValueTask<long> ReadSessionDurationAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT duration_ticks
            FROM battle_sessions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        return reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
    }

    private static async ValueTask<List<YawSample>> ReadYawSamplesAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        List<YawSample> samples = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT replay_time_ticks, entity_id, yaw
            FROM position_samples
            WHERE battle_session_id = $sessionId
              AND entity_id IS NOT NULL
              AND yaw IS NOT NULL
            ORDER BY replay_time_ticks, sequence;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            samples.Add(new YawSample(
                TimeSpan.FromTicks(reader.GetInt64(0)),
                reader.GetInt64(1),
                reader.GetDouble(2)));
        }

        return samples;
    }
}
