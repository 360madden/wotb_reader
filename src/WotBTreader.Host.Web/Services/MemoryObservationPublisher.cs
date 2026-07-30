using Microsoft.AspNetCore.SignalR;
using WotBTreader.ApiContracts;
using WotBTreader.Application.Game;
using WotBTreader.Host.Web.Hubs;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// Polls the game memory observer at a fixed interval and pushes
/// live telemetry to all connected overlay clients via SignalR.
/// Only publishes when the gate is OfflineReplayVerified and the
/// observation is Available. Duplicate values are suppressed.
/// </summary>
internal sealed class MemoryObservationPublisher(
    IGameSessionState sessionState,
    IGameMemoryObserver memoryObserver,
    IHubContext<TelemetryHub> hubContext,
    ILogger<MemoryObservationPublisher> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        GameMemoryResponse? lastPublished = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);

                GameSessionSnapshot snapshot = await sessionState
                    .GetSnapshotAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (snapshot.State != GameSessionVerificationState.OfflineReplayVerified)
                {
                    lastPublished = null;
                    continue;
                }

                GameMemoryObservation observation = await memoryObserver
                    .ObserveAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (observation.Availability != GameMemoryObservationAvailability.Available)
                {
                    continue;
                }

                var response = new GameMemoryResponse
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
                };

                // Suppress duplicate pushes to reduce SignalR traffic.
                if (lastPublished is not null
                    && lastPublished.PlayerHP == response.PlayerHP
                    && lastPublished.PlayerPositionX == response.PlayerPositionX
                    && lastPublished.PlayerPositionZ == response.PlayerPositionZ
                    && lastPublished.ReplayTimeSeconds == response.ReplayTimeSeconds)
                {
                    continue;
                }

                await hubContext.Clients.All
                    .SendAsync("MemoryObservation", response, stoppingToken)
                    .ConfigureAwait(false);

                lastPublished = response;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Memory observation poll cycle failed");
                lastPublished = null;
            }
        }
    }
}
