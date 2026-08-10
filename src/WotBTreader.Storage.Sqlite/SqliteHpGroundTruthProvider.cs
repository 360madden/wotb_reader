using System.Text.Json;
using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Storage.Sqlite;

/// <summary>
/// Reads the decoded HP-affecting event timeline (canonical Damage/Destroyed
/// events) for the record-diffing discovery playbook. The canonical-event
/// columns (replay time, victim entity id, participant) are authoritative;
/// damage/attacker values are best-effort extractions from
/// <c>canonical_events.values_json</c> and stay null when unparseable.
/// </summary>
internal sealed class SqliteHpGroundTruthProvider : IHpGroundTruthProvider
{
    private readonly SqliteStorageContext _context;

    public SqliteHpGroundTruthProvider(SqliteStorageContext context)
    {
        _context = context;
    }

    public async ValueTask<OperationResult<HpGroundTruth>> GetAsync(
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
                // Missing row OR a NULL/zero duration: either way the replay
                // clock span is unknown, so the event timeline would be
                // meaningless. Fail closed instead of returning a degenerate
                // ground truth (evidence-first: unknown stays unknown).
                return OperationResult.Failure<HpGroundTruth>(
                    StorageErrors.NotFound("Battle session"));
            }

            List<HpDamageEvent> events = await ReadDamageEventsAsync(
                connection,
                sessionId,
                cancellationToken).ConfigureAwait(false);

            return OperationResult.Success(new HpGroundTruth(
                TimeSpan.FromTicks(durationTicks),
                events));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<HpGroundTruth>(
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

    private static async ValueTask<List<HpDamageEvent>> ReadDamageEventsAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        List<HpDamageEvent> events = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT participant_id, entity_id, kind, replay_time_ticks, values_json
            FROM canonical_events
            WHERE battle_session_id = $sessionId
              AND kind IN ({(int)CanonicalEventKind.Damage}, {(int)CanonicalEventKind.Destroyed})
            ORDER BY replay_time_ticks, sequence;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ParticipantId? participantId = reader.IsDBNull(0)
                ? null
                : new ParticipantId(Guid.Parse(reader.GetString(0)));
            long? entityId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            CanonicalEventKind kind = (CanonicalEventKind)reader.GetInt32(2);
            TimeSpan replayTime = TimeSpan.FromTicks(reader.GetInt64(3));
            string valuesJson = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

            (int? damage, long? attackerEntityId) = TryParseValues(valuesJson);
            events.Add(new HpDamageEvent(
                participantId,
                entityId,
                replayTime,
                kind,
                damage,
                attackerEntityId,
                valuesJson));
        }

        return events;
    }

    /// <summary>
    /// Best-effort extraction of <c>damage</c>/<c>attackerEntityId</c> from a
    /// Damage canonical event's values JSON. Any parse failure yields nulls —
    /// the event is still returned (its columns are authoritative); values are
    /// never guessed.
    /// </summary>
    private static (int? Damage, long? AttackerEntityId) TryParseValues(string valuesJson)
    {
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(valuesJson);
            JsonElement root = document.RootElement;
            int? damage = root.TryGetProperty("damage", out JsonElement damageElement)
                && damageElement.TryGetInt32(out int parsedDamage)
                ? parsedDamage
                : null;
            long? attacker = root.TryGetProperty("attackerEntityId", out JsonElement attackerElement)
                && attackerElement.TryGetInt64(out long parsedAttacker)
                ? parsedAttacker
                : null;
            return (damage, attacker);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
