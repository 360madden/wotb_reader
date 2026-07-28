using WotBTreader.Host.Web.Contracts;
using WotBTreader.Host.Web.Services;

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
        group.MapGet("/state", GetGameState);
        group.MapGet("/memory", GetGameMemory);
        group.MapPost("/launch", LaunchGame);
        return builder;
    }

    internal static IResult GetGameState(GameStateService gameState)
    {
        ArgumentNullException.ThrowIfNull(gameState);
        GameStateResponse state = gameState.GetState();
        return Results.Ok(state);
    }

    internal static IResult GetGameMemory(GameStateService gameState)
    {
        ArgumentNullException.ThrowIfNull(gameState);
        GameMemoryResponse memory = gameState.GetMemory();
        return Results.Ok(memory);
    }

    internal static IResult LaunchGame(GameStateService gameState, GameLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(gameState);
        ArgumentNullException.ThrowIfNull(request);

        GameLaunchResponse result = gameState.LaunchReplay(request.ReplayPath);
        return result.Success
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }
}
