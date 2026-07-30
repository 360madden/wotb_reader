using System.Text.Json;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Replay;

/// <summary>
/// Reads and validates game-version offset tables from the
/// <c>memory-offsets/</c> directory. Every offset table is keyed by exact
/// game version and executable SHA-256; mismatches return unsupported.
/// </summary>
public interface IOffsetTableReader
{
    /// <summary>
    /// Loads the offset table for the given game version and executable hash.
    /// Returns <c>null</c> when no file exists for this version/hash;
    /// returns a failed result when a file exists but the hash, schema, or
    /// required fields are invalid.
    /// </summary>
    OperationResult<OffsetTable?> Load(
        string gameVersion,
        string executableSha256,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation that reads offset JSON files from a directory,
/// validates the schema and hash, and exposes per-field validation state.
/// </summary>
internal sealed class OffsetTableReader : IOffsetTableReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _offsetsDirectory;

    public OffsetTableReader(string offsetsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offsetsDirectory);
        _offsetsDirectory = offsetsDirectory;
    }

    public OperationResult<OffsetTable?> Load(
        string gameVersion,
        string executableSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableSha256);

        string filePath = Path.Combine(_offsetsDirectory, $"{gameVersion}.json");
        if (!File.Exists(filePath))
        {
            return OperationResult.Success<OffsetTable?>(null);
        }

        OffsetFileJson? raw;
        try
        {
            string json = File.ReadAllText(filePath);
            raw = JsonSerializer.Deserialize<OffsetFileJson>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return OperationResult.Failure<OffsetTable?>(
                new ApplicationError(
                    "offset.read_failed",
                    $"Offset file for version {gameVersion} could not be read."));
        }

        if (raw is null)
        {
            return OperationResult.Failure<OffsetTable?>(
                new ApplicationError(
                    "offset.empty_file",
                    $"Offset file for version {gameVersion} is empty or invalid."));
        }

        // Schema version check.
        if (raw.SchemaVersion != 1)
        {
            return OperationResult.Failure<OffsetTable?>(
                new ApplicationError(
                    "offset.unsupported_schema",
                    $"Offset schema version {raw.SchemaVersion} is not supported."));
        }

        // Game version must match the file name.
        if (!string.Equals(raw.GameVersion, gameVersion, StringComparison.Ordinal))
        {
            return OperationResult.Failure<OffsetTable?>(
                new ApplicationError(
                    "offset.version_mismatch",
                    $"Offset file declares version {raw.GameVersion} but was loaded for {gameVersion}."));
        }

        // Executable hash must match when the file declares one.
        if (!string.IsNullOrWhiteSpace(raw.ExecutableSha256)
            && !string.Equals(raw.ExecutableSha256, executableSha256, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Failure<OffsetTable?>(
                new ApplicationError(
                    "offset.hash_mismatch",
                    "The offset table executable hash does not match the observed executable."));
        }

        // Build the domain model with per-field validation.
        List<OffsetField> fields = [];
        fields.Add(BuildField("replayTime", raw.Offsets?.ReplayTime ?? 0, OffsetFieldType.DoubleField));
        fields.Add(BuildField("playerHP", raw.Offsets?.PlayerHP ?? 0, OffsetFieldType.Int32Field));
        fields.Add(BuildField("playerPositionX", raw.Offsets?.PlayerPositionX ?? 0, OffsetFieldType.FloatField));
        fields.Add(BuildField("playerPositionY", raw.Offsets?.PlayerPositionY ?? 0, OffsetFieldType.FloatField));
        fields.Add(BuildField("playerPositionZ", raw.Offsets?.PlayerPositionZ ?? 0, OffsetFieldType.FloatField));
        fields.Add(BuildField("playerYaw", raw.Offsets?.PlayerYaw ?? 0, OffsetFieldType.FloatField));
        fields.Add(BuildField("cameraPitch", raw.Offsets?.CameraPitch ?? 0, OffsetFieldType.FloatField));
        fields.Add(BuildField("aliveTankCount", raw.Offsets?.AliveTankCount ?? 0, OffsetFieldType.Int32Field));

        OffsetConfidence confidence = raw.Confidence?.ToLowerInvariant() switch
        {
            "low" => OffsetConfidence.Low,
            "medium" => OffsetConfidence.Medium,
            "high" => OffsetConfidence.High,
            _ => OffsetConfidence.None,
        };

        DateTimeOffset? discoveredAt = null;
        if (raw.DiscoveredAtUtc is not null
            && DateTimeOffset.TryParse(raw.DiscoveredAtUtc, out DateTimeOffset parsed))
        {
            discoveredAt = parsed;
        }

        OffsetTable table = new(
            SchemaVersion: raw.SchemaVersion,
            GameVersion: raw.GameVersion ?? gameVersion,
            ExecutableSha256: raw.ExecutableSha256 ?? string.Empty,
            DiscoveredAtUtc: discoveredAt,
            Confidence: confidence,
            Notes: raw.Notes,
            Fields: fields);

        return OperationResult.Success<OffsetTable?>(table);
    }

    private static OffsetField BuildField(string name, long offset, OffsetFieldType fieldType)
    {
        OffsetFieldStatus status = offset == 0
            ? OffsetFieldStatus.Unknown
            : OffsetFieldStatus.Candidate;

        OffsetConfidence confidence = offset == 0
            ? OffsetConfidence.None
            : OffsetConfidence.Low;

        return new OffsetField(
            Name: name,
            FieldType: fieldType,
            Offset: offset,
            Status: status,
            Confidence: confidence,
            Evidence: []);
    }

    /// <summary>
    /// JSON shape matching <c>memory-offsets/schema.json</c>.
    /// Only the fields used by the reader are deserialized.
    /// </summary>
    private sealed class OffsetFileJson
    {
        public int SchemaVersion { get; set; }
        public string? GameVersion { get; set; }
        public string? ExecutableSha256 { get; set; }
        public string? DiscoveredAtUtc { get; set; }
        public string? Confidence { get; set; }
        public string? Notes { get; set; }
        public OffsetFieldsJson? Offsets { get; set; }
    }

    private sealed class OffsetFieldsJson
    {
        public long ReplayTime { get; set; }
        public long PlayerHP { get; set; }
        public long PlayerPositionX { get; set; }
        public long PlayerPositionY { get; set; }
        public long PlayerPositionZ { get; set; }
        public long PlayerYaw { get; set; }
        public long CameraPitch { get; set; }
        public long AliveTankCount { get; set; }
    }
}
