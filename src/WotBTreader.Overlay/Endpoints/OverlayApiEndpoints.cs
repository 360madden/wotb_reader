using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WotBTreader.ApiContracts;
using WotBTreader.Overlay.Services;

namespace WotBTreader.Overlay.Endpoints;

/// <summary>
/// Embedded HTTP automation API for the WPF overlay.
/// All endpoints are loopback-only and provide query/control surfaces for
/// scripting the overlay from curl, basher, or other automation tools.
/// </summary>
internal static class OverlayApiEndpoints
{
    public static IEndpointRouteBuilder MapOverlayApi(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder group = builder.MapGroup("/api/v1");

        // Read
        group.MapGet("/status", GetStatusAsync);

        // Mutations — POST for scripting convenience (curl -X POST)
        group.MapPost("/sessions/refresh", PostRefreshSessions);
        group.MapPost("/launch", PostLaunch);
        group.MapPost("/playback/play", PostPlay);
        group.MapPost("/playback/pause", PostPause);
        group.MapPost("/playback/seek", PostSeek);
        group.MapPost("/playback/speed", PostSpeed);
        group.MapPost("/sessions/select", PostSelectSession);

        return builder;
    }

    internal static IResult GetStatusAsync(HttpContext context)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        OverlayStatusResponse status = OverlayApiState.Instance.GetStatus();
        return Results.Ok(status);
    }

    internal static IResult PostRefreshSessions(HttpContext context)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        OverlayApiState.Instance.PostRefreshSessions();
        return Results.Ok(new { message = "refresh dispatched" });
    }

    internal static IResult PostLaunch(HttpContext context, LaunchRequest request)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (string.IsNullOrWhiteSpace(request.ReplayPath))
        {
            return Results.BadRequest(new LaunchResponse
            {
                Success = false,
                Message = "replayPath is required and must not be empty.",
            });
        }

        OverlayApiState.Instance.PostLaunch(request.ReplayPath);
        return Results.Ok(new LaunchResponse
        {
            Success = true,
            Message = "launch dispatched",
        });
    }

    internal static IResult PostPlay(HttpContext context)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        OverlayApiState.Instance.PostPlay();
        return Results.Ok(new { message = "play dispatched" });
    }

    internal static IResult PostPause(HttpContext context)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        OverlayApiState.Instance.PostPause();
        return Results.Ok(new { message = "pause dispatched" });
    }

    internal static IResult PostSeek(HttpContext context, SeekRequest request)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.Seconds < 0)
        {
            return Results.BadRequest(new { error = "seconds must be zero or greater." });
        }

        OverlayApiState.Instance.PostSeek(request.Seconds);
        return Results.Ok(new { message = "seek dispatched", seconds = request.Seconds });
    }

    internal static IResult PostSpeed(HttpContext context, SpeedRequest request)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.Speed is not (0.5 or 1.0 or 2.0 or 4.0 or 8.0))
        {
            return Results.BadRequest(new { error = "speed must be 0.5, 1, 2, 4, or 8." });
        }

        OverlayApiState.Instance.PostSetSpeed(request.Speed);
        return Results.Ok(new { message = "speed dispatched", speed = request.Speed });
    }

    internal static IResult PostSelectSession(HttpContext context, SelectSessionRequest request)
    {
        if (!IsLoopback(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (request.BattleSessionId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "battleSessionId must not be empty." });
        }

        OverlayApiState.Instance.PostSelectSession(request.BattleSessionId);
        return Results.Ok(new { message = "select dispatched", battleSessionId = request.BattleSessionId });
    }

    private static bool IsLoopback(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        return remote is not null && IPAddress.IsLoopback(remote);
    }
}
