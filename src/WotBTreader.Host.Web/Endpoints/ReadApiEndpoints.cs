using WotBTreader.ApiContracts;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Game;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Discovery;
using WotBTreader.Core.Overlay;
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
        group.MapGet("/live/frame", GetLiveFrameAsync);
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
        CancellationToken cancellationToken,
        IGameMemoryScanner? scanner = null)
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

        // CAM-005 seam: when a gate-verified session is live, the memory
        // camera (GameCamera pose) replaces the viewpoint approximation.
        // Fail-closed: any read/status problem yields null and the frame
        // falls back to the decoded viewpoint camera.
        OverlayCamera? cameraOverride = await TryReadMemoryCameraAsync(
            scanner, cancellationToken).ConfigureAwait(false);

        OperationResult<OverlayFrame> frameResult = await frames.GetFrameAsync(
            new BattleSessionId(battleSessionId),
            TimeSpan.FromSeconds(resolvedTime),
            cancellationToken,
            cameraOverride).ConfigureAwait(false);
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

        return Results.Ok(ToOverlayFrameResponse(projection));
    }

    /// <summary>
    /// Serves one composed LIVE frame projected to viewport pixels — the
    /// <c>LiveFrameSource</c> seam. Same <see cref="OverlayFrameResponse"/>
    /// shape as the replay frame, so the HUD renders live nameplates without
    /// touching its render path. Query params: fov (vertical degrees, default
    /// 90), width/height (viewport pixels, default 1920x1080). The frame
    /// comes from the gated coordinator surface (ONE guarded reader lease,
    /// ONE G2 clock label); hp is honestly unknown (empty bar) until L1
    /// lands, and pips/kills/scoreboard are absent (decode-projection
    /// features). Fail-closed: a failed read is 503 retryable; a gate-level
    /// non-resolved frame (pre-battle-inactive, unsupported build, revoked
    /// authorization) is 409 so the HUD keeps its last-good frame.
    /// </summary>
    internal static async Task<IResult> GetLiveFrameAsync(
        HttpContext context,
        IGameMemoryScanner scanner,
        ISessionQueryRepository sessions,
        Guid? sessionId,
        double? fov,
        double? width,
        double? height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(sessions);

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

        // Optional per-id decoded-roster join (design
        // docs/operations/live-roster-name-join-design.md): when a session
        // id is supplied, load its decoded participants and map entity id ->
        // participant with the SAME first-match convention as
        // ReplayFrameSource. Fail-closed: a missing/unknown session degrades
        // to anonymous names (the join is best-effort per id, never an error
        // on the live frame). Loaded BEFORE the frame read so the decoded
        // own entity id can drive the frame's own-row damage-dealt
        // consumption.
        IReadOnlyDictionary<long, Participant>? participants = null;
        long? ownEntityId = null;
        if (sessionId is { } requestedSession)
        {
            OperationResult<ReplayDecodeProjection> rosterResult =
                await sessions.GetProjectionAsync(
                    new BattleSessionId(requestedSession),
                    cancellationToken).ConfigureAwait(false);
            if (rosterResult.IsSuccess && rosterResult.Value is not null)
            {
                participants = rosterResult.Value.Participants
                    .Where(participant => participant.EntityId is not null)
                    .GroupBy(participant => participant.EntityId!.Value)
                    .ToDictionary(group => group.Key, group => group.First());

                // Own-nameplate refinement (name-join design step 4): the
                // decoded session's viewpoint participant identifies the
                // player's own tank — the "self" marker the HUD suppresses.
                // Fail-closed: no viewpoint id or no matching participant
                // leaves OwnEntityId null (no suppression, names intact).
                if (rosterResult.Value.Session?.ViewpointParticipantId is { } viewpointId)
                {
                    ownEntityId = rosterResult.Value.Participants
                        .FirstOrDefault(participant => participant.Id == viewpointId)
                        ?.EntityId;
                }
            }
        }

        // Forward the session id into the discover call so the batch core's
        // ONE G2 replay-clock snapshot runs (same clock source the
        // /discover/entity-regions path uses): the frame then carries a real
        // estimated replay time instead of 0.0. Fail-closed: an unknown or
        // stale session leaves ReplayTimeSeconds null (frame 0.0), never an
        // error on the live frame. The decoded own entity id drives the
        // own-row damage-dealt read (honest, fail-closed).
        OperationResult<LiveFrameReadResult> frameResult = await scanner.ReadLiveFrameAsync(
            new WotBTreader.Application.Game.LiveFrameReadRequest(
                sessionId is { } forwarded ? new BattleSessionId(forwarded) : null,
                ownEntityId),
            cancellationToken).ConfigureAwait(false);
        if (!frameResult.IsSuccess || frameResult.Value is null)
        {
            return Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                frameResult.Error?.Code ?? "api.live_frame.unavailable",
                frameResult.Error?.Message ?? "The live frame read is unavailable.",
                retryable: true);
        }

        LiveFrameReadResult frame = frameResult.Value;
        if (frame.Status != Type10EntityPositionStatus.Resolved)
        {
            return Problem(
                context,
                StatusCodes.Status409Conflict,
                "api.live_frame.not_resolved",
                $"The live frame is not resolved ({frame.Status}, {frame.FailureStage}).",
                retryable: true);
        }

        OverlayFrameProjection projection = LiveFrameProjector.Project(
            frame,
            resolvedFov * Math.PI / 180.0,
            resolvedWidth,
            resolvedHeight,
            participants);
        return Results.Ok(ToOverlayFrameResponse(projection, ownEntityId));
    }

    /// <summary>
    /// The single projection→response mapping shared by the replay frame and
    /// the live frame endpoints, so both sources serialize identically.
    /// </summary>
    private static OverlayFrameResponse ToOverlayFrameResponse(
        OverlayFrameProjection projection,
        long? ownEntityId = null) => new()
        {
            ReplayTimeSeconds = projection.ReplayTime.TotalSeconds,
            CameraX = projection.CameraX,
            CameraY = projection.CameraY,
            CameraZ = projection.CameraZ,
            CameraYawRadians = projection.CameraYawRadians,
            CameraPitchRadians = projection.CameraPitchRadians,
            OwnEntityId = ownEntityId,
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
            MaxHealth = tank.MaxHealth,
            CurrentHealth = tank.CurrentHealth,
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
            PenBadge = projection.PenBadge is { } badge
                ? new OverlayPenBadgeResponse
                {
                    AimedEntityId = badge.AimedEntityId,
                    Face = badge.Face.ToString(),
                    Band = badge.Verdict.Band.ToString(),
                    EffectiveArmorMm = badge.Verdict.EffectiveArmorMm,
                    PenetrationMmAtRange = badge.Verdict.PenetrationMmAtRange,
                    Ricochet = badge.Verdict.Ricochet,
                }
                : null,
        };

    /// <summary>
    /// CAM-005 seam: reads the gate-verified GameCamera pose (the CAM-001
    /// fixed member-path) and maps it to the overlay camera. Fail-closed —
    /// null when there is no scanner, the gate is not satisfied, the pose is
    /// not <see cref="CameraPoseStatus.Resolved"/>, or the request is
    /// cancelled, so the frame always falls back to the decoded viewpoint.
    /// </summary>
    private static async ValueTask<OverlayCamera?> TryReadMemoryCameraAsync(
        IGameMemoryScanner? scanner,
        CancellationToken cancellationToken)
    {
        if (scanner is null)
        {
            return null;
        }

        OperationResult<CameraPoseReadResult> poseResult;
        try
        {
            poseResult = await scanner.ReadCameraPoseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (!poseResult.IsSuccess || poseResult.Value is null
            || poseResult.Value.Status != CameraPoseStatus.Resolved)
        {
            return null;
        }

        CameraPoseReadResult pose = poseResult.Value;
        return new OverlayCamera(
            pose.X,
            pose.Y,
            pose.Z,
            pose.YawRadians,
            pose.PitchRadians,
            RollRadians: null);
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
