using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal sealed class SqliteDecodeRunRepository : IDecodeRunRepository
{
    private const string DecodeColumns =
        """
        id, source_artifact_id, decoder_id, decoder_version, schema_version,
        status, capabilities, started_at_utc, completed_at_utc,
        failure_code, failure_summary
        """;

    private readonly SqliteStorageContext _context;

    public SqliteDecodeRunRepository(SqliteStorageContext context)
    {
        _context = context;
    }

    public async ValueTask<OperationResult<DecodeRun>> StartAsync(
        DecodeRun decodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decodeRun);
        if (decodeRun.Status is not (DecodeRunStatus.Pending or DecodeRunStatus.Running) ||
            decodeRun.CompletedAtUtc is not null ||
            decodeRun.FailureCode is not null ||
            decodeRun.FailureSummary is not null)
        {
            return OperationResult.Failure<DecodeRun>(
                StorageErrors.Invalid("A new decode run must be pending or running."));
        }

        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO decode_runs(
                    id, source_artifact_id, decoder_id, decoder_version, schema_version,
                    status, capabilities, started_at_utc, completed_at_utc,
                    failure_code, failure_summary)
                VALUES(
                    $id, $sourceArtifactId, $decoderId, $decoderVersion, $schemaVersion,
                    $status, $capabilities, $startedAtUtc, NULL, NULL, NULL);
                """;
            command.Parameters.AddWithValue(
                "$id",
                SqliteValueConversions.Guid(decodeRun.Id.Value));
            command.Parameters.AddWithValue(
                "$sourceArtifactId",
                SqliteValueConversions.Guid(decodeRun.SourceArtifactId.Value));
            command.Parameters.AddWithValue("$decoderId", decodeRun.DecoderId);
            command.Parameters.AddWithValue("$decoderVersion", decodeRun.DecoderVersion);
            command.Parameters.AddWithValue("$schemaVersion", decodeRun.SchemaVersion);
            command.Parameters.AddWithValue("$status", (int)decodeRun.Status);
            command.Parameters.AddWithValue("$capabilities", (int)decodeRun.Capabilities);
            command.Parameters.AddWithValue(
                "$startedAtUtc",
                SqliteValueConversions.Utc(decodeRun.StartedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult.Success(
                decodeRun with { StartedAtUtc = decodeRun.StartedAtUtc.ToUniversalTime() });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return OperationResult.Failure<DecodeRun>(
                StorageErrors.Conflict("The decode run already exists or its source is unavailable."));
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<DecodeRun>(StorageErrors.From(exception));
        }
    }

    public async ValueTask<OperationResult<DecodeRunSummary>> CommitAsync(
        ReplayDecodeProjection projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ApplicationError? validationError = ValidateProjection(projection);
        if (validationError is not null)
        {
            return OperationResult.Failure<DecodeRunSummary>(validationError);
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
                DecodeRun? existing = await ReadDecodeRunAsync(
                    connection,
                    transaction,
                    projection.DecodeRun.Id,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    return OperationResult.Failure<DecodeRunSummary>(
                        StorageErrors.NotFound("Decode run"));
                }

                if (existing.Status is not (DecodeRunStatus.Pending or DecodeRunStatus.Running))
                {
                    return OperationResult.Failure<DecodeRunSummary>(
                        StorageErrors.Conflict("The decode run is already final."));
                }

                if (existing.SourceArtifactId != projection.DecodeRun.SourceArtifactId)
                {
                    return OperationResult.Failure<DecodeRunSummary>(
                        StorageErrors.Conflict("The decode run source cannot change."));
                }

                if (projection.Session is not null)
                {
                    await InsertSessionAsync(connection, transaction, projection.Session, cancellationToken)
                        .ConfigureAwait(false);
                    await InsertParticipantsAsync(
                        connection,
                        transaction,
                        projection.Participants,
                        cancellationToken).ConfigureAwait(false);
                    await InsertPositionsAsync(
                        connection,
                        transaction,
                        projection.Positions,
                        cancellationToken).ConfigureAwait(false);
                    await InsertEventsAsync(
                        connection,
                        transaction,
                        projection.Events,
                        cancellationToken).ConfigureAwait(false);
                }

                await InsertRawRecordsAsync(
                    connection,
                    transaction,
                    projection.RawRecords,
                    cancellationToken).ConfigureAwait(false);
                await InsertWarningsAsync(
                    connection,
                    transaction,
                    projection.DecodeRun.Id,
                    projection.Warnings,
                    cancellationToken).ConfigureAwait(false);
                await CompleteDecodeRunAsync(
                    connection,
                    transaction,
                    projection.DecodeRun,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult.Success(new DecodeRunSummary(
                    projection.DecodeRun with
                    {
                        StartedAtUtc = projection.DecodeRun.StartedAtUtc.ToUniversalTime(),
                        CompletedAtUtc = projection.DecodeRun.CompletedAtUtc?.ToUniversalTime(),
                    },
                    projection.Session,
                    projection.Participants.Count,
                    projection.Positions.Count,
                    projection.Events.Count,
                    projection.RawRecords.Count));
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
            return OperationResult.Failure<DecodeRunSummary>(
                StorageErrors.Conflict("Decode evidence violates an immutable storage constraint."));
        }
        catch (StorageConcurrencyException)
        {
            return OperationResult.Failure<DecodeRunSummary>(
                StorageErrors.Conflict("The decode run changed concurrently."));
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<DecodeRunSummary>(StorageErrors.From(exception));
        }
    }

    public async ValueTask<OperationResult<DecodeRun>> FailAsync(
        DecodeRunId decodeRunId,
        DecodeRunStatus finalStatus,
        string failureCode,
        string failureSummary,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        if (finalStatus is not (
                DecodeRunStatus.Failed or
                DecodeRunStatus.Unsupported or
                DecodeRunStatus.Cancelled) ||
            string.IsNullOrWhiteSpace(failureCode) ||
            string.IsNullOrWhiteSpace(failureSummary))
        {
            return OperationResult.Failure<DecodeRun>(
                StorageErrors.Invalid("A valid terminal failure status and details are required."));
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
                DecodeRun? existing = await ReadDecodeRunAsync(
                    connection,
                    transaction,
                    decodeRunId,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    return OperationResult.Failure<DecodeRun>(StorageErrors.NotFound("Decode run"));
                }

                if (existing.Status is not (DecodeRunStatus.Pending or DecodeRunStatus.Running))
                {
                    if (existing.Status == finalStatus &&
                        string.Equals(existing.FailureCode, failureCode, StringComparison.Ordinal) &&
                        string.Equals(existing.FailureSummary, failureSummary, StringComparison.Ordinal))
                    {
                        return OperationResult.Success(existing);
                    }

                    return OperationResult.Failure<DecodeRun>(
                        StorageErrors.Conflict("The decode run is already final."));
                }

                DateTimeOffset normalizedCompletion = completedAtUtc.ToUniversalTime();
                await using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE decode_runs
                    SET status = $status,
                        completed_at_utc = $completedAtUtc,
                        failure_code = $failureCode,
                        failure_summary = $failureSummary
                    WHERE id = $id
                      AND status IN ($pending, $running);
                    """;
                update.Parameters.AddWithValue("$status", (int)finalStatus);
                update.Parameters.AddWithValue(
                    "$completedAtUtc",
                    SqliteValueConversions.Utc(normalizedCompletion));
                update.Parameters.AddWithValue("$failureCode", failureCode);
                update.Parameters.AddWithValue("$failureSummary", failureSummary);
                update.Parameters.AddWithValue(
                    "$id",
                    SqliteValueConversions.Guid(decodeRunId.Value));
                update.Parameters.AddWithValue("$pending", (int)DecodeRunStatus.Pending);
                update.Parameters.AddWithValue("$running", (int)DecodeRunStatus.Running);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    return OperationResult.Failure<DecodeRun>(
                        StorageErrors.Conflict("The decode run changed concurrently."));
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OperationResult.Success(existing with
                {
                    Status = finalStatus,
                    CompletedAtUtc = normalizedCompletion,
                    FailureCode = failureCode,
                    FailureSummary = failureSummary,
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
        catch (SqliteException exception)
        {
            return OperationResult.Failure<DecodeRun>(StorageErrors.From(exception));
        }
    }

    public async ValueTask<OperationResult<DecodeRunSummary>> GetAsync(
        DecodeRunId decodeRunId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            DecodeRun? decodeRun = await ReadDecodeRunAsync(
                connection,
                transaction: null,
                decodeRunId,
                cancellationToken).ConfigureAwait(false);
            if (decodeRun is null)
            {
                return OperationResult.Failure<DecodeRunSummary>(
                    StorageErrors.NotFound("Decode run"));
            }

            BattleSession? session = await ReadSessionByRunAsync(
                connection,
                decodeRunId,
                cancellationToken).ConfigureAwait(false);
            (int participants, int positions, int events, int rawRecords) =
                await ReadCountsAsync(connection, decodeRunId, session?.Id, cancellationToken)
                    .ConfigureAwait(false);
            return OperationResult.Success(new DecodeRunSummary(
                decodeRun,
                session,
                participants,
                positions,
                events,
                rawRecords));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<DecodeRunSummary>(StorageErrors.From(exception));
        }
    }

    internal static async ValueTask<DecodeRun?> ReadDecodeRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DecodeRunId decodeRunId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {DecodeColumns} FROM decode_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(decodeRunId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteDomainReaders.ReadDecodeRun(reader)
            : null;
    }

    internal static async ValueTask<BattleSession?> ReadSessionByRunAsync(
        SqliteConnection connection,
        DecodeRunId decodeRunId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, decode_run_id, game_version, arena_identity, map_id, map_name,
                   battle_time_utc, duration_ticks, viewpoint_participant_id, schema_version
            FROM battle_sessions
            WHERE decode_run_id = $decodeRunId;
            """;
        command.Parameters.AddWithValue(
            "$decodeRunId",
            SqliteValueConversions.Guid(decodeRunId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteDomainReaders.ReadBattleSession(reader)
            : null;
    }

    private static ApplicationError? ValidateProjection(ReplayDecodeProjection projection)
    {
        DecodeRun run = projection.DecodeRun;
        if (run.Status != DecodeRunStatus.Succeeded ||
            run.CompletedAtUtc is null ||
            run.FailureCode is not null ||
            run.FailureSummary is not null)
        {
            return StorageErrors.Invalid("A committed decode projection must be successful and complete.");
        }

        if (projection.Session is null &&
            (projection.Participants.Count != 0 ||
             projection.Positions.Count != 0 ||
             projection.Events.Count != 0))
        {
            return StorageErrors.Invalid("Session-scoped evidence requires a battle session.");
        }

        if (projection.Session is not null && projection.Session.DecodeRunId != run.Id)
        {
            return StorageErrors.Invalid("The battle session belongs to a different decode run.");
        }

        SourceArtifactId sourceId = run.SourceArtifactId;
        BattleSessionId? sessionId = projection.Session?.Id;
        if (projection.Participants.Any(item =>
                item.BattleSessionId != sessionId ||
                !IsValidEvidence(item.Evidence, sourceId)) ||
            projection.Positions.Any(item =>
                item.BattleSessionId != sessionId ||
                !IsValidEvidence(item.Evidence, sourceId)) ||
            projection.Events.Any(item =>
                item.DecodeRunId != run.Id ||
                item.BattleSessionId != sessionId ||
                !IsValidEvidence(item.Evidence, sourceId)) ||
            projection.RawRecords.Any(item =>
                item.DecodeRunId != run.Id ||
                !IsValidEvidence(item.Evidence, sourceId)))
        {
            return StorageErrors.Invalid("Decode evidence has inconsistent ownership or byte ranges.");
        }

        return null;
    }

    private static bool IsValidEvidence(EvidenceReference evidence, SourceArtifactId sourceId) =>
        evidence.SourceArtifactId == sourceId &&
        evidence.Offset >= 0 &&
        evidence.Length >= 0;

    private static async ValueTask InsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BattleSession session,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO battle_sessions(
                id, decode_run_id, game_version, arena_identity, map_id, map_name,
                battle_time_utc, duration_ticks, viewpoint_participant_id, schema_version)
            VALUES(
                $id, $decodeRunId, $gameVersion, $arenaIdentity, $mapId, $mapName,
                $battleTimeUtc, $durationTicks, $viewpointParticipantId, $schemaVersion);
            """;
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(session.Id.Value));
        command.Parameters.AddWithValue(
            "$decodeRunId",
            SqliteValueConversions.Guid(session.DecodeRunId.Value));
        command.Parameters.AddWithValue("$gameVersion", session.GameVersion);
        SqliteValueConversions.AddNullable(command.Parameters, "$arenaIdentity", session.ArenaIdentity);
        SqliteValueConversions.AddNullable(command.Parameters, "$mapId", session.MapId);
        SqliteValueConversions.AddNullable(command.Parameters, "$mapName", session.MapName);
        command.Parameters.AddWithValue(
            "$battleTimeUtc",
            session.BattleTimeUtc is null
                ? DBNull.Value
                : SqliteValueConversions.Utc(session.BattleTimeUtc.Value));
        SqliteValueConversions.AddNullable(
            command.Parameters,
            "$durationTicks",
            session.Duration?.Ticks);
        command.Parameters.AddWithValue(
            "$viewpointParticipantId",
            session.ViewpointParticipantId is null
                ? DBNull.Value
                : SqliteValueConversions.Guid(session.ViewpointParticipantId.Value.Value));
        command.Parameters.AddWithValue("$schemaVersion", session.SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertParticipantsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Participant> participants,
        CancellationToken cancellationToken)
    {
        foreach (Participant participant in participants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO participants(
                    id, battle_session_id, account_id, entity_id, team_number,
                    player_name, clan_tag, vehicle_compact_descriptor, tank_id, tank_name,
                    tank_class, bot_status, bot_status_confidence, battle_stats_json,
                    evidence_source_artifact_id, evidence_archive_entry,
                    evidence_offset, evidence_length, evidence_sha256)
                VALUES(
                    $id, $battleSessionId, $accountId, $entityId, $teamNumber,
                    $playerName, $clanTag, $vehicleCompactDescriptor, $tankId, $tankName,
                    $tankClass, $botStatus, $botStatusConfidence, $battleStatsJson,
                    $evidenceSourceArtifactId, $evidenceArchiveEntry,
                    $evidenceOffset, $evidenceLength, $evidenceSha256);
                """;
            command.Parameters.AddWithValue(
                "$id",
                SqliteValueConversions.Guid(participant.Id.Value));
            command.Parameters.AddWithValue(
                "$battleSessionId",
                SqliteValueConversions.Guid(participant.BattleSessionId.Value));
            SqliteValueConversions.AddNullable(command.Parameters, "$accountId", participant.AccountId);
            SqliteValueConversions.AddNullable(command.Parameters, "$entityId", participant.EntityId);
            SqliteValueConversions.AddNullable(command.Parameters, "$teamNumber", participant.TeamNumber);
            SqliteValueConversions.AddNullable(command.Parameters, "$playerName", participant.PlayerName);
            SqliteValueConversions.AddNullable(command.Parameters, "$clanTag", participant.ClanTag);
            SqliteValueConversions.AddNullable(
                command.Parameters,
                "$vehicleCompactDescriptor",
                participant.VehicleCompactDescriptor);
            SqliteValueConversions.AddNullable(command.Parameters, "$tankId", participant.TankId);
            SqliteValueConversions.AddNullable(command.Parameters, "$tankName", participant.TankName);
            command.Parameters.AddWithValue("$tankClass", (int)participant.TankClass);
            command.Parameters.AddWithValue("$botStatus", (int)participant.BotStatus);
            command.Parameters.AddWithValue(
                "$botStatusConfidence",
                (int)participant.BotStatusConfidence);
            SqliteValueConversions.AddNullable(
                command.Parameters,
                "$battleStatsJson",
                BattleStatsJson.Serialize(participant.BattleStats));
            SqliteValueConversions.AddEvidence(command.Parameters, participant.Evidence);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertPositionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PositionSample> positions,
        CancellationToken cancellationToken)
    {
        foreach (PositionSample position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO position_samples(
                    id, battle_session_id, participant_id, entity_id, sequence,
                    replay_time_ticks, raw_x, raw_y, raw_z, normalized_x, normalized_y,
                    raw_coordinate_space, normalized_coordinate_space,
                    evidence_source_artifact_id, evidence_archive_entry,
                    evidence_offset, evidence_length, evidence_sha256)
                VALUES(
                    $id, $battleSessionId, $participantId, $entityId, $sequence,
                    $replayTimeTicks, $rawX, $rawY, $rawZ, $normalizedX, $normalizedY,
                    $rawCoordinateSpace, $normalizedCoordinateSpace,
                    $evidenceSourceArtifactId, $evidenceArchiveEntry,
                    $evidenceOffset, $evidenceLength, $evidenceSha256);
                """;
            command.Parameters.AddWithValue(
                "$id",
                SqliteValueConversions.Guid(position.Id.Value));
            command.Parameters.AddWithValue(
                "$battleSessionId",
                SqliteValueConversions.Guid(position.BattleSessionId.Value));
            command.Parameters.AddWithValue(
                "$participantId",
                position.ParticipantId is null
                    ? DBNull.Value
                    : SqliteValueConversions.Guid(position.ParticipantId.Value.Value));
            SqliteValueConversions.AddNullable(command.Parameters, "$entityId", position.EntityId);
            command.Parameters.AddWithValue("$sequence", position.Sequence);
            command.Parameters.AddWithValue("$replayTimeTicks", position.ReplayTime.Ticks);
            command.Parameters.AddWithValue("$rawX", position.RawX);
            command.Parameters.AddWithValue("$rawY", position.RawY);
            command.Parameters.AddWithValue("$rawZ", position.RawZ);
            SqliteValueConversions.AddNullable(command.Parameters, "$normalizedX", position.NormalizedX);
            SqliteValueConversions.AddNullable(command.Parameters, "$normalizedY", position.NormalizedY);
            command.Parameters.AddWithValue(
                "$rawCoordinateSpace",
                (int)position.RawCoordinateSpace);
            command.Parameters.AddWithValue(
                "$normalizedCoordinateSpace",
                position.NormalizedCoordinateSpace is null
                    ? DBNull.Value
                    : (int)position.NormalizedCoordinateSpace.Value);
            SqliteValueConversions.AddEvidence(command.Parameters, position.Evidence);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<CanonicalEvent> events,
        CancellationToken cancellationToken)
    {
        foreach (CanonicalEvent canonicalEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO canonical_events(
                    id, decode_run_id, battle_session_id, sequence, kind,
                    replay_time_ticks, participant_id, entity_id, values_json, confidence,
                    evidence_source_artifact_id, evidence_archive_entry,
                    evidence_offset, evidence_length, evidence_sha256)
                VALUES(
                    $id, $decodeRunId, $battleSessionId, $sequence, $kind,
                    $replayTimeTicks, $participantId, $entityId, $valuesJson, $confidence,
                    $evidenceSourceArtifactId, $evidenceArchiveEntry,
                    $evidenceOffset, $evidenceLength, $evidenceSha256);
                """;
            command.Parameters.AddWithValue(
                "$id",
                SqliteValueConversions.Guid(canonicalEvent.Id.Value));
            command.Parameters.AddWithValue(
                "$decodeRunId",
                SqliteValueConversions.Guid(canonicalEvent.DecodeRunId.Value));
            command.Parameters.AddWithValue(
                "$battleSessionId",
                SqliteValueConversions.Guid(canonicalEvent.BattleSessionId.Value));
            command.Parameters.AddWithValue("$sequence", canonicalEvent.Sequence);
            command.Parameters.AddWithValue("$kind", (int)canonicalEvent.Kind);
            command.Parameters.AddWithValue(
                "$replayTimeTicks",
                canonicalEvent.ReplayTime.Ticks);
            command.Parameters.AddWithValue(
                "$participantId",
                canonicalEvent.ParticipantId is null
                    ? DBNull.Value
                    : SqliteValueConversions.Guid(canonicalEvent.ParticipantId.Value.Value));
            SqliteValueConversions.AddNullable(command.Parameters, "$entityId", canonicalEvent.EntityId);
            command.Parameters.AddWithValue("$valuesJson", canonicalEvent.ValuesJson);
            command.Parameters.AddWithValue("$confidence", (int)canonicalEvent.Confidence);
            SqliteValueConversions.AddEvidence(command.Parameters, canonicalEvent.Evidence);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertRawRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RawRecord> rawRecords,
        CancellationToken cancellationToken)
    {
        foreach (RawRecord record in rawRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO raw_records(
                    id, decode_run_id, ordinal, record_kind, replay_time_ticks,
                    evidence_source_artifact_id, evidence_archive_entry,
                    evidence_offset, evidence_length, evidence_sha256, properties_json)
                VALUES(
                    $id, $decodeRunId, $ordinal, $recordKind, $replayTimeTicks,
                    $evidenceSourceArtifactId, $evidenceArchiveEntry,
                    $evidenceOffset, $evidenceLength, $evidenceSha256, $propertiesJson);
                """;
            command.Parameters.AddWithValue(
                "$id",
                SqliteValueConversions.Guid(record.Id.Value));
            command.Parameters.AddWithValue(
                "$decodeRunId",
                SqliteValueConversions.Guid(record.DecodeRunId.Value));
            command.Parameters.AddWithValue("$ordinal", record.Ordinal);
            command.Parameters.AddWithValue("$recordKind", record.RecordKind);
            command.Parameters.AddWithValue(
                "$replayTimeTicks",
                record.ReplayTime is null ? DBNull.Value : record.ReplayTime.Value.Ticks);
            SqliteValueConversions.AddEvidence(command.Parameters, record.Evidence);
            SqliteValueConversions.AddNullable(
                command.Parameters,
                "$propertiesJson",
                record.PropertiesJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask InsertWarningsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DecodeRunId decodeRunId,
        IReadOnlyList<string> warnings,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < warnings.Count; index++)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO decode_warnings(decode_run_id, ordinal, warning)
                VALUES($decodeRunId, $ordinal, $warning);
                """;
            command.Parameters.AddWithValue(
                "$decodeRunId",
                SqliteValueConversions.Guid(decodeRunId.Value));
            command.Parameters.AddWithValue("$ordinal", index);
            command.Parameters.AddWithValue("$warning", warnings[index]);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask CompleteDecodeRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DecodeRun decodeRun,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE decode_runs
            SET decoder_id = $decoderId,
                decoder_version = $decoderVersion,
                schema_version = $schemaVersion,
                status = $status,
                capabilities = $capabilities,
                completed_at_utc = $completedAtUtc,
                failure_code = NULL,
                failure_summary = NULL
            WHERE id = $id
              AND status IN ($pending, $running);
            """;
        command.Parameters.AddWithValue("$decoderId", decodeRun.DecoderId);
        command.Parameters.AddWithValue("$decoderVersion", decodeRun.DecoderVersion);
        command.Parameters.AddWithValue("$schemaVersion", decodeRun.SchemaVersion);
        command.Parameters.AddWithValue("$status", (int)decodeRun.Status);
        command.Parameters.AddWithValue("$capabilities", (int)decodeRun.Capabilities);
        command.Parameters.AddWithValue(
            "$completedAtUtc",
            SqliteValueConversions.Utc(decodeRun.CompletedAtUtc!.Value));
        command.Parameters.AddWithValue(
            "$id",
            SqliteValueConversions.Guid(decodeRun.Id.Value));
        command.Parameters.AddWithValue("$pending", (int)DecodeRunStatus.Pending);
        command.Parameters.AddWithValue("$running", (int)DecodeRunStatus.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new StorageConcurrencyException();
        }
    }

    private static async ValueTask<(int Participants, int Positions, int Events, int RawRecords)>
        ReadCountsAsync(
            SqliteConnection connection,
            DecodeRunId decodeRunId,
            BattleSessionId? sessionId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                CASE WHEN $sessionId IS NULL THEN 0
                     ELSE (SELECT count(*) FROM participants WHERE battle_session_id = $sessionId)
                END,
                CASE WHEN $sessionId IS NULL THEN 0
                     ELSE (SELECT count(*) FROM position_samples WHERE battle_session_id = $sessionId)
                END,
                CASE WHEN $sessionId IS NULL THEN 0
                     ELSE (SELECT count(*) FROM canonical_events WHERE battle_session_id = $sessionId)
                END,
                (SELECT count(*) FROM raw_records WHERE decode_run_id = $decodeRunId);
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            sessionId is null
                ? DBNull.Value
                : SqliteValueConversions.Guid(sessionId.Value.Value));
        command.Parameters.AddWithValue(
            "$decodeRunId",
            SqliteValueConversions.Guid(decodeRunId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0, 0, 0, 0);
        }

        return (
            checked((int)reader.GetInt64(0)),
            checked((int)reader.GetInt64(1)),
            checked((int)reader.GetInt64(2)),
            checked((int)reader.GetInt64(3)));
    }

    private sealed class StorageConcurrencyException : Exception;
}
