using WotBTreader.ApiContracts;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Host.Web.Endpoints;

/// <summary>
/// Game interaction API — query game/replay state, poll memory, and launch replays.
/// Endpoints are loopback-gated by the existing LoopbackOnlyMiddleware.
/// </summary>
internal static class GameApiEndpoints
{
    public static IEndpointRouteBuilder MapGameApi(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder group = builder.MapGroup("/api/v1/game");
        group.MapGet("/state", GetGameStateAsync);
        group.MapGet("/memory", GetGameMemoryAsync);
        group.MapPost("/launch", LaunchGameAsync);
        return builder;
    }

    internal static async Task<IResult> GetGameStateAsync(
        IGameSessionState gameSessionState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameSessionState);

        GameSessionSnapshot snapshot = await gameSessionState
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new GameStateResponse
        {
            GamePresent = snapshot.GamePresent,
            VerificationState = snapshot.State.ToString(),
            ObservedAtUtc = snapshot.ObservedAtUtc,
            EvidenceExpiresAtUtc = snapshot.EvidenceExpiresAtUtc,
            ReasonCode = snapshot.ReasonCode,
        });
    }

    internal static async Task<IResult> GetGameMemoryAsync(
        IGameMemoryObserver gameMemoryObserver,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameMemoryObserver);

        GameMemoryObservation observation = await gameMemoryObserver
            .ObserveAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new GameMemoryResponse
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
        });
    }

    internal static async Task<IResult> LaunchGameAsync(
        IGameReplayLauncher gameReplayLauncher,
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameReplayLauncher);
        ArgumentNullException.ThrowIfNull(request);

        if (!Guid.TryParse(request.SourceArtifactId, out Guid sourceArtifactId) || sourceArtifactId == Guid.Empty)
        {
            return Results.BadRequest(new GameLaunchResponse
            {
                Success = false,
                Message = "launch.source_artifact.invalid",
            });
        }

        try
        {
            OperationResult<GameReplayLaunchOutcome> result = await gameReplayLauncher
                .LaunchAsync(new GameReplayLaunchRequest(new SourceArtifactId(sourceArtifactId)), cancellationToken)
                .ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(new GameLaunchResponse
                {
                    Success = true,
                    Message = "launch.accepted",
                })
                : Results.BadRequest(new GameLaunchResponse
                {
                    Success = false,
                    Message = ErrorCode(result.Error),
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Results.BadRequest(new GameLaunchResponse
            {
                Success = false,
                Message = $"launch.failed: {exception.GetType().Name}",
            });
        }
    }

    private static string ErrorCode(ApplicationError? error) =>
        string.IsNullOrWhiteSpace(error?.Code) ? "launch.failed" : error.Code;
}
