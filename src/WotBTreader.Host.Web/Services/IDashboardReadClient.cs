using WotBTreader.ApiContracts;
using WotBTreader.Core;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// In-process read surface for Blazor pages. Returns the same wire DTOs as the
/// HTTP read API so the UI cannot drift from the published contract.
/// </summary>
public interface IDashboardReadClient
{
    /// <summary>
    /// Lists decoded battle sessions with offset/limit paging.
    /// Results are ordered by import time descending (newest first).
    /// </summary>
    /// <param name="offset">Zero-based page offset.</param>
    /// <param name="limit">Maximum items to return (1–200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of session summaries.</returns>
    Task<SessionPageResponse> ListSessionsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns full session detail including position samples capped at
    /// <see cref="WotBTreader.Host.Web.Endpoints.ReadApiEndpoints.MaximumPositionSamples"/>.
    /// </summary>
    /// <param name="battleSessionId">The session to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detail response, or <see langword="null"/> when the session does not exist.</returns>
    Task<SessionDetailResponse?> GetSessionAsync(
        Guid battleSessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs the non-mutating health checks and returns the report.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A doctor report with per-check status.</returns>
    Task<WotBTreader.Application.Diagnostics.DoctorReport> GetDoctorAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists comparison runs with offset/limit paging (max 200 per page).
    /// Results are ordered by creation time descending.
    /// </summary>
    /// <param name="offset">Zero-based page offset.</param>
    /// <param name="limit">Maximum items to return (1–200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of comparison run metadata records.</returns>
    Task<IReadOnlyList<ComparisonRun>> ListComparisonsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the full comparison result including summary counts and
    /// classified items.
    /// </summary>
    /// <param name="comparisonRunId">The comparison run to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The comparison with summary and items, or <see langword="null"/> when not found.</returns>
    Task<TelemetryComparison?> GetComparisonAsync(
        Guid comparisonRunId,
        CancellationToken cancellationToken);
}
