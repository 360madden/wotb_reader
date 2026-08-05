using System.Globalization;
using System.Text.RegularExpressions;
using WotBTreader.ApiContracts;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;

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
        group.MapPost("/discover/read", ReadOffsetsAsync);
        group.MapGet("/discover/trajectory/{battleSessionId:guid}", GetTrajectoryAsync);
        group.MapPost("/discover/correlate", CorrelateAsync);
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

        if (request.MaxCandidates is < 1 or > 10_000
            || request.MinRegionSize < 4096
            || request.Alignment is not (1 or 2 or 4 or 8))
        {
            return Results.BadRequest(new { error = "discover.invalid_options" });
        }

        MemoryScanRequest scanRequest = new(
            FieldName: request.FieldName,
            FieldType: fieldType,
            ExpectedValue: expectedValue,
            ToleranceMask: tolerance,
            MaxCandidates: request.MaxCandidates,
            MinRegionSize: request.MinRegionSize,
            Alignment: request.Alignment,
            RegionSelection: request.IncludeImageRegions && request.ImageRegionsOnly
                ? MemoryRegionSelection.Image
                : request.IncludeImageRegions
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

        if (request.MaxCandidates is < 1 or > 10_000
            || request.MinRegionSize < 4096
            || request.Alignment is not (1 or 2 or 4 or 8))
        {
            return Results.BadRequest(new { error = "discover.invalid_options" });
        }

        MemoryScanRequest scanRequest = new(
            request.FieldName,
            "Bytes",
            expected,
            tolerance,
            request.MaxCandidates,
            request.MinRegionSize,
            request.Alignment,
            request.IncludeImageRegions && request.ImageRegionsOnly
                ? MemoryRegionSelection.Image
                : request.IncludeImageRegions
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
        OffsetSnapshotRequest request,
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
        bool widthMatchesKind = kind.Value switch
        {
            MemoryValueKind.FloatValue or MemoryValueKind.Int32Value or MemoryValueKind.UInt32Value
                => request.ValueSize == 4,
            MemoryValueKind.DoubleValue or MemoryValueKind.Int64Value or MemoryValueKind.UInt64Value
                => request.ValueSize == 8,
            MemoryValueKind.Bytes => true,
            _ => false,
        };
        if (request.ValueSize is not (1 or 2 or 4 or 8)
            || !widthMatchesKind
            || request.Alignment is not (1 or 2 or 4 or 8)
            || request.MinAddress < 0
            || request.MaxAddress < 0
            || request.MaxBytes < 0
            || request.MaxBytes > OffsetSnapshotRequest.MaximumSnapshotBytes
            || (request.MaxAddress > 0 && request.MinAddress >= request.MaxAddress)
            || (request.FloatMin.HasValue && !float.IsFinite(request.FloatMin.Value))
            || (request.FloatMax.HasValue && !float.IsFinite(request.FloatMax.Value))
            || (request.FloatMin.HasValue && request.FloatMax.HasValue
                && request.FloatMin.Value > request.FloatMax.Value)
            || (request.IntMin.HasValue && request.IntMax.HasValue
                && request.IntMin.Value > request.IntMax.Value)
            || (request.LongMin.HasValue && request.LongMax.HasValue
                && request.LongMin.Value > request.LongMax.Value)
            || (request.UIntMin.HasValue && request.UIntMax.HasValue
                && request.UIntMin.Value > request.UIntMax.Value))
        {
            return Results.BadRequest(new { error = "discover.invalid_options" });
        }

        var snapReq = new MemorySnapshotRequest(
            ValueSize: request.ValueSize,
            FloatMin: request.FloatMin,
            FloatMax: request.FloatMax,
            IntMin: request.IntMin,
            IntMax: request.IntMax,
            MinAddress: request.MinAddress,
            MaxAddress: request.MaxAddress,
            ValueKind: kind.Value,
            Alignment: request.Alignment,
            RegionSelection: request.IncludeImageRegions && request.ImageRegionsOnly
                ? MemoryRegionSelection.Image
                : request.IncludeImageRegions
                    ? MemoryRegionSelection.Default | MemoryRegionSelection.Image
                    : MemoryRegionSelection.Default,
            MaxBytes: request.MaxBytes,
            LongMin: kind == MemoryValueKind.Int64Value ? request.LongMin : null,
            LongMax: kind == MemoryValueKind.Int64Value ? request.LongMax : null,
            UIntMin: kind is MemoryValueKind.UInt32Value or MemoryValueKind.UInt64Value ? request.UIntMin : null,
            UIntMax: kind is MemoryValueKind.UInt32Value or MemoryValueKind.UInt64Value ? request.UIntMax : null);
        var result = await scanner.CreateSnapshotAsync(snapReq, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new OffsetSnapshotResponse { SessionId = result.Value ?? string.Empty })
            : Results.BadRequest(new { error = result.Error?.Code });
    }

    internal static async Task<IResult> CompareSnapshotAsync(
        IGameMemoryScanner scanner,
        string sessionId,
        OffsetCompareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(sessionId))
            return Results.BadRequest(new { error = "discover.session_id_required" });
        bool isDelta = request.CompareMode == "delta";
        bool isExact = request.CompareMode == "exact";
        if (request.CompareMode is not ("changed" or "unchanged" or "increased" or "decreased" or "delta" or "exact"))
            return Results.BadRequest(new { error = "discover.invalid_compare_mode" });
        if (request.MaxCandidates is < 1 or > 500)
            return Results.BadRequest(new { error = "discover.invalid_options" });
        if ((isDelta || isExact)
            && (!request.DeltaTarget.HasValue || !request.DeltaTolerance.HasValue
                || !double.IsFinite(request.DeltaTarget.Value)
                || !double.IsFinite(request.DeltaTolerance.Value)
                || request.DeltaTolerance.Value < 0))
        {
            return Results.BadRequest(new
            {
                error = isExact ? "discover.invalid_exact_options" : "discover.invalid_delta_options",
            });
        }
        if (!isDelta && !isExact && (request.DeltaTarget.HasValue || request.DeltaTolerance.HasValue))
        {
            return Results.BadRequest(new { error = "discover.delta_only_with_delta_mode" });
        }

        var result = await scanner.CompareAsync(
            sessionId, request.CompareMode, request.MaxCandidates,
            cancellationToken,
            request.RollingBaseline,
            request.DeltaTarget,
            request.DeltaTolerance).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Results.BadRequest(new { error = result.Error?.Code ?? "discover.compare_failed" });
        var r = result.Value!;
        return Results.Ok(new OffsetCompareResponse
        {
            CompletedAtUtc = r.CompletedAtUtc,
            PreviousCount = r.PreviousCount,
            CurrentCount = r.CurrentCount,
            ChangedCount = r.ChangedCount,
            UnchangedCount = r.UnchangedCount,
            IncreasedCount = r.IncreasedCount,
            DecreasedCount = r.DecreasedCount,
            Truncated = r.Truncated,
            ComparedAgainstRollingBaseline = r.ComparedAgainstRollingBaseline,
            RetainedCount = r.RetainedCount,
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

    internal static IResult DiscardSessionAsync(
        IGameMemoryScanner scanner, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Results.BadRequest(new { error = "discover.session_id_required" });
        }

        scanner.DiscardSession(sessionId);
        return Results.Ok(new OffsetDiscardResponse { Discarded = sessionId });
    }

    internal static async Task<IResult> DiscoverNeighborhoodAsync(
        IGameMemoryScanner scanner,
        OffsetNeighborhoodRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReferenceOffset < 0)
            return Results.BadRequest(new { error = "discover.reference_offset_required" });
        if (request.WindowSize is < 64 or > 4096)
        {
            return Results.BadRequest(new { error = "discover.invalid_window_size" });
        }
        if ((request.FloatMin.HasValue && !float.IsFinite(request.FloatMin.Value))
            || (request.FloatMax.HasValue && !float.IsFinite(request.FloatMax.Value))
            || (request.FloatMin.HasValue && request.FloatMax.HasValue
                && request.FloatMin.Value > request.FloatMax.Value)
            || (request.IntMin.HasValue && request.IntMax.HasValue
                && request.IntMin.Value > request.IntMax.Value))
        {
            return Results.BadRequest(new { error = "discover.invalid_options" });
        }
        var req = new MemoryNeighborhoodRequest(
            request.ReferenceOffset,
            request.WindowSize,
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

    internal static async Task<IResult> ReadOffsetsAsync(
        IGameMemoryScanner scanner,
        OffsetReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Addresses is null || request.Addresses.Count is < 1 or > 2000)
        {
            return Results.BadRequest(new { error = "discover.invalid_options" });
        }

        MemoryValueKind? kind = request.ValueKind switch
        {
            "Int32" => MemoryValueKind.Int32Value,
            "Float" => MemoryValueKind.FloatValue,
            "Double" => MemoryValueKind.DoubleValue,
            "UInt32" => MemoryValueKind.UInt32Value,
            "Int64" => MemoryValueKind.Int64Value,
            "UInt64" => MemoryValueKind.UInt64Value,
            _ => null,
        };
        if (kind is null)
        {
            return Results.BadRequest(new { error = "discover.invalid_value_kind" });
        }

        bool widthMatchesKind = kind.Value switch
        {
            MemoryValueKind.FloatValue or MemoryValueKind.Int32Value or MemoryValueKind.UInt32Value
                => request.ValueSize == 4,
            MemoryValueKind.DoubleValue or MemoryValueKind.Int64Value or MemoryValueKind.UInt64Value
                => request.ValueSize == 8,
            _ => false,
        };
        if (request.ValueSize is not (4 or 8) || !widthMatchesKind)
        {
            return Results.BadRequest(new { error = "discover.invalid_options" });
        }

        List<long> addresses = new(request.Addresses.Count);
        foreach (string raw in request.Addresses)
        {
            string hex = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
            if (!IsHexString(hex)
                || hex.Length > 16
                || !long.TryParse(
                    hex,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out long value)
                || value <= 0)
            {
                return Results.BadRequest(new { error = "discover.invalid_address" });
            }

            addresses.Add(value);
        }

        OperationResult<MemoryReadResult> result = await scanner
            .ReadAddressesAsync(
                new MemoryReadRequest(addresses, request.ValueSize, kind.Value),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return Results.BadRequest(new { error = result.Error?.Code ?? "discover.read_failed" });
        }

        MemoryReadResult readResult = result.Value!;
        return Results.Ok(new OffsetReadResponse
        {
            CompletedAtUtc = readResult.CompletedAtUtc,
            RequestedCount = readResult.Reads.Count,
            ReadCount = readResult.Reads.Count(static item => item.ReadOk),
            Reads = readResult.Reads.Select(item => new OffsetReadItem
            {
                AbsoluteAddress = $"0x{item.AbsoluteAddress:X}",
                ReadOk = item.ReadOk,
                ObservedValueHex = item.ObservedValue is null
                    ? string.Empty
                    : Convert.ToHexString(item.ObservedValue),
                ValueSummary = item.ValueSummary,
            }).ToList(),
        });
    }

    internal static async Task<IResult> GetTrajectoryAsync(
        ITrajectoryGroundTruthProvider provider,
        Guid battleSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        OperationResult<TrajectoryGroundTruth> result = await provider
            .GetAsync(new BattleSessionId(battleSessionId), cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            return Results.NotFound(new { error = result.Error?.Code ?? "trajectory.not_found" });
        }

        TrajectoryGroundTruth groundTruth = result.Value;
        return Results.Ok(new TrajectoryResponse(
            battleSessionId,
            groundTruth.DurationTicks,
            groundTruth.Entities.Select(entity => new TrajectoryEntityResponse(
                entity.EntityId,
                entity.ParticipantId?.Value.ToString(),
                entity.TankName,
                entity.IsViewpoint,
                entity.Samples.Select(sample => new TrajectorySampleResponse(
                    sample.ReplayTimeTicks,
                    sample.X,
                    sample.Y,
                    sample.Z)).ToList())).ToList()));
    }

    internal static async Task<IResult> CorrelateAsync(
        ITrajectoryGroundTruthProvider provider,
        CorrelateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        if (request.GroundTruthSessionId == Guid.Empty
            || request.ReplayStartWallTimeUtc <= DateTimeOffset.MinValue
            || !double.IsFinite(request.TolerancePerAxis)
            || request.TolerancePerAxis <= 0
            || request.TolerancePerAxis > 1000
            || request.MaxTimeShiftSeconds is < 0
                or > TrajectoryCorrelationScorer.MaximumTimeShiftSeconds
            || !double.IsFinite(request.MinMovingSpan)
            || request.MinMovingSpan < 0
            || !double.IsFinite(request.ShiftStepSeconds)
            || request.ShiftStepSeconds <= 0
            || request.ShiftStepSeconds > 1.0
            || request.Observations is null
            || request.Observations.Count is < 1 or > 2000)
        {
            return Results.BadRequest(new { error = "discover.invalid_options" });
        }

        foreach (CorrelationSeriesRequest series in request.Observations)
        {
            // Null-check BEFORE any member access: a deserializer can produce
            // a null series or a null Address, and StartsWith on null would
            // 500 instead of 400.
            if (series is null
                || string.IsNullOrWhiteSpace(series.Address)
                || series.Samples is null
                || series.Samples.Count is < 1 or > 5000)
            {
                return Results.BadRequest(new { error = "discover.invalid_options" });
            }

            string hex = series.Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? series.Address[2..]
                : series.Address;
            if (!IsHexString(hex)
                || hex.Length > 16
                || !long.TryParse(
                    hex,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out long address)
                || address <= 0)
            {
                return Results.BadRequest(new { error = "discover.invalid_options" });
            }
        }

        OperationResult<TrajectoryGroundTruth> groundResult = await provider
            .GetAsync(new BattleSessionId(request.GroundTruthSessionId), cancellationToken)
            .ConfigureAwait(false);
        if (!groundResult.IsSuccess || groundResult.Value is null)
        {
            return Results.BadRequest(new { error = groundResult.Error?.Code ?? "trajectory.not_found" });
        }

        IReadOnlyList<ObservedAddressSeries> observations =
            [.. request.Observations.Select(series => new ObservedAddressSeries(
                series.Address,
                [.. series.Samples.Select(sample => new CorrelationSample(
                    sample.WallTimeUtc,
                    sample.Value))]))];

        IReadOnlyList<TrajectoryCorrelationResult> results = TrajectoryCorrelationScorer.Score(
            groundResult.Value,
            request.ReplayStartWallTimeUtc,
            observations,
            request.TolerancePerAxis,
            request.MaxTimeShiftSeconds,
            request.MinMovingSpan,
            request.ShiftStepSeconds);

        // M2 family mapping: group the scored addresses into candidate
        // coordinate families (survivor + sibling components within one small
        // byte window, same entity). Pure logic; no live memory access.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
            results,
            request.MaxTimeShiftSeconds);

        return Results.Ok(new CorrelateResponse
        {
            CompletedAtUtc = DateTimeOffset.UtcNow,
            AddressesScored = results.Count,
            // Only samples the scorer actually retained and scored count;
            // non-finite samples are dropped before matching.
            TotalSamples = results.Sum(static result => result.TotalSamples),
            Results = results.Select(result => new CorrelateResultItemResponse(
                result.Address,
                result.ParticipantId?.Value.ToString(),
                result.EntityId,
                result.Axis,
                result.Sign,
                result.ShiftSeconds,
                result.ShiftMinSeconds,
                result.ShiftMaxSeconds,
                result.MatchCount,
                result.TotalSamples,
                result.Span,
                result.Score)).ToList(),
            Families = families.Select(family => new TrajectoryFamilyResponse(
                family.BaseAddress,
                family.SpanBytes,
                [.. family.AxesCovered],
                family.Complete,
                family.Members.Select(member => new FamilyMemberResponse(
                    member.Address,
                    member.OffsetBytes,
                    member.Axis,
                    member.Sign,
                    member.ShiftSeconds,
                    member.ShiftMinSeconds,
                    member.ShiftMaxSeconds,
                    member.Score,
                    member.EdgeAligned)).ToList())).ToList(),
        });
    }

    // Stable error code only. ApplicationError messages may contain absolute
    // paths or machine details and must never reach the wire (privacy rule).
    private static string ErrorCode(ApplicationError? error) =>
        string.IsNullOrWhiteSpace(error?.Code) ? "launch.failed" : error.Code;
}
