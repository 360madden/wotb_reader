using WotBTreader.ApiContracts;
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

        return new SessionPageResponse
        {
            Offset = offset,
            Limit = limit,
            Count = page.Count,
            Items = [.. page.Select(ReadContractMapping.ToResponse)],
        };
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

        return new SessionDetailResponse
        {
            DecodeRun = projection.DecodeRun.ToResponse(),
            Session = projection.Session?.ToResponse(),
            Participants = [.. projection.Participants.Select(ReadContractMapping.ToResponse)],
            Positions =
            [
                .. projection.Positions
                    .Take(ReadApiEndpoints.MaximumPositionSamples)
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
                    .Take(ReadApiEndpoints.MaximumEvents)
                    .Select(ReadContractMapping.ToResponse),
            ],
        };
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
}
