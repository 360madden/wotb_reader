using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal sealed class SqliteComparisonRunRepository : IComparisonRunRepository
{
    private readonly SqliteStorageContext _context;

    public SqliteComparisonRunRepository(SqliteStorageContext context)
    {
        _context = context;
    }

    public async ValueTask<IReadOnlyList<ComparisonRun>> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);

        await using SqliteConnection connection =
            await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, left_source_artifact_id, right_source_artifact_id,
                   comparator_id, comparator_version, schema_version,
                   timestamp_window_ticks, created_at_utc
            FROM comparison_runs
            ORDER BY created_at_utc DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        List<ComparisonRun> runs = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(new ComparisonRun(
                new ComparisonRunId(Guid.Parse(reader.GetString(0))),
                new SourceArtifactId(Guid.Parse(reader.GetString(1))),
                new SourceArtifactId(Guid.Parse(reader.GetString(2))),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                TimeSpan.FromTicks(reader.GetInt64(6)),
                SqliteValueConversions.ReadUtc(reader, 7)));
        }

        return runs;
    }

    public async ValueTask<OperationResult<TelemetryComparison>> AddAsync(
        TelemetryComparison comparison,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.Items.Any(item => item.ComparisonRunId != comparison.Run.Id))
        {
            return OperationResult.Failure<TelemetryComparison>(
                StorageErrors.Invalid("A comparison item belongs to a different run."));
        }

        if (!TryCalculateSummary(comparison.Items, out ComparisonSummary calculatedSummary))
        {
            return OperationResult.Failure<TelemetryComparison>(
                StorageErrors.Invalid("A comparison item has an unknown classification."));
        }

        if (calculatedSummary != comparison.Summary)
        {
            return OperationResult.Failure<TelemetryComparison>(
                StorageErrors.Invalid("The comparison summary does not match its items."));
        }

        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                await InsertRunAsync(
                    connection,
                    transaction,
                    comparison.Run,
                    cancellationToken).ConfigureAwait(false);
                foreach (ComparisonItem item in comparison.Items)
                {
                    await InsertItemAsync(
                        connection,
                        transaction,
                        item,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult.Success(comparison with
                {
                    Run = comparison.Run with
                    {
                        CreatedAtUtc = comparison.Run.CreatedAtUtc.ToUniversalTime(),
                    },
                });
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
            return OperationResult.Failure<TelemetryComparison>(
                StorageErrors.Conflict("The comparison run already exists or references missing data."));
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<TelemetryComparison>(StorageErrors.From(exception));
        }
    }

    public async ValueTask<OperationResult<TelemetryComparison>> GetAsync(
        ComparisonRunId comparisonRunId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            ComparisonRun? run = await ReadRunAsync(
                connection,
                comparisonRunId,
                cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return OperationResult.Failure<TelemetryComparison>(
                    StorageErrors.NotFound("Comparison run"));
            }

            List<ComparisonItem> items = [];
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, comparison_run_id, sequence, classification, event_type,
                       left_replay_time_ticks, right_replay_time_ticks,
                       participant_identity, field, left_value, right_value, explanation
                FROM comparison_items
                WHERE comparison_run_id = $comparisonRunId
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue(
                "$comparisonRunId",
                SqliteValueConversions.Guid(comparisonRunId.Value));
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadItem(reader));
            }

            if (!TryCalculateSummary(items, out ComparisonSummary summary))
            {
                return OperationResult.Failure<TelemetryComparison>(StorageErrors.Internal());
            }

            return OperationResult.Success(new TelemetryComparison(run, summary, items));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<TelemetryComparison>(StorageErrors.From(exception));
        }
    }

    private static async ValueTask InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ComparisonRun run,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO comparison_runs(
                id, left_source_artifact_id, right_source_artifact_id,
                comparator_id, comparator_version, schema_version,
                timestamp_window_ticks, created_at_utc)
            VALUES(
                $id, $leftSourceArtifactId, $rightSourceArtifactId,
                $comparatorId, $comparatorVersion, $schemaVersion,
                $timestampWindowTicks, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(run.Id.Value));
        command.Parameters.AddWithValue(
            "$leftSourceArtifactId",
            SqliteValueConversions.Guid(run.LeftSourceArtifactId.Value));
        command.Parameters.AddWithValue(
            "$rightSourceArtifactId",
            SqliteValueConversions.Guid(run.RightSourceArtifactId.Value));
        command.Parameters.AddWithValue("$comparatorId", run.ComparatorId);
        command.Parameters.AddWithValue("$comparatorVersion", run.ComparatorVersion);
        command.Parameters.AddWithValue("$schemaVersion", run.SchemaVersion);
        command.Parameters.AddWithValue("$timestampWindowTicks", run.TimestampWindow.Ticks);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            SqliteValueConversions.Utc(run.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ComparisonItem item,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO comparison_items(
                id, comparison_run_id, sequence, classification, event_type,
                left_replay_time_ticks, right_replay_time_ticks,
                participant_identity, field, left_value, right_value, explanation)
            VALUES(
                $id, $comparisonRunId, $sequence, $classification, $eventType,
                $leftReplayTimeTicks, $rightReplayTimeTicks,
                $participantIdentity, $field, $leftValue, $rightValue, $explanation);
            """;
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(item.Id.Value));
        command.Parameters.AddWithValue(
            "$comparisonRunId",
            SqliteValueConversions.Guid(item.ComparisonRunId.Value));
        command.Parameters.AddWithValue("$sequence", item.Sequence);
        command.Parameters.AddWithValue("$classification", (int)item.Classification);
        command.Parameters.AddWithValue("$eventType", item.EventType);
        command.Parameters.AddWithValue(
            "$leftReplayTimeTicks",
            item.LeftReplayTime is null ? DBNull.Value : item.LeftReplayTime.Value.Ticks);
        command.Parameters.AddWithValue(
            "$rightReplayTimeTicks",
            item.RightReplayTime is null ? DBNull.Value : item.RightReplayTime.Value.Ticks);
        SqliteValueConversions.AddNullable(
            command.Parameters,
            "$participantIdentity",
            item.ParticipantIdentity);
        SqliteValueConversions.AddNullable(command.Parameters, "$field", item.Field);
        SqliteValueConversions.AddNullable(command.Parameters, "$leftValue", item.LeftValue);
        SqliteValueConversions.AddNullable(command.Parameters, "$rightValue", item.RightValue);
        command.Parameters.AddWithValue("$explanation", item.Explanation);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ComparisonRun?> ReadRunAsync(
        SqliteConnection connection,
        ComparisonRunId comparisonRunId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, left_source_artifact_id, right_source_artifact_id,
                   comparator_id, comparator_version, schema_version,
                   timestamp_window_ticks, created_at_utc
            FROM comparison_runs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue(
            "$id",
            SqliteValueConversions.Guid(comparisonRunId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ComparisonRun(
            new ComparisonRunId(Guid.Parse(reader.GetString(0))),
            new SourceArtifactId(Guid.Parse(reader.GetString(1))),
            new SourceArtifactId(Guid.Parse(reader.GetString(2))),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            TimeSpan.FromTicks(reader.GetInt64(6)),
            SqliteValueConversions.ReadUtc(reader, 7));
    }

    private static ComparisonItem ReadItem(SqliteDataReader reader) =>
        new(
            new ComparisonItemId(Guid.Parse(reader.GetString(0))),
            new ComparisonRunId(Guid.Parse(reader.GetString(1))),
            reader.GetInt64(2),
            (ComparisonClassification)reader.GetInt32(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : TimeSpan.FromTicks(reader.GetInt64(5)),
            reader.IsDBNull(6) ? null : TimeSpan.FromTicks(reader.GetInt64(6)),
            SqliteValueConversions.ReadNullableString(reader, 7),
            SqliteValueConversions.ReadNullableString(reader, 8),
            SqliteValueConversions.ReadNullableString(reader, 9),
            SqliteValueConversions.ReadNullableString(reader, 10),
            reader.GetString(11));

    private static bool TryCalculateSummary(
        IEnumerable<ComparisonItem> items,
        out ComparisonSummary summary)
    {
        int exact = 0;
        int tolerant = 0;
        int mismatch = 0;
        int missing = 0;
        int extra = 0;
        int uncomparable = 0;
        foreach (ComparisonItem item in items)
        {
            switch (item.Classification)
            {
                case ComparisonClassification.Exact:
                    exact++;
                    break;
                case ComparisonClassification.Tolerant:
                    tolerant++;
                    break;
                case ComparisonClassification.Mismatch:
                    mismatch++;
                    break;
                case ComparisonClassification.Missing:
                    missing++;
                    break;
                case ComparisonClassification.Extra:
                    extra++;
                    break;
                case ComparisonClassification.Uncomparable:
                    uncomparable++;
                    break;
                default:
                    summary = default!;
                    return false;
            }
        }

        summary = new ComparisonSummary(exact, tolerant, mismatch, missing, extra, uncomparable);
        return true;
    }
}
