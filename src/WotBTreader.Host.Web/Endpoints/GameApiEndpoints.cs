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
        group.MapPost("/discover", DiscoverOffsetsAsync);
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

        string fieldType = request.FieldType switch
        {
            "Float" => "Float",
            "Int32" => "Int32",
            "Double" => "Double",
            _ => "Float",
        };

        MemoryScanRequest scanRequest = new(
            FieldName: request.FieldName,
            FieldType: fieldType,
            ExpectedValue: expectedValue,
            ToleranceMask: tolerance,
            MaxCandidates: Math.Clamp(request.MaxCandidates, 1, 10_000),
            MinRegionSize: Math.Max(request.MinRegionSize, 4096));

        OperationResult<MemoryScanResult> result =
            await scanner.ScanAsync(scanRequest, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return Results.BadRequest(new
            {
                error = result.Error?.Code ?? "discover.failed",
                detail = result.Error?.Message,
            });
        }

        MemoryScanResult scanResult = result.Value!;

        List<OffsetDiscoveryCandidate> candidates = [];
        foreach (MemoryScanCandidate c in scanResult.Candidates)
        {
            candidates.Add(new OffsetDiscoveryCandidate
            {
                AbsoluteAddress = $"0x{c.AbsoluteAddress:X}",
                RelativeOffset = $"0x{c.RelativeOffset:X}",
                RelativeOffsetDecimal = c.RelativeOffset,
                ObservedValueHex = Convert.ToHexString(c.ObservedValue),
                ValueSummary = c.ValueSummary,
            });
        }

        return Results.Ok(new OffsetDiscoveryResponse
        {
            CompletedAtUtc = scanResult.CompletedAtUtc,
            BaseAddress = $"0x{scanResult.BaseAddress:X}",
            RegionsScanned = scanResult.RegionsScanned,
            BytesScanned = scanResult.BytesScanned,
            TotalMatchesBeforeTruncation = scanResult.TotalMatchesBeforeTruncation,
            Candidates = candidates,
        });
    }

    private static bool IsHexString(string value) =>
        value.Length > 0
        && value.Length % 2 == 0
        && Regex.IsMatch(value, "^[0-9a-fA-F]+$");

    private static string ErrorCode(ApplicationError? error) =>
        string.IsNullOrWhiteSpace(error?.Code) ? "launch.failed" : error.Code;
}
