using System.Globalization;
using Microsoft.Data.Sqlite;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal static class SqliteValueConversions
{
    public static string Guid(Guid value) => value.ToString("D");

    public static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ReadUtc(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(
            reader.GetString(ordinal),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind).ToUniversalTime();

    public static DateTimeOffset? ReadNullableUtc(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadUtc(reader, ordinal);

    public static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? ReadNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static double? ReadNullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    public static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static void AddNullable<T>(SqliteParameterCollection parameters, string name, T? value)
        where T : struct =>
        parameters.AddWithValue(name, value is null ? DBNull.Value : value.Value);

    public static void AddNullable(
        SqliteParameterCollection parameters,
        string name,
        string? value) =>
        parameters.AddWithValue(name, value is null ? DBNull.Value : value);

    public static void AddEvidence(
        SqliteParameterCollection parameters,
        EvidenceReference evidence)
    {
        parameters.AddWithValue(
            "$evidenceSourceArtifactId",
            Guid(evidence.SourceArtifactId.Value));
        AddNullable(parameters, "$evidenceArchiveEntry", evidence.ArchiveEntry);
        parameters.AddWithValue("$evidenceOffset", evidence.Offset);
        parameters.AddWithValue("$evidenceLength", evidence.Length);
        parameters.AddWithValue("$evidenceSha256", evidence.Sha256.Value);
    }

    public static EvidenceReference ReadEvidence(SqliteDataReader reader, int startOrdinal) =>
        new(
            new SourceArtifactId(System.Guid.Parse(reader.GetString(startOrdinal))),
            ReadNullableString(reader, startOrdinal + 1),
            reader.GetInt64(startOrdinal + 2),
            reader.GetInt32(startOrdinal + 3),
            new ContentHash(reader.GetString(startOrdinal + 4)));
}
