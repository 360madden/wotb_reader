namespace WotBTreader.ApiContracts;

/// <summary>
/// Request to scan the game process memory for a specific value and discover
/// its offset. POST /api/v1/game/discover.
/// </summary>
public sealed record OffsetDiscoveryRequest
{
    /// <summary>Which field we're trying to discover (e.g. "playerPositionX").</summary>
    public string FieldName { get; init; } = string.Empty;

    /// <summary>The C++ type: Float, Int32, or Double.</summary>
    public string FieldType { get; init; } = "Float";

    /// <summary>
    /// The expected value to search for. For floats/int32s, use the raw bytes
    /// as a hex string (e.g. "0000803F" for 1.0f little-endian).
    /// </summary>
    public string ExpectedValueHex { get; init; } = string.Empty;

    /// <summary>
    /// Optional per-byte tolerance mask as hex. Zero bytes require exact
    /// match; non-zero bytes are wildcards (any value matches).
    /// </summary>
    public string? ToleranceMaskHex { get; init; }

    /// <summary>Maximum number of candidates to return (1–10000, default 500).</summary>
    public int MaxCandidates { get; init; } = 500;

    /// <summary>Minimum region size in bytes to scan (default 4096).</summary>
    public long MinRegionSize { get; init; } = 4096;
}

/// <summary>One candidate address found by the offset discovery scanner.</summary>
public sealed record OffsetDiscoveryCandidate
{
    /// <summary>Absolute virtual address in the process (hex).</summary>
    public string AbsoluteAddress { get; init; } = "0x0";

    /// <summary>Module-relative offset from the main module base (hex).</summary>
    public string RelativeOffset { get; init; } = "0x0";

    /// <summary>Module-relative offset as a decimal integer.</summary>
    public long RelativeOffsetDecimal { get; init; }

    /// <summary>The raw value at the address as a hex string.</summary>
    public string ObservedValueHex { get; init; } = string.Empty;

    /// <summary>Human-readable value summary.</summary>
    public string ValueSummary { get; init; } = string.Empty;
}

/// <summary>Results of one memory scan pass for offset discovery.</summary>
public sealed record OffsetDiscoveryResponse
{
    /// <summary>UTC timestamp when the scan completed.</summary>
    public DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>Base address of the main module at scan time (hex).</summary>
    public string BaseAddress { get; init; } = "0x0";

    /// <summary>Count of memory regions scanned.</summary>
    public int RegionsScanned { get; init; }

    /// <summary>Approximate bytes scanned.</summary>
    public long BytesScanned { get; init; }

    /// <summary>Total matches before candidate cap, or 0 if all returned.</summary>
    public int TotalMatchesBeforeTruncation { get; init; }

    /// <summary>The top candidates, sorted by ascending address.</summary>
    public List<OffsetDiscoveryCandidate> Candidates { get; init; } = [];
}
