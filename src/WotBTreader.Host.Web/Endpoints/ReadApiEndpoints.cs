using WotBTreader.ApiContracts;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core.Overlay;
using WotBTreader.Core;
using WotBTreader.Host.Web.Contracts;
using WotBTreader.Host.Web.Infrastructure;
using WotBTreader.Host.Web.Services;

namespace WotBTreader.Host.Web.Endpoints;

/// <summary>
/// Read-only loopback API. Every route here is a GET, so
/// <see cref="MutationProtectionMiddleware"/> lets it through without a
/// capability; anything that writes must not be added to this group.
/// </summary>
internal static class ReadApiEndpoints
{
    internal const int DefaultPageSize = 50;
    internal const int MaximumPageSize = 200;

    /// <summary>Caps one response; a long battle produces far more samples.</summary>
    internal const int MaximumPositionSamples = 5_000;

    /// <summary>Caps one response; a long battle produces far more events than useful in a feed.</summary>
    internal const int MaximumEvents = 2_000;

    public static IEndpointRouteBuilder MapReadApi(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder group = builder.MapGroup("/api/v1");
        group.MapGet("/doctor", GetDoctorAsync);
        group.MapGet("/sessions", ListSessionsAsync);
        group.MapGet("/sessions/{battleSessionId:guid}", GetSessionAsync);
        group.MapGet("/maps/boundaries", GetMapBoundariesAsync);
        group.MapGet("/maps/{mapId}/minimap", GetMinimapAsync);
        group.MapGet("/decode-runs/{decodeRunId:guid}", GetDecodeRunAsync);
        group.MapGet("/sessions/{battleSessionId:guid}/frame", GetOverlayFrameAsync);
        return builder;
    }

    internal static async Task<IResult> GetDoctorAsync(
        IDoctorService doctor,
        CancellationToken cancellationToken)
    {
        DoctorReport report = await doctor.RunAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(report);
    }

    internal static async Task<IResult> ListSessionsAsync(
        HttpContext context,
        ISessionQueryRepository sessions,
        int? offset,
        int? limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        int resolvedOffset = offset ?? 0;
        int resolvedLimit = limit ?? DefaultPageSize;
        if (resolvedOffset < 0 || resolvedLimit < 1 || resolvedLimit > MaximumPageSize)
        {
            return Problem(
                context,
                StatusCodes.Status400BadRequest,
                "api.sessions.range",
                $"offset must be zero or greater and limit must be between 1 and {MaximumPageSize}.",
                retryable: false);
        }

        IReadOnlyList<DecodeRunSummary> page = await sessions
            .ListAsync(resolvedOffset, resolvedLimit, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new SessionPageResponse
        {
            Offset = resolvedOffset,
            Limit = resolvedLimit,
            Count = page.Count,
            Items = [.. page.Select(ReadContractMapping.ToResponse)],
        });
    }

    internal static async Task<IResult> GetSessionAsync(
        HttpContext context,
        ISessionQueryRepository sessions,
        Guid battleSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        OperationResult<ReplayDecodeProjection> result = await sessions
            .GetProjectionAsync(new BattleSessionId(battleSessionId), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            return FromError(context, result.Error);
        }

        ReplayDecodeProjection projection = result.Value;
        bool truncated = projection.Positions.Count > MaximumPositionSamples;

        return Results.Ok(new SessionDetailResponse
        {
            DecodeRun = projection.DecodeRun.ToResponse(),
            Session = projection.Session?.ToResponse(),
            Participants = [.. projection.Participants.Select(ReadContractMapping.ToResponse)],
            Positions =
            [
                .. projection.Positions
                    .Take(MaximumPositionSamples)
                    .Select(ReadContractMapping.ToResponse),
            ],
            PositionsTruncated = truncated,
            TotalPositionCount = projection.Positions.Count,
            EventCount = projection.Events.Count,
            RawRecordCount = projection.RawRecords.Count,
            Warnings = projection.Warnings,
            Events =
            [
                .. projection.Events
                    .Where(e => e.Kind != CanonicalEventKind.Position)
                    .Take(MaximumEvents)
                    .Select(ReadContractMapping.ToResponse),
            ],
        });
    }

    internal static async Task<IResult> GetMapBoundariesAsync(
        ISessionQueryRepository sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        IReadOnlyList<MapBoundary> boundaries = await sessions
            .GetMapBoundariesAsync(cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(boundaries.Select(ReadContractMapping.ToResponse));
    }

    internal static async Task<IResult> GetMinimapAsync(
        MinimapTextureService minimapService,
        string mapId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(minimapService);
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return Results.BadRequest("A map identifier is required.");
        }

        byte[]? pngBytes = await minimapService
            .GetMinimapPngAsync(mapId, cancellationToken)
            .ConfigureAwait(false);

        if (pngBytes is null)
        {
            return Results.NotFound($"No minimap texture is available for map '{mapId}'.");
        }

        return Results.File(pngBytes, "image/png");
    }

    /// <summary>
    /// Serves one overlay frame for the W2S HUD: the viewpoint camera and
    /// every roster tank projected to viewport pixels at a replay time.
    /// Query params: timeSeconds (default 0), fov (vertical degrees, default
    /// 90), width/height (viewport pixels, default 1920x1080). The projection
    /// is server-side so every HUD client sees identical pixels.
    /// </summary>
    internal static async Task<IResult> GetOverlayFrameAsync(
        HttpContext context,
        IOverlayFrameSource frames,
        IBeaconStore beacons,
        Guid battleSessionId,
        double? timeSeconds,
        double? fov,
        double? width,
        double? height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(beacons);

        double resolvedTime = timeSeconds ?? 0.0;
        if (!double.IsFinite(resolvedTime) || resolvedTime < 0)
        {
            return Problem(
                context,
                StatusCodes.Status400BadRequest,
                "api.frame.time",
                "timeSeconds must be a finite non-negative number of seconds.",
                retryable: false);
        }

        double resolvedFov = fov ?? 90.0;
        if (!double.IsFinite(resolvedFov) || resolvedFov <= 0 || resolvedFov >= 180)
        {
            return Problem(
                context,
                StatusCodes.Status400BadRequest,
                "api.frame.fov",
                "fov must be a positive number of degrees below 180.",
                retryable: false);
        }

        double resolvedWidth = width ?? 1920.0;
        double resolvedHeight = height ?? 1080.0;
        if (!double.IsFinite(resolvedWidth) || resolvedWidth <= 0
            || !double.IsFinite(resolvedHeight) || resolvedHeight <= 0)
        {
            return Problem(
                context,
                StatusCodes.Status400BadRequest,
                "api.frame.viewport",
                "width and height must be positive viewport pixel dimensions.",
                retryable: false);
        }

        OperationResult<OverlayFrame> frameResult = await frames.GetFrameAsync(
            new BattleSessionId(battleSessionId),
            TimeSpan.FromSeconds(resolvedTime),
            cancellationToken).ConfigureAwait(false);
        if (!frameResult.IsSuccess || frameResult.Value is null)
        {
            return Problem(
                context,
                StatusCodes.Status404NotFound,
                frameResult.Error?.Code ?? "api.frame.missing",
                frameResult.Error?.Message ?? "The session has no overlay frame at this time.",
                retryable: false);
        }

        IReadOnlyList<OverlayBeacon> sessionBeacons = await beacons.GetBeaconsAsync(
            new BattleSessionId(battleSessionId),
            cancellationToken).ConfigureAwait(false);

        OverlayFrameProjection projection = OverlayFrameProjector.Project(
            frameResult.Value,
            resolvedFov * Math.PI / 180.0,
            resolvedWidth,
            resolvedHeight,
            sessionBeacons);

        return Results.Ok(new OverlayFrameResponse
        {
            ReplayTimeSeconds = projection.ReplayTime.TotalSeconds,
            CameraX = projection.CameraX,
            CameraY = projection.CameraY,
            CameraZ = projection.CameraZ,
            CameraYawRadians = projection.CameraYawRadians,
            CameraPitchRadians = projection.CameraPitchRadians,
            Tanks = [.. projection.Tanks.Select(tank => new OverlayTankResponse
            {
                EntityId = tank.EntityId,
                PlayerName = tank.PlayerName,
                TankName = tank.TankName,
                ClanTag = tank.ClanTag,
                TeamNumber = tank.TeamNumber,
                HpFraction = tank.HpFraction,
                Alive = tank.Alive,
                DistanceMeters = tank.DistanceMeters,
                WorldX = tank.WorldX,
                WorldZ = tank.WorldZ,
                ScreenX = tank.ScreenX,
                ScreenY = tank.ScreenY,
                Depth = tank.Depth,
                InViewport = tank.InViewport,
                ScreenHeadingDegrees = tank.ScreenHeadingDegrees,
                DamageDealt = tank.DamageDealt,
                DamageTaken = tank.DamageTaken,
                Kills = tank.Kills,
            })],
            Beacons = [.. projection.Beacons.Select(beacon => new OverlayBeaconResponse
            {
                Name = beacon.Name,
                Color = beacon.Color,
                DistanceMeters = beacon.DistanceMeters,
                WorldX = beacon.WorldX,
                WorldZ = beacon.WorldZ,
                ScreenX = beacon.ScreenX,
                ScreenY = beacon.ScreenY,
                Depth = beacon.Depth,
                InViewport = beacon.InViewport,
            })],
            Pips = [.. projection.Pips.Select(pip => new OverlayPipResponse
            {
                EntityId = pip.EntityId,
                Kind = pip.Kind.ToString(),
                Damage = pip.Damage,
                ScreenX = pip.ScreenX,
                ScreenY = pip.ScreenY,
            })],
            Kills = [.. projection.Kills.Select(kill => new OverlayKillResponse
            {
                VictimEntityId = kill.VictimEntityId,
                KillerEntityId = kill.KillerEntityId,
                ReplayTimeSeconds = kill.ReplayTime.TotalSeconds,
            })],
        });
    }

    internal static async Task<IResult> GetDecodeRunAsync(
        HttpContext context,
        IDecodeRunRepository decodeRuns,
        Guid decodeRunId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decodeRuns);

        OperationResult<DecodeRunSummary> result = await decodeRuns
            .GetAsync(new DecodeRunId(decodeRunId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess && result.Value is not null
            ? Results.Ok(result.Value.ToResponse())
            : FromError(context, result.Error);
    }

    private static IResult FromError(HttpContext context, ApplicationError? error)
    {
        ApplicationError resolved = error ??
            new ApplicationError("internal.unknown", "An unknown application error occurred.");
        return Problem(
            context,
            MapStatusCode(resolved.Code),
            resolved.Code,
            resolved.Message,
            resolved.Retryable);
    }

    internal static int MapStatusCode(string errorCode)
    {
        if (errorCode.Contains("not_found", StringComparison.Ordinal))
        {
            return StatusCodes.Status404NotFound;
        }

        if (errorCode.Contains("conflict", StringComparison.Ordinal) ||
            errorCode.Contains("busy", StringComparison.Ordinal))
        {
            return StatusCodes.Status409Conflict;
        }

        if (errorCode.Contains("invalid", StringComparison.Ordinal) ||
            errorCode.Contains("malformed", StringComparison.Ordinal))
        {
            return StatusCodes.Status400BadRequest;
        }

        if (errorCode.Contains("unsupported", StringComparison.Ordinal))
        {
            return StatusCodes.Status501NotImplemented;
        }

        return StatusCodes.Status500InternalServerError;
    }

    private static IResult Problem(
        HttpContext context,
        int statusCode,
        string code,
        string detail,
        bool retryable) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            type: $"urn:wotbtreader:problem:{code}",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code,
                ["retryable"] = retryable,
                ["correlationId"] = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString(),
            });
}
