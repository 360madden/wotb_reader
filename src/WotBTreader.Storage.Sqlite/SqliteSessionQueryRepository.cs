using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal sealed class SqliteSessionQueryRepository : ISessionQueryRepository
{
    private const int MaximumPageSize = 1_000;
    private readonly SqliteStorageContext _context;

    public SqliteSessionQueryRepository(SqliteStorageContext context)
    {
        _context = context;
    }

    public async ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumPageSize);

        await using SqliteConnection connection =
            await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                d.id, d.source_artifact_id, d.decoder_id, d.decoder_version,
                d.schema_version, d.status, d.capabilities, d.started_at_utc,
                d.completed_at_utc, d.failure_code, d.failure_summary,
                s.id, s.decode_run_id, s.game_version, s.arena_identity, s.map_id,
                s.map_name, s.battle_time_utc, s.duration_ticks,
                s.viewpoint_participant_id, s.schema_version,
                CASE WHEN s.id IS NULL THEN 0
                     ELSE (SELECT count(*) FROM participants p WHERE p.battle_session_id = s.id)
                END,
                CASE WHEN s.id IS NULL THEN 0
                     ELSE (SELECT count(*) FROM position_samples p WHERE p.battle_session_id = s.id)
                END,
                CASE WHEN s.id IS NULL THEN 0
                     ELSE (SELECT count(*) FROM canonical_events e WHERE e.battle_session_id = s.id)
                END,
                (SELECT count(*) FROM raw_records r WHERE r.decode_run_id = d.id)
            FROM decode_runs d
            LEFT JOIN battle_sessions s ON s.decode_run_id = d.id
            ORDER BY d.started_at_utc DESC, d.id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        List<DecodeRunSummary> summaries = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            DecodeRun run = SqliteDomainReaders.ReadDecodeRun(reader);
            BattleSession? session = reader.IsDBNull(11)
                ? null
                : SqliteDomainReaders.ReadBattleSession(reader, 11);
            summaries.Add(new DecodeRunSummary(
                run,
                session,
                checked((int)reader.GetInt64(21)),
                checked((int)reader.GetInt64(22)),
                checked((int)reader.GetInt64(23)),
                checked((int)reader.GetInt64(24))));
        }

        return summaries;
    }

    public async ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
        BattleSessionId battleSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            BattleSession? session = await ReadSessionAsync(
                connection,
                battleSessionId,
                cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                return OperationResult.Failure<ReplayDecodeProjection>(
                    StorageErrors.NotFound("Battle session"));
            }

            DecodeRun? run = await SqliteDecodeRunRepository.ReadDecodeRunAsync(
                connection,
                transaction: null,
                session.DecodeRunId,
                cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                return OperationResult.Failure<ReplayDecodeProjection>(
                    StorageErrors.NotFound("Decode run"));
            }

            IReadOnlyList<Participant> participants = await ReadParticipantsAsync(
                connection,
                battleSessionId,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PositionSample> positions = await ReadPositionsAsync(
                connection,
                battleSessionId,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CanonicalEvent> events = await ReadEventsAsync(
                connection,
                battleSessionId,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<RawRecord> rawRecords = await ReadRawRecordsAsync(
                connection,
                run.Id,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> warnings = await ReadWarningsAsync(
                connection,
                run.Id,
                cancellationToken).ConfigureAwait(false);

            return OperationResult.Success(new ReplayDecodeProjection(
                run,
                session,
                participants,
                positions,
                events,
                rawRecords,
                warnings));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<ReplayDecodeProjection>(
                StorageErrors.From(exception));
        }
    }

    private static async ValueTask<BattleSession?> ReadSessionAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, decode_run_id, game_version, arena_identity, map_id, map_name,
                   battle_time_utc, duration_ticks, viewpoint_participant_id, schema_version
            FROM battle_sessions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteDomainReaders.ReadBattleSession(reader)
            : null;
    }

    private static async ValueTask<IReadOnlyList<Participant>> ReadParticipantsAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, battle_session_id, account_id, entity_id, team_number,
                   player_name, clan_tag, vehicle_compact_descriptor, tank_id, tank_name,
                   tank_class, bot_status, bot_status_confidence,
                   evidence_source_artifact_id, evidence_archive_entry,
                   evidence_offset, evidence_length, evidence_sha256
            FROM participants
            WHERE battle_session_id = $sessionId
            ORDER BY team_number, entity_id, id;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            SqliteValueConversions.Guid(sessionId.Value));
        List<Participant> values = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(SqliteDomainReaders.ReadParticipant(reader));
        }

        return values;
    }

    private static async ValueTask<IReadOnlyList<PositionSample>> ReadPositionsAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, battle_session_id, participant_id, entity_id, sequence,
                   replay_time_ticks, raw_x, raw_y, raw_z, normalized_x, normalized_y,
                   raw_coordinate_space, normalized_coordinate_space,
                   evidence_source_artifact_id, evidence_archive_entry,
                   evidence_offset, evidence_length, evidence_sha256
            FROM position_samples
            WHERE battle_session_id = $sessionId
            ORDER BY replay_time_ticks, sequence;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            SqliteValueConversions.Guid(sessionId.Value));
        List<PositionSample> values = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(SqliteDomainReaders.ReadPosition(reader));
        }

        return values;
    }

    private static async ValueTask<IReadOnlyList<CanonicalEvent>> ReadEventsAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, decode_run_id, battle_session_id, sequence, kind,
                   replay_time_ticks, participant_id, entity_id, values_json, confidence,
                   evidence_source_artifact_id, evidence_archive_entry,
                   evidence_offset, evidence_length, evidence_sha256
            FROM canonical_events
            WHERE battle_session_id = $sessionId
            ORDER BY replay_time_ticks, sequence;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            SqliteValueConversions.Guid(sessionId.Value));
        List<CanonicalEvent> values = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(SqliteDomainReaders.ReadCanonicalEvent(reader));
        }

        return values;
    }

    private static async ValueTask<IReadOnlyList<RawRecord>> ReadRawRecordsAsync(
        SqliteConnection connection,
        DecodeRunId decodeRunId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, decode_run_id, ordinal, record_kind, replay_time_ticks,
                   evidence_source_artifact_id, evidence_archive_entry,
                   evidence_offset, evidence_length, evidence_sha256, properties_json
            FROM raw_records
            WHERE decode_run_id = $decodeRunId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue(
            "$decodeRunId",
            SqliteValueConversions.Guid(decodeRunId.Value));
        List<RawRecord> values = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(SqliteDomainReaders.ReadRawRecord(reader));
        }

        return values;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadWarningsAsync(
        SqliteConnection connection,
        DecodeRunId decodeRunId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT warning
            FROM decode_warnings
            WHERE decode_run_id = $decodeRunId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue(
            "$decodeRunId",
            SqliteValueConversions.Guid(decodeRunId.Value));
        List<string> values = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
