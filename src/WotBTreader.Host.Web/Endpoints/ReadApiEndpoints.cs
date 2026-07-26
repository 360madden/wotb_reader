using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Host.Web.Contracts;
using WotBTreader.Host.Web.Infrastructure;

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

    public static IEndpointRouteBuilder MapReadApi(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder group = builder.MapGroup("/api/v1");
        group.MapGet("/doctor", GetDoctorAsync);
        group.MapGet("/sessions", ListSessionsAsync);
        group.MapGet("/sessions/{battleSessionId:guid}", GetSessionAsync);
        group.MapGet("/decode-runs/{decodeRunId:guid}", GetDecodeRunAsync);
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

        return Results.Ok(new SessionPageResponse(
            resolvedOffset,
            resolvedLimit,
            page.Count,
            [.. page.Select(ToSummary)]));
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

        return Results.Ok(new SessionDetailResponse(
            DecodeRunResponse.From(projection.DecodeRun),
            projection.Session is null ? null : BattleSessionResponse.From(projection.Session),
            [.. projection.Participants.Select(ParticipantResponse.From)],
            [.. projection.Positions.Take(MaximumPositionSamples).Select(PositionSampleResponse.From)],
            truncated,
            projection.Positions.Count,
            projection.Events.Count,
            projection.RawRecords.Count,
            projection.Warnings));
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
            ? Results.Ok(ToSummary(result.Value))
            : FromError(context, result.Error);
    }

    private static SessionSummaryResponse ToSummary(DecodeRunSummary summary) =>
        new(
            DecodeRunResponse.From(summary.DecodeRun),
            summary.Session is null ? null : BattleSessionResponse.From(summary.Session),
            summary.ParticipantCount,
            summary.PositionCount,
            summary.EventCount,
            summary.RawRecordCount);

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
