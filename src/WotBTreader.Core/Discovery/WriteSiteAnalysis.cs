using System.Globalization;

namespace WotBTreader.Core.Discovery;

/// <summary>One loaded module snapshot from a write-trace capture (basename only).</summary>
public sealed record WriteSiteModule(
    string Name,
    ulong BaseAddress,
    uint Size);

/// <summary>One captured write used as input to offline write-site analysis.</summary>
public sealed record WriteSiteHitInput(
    ulong Address,
    ulong Rip,
    string? Rva,
    string? InstructionHex,
    IReadOnlyDictionary<string, uint>? Registers);

/// <summary>Resolved module ownership for one absolute RIP.</summary>
public sealed record WriteSiteModuleResolve(
    string? ModuleName,
    ulong? ModuleBase,
    uint? ModuleRva,
    string RvaLabel);

/// <summary>One (register, displacement) object-base candidate.</summary>
public sealed record WriteSiteObjectBaseCandidate(
    string Register,
    ulong Base,
    int Displacement,
    int SupportHits);

/// <summary>One sibling float slot to re-read in a later live offline-replay window.</summary>
public sealed record WriteSiteSiblingPlanEntry(
    string Address,
    int OffsetFromBase,
    string HypothesizedAxis);

/// <summary>Aggregated evidence for one unique write-site RIP.</summary>
public sealed record WriteSiteSummary(
    string Rip,
    string RvaLabel,
    string? ModuleName,
    ulong? ModuleBase,
    uint? ModuleRva,
    string? InstructionHex,
    string InstructionHint,
    int HitCount,
    IReadOnlyList<string> MemberAddresses,
    IReadOnlyDictionary<string, uint>? RegistersSample,
    IReadOnlyList<WriteSiteObjectBaseCandidate> ObjectBaseCandidates);

/// <summary>Evidence-first resolver scaffold (never promotes offsets by itself).</summary>
public sealed record WriteSiteResolverHints(
    string AddressKind,
    string Confidence,
    string Rationale,
    IReadOnlyList<WriteSiteSiblingPlanEntry> SiblingReadPlan);

/// <summary>Full offline analysis of a write-trace capture.</summary>
public sealed record WriteSiteAnalysisResult(
    IReadOnlyList<WriteSiteSummary> WriteSites,
    WriteSiteResolverHints ResolverHints);

/// <summary>
/// Pure offline analysis of guard-page write-trace captures (strategy-v4 M2 tail).
/// Resolves absolute RIPs against a module list, aggregates write sites, infers
/// probable object bases from register arithmetic, builds a sibling-read plan,
/// and emits an evidence-first resolver classification. No IO and no Win32.
/// Unknown stays unknown — empty candidates and low confidence are valid.
/// </summary>
public static class WriteSiteAnalysis
{
    /// <summary>Default maximum absolute displacement (bytes) for object-base candidates.</summary>
    public const int DefaultMaxDisplacement = 0x200;

    /// <summary>Common float-pack displacements always admitted when exact.</summary>
    private static readonly int[] PreferredDisplacements = [0, 4, 8, 12, 16];

    private static readonly string[] IntegerRegisters =
        ["eax", "ebx", "ecx", "edx", "esi", "edi", "ebp"];

    /// <summary>
    /// Resolves an absolute RIP into a module-relative label. When no module
    /// contains the RIP, returns <c>jit</c> with null module fields (fail-closed;
    /// never invents a default image base).
    /// </summary>
    public static WriteSiteModuleResolve ResolveRip(
        ulong rip,
        IReadOnlyList<WriteSiteModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        foreach (WriteSiteModule module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.Name) || module.Size == 0)
            {
                continue;
            }

            ulong end = module.BaseAddress + module.Size;
            if (rip >= module.BaseAddress && rip < end)
            {
                uint rva = (uint)(rip - module.BaseAddress);
                return new WriteSiteModuleResolve(
                    module.Name,
                    module.BaseAddress,
                    rva,
                    $"{module.Name}+0x{rva:X}");
            }
        }

        return new WriteSiteModuleResolve(null, null, null, "jit");
    }

    /// <summary>
    /// Classifies a short x86 instruction prefix as a known store hint, or
    /// <c>unknown</c>. Evidence-only — never used alone to promote a field.
    /// </summary>
    public static string ClassifyInstructionHint(string? instructionHex)
    {
        if (string.IsNullOrWhiteSpace(instructionHex))
        {
            return "unknown";
        }

        string hex = instructionHex.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        if (hex.Length < 2 || hex.Length % 2 != 0)
        {
            return "unknown";
        }

        // Common SSE scalar float stores used for world/transform updates.
        // F3 0F 11 /r  movss m32, xmm
        // F3 0F 10 /r  movss xmm, m32 (load — not a write site, but reported honestly)
        if (hex.StartsWith("F30F11", StringComparison.Ordinal))
        {
            return "movss [mem],xmm";
        }

        if (hex.StartsWith("F30F10", StringComparison.Ordinal))
        {
            return "movss xmm,[mem]";
        }

        // D9 1x / D9 5x  fstp m32 (x87)
        if (hex.StartsWith("D91", StringComparison.Ordinal) || hex.StartsWith("D95", StringComparison.Ordinal))
        {
            return "fstp m32";
        }

        // 89 /r  mov r/m32, r32  or  8B /r mov r32, r/m32
        if (hex.StartsWith("89", StringComparison.Ordinal))
        {
            return "mov r/m32,r32";
        }

        return "unknown";
    }

    /// <summary>
    /// Aggregates hits into unique write sites, ranks object-base candidates,
    /// builds a sibling-read plan from the best base (if any), and classifies
    /// the resolver kind. Fail-closed: no forced winner when evidence is weak
    /// or ambiguous.
    /// </summary>
    public static WriteSiteAnalysisResult Analyze(
        IReadOnlyList<WriteSiteHitInput> hits,
        IReadOnlyList<WriteSiteModule> modules,
        int maxDisplacement = DefaultMaxDisplacement)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(modules);

        ArgumentOutOfRangeException.ThrowIfNegative(maxDisplacement);

        var byRip = new Dictionary<ulong, List<WriteSiteHitInput>>();
        foreach (WriteSiteHitInput hit in hits)
        {
            if (!byRip.TryGetValue(hit.Rip, out List<WriteSiteHitInput>? list))
            {
                list = [];
                byRip[hit.Rip] = list;
            }

            list.Add(hit);
        }

        var writeSites = new List<WriteSiteSummary>();
        var allBases = new List<(WriteSiteObjectBaseCandidate Candidate, ulong Rip)>();

        foreach ((ulong rip, List<WriteSiteHitInput> group) in byRip.OrderBy(static kv => kv.Key))
        {
            WriteSiteModuleResolve resolved = ResolveRip(rip, modules);
            // Prefer the capture-side rva string when present and non-empty.
            string rvaLabel = !string.IsNullOrWhiteSpace(group[0].Rva)
                ? group[0].Rva!
                : resolved.RvaLabel;

            string? instructionHex = group
                .Select(static h => h.InstructionHex)
                .FirstOrDefault(static h => !string.IsNullOrWhiteSpace(h));

            IReadOnlyDictionary<string, uint>? regsSample = group
                .Select(static h => h.Registers)
                .FirstOrDefault(static r => r is { Count: > 0 });

            var members = group
                .Select(static h => FormatHex(h.Address))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static a => a, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            IReadOnlyList<WriteSiteObjectBaseCandidate> bases =
                RankObjectBaseCandidates(group, maxDisplacement);
            foreach (WriteSiteObjectBaseCandidate candidate in bases)
            {
                allBases.Add((candidate, rip));
            }

            writeSites.Add(new WriteSiteSummary(
                FormatHex(rip),
                rvaLabel,
                resolved.ModuleName,
                resolved.ModuleBase,
                resolved.ModuleRva,
                instructionHex,
                ClassifyInstructionHint(instructionHex),
                group.Count,
                members,
                regsSample,
                bases));
        }

        WriteSiteResolverHints hints = BuildResolverHints(writeSites, allBases, maxDisplacement);
        return new WriteSiteAnalysisResult(writeSites, hints);
    }

    /// <summary>
    /// Builds sibling float slots around an object base for a later live
    /// <c>discover/read</c> pass. Axes stay <c>unknown</c> until values match
    /// trajectory evidence.
    /// </summary>
    public static IReadOnlyList<WriteSiteSiblingPlanEntry> BuildSiblingReadPlan(
        ulong objectBase,
        int armedDisplacement,
        int radiusBytes = 16)
    {
        if (radiusBytes < 0 || radiusBytes > 0x1000)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusBytes));
        }

        var plan = new List<WriteSiteSiblingPlanEntry>();
        for (int off = -radiusBytes; off <= radiusBytes; off += 4)
        {
            if (off == armedDisplacement)
            {
                continue;
            }

            long abs = (long)objectBase + off;
            if (abs < 0)
            {
                continue;
            }

            plan.Add(new WriteSiteSiblingPlanEntry(
                FormatHex((ulong)abs),
                off,
                "unknown"));
        }

        return plan;
    }

    private static WriteSiteObjectBaseCandidate[] RankObjectBaseCandidates(
        IReadOnlyList<WriteSiteHitInput> group,
        int maxDisplacement)
    {
        // Key: (reg, signedDisp) → (support, first base)
        var counts = new Dictionary<(string Reg, int Disp), (int Support, ulong Base)>();

        foreach (WriteSiteHitInput hit in group)
        {
            if (hit.Registers is null || hit.Registers.Count == 0)
            {
                continue;
            }

            foreach (string reg in IntegerRegisters)
            {
                if (!hit.Registers.TryGetValue(reg, out uint regValue))
                {
                    continue;
                }

                // Destination - register as signed 32-bit (x86 address arithmetic).
                int disp = unchecked((int)(hit.Address - regValue));
                if (!IsPlausibleDisplacement(disp, maxDisplacement))
                {
                    continue;
                }

                ulong baseAddr = unchecked(hit.Address - (uint)disp);
                var key = (reg, disp);
                if (counts.TryGetValue(key, out (int Support, ulong Base) existing))
                {
                    counts[key] = (existing.Support + 1, existing.Base);
                }
                else
                {
                    counts[key] = (1, baseAddr);
                }
            }
        }

        return counts
            .Select(static kv => new WriteSiteObjectBaseCandidate(
                kv.Key.Reg,
                kv.Value.Base,
                kv.Key.Disp,
                kv.Value.Support))
            .Where(static c => c.SupportHits >= 2)
            .OrderByDescending(static c => c.SupportHits)
            .ThenBy(static c => Math.Abs(c.Displacement))
            .ThenBy(static c => c.Register, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsPlausibleDisplacement(int disp, int maxDisplacement)
    {
        if (PreferredDisplacements.Contains(disp))
        {
            return true;
        }

        return Math.Abs(disp) <= maxDisplacement;
    }

    private static WriteSiteResolverHints BuildResolverHints(
        List<WriteSiteSummary> writeSites,
        IReadOnlyList<(WriteSiteObjectBaseCandidate Candidate, ulong Rip)> allBases,
        int maxDisplacement)
    {
        if (writeSites.Count == 0)
        {
            return new WriteSiteResolverHints(
                "unknown",
                "none",
                "no write hits in capture",
                []);
        }

        bool anyModule = writeSites.Any(static s =>
            s.ModuleName is not null &&
            !string.Equals(s.RvaLabel, "jit", StringComparison.OrdinalIgnoreCase));

        // Best object base: highest support across sites; require a clear winner.
        WriteSiteObjectBaseCandidate? best = null;
        int bestSupport = 0;
        int runnerSupport = 0;
        foreach ((WriteSiteObjectBaseCandidate candidate, _) in allBases)
        {
            if (candidate.SupportHits > bestSupport)
            {
                runnerSupport = bestSupport;
                bestSupport = candidate.SupportHits;
                best = candidate;
            }
            else if (candidate.SupportHits > runnerSupport)
            {
                runnerSupport = candidate.SupportHits;
            }
        }

        if (best is not null && bestSupport >= 2 && bestSupport > runnerSupport)
        {
            IReadOnlyList<WriteSiteSiblingPlanEntry> plan = BuildSiblingReadPlan(
                best.Base,
                best.Displacement);

            return new WriteSiteResolverHints(
                "member-displacement",
                bestSupport >= 4 ? "medium" : "low",
                $"consensus object base via {best.Register}+0x{best.Displacement:X} support={bestSupport}",
                plan);
        }

        if (best is not null && bestSupport >= 2 && bestSupport == runnerSupport)
        {
            return new WriteSiteResolverHints(
                "unknown",
                "low",
                "ambiguous object-base candidates with equal support",
                []);
        }

        if (anyModule)
        {
            return new WriteSiteResolverHints(
                "heap-dynamic",
                "low",
                "write sites resolved into modules but no consensus object base",
                []);
        }

        return new WriteSiteResolverHints(
            "heap-dynamic",
            "low",
            "absolute write sites only; module map missing or RIP outside modules",
            []);
    }

    private static string FormatHex(ulong value) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}");
}
