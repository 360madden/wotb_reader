using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// Warms the projection cache for the most recent session when the web host
/// starts, so the first frame request after startup is as fast as every later
/// one (the CLI decodes replays in a separate process, so decode-time cache
/// warming never reaches this host). Best-effort by design: a failure only
/// costs the normal cold-first-frame path, never startup.
/// </summary>
internal sealed class ProjectionCacheWarmer(
    ISessionQueryRepository sessions,
    IProjectionCache cache,
    ILogger<ProjectionCacheWarmer> logger) : BackgroundService
{
    private static readonly EventId WarmSucceededEvent = new(4300, "ProjectionCacheWarmSucceeded");
    private static readonly EventId WarmSkippedEvent = new(4301, "ProjectionCacheWarmSkipped");
    private static readonly EventId WarmFailedEvent = new(4302, "ProjectionCacheWarmFailed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Storage initialization is another hosted service; it may not have
        // finished when this task starts. Retry briefly rather than racing it.
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                IReadOnlyList<DecodeRunSummary> latest = await sessions
                    .ListAsync(0, 1, stoppingToken)
                    .ConfigureAwait(false);
                DecodeRunSummary? summary = latest.Count > 0 ? latest[0] : null;
                if (summary?.Session is null)
                {
                    logger.LogInformation(WarmSkippedEvent, "[ProjectionCacheWarmer] No sessions to warm.");
                    return;
                }

                OperationResult<ReplayDecodeProjection> result = await sessions
                    .GetProjectionAsync(summary.Session.Id, stoppingToken)
                    .ConfigureAwait(false);
                if (result.IsSuccess && result.Value is not null)
                {
                    cache.Store(summary.Session.Id, result.Value);
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            WarmSucceededEvent,
                            "[ProjectionCacheWarmer] Warmed session {SessionId} ({PositionCount} positions).",
                            summary.Session.Id,
                            result.Value.Positions.Count);
                    }

                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        WarmFailedEvent,
                        exception,
                        "[ProjectionCacheWarmer] Attempt {Attempt} failed.",
                        attempt);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
