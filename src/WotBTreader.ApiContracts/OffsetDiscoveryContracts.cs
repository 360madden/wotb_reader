using System.Text.Json.Serialization;

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

    /// <summary>
    /// Optional numeric tolerance for a Float scan. Unlike a byte mask, this
    /// compares decoded single-precision values and preserves exponent bits.
    /// Must be finite and non-negative.
    /// </summary>
    public float? FloatTolerance { get; init; }

    /// <summary>Maximum number of candidates to return (1–10000, default 500).</summary>
    public int MaxCandidates { get; init; } = 500;

    /// <summary>Minimum region size in bytes to scan (default 4096).</summary>
    public long MinRegionSize { get; init; } = 4096;

    /// <summary>Address alignment: 1, 2, 4, or 8.</summary>
    public int Alignment { get; init; } = 1;

    /// <summary>Whether image mappings are included in addition to private/mapped regions.</summary>
    public bool IncludeImageRegions { get; init; }

    /// <summary>Whether working-set page classification is requested.</summary>
    public bool IncludeWorkingSetClassification { get; init; }
}

/// <summary>One candidate address found by the offset discovery scanner.</summary>
public sealed record OffsetDiscoveryCandidate
{
    /// <summary>Absolute virtual address in the process (hex).</summary>
    public string AbsoluteAddress { get; init; } = "0x0";

    /// <summary>Arithmetic displacement from the supplied scan base (hex); this is not a module RVA without proven main-image ownership.</summary>
    public string BaseDisplacement { get; init; } = "0x0";

    /// <summary>Arithmetic displacement from the supplied scan base as a decimal integer.</summary>
    public long BaseDisplacementDecimal { get; init; }

    /// <summary>Compatibility alias for clients using the former field name.</summary>
    [Obsolete("Use BaseDisplacement; this alias is retained for wire compatibility.")]
    [JsonPropertyName("relativeOffset")]
    public string RelativeOffset => BaseDisplacement;

    /// <summary>Compatibility alias for clients using the former field name.</summary>
    [Obsolete("Use BaseDisplacementDecimal; this alias is retained for wire compatibility.")]
    [JsonPropertyName("relativeOffsetDecimal")]
    public long RelativeOffsetDecimal => BaseDisplacementDecimal;

    /// <summary>The raw value at the address as a hex string.</summary>
    public string ObservedValueHex { get; init; } = string.Empty;

    /// <summary>Human-readable value summary.</summary>
    public string ValueSummary { get; init; } = string.Empty;

    /// <summary>How the address should be interpreted by evidence tooling.</summary>
    public string AddressKind { get; init; } = "absolute";

    /// <summary>Whether working-set evidence indicates a private page with COW-compatible protection; this is not proof that a COW event occurred.</summary>
    public bool IsCopyOnWrite { get; init; }
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

    /// <summary>Target process architecture measured by the scanner.</summary>
    public string TargetArchitecture { get; init; } = "unknown";

    /// <summary>Main executable name captured from the authorized process identity; module membership is not inferred from this field.</summary>
    public string ModuleName { get; init; } = "unknown";

    /// <summary>Measured main module image size, when available; zero means unavailable.</summary>
    public long ModuleSize { get; init; }

    /// <summary>Alignment used by the scan.</summary>
    public int Alignment { get; init; } = 1;

    /// <summary>Whether candidate output was capped.</summary>
    public bool Truncated { get; init; }

    /// <summary>value, aob, or neighborhood.</summary>
    public string ScanKind { get; init; } = "value";
}

/// <summary>Request for a bounded, single-root module pointer-chain evidence probe.</summary>
public sealed record PointerChainDiscoveryRequest
{
    public long RootRelativeOffset { get; init; }
    public List<long> PointerOffsets { get; init; } = [];
    public int MaxDepth { get; init; } = 4;
}

/// <summary>One bounded pointer-chain evidence result.</summary>
public sealed record PointerChainDiscoveryCandidate
{
    public string RootAddress { get; init; } = "0x0";
    public string FinalAddress { get; init; } = "0x0";
    public List<string> TraversedAddresses { get; init; } = [];
    public string AddressKind { get; init; } = "pointer-chain";
}

/// <summary>Response from a bounded, single-root pointer-chain evidence probe.</summary>
public sealed record PointerChainDiscoveryResponse
{
    public DateTimeOffset CompletedAtUtc { get; init; }
    public List<PointerChainDiscoveryCandidate> Candidates { get; init; } = [];
    public int RejectedChains { get; init; }
}
