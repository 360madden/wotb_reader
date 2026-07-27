using WotBTreader.Core;
using WotBTreader.Host.Web.Contracts;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// In-process read surface for Blazor pages. Returns the same wire DTOs as the
/// HTTP read API so the UI cannot drift from the published contract.
/// </summary>
public interface IDashboardReadClient
{
    /// <summary>Lists decoded battle sessions with offset/limit paging.</summary>
    Task<SessionPageResponse> ListSessionsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns full session detail including position samples capped at
    /// <see cref="WotBTreader.Host.Web.Endpoints.ReadApiEndpoints.MaximumPositionSamples"/>.
    /// Returns <see langword="null"/> when the session does not exist.
    /// </summary>
    Task<SessionDetailResponse?> GetSessionAsync(
        Guid battleSessionId,
        CancellationToken cancellationToken);

    /// <summary>Runs the non-mutating health checks and returns the report.</summary>
    Task<WotBTreader.Application.Diagnostics.DoctorReport> GetDoctorAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists comparison runs with offset/limit paging (max 200 per page).
    /// Results are ordered by creation time descending.
    /// </summary>
    Task<IReadOnlyList<ComparisonRun>> ListComparisonsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the full comparison result including summary counts and
    /// classified items. Returns <see langword="null"/> when the comparison
    /// run does not exist.
    /// </summary>
    Task<TelemetryComparison?> GetComparisonAsync(
        Guid comparisonRunId,
        CancellationToken cancellationToken);
}
