namespace WotBTreader.Core;

public readonly record struct SourceArtifactId(Guid Value)
{
    public static SourceArtifactId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct DecodeRunId(Guid Value)
{
    public static DecodeRunId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct RawRecordId(Guid Value)
{
    public static RawRecordId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct CanonicalEventId(Guid Value)
{
    public static CanonicalEventId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct BattleSessionId(Guid Value)
{
    public static BattleSessionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ParticipantId(Guid Value)
{
    public static ParticipantId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct PositionSampleId(Guid Value)
{
    public static PositionSampleId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ComparisonRunId(Guid Value)
{
    public static ComparisonRunId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ComparisonItemId(Guid Value)
{
    public static ComparisonItemId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ReplayClockSegmentId(Guid Value)
{
    public static ReplayClockSegmentId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}

public sealed record ContentHash
{
    public const int Sha256HexLength = 64;

    public ContentHash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != Sha256HexLength || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A content hash must be a 64-character SHA-256 hex string.", nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public override string ToString() => Value;
}
