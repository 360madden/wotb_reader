using System.Globalization;

namespace WotBTreader.Core.Discovery;

/// <summary>
/// One member of a candidate coordinate family: a scored address that sits
/// inside the family's byte window, with its offset relative to the family
/// base address. A member whose ambiguity band rides the time-shift sweep
/// edge is flagged (it is a bad-anchor symptom, exactly as in the driver's
/// survivor audit).
/// </summary>
public sealed record TrajectoryFamilyMember(
    string Address,
    int OffsetBytes,
    string Axis,
    int Sign,
    double ShiftSeconds,
    double ShiftMinSeconds,
    double ShiftMaxSeconds,
    double Score,
    bool EdgeAligned);

/// <summary>
/// A candidate coordinate family: scored addresses that sit inside one small
/// byte window around a common base and reproduce the SAME entity's axes.
/// When the game stores a position as consecutive float32s, the x/y/z
/// components of one entity live at base+0/+4/+8 (possibly sign-flipped or in
/// a different order), so a family whose three members reproduce the three
/// ground-truth axes at distinct offsets is strategy-v4 M2's "one session maps
/// all three coordinate components" result.
/// </summary>
public sealed record TrajectoryFamily(
    string BaseAddress,
    int SpanBytes,
    IReadOnlyList<string> AxesCovered,
    bool Complete,
    IReadOnlyList<TrajectoryFamilyMember> Members);

/// <summary>
/// Groups correlated results into candidate coordinate families (strategy v4,
/// M2 — family mapping). The driver re-reads the ±16-byte window around each
/// provisional survivor for the remaining battle rounds, so the final
/// correlate pass returns the survivor AND its sibling components; this
/// builder turns those results into the family map.
///
/// Grouping rules:
/// <list type="bullet">
/// <item>Members of one family are addresses within the span window (default
/// 16 bytes) of the LOWEST member (base-relative, not chain-relative: a
/// vector is a locality, not a chain) AND reproduce the same entity (same
/// <see cref="TrajectoryCorrelationResult.EntityId"/> and
/// <see cref="TrajectoryCorrelationResult.ParticipantId"/>; two nulls are
/// equal).</item>
/// <item>A singleton result is not a family — it is already fully represented
/// in the results list; families exist only where neighbors were observed.</item>
/// <item><see cref="TrajectoryFamily.Complete"/> is true only for the CLEAN
/// triple: exactly three members, one per axis x/y/z, none edge-aligned.
/// A multi-copy family (two members claiming the same axis) is still
/// reported — synchronized copies are a success signal — but it is not the
/// clean evidence artifact, so it is flagged incomplete.</item>
/// </list>
/// </summary>
public static class TrajectoryFamilyBuilder
{
    /// <summary>
    /// Default byte window: three consecutive float32s are 12 bytes; 16 leaves
    /// headroom for padding or a 4-byte interleaved field.
    /// </summary>
    public const int DefaultMaxSpanBytes = 16;

    private const int EdgeSlackSeconds = 2;

    /// <summary>
    /// Groups <paramref name="results"/> into families. Deterministic ordering:
    /// member count descending, completeness first, then numeric base address
    /// ascending.
    /// </summary>
    /// <param name="results">Correlated results (may contain nulls, malformed
    /// addresses, or singletons; all are tolerated).</param>
    /// <param name="maxTimeShiftSeconds">The shift sweep bound used when the
    /// results were scored; drives the edge-aligned audit (mirrors the driver:
    /// a band edge within 2s of the sweep boundary is edge-aligned).</param>
    /// <param name="maxSpanBytes">Family byte window (1–4096).</param>
    public static IReadOnlyList<TrajectoryFamily> Build(
        IReadOnlyList<TrajectoryCorrelationResult> results,
        int maxTimeShiftSeconds,
        int maxSpanBytes = DefaultMaxSpanBytes)
    {
        if (results is null)
        {
            return [];
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxTimeShiftSeconds);

        if (maxSpanBytes is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSpanBytes));
        }

        List<(TrajectoryCorrelationResult Result, long Address)> parsed = [];
        foreach (TrajectoryCorrelationResult result in results)
        {
            if (result is null || string.IsNullOrWhiteSpace(result.Address))
            {
                continue;
            }

            string hex = result.Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? result.Address[2..]
                : result.Address;
            if (!long.TryParse(
                    hex,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out long address)
                || address <= 0)
            {
                // The scorer emits wire-valid addresses, but a corrupted
                // pipeline or hostile caller can still reach here; skip rather
                // than fail the whole report.
                continue;
            }

            parsed.Add((result, address));
        }

        if (parsed.Count == 0)
        {
            return [];
        }

        // Group by ENTITY first: a family's members must reproduce the SAME
        // entity's axes (a neighbor reproducing a different entity's y is not
        // a component of this survivor's vector). Grouping per entity also
        // survives interleaved foreign addresses (e.g. entity 1 at 0x1000 and
        // 0x1008 with entity 2 at 0x1004 between them) instead of splitting
        // the legitimate same-entity pair into singletons.
        Dictionary<(long? EntityId, Guid? ParticipantId), List<(TrajectoryCorrelationResult Result, long Address)>>
            byEntity = new();
        foreach ((TrajectoryCorrelationResult Result, long Address) item in parsed)
        {
            (long? EntityId, Guid? ParticipantId) key =
                (item.Result.EntityId, item.Result.ParticipantId?.Value);
            if (!byEntity.TryGetValue(
                    key,
                    out List<(TrajectoryCorrelationResult Result, long Address)>? entityList))
            {
                entityList = [];
                byEntity[key] = entityList;
            }

            entityList.Add(item);
        }

        List<(long BaseValue, TrajectoryFamily Family)> built = [];
        foreach (List<(TrajectoryCorrelationResult Result, long Address)> entityAddresses in byEntity.Values)
        {
            // Base-relative span grouping within one entity. Addresses are
            // sorted ascending, so an address either extends the LAST group
            // (within span of its base) or starts a new one; it can never fit
            // an earlier group because every earlier base is <= the last
            // group's base.
            List<List<(TrajectoryCorrelationResult Result, long Address)>> groups = [];
            foreach ((TrajectoryCorrelationResult Result, long Address) item in entityAddresses
                         .OrderBy(static p => p.Address))
            {
                if (groups.Count > 0)
                {
                    List<(TrajectoryCorrelationResult Result, long Address)> last = groups[^1];
                    if (item.Address - last[0].Address <= maxSpanBytes)
                    {
                        last.Add(item);
                        continue;
                    }
                }

                groups.Add([item]);
            }

            foreach (List<(TrajectoryCorrelationResult Result, long Address)> group in groups)
            {
                if (group.Count < 2)
                {
                    continue;
                }

                long baseAddress = group[0].Address;
                List<TrajectoryFamilyMember> members = [.. group
                    .Select(item => new TrajectoryFamilyMember(
                        item.Result.Address,
                        (int)(item.Address - baseAddress),
                        item.Result.Axis,
                        item.Result.Sign,
                        item.Result.ShiftSeconds,
                        item.Result.ShiftMinSeconds,
                        item.Result.ShiftMaxSeconds,
                        item.Result.Score,
                        IsEdgeAligned(item.Result, maxTimeShiftSeconds)))
                    .OrderBy(static m => m.OffsetBytes)];

                List<string> axesCovered = [.. members
                    .Select(static m => m.Axis)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static axis => AxisOrder(axis))];

                bool complete = members.Count == 3
                    && axesCovered.Count == 3
                    && members.All(static m => !m.EdgeAligned);

                built.Add((
                    baseAddress,
                    new TrajectoryFamily(
                        $"0x{baseAddress:X}",
                        members[^1].OffsetBytes,
                        axesCovered,
                        complete,
                        members)));
            }
        }

        return [.. built
            .OrderByDescending(static b => b.Family.Members.Count)
            .ThenByDescending(static b => b.Family.Complete)
            .ThenBy(static b => b.BaseValue)
            .Select(static b => b.Family)];
    }

    private static bool IsEdgeAligned(
        TrajectoryCorrelationResult result,
        int maxTimeShiftSeconds)
    {
        // Mirrors the driver: a band edge within 2s of the sweep boundary is
        // edge-aligned (the closest-to-zero reported shift can mask this).
        int threshold = Math.Max(EdgeSlackSeconds, maxTimeShiftSeconds - EdgeSlackSeconds);
        return Math.Abs(result.ShiftMinSeconds) >= threshold
            || Math.Abs(result.ShiftMaxSeconds) >= threshold;
    }

    private static int AxisOrder(string axis) => axis switch
    {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        _ => 3,
    };
}
