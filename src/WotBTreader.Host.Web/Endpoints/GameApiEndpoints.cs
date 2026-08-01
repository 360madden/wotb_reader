using System.Text.RegularExpressions;
using WotBTreader.ApiContracts;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Host.Web.Endpoints;

/// <summary>
/// Game interaction API — query game/replay state, poll memory, and launch replays.
/// Endpoints are loopback-gated by the existing LoopbackOnlyMiddleware.
/// </summary>
internal static class GameApiEndpoints
{
    public static IEndpointRouteBuilder MapGameApi(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder group = builder.MapGroup("/api/v1/game");
        group.MapGet("/state", GetGameStateAsync);
        group.MapGet("/memory", GetGameMemoryAsync);
        group.MapPost("/launch", LaunchGameAsync);
        group.MapPost("/start", StartGameAsync);
        group.MapPost("/discover", DiscoverOffsetsAsync);
        group.MapPost("/discover/pattern", DiscoverPatternAsync);
        group.MapPost("/discover/pointer-chain", DiscoverPointerChainAsync);
        group.MapPost("/discover/snapshot", CreateSnapshotAsync);
        group.MapPost("/discover/compare/{sessionId}", CompareSnapshotAsync);
        group.MapDelete("/discover/session/{sessionId}", DiscardSessionAsync);
        group.MapPost("/discover/neighborhood", DiscoverNeighborhoodAsync);
        return builder;
    }

    internal static async Task<IResult> GetGameStateAsync(
        IGameSessionState gameSessionState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameSessionState);

        GameSessionSnapshot snapshot = await gameSessionState
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new GameStateResponse
        {
            GamePresent = snapshot.GamePresent,
            VerificationState = snapshot.State.ToString(),
            ObservedAtUtc = snapshot.ObservedAtUtc,
            EvidenceExpiresAtUtc = snapshot.EvidenceExpiresAtUtc,
            ReasonCode = snapshot.ReasonCode,
        });
    }

    internal static async Task<IResult> GetGameMemoryAsync(
        IGameMemoryObserver gameMemoryObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameMemoryObserver);

        GameMemoryObservation observation = await gameMemoryObserver
            .ObserveAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new GameMemoryResponse
        {
            CapturedAtUtc = observation.CapturedAtUtc,
            Availability = observation.Availability.ToString(),
            ReplayTimeSeconds = observation.ReplayTimeSeconds,
            PlayerHP = observation.PlayerHitPoints,
            PlayerPositionX = observation.PlayerPositionX,
            PlayerPositionY = observation.PlayerPositionY,
            PlayerPositionZ = observation.PlayerPositionZ,
            PlayerYaw = observation.PlayerYaw,
            CameraPitch = observation.CameraPitch,
            AliveTankCount = observation.AliveTankCount,
        });
    }

    internal static async Task<IResult> StartGameAsync(
        IGameProcessLauncher gameProcessLauncher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameProcessLauncher);

        OperationResult<GameProcessLaunchOutcome> result =
            await gameProcessLauncher.LaunchAsync(cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Results.Ok(new { pid = result.Value!.ProcessId, launchedAtUtc = result.Value.LaunchedAtUtc })
            : Results.BadRequest(new { error = result.Error?.Code ?? "start.failed" });
    }

    internal static async Task<IResult> LaunchGameAsync(
        IGameReplayLauncher gameReplayLauncher,
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameReplayLauncher);
        ArgumentNullException.ThrowIfNull(request);

        if (!Guid.TryParse(request.SourceArtifactId, out Guid sourceArtifactId) || sourceArtifactId == Guid.Empty)
        {
            return Results.BadRequest(new GameLaunchResponse
            {
                Success = false,
                Message = "launch.source_artifact.invalid",
            });
        }

        try
        {
            OperationResult<GameReplayLaunchOutcome> result = await gameReplayLauncher
                .LaunchAsync(new GameReplayLaunchRequest(new SourceArtifactId(sourceArtifactId)), cancellationToken)
                .ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(new GameLaunchResponse
                {
                    Success = true,
                    Message = "launch.accepted",
                })
                : Results.BadRequest(new GameLaunchResponse
                {
                    Success = false,
                    Message = ErrorCode(result.Error),
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Results.BadRequest(new GameLaunchResponse
            {
                Success = false,
                Message = $"launch.failed: {exception.GetType().Name}",
            });
        }
    }

    internal static async Task<IResult> DiscoverOffsetsAsync(
        IGameMemoryScanner scanner,
        OffsetDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.FieldName))
        {
            return Results.BadRequest(new { error = "discover.field_name_required" });
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedValueHex)
            || !IsHexString(request.ExpectedValueHex))
        {
            return Results.BadRequest(new { error = "discover.invalid_hex" });
        }

        byte[] expectedValue;
        try
        {
            expectedValue = Convert.FromHexString(request.ExpectedValueHex);
        }
        catch
        {
            return Results.BadRequest(new { error = "discover.invalid_hex" });
        }

        if (expectedValue.Length is < 1 or > 8)
        {
            return Results.BadRequest(new
            {
                error = "discover.invalid_value_length",
            });
        }

        byte[]? tolerance = null;
        if (!string.IsNullOrWhiteSpace(request.ToleranceMaskHex))
        {
            if (!IsHexString(request.ToleranceMaskHex))
            {
                return Results.BadRequest(new { error = "discover.invalid_tolerance_hex" });
            }

            tolerance = Convert.FromHexString(request.ToleranceMaskHex);
            if (tolerance.Length != expectedValue.Length)
            {
                return Results.BadRequest(new
                {
                    error = "discover.tolerance_length_mismatch",
                });
            }
        }

        string? fieldType = request.FieldType switch
        {
            "Float" => "Float",
            "Int32" => "Int32",
            "Double" => "Double",
            _ => null,
        };
        if (fieldType is null)
        {
            return Results.BadRequest(new { error = "discover.invalid_field_type" });
        }

        int expectedWidth = fieldType switch
        {
            "Float" or "Int32" => sizeof(float),
            "Double" => sizeof(double),
            _ => 0,
        };
        if (expectedValue.Length != expectedWidth)
        {
            return Results.BadRequest(new { error = "discover.invalid_value_width" });
        }

        if (request.FloatTolerance is float floatTolerance
            && (!float.IsFinite(floatTolerance) || floatTolerance < 0))
        {
            return Results.BadRequest(new { error = "discover.invalid_float_tolerance" });
        }

        if (request.FloatTolerance.HasValue && fieldType != "Float")
        {
            return Results.BadRequest(new { error = "discover.float_tolerance_type_mismatch" });
        }

        if (fieldType == "Float"
            && !float.IsFinite(BitConverter.ToSingle(expectedValue)))
        {
            return Results.BadRequest(
                new { error = "discover.invalid_float_value" });
        }

        if (request.FloatTolerance.HasValue && !string.IsNullOrWhiteSpace(request.ToleranceMaskHex))
        {
            return Results.BadRequest(new { error = "discover.tolerance_conflict" });
        }

        MemoryScanRequest scanRequest = new(
            FieldName: request.FieldName,
            FieldType: fieldType,
            ExpectedValue: expectedValue,
            ToleranceMask: tolerance,
            MaxCandidates: Math.Clamp(request.MaxCandidates, 1, 10_000),
            MinRegionSize: Math.Max(request.MinRegionSize, 4096),
            Alignment: request.Alignment is 1 or 2 or 4 or 8 ? request.Alignment : 1,
            RegionSelection: request.IncludeImageRegions
                ? MemoryRegionSelection.Default | MemoryRegionSelection.Image
                : MemoryRegionSelection.Default,
            IncludeWorkingSetClassification: request.IncludeWorkingSetClassification,
            FloatTolerance: request.FloatTolerance);

        OperationResult<MemoryScanResult> result =
            await scanner.ScanAsync(scanRequest, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return Results.BadRequest(new
            {
                error = result.Error?.Code ?? "discover.failed",
            });
        }

        MemoryScanResult scanResult = result.Value!;

        return Results.Ok(ToDiscoveryResponse(scanResult));
    }

    internal static async Task<IResult> DiscoverPatternAsync(
        IGameMemoryScanner scanner,
        OffsetDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FieldName))
        {
            return Results.BadRequest(new { error = "discover.field_name_required" });
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedValueHex)
            || !IsHexString(request.ExpectedValueHex))
        {
            return Results.BadRequest(new { error = "discover.invalid_hex" });
        }

        if (request.FloatTolerance.HasValue)
        {
            return Results.BadRequest(new { error = "discover.float_tolerance_not_supported_for_pattern" });
        }

        if (!string.IsNullOrWhiteSpace(request.ToleranceMaskHex)
            && !IsHexString(request.ToleranceMaskHex))
        {
            return Results.BadRequest(new { error = "discover.invalid_tolerance_hex" });
        }

        byte[] expected;
        byte[]? tolerance;
        try
        {
            expected = Convert.FromHexString(request.ExpectedValueHex);
            tolerance = string.IsNullOrWhiteSpace(request.ToleranceMaskHex)
                ? null
                : Convert.FromHexString(request.ToleranceMaskHex);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { error = "discover.invalid_hex" });
        }

        if (expected.Length is < 1 or > 64
            || (tolerance is not null && tolerance.Length != expected.Length))
        {
            return Results.BadRequest(new { error = "discover.pattern_length_invalid" });
        }

        MemoryScanRequest scanRequest = new(
            request.FieldName,
            "Bytes",
            expected,
            tolerance,
            Math.Clamp(request.MaxCandidates, 1, 10_000),
            Math.Max(request.MinRegionSize, 1),
            request.Alignment is 1 or 2 or 4 or 8 ? request.Alignment : 1,
            request.IncludeImageRegions
                ? MemoryRegionSelection.Default | MemoryRegionSelection.Image
                : MemoryRegionSelection.Default,
            request.IncludeWorkingSetClassification,
            MemoryValueKind.Bytes);

        OperationResult<MemoryScanResult> result = await scanner
            .ScanPatternAsync(scanRequest, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(ToDiscoveryResponse(result.Value!))
            : Results.BadRequest(new { error = result.Error?.Code ?? "discover.pattern_failed" });
    }

    internal static async Task<IResult> DiscoverPointerChainAsync(
        IGameMemoryScanner scanner,
        PointerChainDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        if (request.RootRelativeOffset < 0
            || request.PointerOffsets is null
            || request.PointerOffsets.Count is < 1 or > 4
            || request.MaxDepth is < 1 or > 4)
        {
            return Results.BadRequest(new { error = "discover.pointer_chain.invalid_request" });
        }

        OperationResult<MemoryPointerChainResult> result = await scanner
            .ResolvePointerChainAsync(
                new MemoryPointerChainRequest(
                    request.RootRelativeOffset,
                    request.PointerOffsets,
                    request.MaxDepth),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Results.BadRequest(new { error = result.Error?.Code ?? "discover.pointer_chain.failed" });
        }

        MemoryPointerChainResult value = result.Value!;
        return Results.Ok(new PointerChainDiscoveryResponse
        {
            CompletedAtUtc = value.CompletedAtUtc,
            RejectedChains = value.RejectedChains,
            Candidates = value.Candidates.Select(candidate => new PointerChainDiscoveryCandidate
            {
                RootAddress = $"0x{candidate.RootAddress:X}",
                FinalAddress = $"0x{candidate.FinalAddress:X}",
                TraversedAddresses = candidate.TraversedAddresses
                    .Select(address => $"0x{address:X}")
                    .ToList(),
                AddressKind = candidate.AddressKind,
            }).ToList(),
        });
    }

    private static OffsetDiscoveryResponse ToDiscoveryResponse(MemoryScanResult result) =>
        new()
        {
            CompletedAtUtc = result.CompletedAtUtc,
            BaseAddress = $"0x{result.BaseAddress:X}",
            RegionsScanned = result.RegionsScanned,
            BytesScanned = result.BytesScanned,
            TotalMatchesBeforeTruncation = result.TotalMatchesBeforeTruncation,
            TargetArchitecture = result.TargetArchitecture,
            ModuleName = result.ModuleName,
            ModuleSize = result.ModuleSize,
            Alignment = result.Alignment,
            Truncated = result.Truncated,
            ScanKind = result.ScanKind,
            Candidates = result.Candidates.Select(c => new OffsetDiscoveryCandidate
            {
                AbsoluteAddress = $"0x{c.AbsoluteAddress:X}",
                BaseDisplacement = $"0x{c.BaseDisplacement:X}",
                BaseDisplacementDecimal = c.BaseDisplacement,
                ObservedValueHex = Convert.ToHexString(c.ObservedValue),
                ValueSummary = c.ValueSummary,
                AddressKind = c.AddressKind,
                IsCopyOnWrite = c.IsCopyOnWrite,
            }).ToList(),
        };

    private static bool IsHexString(string value) =>
        value.Length > 0
        && value.Length % 2 == 0
        && Regex.IsMatch(value, "^[0-9a-fA-F]+$");

    internal static async Task<IResult> CreateSnapshotAsync(
        IGameMemoryScanner scanner,
        SnapshotRequestApi request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        MemoryValueKind? kind = request.ValueKind switch
        {
            "Int32" => MemoryValueKind.Int32Value,
            "Float" => MemoryValueKind.FloatValue,
            "Double" => MemoryValueKind.DoubleValue,
            "UInt32" => MemoryValueKind.UInt32Value,
            "Int64" => MemoryValueKind.Int64Value,
            "UInt64" => MemoryValueKind.UInt64Value,
            "Bytes" => MemoryValueKind.Bytes,
            _ => null,
        };
        if (kind is null)
        {
            return Results.BadRequest(new { error = "discover.invalid_value_kind" });
        }

        var snapReq = new MemorySnapshotRequest(
            ValueSize: Math.Clamp(request.ValueSize, 1, 8),
            FloatMin: request.FloatMin,
            FloatMax: request.FloatMax,
            IntMin: request.IntMin,
            IntMax: request.IntMax,
            MinAddress: request.MinAddress,
            MaxAddress: request.MaxAddress,
            ValueKind: kind.Value,
            Alignment: request.Alignment is 1 or 2 or 4 or 8 ? request.Alignment : 1,
            RegionSelection: request.IncludeImageRegions
                ? MemoryRegionSelection.Default | MemoryRegionSelection.Image
                : MemoryRegionSelection.Default,
            LongMin: kind == MemoryValueKind.Int64Value ? request.LongMin : null,
            LongMax: kind == MemoryValueKind.Int64Value ? request.LongMax : null,
            UIntMin: kind is MemoryValueKind.UInt32Value or MemoryValueKind.UInt64Value ? request.UIntMin : null,
            UIntMax: kind is MemoryValueKind.UInt32Value or MemoryValueKind.UInt64Value ? request.UIntMax : null);
        var result = await scanner.CreateSnapshotAsync(snapReq, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new { sessionId = result.Value })
            : Results.BadRequest(new { error = result.Error?.Code });
    }

    internal static async Task<IResult> CompareSnapshotAsync(
        IGameMemoryScanner scanner,
        string sessionId,
        CompareRequestApi request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(sessionId))
            return Results.BadRequest(new { error = "discover.session_id_required" });
        string mode = request.CompareMode switch
        {
            "changed" or "unchanged" or "increased" or "decreased" => request.CompareMode,
            _ => "changed",
        };
        var result = await scanner.CompareAsync(
            sessionId, mode, Math.Clamp(request.MaxCandidates, 1, 500),
            cancellationToken,
            request.RollingBaseline).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Results.BadRequest(new { error = result.Error?.Code ?? "discover.compare_failed" });
        var r = result.Value!;
        return Results.Ok(new
        {
            r.CompletedAtUtc,
            r.PreviousCount,
            r.CurrentCount,
            r.ChangedCount,
            r.UnchangedCount,
            r.IncreasedCount,
            r.DecreasedCount,
            r.Truncated,
            r.ComparedAgainstRollingBaseline,
            r.RetainedCount,
            candidates = r.Candidates.Select(c => new OffsetDiscoveryCandidate
            {
                AbsoluteAddress = $"0x{c.AbsoluteAddress:X}",
                BaseDisplacement = $"0x{c.BaseDisplacement:X}",
                BaseDisplacementDecimal = c.BaseDisplacement,
                ObservedValueHex = Convert.ToHexString(c.ObservedValue),
                ValueSummary = c.ValueSummary,
                AddressKind = c.AddressKind,
                IsCopyOnWrite = c.IsCopyOnWrite,
            }).ToList(),
        });
    }

    internal static IResult DiscardSessionAsync(
        IGameMemoryScanner scanner, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Results.BadRequest(new { error = "discover.session_id_required" });
        }

        scanner.DiscardSession(sessionId);
        return Results.Ok(new { discarded = sessionId });
    }

    internal static async Task<IResult> DiscoverNeighborhoodAsync(
        IGameMemoryScanner scanner,
        NeighborhoodRequestApi request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReferenceOffset < 0)
            return Results.BadRequest(new { error = "discover.reference_offset_required" });
        var req = new MemoryNeighborhoodRequest(
            request.ReferenceOffset,
            Math.Clamp(request.WindowSize, 64, 4096),
            request.IncludeFloat, request.IncludeInt32, request.IncludeDouble,
            request.FloatMin, request.FloatMax, request.IntMin, request.IntMax,
            request.IncludeWorkingSetClassification);
        var result = await scanner.ScanNeighborhoodAsync(req, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Results.BadRequest(new { error = result.Error?.Code });
        var r = result.Value!;
        return Results.Ok(new OffsetDiscoveryResponse
        {
            CompletedAtUtc = r.CompletedAtUtc,
            BaseAddress = $"0x{r.BaseAddress:X}",
            RegionsScanned = r.RegionsScanned,
            BytesScanned = r.BytesScanned,
            TotalMatchesBeforeTruncation = r.TotalMatchesBeforeTruncation,
            TargetArchitecture = r.TargetArchitecture,
            ModuleName = r.ModuleName,
            ModuleSize = r.ModuleSize,
            Alignment = r.Alignment,
            Truncated = r.Truncated,
            ScanKind = r.ScanKind,
            Candidates = r.Candidates.Select(c => new OffsetDiscoveryCandidate
            {
                AbsoluteAddress = $"0x{c.AbsoluteAddress:X}",
                BaseDisplacement = $"0x{c.BaseDisplacement:X}",
                BaseDisplacementDecimal = c.BaseDisplacement,
                ObservedValueHex = Convert.ToHexString(c.ObservedValue),
                ValueSummary = c.ValueSummary,
                AddressKind = c.AddressKind,
                IsCopyOnWrite = c.IsCopyOnWrite,
            }).ToList(),
        });
    }

    // Stable error code only. ApplicationError messages may contain absolute
    // paths or machine details and must never reach the wire (privacy rule).
    private static string ErrorCode(ApplicationError? error) =>
        string.IsNullOrWhiteSpace(error?.Code) ? "launch.failed" : error.Code;
}

internal sealed record SnapshotRequestApi
{
    public int ValueSize { get; init; } = 4;
    public float? FloatMin { get; init; }
    public float? FloatMax { get; init; }
    public int? IntMin { get; init; }
    public int? IntMax { get; init; }
    public long? LongMin { get; init; }
    public long? LongMax { get; init; }
    public ulong? UIntMin { get; init; }
    public ulong? UIntMax { get; init; }
    public long MinAddress { get; init; }
    public long MaxAddress { get; init; }
    public string ValueKind { get; init; } = "Int32";
    public int Alignment { get; init; } = 1;
    public bool IncludeImageRegions { get; init; }
    public bool RollingBaseline { get; init; }
}

internal sealed record CompareRequestApi
{
    public string CompareMode { get; init; } = "changed";
    public int MaxCandidates { get; init; } = 100;
    public bool RollingBaseline { get; init; }
}

internal sealed record NeighborhoodRequestApi
{
    public long ReferenceOffset { get; init; }
    public int WindowSize { get; init; } = 512;
    public bool IncludeFloat { get; init; } = true;
    public bool IncludeInt32 { get; init; } = true;
    public bool IncludeDouble { get; init; }
    public float? FloatMin { get; init; }
    public float? FloatMax { get; init; }
    public int? IntMin { get; init; }
    public int? IntMax { get; init; }
    public bool IncludeWorkingSetClassification { get; init; }
}
