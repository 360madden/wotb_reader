using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Storage.Sqlite;

/// <summary>
/// Reads per-entity position time-series from the decoded session for the
/// replay-guided correlation campaign. Downsampling bounds memory and wire
/// size (256 samples per entity is ample for the scorer's interpolated
/// lookup: the decoded stream is ~10 Hz, so each retained sample represents a
/// ~100 ms step).
/// </summary>
internal sealed class SqliteTrajectoryGroundTruthProvider : ITrajectoryGroundTruthProvider
{
    private const int MaximumSamplesPerEntity = 256;

    private readonly SqliteStorageContext _context;

    public SqliteTrajectoryGroundTruthProvider(SqliteStorageContext context)
    {
        _context = context;
    }

    public async ValueTask<OperationResult<TrajectoryGroundTruth>> GetAsync(
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            (long durationTicks, ParticipantId? viewpoint) = await ReadSessionClockAsync(
                connection,
                sessionId,
                cancellationToken).ConfigureAwait(false);
            if (durationTicks <= 0)
            {
                // Missing row OR a NULL/zero duration: either way the replay
                // clock span is unknown, so the ground-truth window would be
                // meaningless. Fail closed instead of returning a degenerate
                // trajectory (evidence-first: unknown stays unknown).
                return OperationResult.Failure<TrajectoryGroundTruth>(
                    StorageErrors.NotFound("Battle session"));
            }

            Dictionary<Guid, (long? EntityId, string? TankName)> participants =
                await ReadParticipantsAsync(
                    connection,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);

            Dictionary<Guid, List<TrajectorySample>> samplesByParticipant = await ReadPositionsAsync(
                connection,
                sessionId,
                cancellationToken).ConfigureAwait(false);

            List<EntityTrajectory> entities = [];
            foreach ((Guid participantId, List<TrajectorySample> samples) in samplesByParticipant)
            {
                if (!participants.TryGetValue(participantId, out (long? EntityId, string? TankName) meta)
                    || samples.Count < 2)
                {
                    continue;
                }

                List<TrajectorySample> downsampled = Downsample(
                    samples,
                    MaximumSamplesPerEntity);
                entities.Add(new EntityTrajectory(
                    new ParticipantId(participantId),
                    meta.EntityId,
                    meta.TankName,
                    IsViewpoint: viewpoint?.Value == participantId,
                    downsampled));
            }

            return OperationResult.Success(new TrajectoryGroundTruth(
                durationTicks,
                entities));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<TrajectoryGroundTruth>(
                StorageErrors.From(exception));
        }
    }

    private static async ValueTask<(long DurationTicks, ParticipantId? Viewpoint)> ReadSessionClockAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT duration_ticks, viewpoint_participant_id
            FROM battle_sessions
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (-1, null);
        }

        long durationTicks = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        ParticipantId? viewpoint = reader.IsDBNull(1)
            ? null
            : new ParticipantId(Guid.Parse(reader.GetString(1)));
        return (durationTicks, viewpoint);
    }

    private static async ValueTask<Dictionary<Guid, (long? EntityId, string? TankName)>> ReadParticipantsAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, (long? EntityId, string? TankName)> values = new();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, entity_id, tank_name
            FROM participants
            WHERE battle_session_id = $sessionId;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid participantId = Guid.Parse(reader.GetString(0));
            long? entityId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            string? tankName = reader.IsDBNull(2) ? null : reader.GetString(2);
            values[participantId] = (entityId, tankName);
        }

        return values;
    }

    private static async ValueTask<Dictionary<Guid, List<TrajectorySample>>> ReadPositionsAsync(
        SqliteConnection connection,
        BattleSessionId sessionId,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, List<TrajectorySample>> values = new();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT participant_id, replay_time_ticks, raw_x, raw_y, raw_z
            FROM position_samples
            WHERE battle_session_id = $sessionId AND participant_id IS NOT NULL
            ORDER BY replay_time_ticks, sequence;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            SqliteValueConversions.Guid(sessionId.Value));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid participantId = Guid.Parse(reader.GetString(0));
            if (!values.TryGetValue(participantId, out List<TrajectorySample>? list))
            {
                list = [];
                values[participantId] = list;
            }

            list.Add(new TrajectorySample(
                reader.GetInt64(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4)));
        }

        return values;
    }

    /// <summary>
    /// Keeps the first sample of each evenly-spaced tick bucket, preserving
    /// the shape of the trajectory while bounding the series length.
    /// </summary>
    private static List<TrajectorySample> Downsample(
        List<TrajectorySample> samples,
        int maximumSamples)
    {
        if (samples.Count <= maximumSamples)
        {
            return samples;
        }

        long minTick = samples[0].ReplayTimeTicks;
        long maxTick = samples[^1].ReplayTimeTicks;
        double step = Math.Max(1, (double)(maxTick - minTick) / maximumSamples);
        List<TrajectorySample> kept = [];
        bool first = true;
        long lastKeptTick = 0;
        foreach (TrajectorySample sample in samples)
        {
            // Overflow hazard: `tick - long.MinValue` wraps NEGATIVE for every
            // non-negative tick (0 - long.MinValue == long.MinValue in unchecked
            // arithmetic), so a lastKeptTick seed of long.MinValue dropped the
            // whole series whenever downsampling actually ran (battles longer
            // than ~256 samples). Guard the first sample explicitly.
            if (first || sample.ReplayTimeTicks - lastKeptTick >= step)
            {
                kept.Add(sample);
                lastKeptTick = sample.ReplayTimeTicks;
                first = false;
            }
        }

        // Always keep the final sample so the trajectory window is exact.
        if (kept.Count > 0 && !ReferenceEquals(kept[^1], samples[^1]))
        {
            kept.Add(samples[^1]);
        }

        return kept;
    }
}
