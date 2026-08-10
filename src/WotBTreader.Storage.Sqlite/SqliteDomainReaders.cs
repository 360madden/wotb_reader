using Microsoft.Data.Sqlite;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal static class SqliteDomainReaders
{
    public static DecodeRun ReadDecodeRun(SqliteDataReader reader, int start = 0) =>
        new(
            new DecodeRunId(Guid.Parse(reader.GetString(start))),
            new SourceArtifactId(Guid.Parse(reader.GetString(start + 1))),
            reader.GetString(start + 2),
            reader.GetString(start + 3),
            reader.GetString(start + 4),
            (DecodeRunStatus)reader.GetInt32(start + 5),
            (ReplayCapability)reader.GetInt32(start + 6),
            SqliteValueConversions.ReadUtc(reader, start + 7),
            SqliteValueConversions.ReadNullableUtc(reader, start + 8),
            SqliteValueConversions.ReadNullableString(reader, start + 9),
            SqliteValueConversions.ReadNullableString(reader, start + 10));

    public static BattleSession ReadBattleSession(SqliteDataReader reader, int start = 0) =>
        new(
            new BattleSessionId(Guid.Parse(reader.GetString(start))),
            new DecodeRunId(Guid.Parse(reader.GetString(start + 1))),
            reader.GetString(start + 2),
            SqliteValueConversions.ReadNullableString(reader, start + 3),
            SqliteValueConversions.ReadNullableString(reader, start + 4),
            SqliteValueConversions.ReadNullableString(reader, start + 5),
            SqliteValueConversions.ReadNullableUtc(reader, start + 6),
            reader.IsDBNull(start + 7)
                ? null
                : TimeSpan.FromTicks(reader.GetInt64(start + 7)),
            reader.IsDBNull(start + 8)
                ? null
                : new ParticipantId(Guid.Parse(reader.GetString(start + 8))),
            reader.GetString(start + 9));

    public static Participant ReadParticipant(SqliteDataReader reader) =>
        new(
            new ParticipantId(Guid.Parse(reader.GetString(0))),
            new BattleSessionId(Guid.Parse(reader.GetString(1))),
            SqliteValueConversions.ReadNullableInt64(reader, 2),
            SqliteValueConversions.ReadNullableInt64(reader, 3),
            SqliteValueConversions.ReadNullableInt32(reader, 4),
            SqliteValueConversions.ReadNullableString(reader, 5),
            SqliteValueConversions.ReadNullableString(reader, 6),
            SqliteValueConversions.ReadNullableInt32(reader, 7),
            SqliteValueConversions.ReadNullableString(reader, 8),
            SqliteValueConversions.ReadNullableString(reader, 9),
            (TankClass)reader.GetInt32(10),
            (BotStatus)reader.GetInt32(11),
            (EvidenceConfidence)reader.GetInt32(12),
            BattleStatsJson.Deserialize(SqliteValueConversions.ReadNullableString(reader, 18)),
            SqliteValueConversions.ReadEvidence(reader, 13));

    public static PositionSample ReadPosition(SqliteDataReader reader) =>
        new(
            new PositionSampleId(Guid.Parse(reader.GetString(0))),
            new BattleSessionId(Guid.Parse(reader.GetString(1))),
            reader.IsDBNull(2)
                ? null
                : new ParticipantId(Guid.Parse(reader.GetString(2))),
            SqliteValueConversions.ReadNullableInt64(reader, 3),
            reader.GetInt64(4),
            TimeSpan.FromTicks(reader.GetInt64(5)),
            reader.GetDouble(6),
            reader.GetDouble(7),
            reader.GetDouble(8),
            SqliteValueConversions.ReadNullableDouble(reader, 9),
            SqliteValueConversions.ReadNullableDouble(reader, 10),
            (CoordinateSpace)reader.GetInt32(11),
            reader.IsDBNull(12) ? null : (CoordinateSpace)reader.GetInt32(12),
            SqliteValueConversions.ReadEvidence(reader, 16),
            SqliteValueConversions.ReadNullableDouble(reader, 13),
            SqliteValueConversions.ReadNullableDouble(reader, 14),
            SqliteValueConversions.ReadNullableDouble(reader, 15));

    public static CanonicalEvent ReadCanonicalEvent(SqliteDataReader reader) =>
        new(
            new CanonicalEventId(Guid.Parse(reader.GetString(0))),
            new DecodeRunId(Guid.Parse(reader.GetString(1))),
            new BattleSessionId(Guid.Parse(reader.GetString(2))),
            reader.GetInt64(3),
            (CanonicalEventKind)reader.GetInt32(4),
            TimeSpan.FromTicks(reader.GetInt64(5)),
            reader.IsDBNull(6)
                ? null
                : new ParticipantId(Guid.Parse(reader.GetString(6))),
            SqliteValueConversions.ReadNullableInt64(reader, 7),
            reader.GetString(8),
            (EvidenceConfidence)reader.GetInt32(9),
            SqliteValueConversions.ReadEvidence(reader, 10));

    public static RawRecord ReadRawRecord(SqliteDataReader reader) =>
        new(
            new RawRecordId(Guid.Parse(reader.GetString(0))),
            new DecodeRunId(Guid.Parse(reader.GetString(1))),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : TimeSpan.FromTicks(reader.GetInt64(4)),
            SqliteValueConversions.ReadEvidence(reader, 5),
            SqliteValueConversions.ReadNullableString(reader, 10));
}
