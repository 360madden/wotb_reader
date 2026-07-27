using WotBTreader.Core;
using WotBTreader.Host.Web.Contracts;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// In-process read surface for Blazor pages. Returns the same wire DTOs as the
/// HTTP read API so the UI cannot drift from the published contract.
/// </summary>
public interface IDashboardReadClient
{
    Task<SessionPageResponse> ListSessionsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<SessionDetailResponse?> GetSessionAsync(
        Guid battleSessionId,
        CancellationToken cancellationToken);

    Task<WotBTreader.Application.Diagnostics.DoctorReport> GetDoctorAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ComparisonRun>> ListComparisonsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<TelemetryComparison?> GetComparisonAsync(
        Guid comparisonRunId,
        CancellationToken cancellationToken);
}
