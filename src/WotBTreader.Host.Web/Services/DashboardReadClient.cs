using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Host.Web.Contracts;
using WotBTreader.Host.Web.Endpoints;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// Maps storage projections into the U8 read-API DTOs for Blazor Server pages.
/// Paging and position caps match <see cref="ReadApiEndpoints"/>.
/// </summary>
internal sealed class DashboardReadClient(
    ISessionQueryRepository sessions,
    IDoctorService doctor,
    IComparisonRunRepository comparisons) : IDashboardReadClient
{
    public async Task<SessionPageResponse> ListSessionsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0 ||
            limit < 1 ||
            limit > ReadApiEndpoints.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"offset must be >= 0 and limit must be between 1 and {ReadApiEndpoints.MaximumPageSize}.");
        }

        IReadOnlyList<DecodeRunSummary> page = await sessions
            .ListAsync(offset, limit, cancellationToken)
            .ConfigureAwait(false);

        return new SessionPageResponse(
            offset,
            limit,
            page.Count,
            [.. page.Select(ToSummary)]);
    }

    public async Task<SessionDetailResponse?> GetSessionAsync(
        Guid battleSessionId,
        CancellationToken cancellationToken)
    {
        OperationResult<ReplayDecodeProjection> result = await sessions
            .GetProjectionAsync(new BattleSessionId(battleSessionId), cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Value is null)
        {
            return null;
        }

        ReplayDecodeProjection projection = result.Value;
        bool truncated = projection.Positions.Count > ReadApiEndpoints.MaximumPositionSamples;

        return new SessionDetailResponse(
            DecodeRunResponse.From(projection.DecodeRun),
            projection.Session is null ? null : BattleSessionResponse.From(projection.Session),
            [.. projection.Participants.Select(ParticipantResponse.From)],
            [.. projection.Positions
                .Take(ReadApiEndpoints.MaximumPositionSamples)
                .Select(PositionSampleResponse.From)],
            truncated,
            projection.Positions.Count,
            projection.Events.Count,
            projection.RawRecords.Count,
            projection.Warnings,
            [.. projection.Events
                .Where(e => e.Kind != CanonicalEventKind.Position)
                .Take(ReadApiEndpoints.MaximumEvents)
                .Select(EventResponse.From)]);
    }

    public async Task<DoctorReport> GetDoctorAsync(CancellationToken cancellationToken) =>
        await doctor.RunAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ComparisonRun>> ListComparisonsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || limit < 1 || limit > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "offset must be >= 0 and limit must be between 1 and 200.");
        }

        return await comparisons
            .ListAsync(offset, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TelemetryComparison?> GetComparisonAsync(
        Guid comparisonRunId,
        CancellationToken cancellationToken)
    {
        OperationResult<TelemetryComparison> result = await comparisons
            .GetAsync(new ComparisonRunId(comparisonRunId), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess ? result.Value : null;
    }

    private static SessionSummaryResponse ToSummary(DecodeRunSummary summary) =>
        new(
            DecodeRunResponse.From(summary.DecodeRun),
            summary.Session is null ? null : BattleSessionResponse.From(summary.Session),
            summary.ParticipantCount,
            summary.PositionCount,
            summary.EventCount,
            summary.RawRecordCount);
}
