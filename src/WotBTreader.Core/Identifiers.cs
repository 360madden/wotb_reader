namespace WotBTreader.Core;

/// <summary>Uniquely identifies a source artifact (imported replay file).</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct SourceArtifactId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static SourceArtifactId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a decode run (one execution of a decoder).</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct DecodeRunId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static DecodeRunId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a raw telemetry record from a decode run.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct RawRecordId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static RawRecordId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a canonical (mapped) telemetry event.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct CanonicalEventId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static CanonicalEventId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a battle session within a decode run.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct BattleSessionId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static BattleSessionId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a battle participant.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct ParticipantId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static ParticipantId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a position sample.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct PositionSampleId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static PositionSampleId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a comparison run between two decode runs.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct ComparisonRunId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static ComparisonRunId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a single comparison item within a comparison run.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct ComparisonItemId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static ComparisonItemId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>Uniquely identifies a segment in the replay-clock synchronisation log.</summary>
/// <param name="Value">The wrapped GUID.</param>
public readonly record struct ReplayClockSegmentId(Guid Value)
{
    /// <summary>Creates a new V7-based identifier.</summary>
    public static ReplayClockSegmentId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// Immutable SHA-256 content hash that validates during construction.
/// Accepts only 64-character hex strings, normalised to lowercase.
/// </summary>
public sealed record ContentHash
{
    /// <summary>Expected length of a SHA-256 hex string.</summary>
    public const int Sha256HexLength = 64;

    /// <summary>
    /// Creates a content hash, validating that <paramref name="value"/>
    /// is a 64-character hex string (case-insensitive). Throws on invalid input.
    /// </summary>
    /// <param name="value">A 64-character SHA-256 hex string.</param>
    /// <exception cref="ArgumentException">Thrown when the value is not a valid SHA-256 hex string.</exception>
    public ContentHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != Sha256HexLength || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A content hash must be a 64-character SHA-256 hex string.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    /// <summary>The normalised lowercase hex string.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
