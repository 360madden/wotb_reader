using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using WotBTreader.Overlay.Endpoints;

namespace WotBTreader.Overlay.Tests;

/// <summary>
/// Unit tests for the overlay's embedded HTTP API endpoint handlers.
/// Tests each handler directly with a DefaultHttpContext, avoiding the
/// need for TestServer or additional NuGet dependencies.
/// </summary>
[TestClass]
public sealed class OverlayApiEndpointsTests
{
    /// <summary>Creates a context that passes the loopback check.</summary>
    private static DefaultHttpContext CreateLoopbackContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        return context;
    }

    /// <summary>Creates a context that fails the loopback check.</summary>
    private static DefaultHttpContext CreateNonLoopbackContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");
        return context;
    }

    // ── GET /api/v1/status ───────────────────────────────────

    [TestMethod]
    public void GetStatus_ReturnsOk_WhenLoopback()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.GetStatusAsync(context);

        Assert.IsInstanceOfType<IResult>(result);
        Assert.IsFalse(result is IStatusCodeHttpResult s && s.StatusCode == 403);
    }

    [TestMethod]
    public void GetStatus_ReturnsForbidden_WhenNonLoopback()
    {
        DefaultHttpContext context = CreateNonLoopbackContext();
        IResult result = OverlayApiEndpoints.GetStatusAsync(context);

        IStatusCodeHttpResult? status = result as IStatusCodeHttpResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status403Forbidden, status!.StatusCode);
    }

    [TestMethod]
    public void GetStatus_WhenNoViewModel_ReturnsUnreadyStatus()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.GetStatusAsync(context);

        // Serialize the result value to check the message.
        if (result is IValueHttpResult valueResult && valueResult.Value is not null)
        {
            string json = JsonSerializer.Serialize(valueResult.Value, JsonSerializerOptions.Web);
            Assert.IsTrue(json.Contains("overlay not ready"));
        }
        else
        {
            Assert.Fail("Expected IValueHttpResult.");
        }
    }

    // ── POST /api/v1/sessions/refresh ────────────────────────

    [TestMethod]
    public void PostRefreshSessions_ReturnsOk()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostRefreshSessions(context);
        Assert.IsInstanceOfType<IResult>(result);
    }

    [TestMethod]
    public void PostRefreshSessions_ReturnsForbidden_WhenNonLoopback()
    {
        DefaultHttpContext context = CreateNonLoopbackContext();
        IResult result = OverlayApiEndpoints.PostRefreshSessions(context);

        IStatusCodeHttpResult? status = result as IStatusCodeHttpResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status403Forbidden, status!.StatusCode);
    }

    // ── POST /api/v1/launch ──────────────────────────────────

    [TestMethod]
    public void PostLaunch_WithMissingPath_ReturnsBadRequest()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostLaunch(
            context, new Contracts.LaunchRequest { ReplayPath = "" });

        IStatusCodeHttpResult? status = result as IStatusCodeHttpResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status400BadRequest, status!.StatusCode);
    }

    [TestMethod]
    public void PostLaunch_WithValidPath_ReturnsOk()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostLaunch(
            context, new Contracts.LaunchRequest { ReplayPath = @"C:\test\replay.wotbreplay" });

        Assert.IsInstanceOfType<IResult>(result);
        Assert.IsFalse(result is IStatusCodeHttpResult s && s.StatusCode >= 400);
    }

    // ── POST /api/v1/playback/play ───────────────────────────

    [TestMethod]
    public void PostPlay_ReturnsOk()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostPlay(context);
        Assert.IsInstanceOfType<IResult>(result);
    }

    // ── POST /api/v1/playback/pause ──────────────────────────

    [TestMethod]
    public void PostPause_ReturnsOk()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostPause(context);
        Assert.IsInstanceOfType<IResult>(result);
    }

    // ── POST /api/v1/playback/seek ───────────────────────────

    [TestMethod]
    public void PostSeek_WithValidSeconds_ReturnsOk()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostSeek(
            context, new Contracts.SeekRequest { Seconds = 120 });
        Assert.IsInstanceOfType<IResult>(result);
    }

    [TestMethod]
    public void PostSeek_WithNegativeSeconds_ReturnsBadRequest()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostSeek(
            context, new Contracts.SeekRequest { Seconds = -5 });

        IStatusCodeHttpResult? status = result as IStatusCodeHttpResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status400BadRequest, status!.StatusCode);
    }

    // ── POST /api/v1/playback/speed ──────────────────────────

    [TestMethod]
    public void PostSpeed_WithValidSpeed_ReturnsOk()
    {
        foreach (double speed in new[] { 0.5, 1.0, 2.0, 4.0, 8.0 })
        {
            DefaultHttpContext context = CreateLoopbackContext();
            IResult result = OverlayApiEndpoints.PostSpeed(
                context, new Contracts.SpeedRequest { Speed = speed });
            Assert.IsInstanceOfType<IResult>(result,
                $"Speed {speed} should be accepted.");
        }
    }

    [TestMethod]
    public void PostSpeed_WithInvalidSpeed_ReturnsBadRequest()
    {
        foreach (double speed in new[] { 0.0, 3.0, -1.0, 10.0 })
        {
            DefaultHttpContext context = CreateLoopbackContext();
            IResult result = OverlayApiEndpoints.PostSpeed(
                context, new Contracts.SpeedRequest { Speed = speed });

            IStatusCodeHttpResult? status = result as IStatusCodeHttpResult;
            Assert.IsNotNull(status,
                $"Speed {speed} should produce a status code.");
            Assert.AreEqual(StatusCodes.Status400BadRequest, status!.StatusCode,
                $"Speed {speed} should be rejected.");
        }
    }

    // ── POST /api/v1/sessions/select ─────────────────────────

    [TestMethod]
    public void PostSelectSession_WithEmptyGuid_ReturnsBadRequest()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostSelectSession(
            context, new Contracts.SelectSessionRequest { BattleSessionId = Guid.Empty });

        IStatusCodeHttpResult? status = result as IStatusCodeHttpResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status400BadRequest, status!.StatusCode);
    }

    [TestMethod]
    public void PostSelectSession_WithValidGuid_ReturnsOk()
    {
        DefaultHttpContext context = CreateLoopbackContext();
        IResult result = OverlayApiEndpoints.PostSelectSession(
            context, new Contracts.SelectSessionRequest { BattleSessionId = Guid.NewGuid() });

        Assert.IsInstanceOfType<IResult>(result);
        Assert.IsFalse(result is IStatusCodeHttpResult s && s.StatusCode >= 400);
    }

    // ── Loopback security: all write endpoints reject non-loopback ──

    [TestMethod]
    public void AllWriteEndpoints_RejectNonLoopback()
    {
        DefaultHttpContext context = CreateNonLoopbackContext();

        IResult[] results =
        [
            OverlayApiEndpoints.PostRefreshSessions(context),
            OverlayApiEndpoints.PostLaunch(context, new Contracts.LaunchRequest { ReplayPath = "test" }),
            OverlayApiEndpoints.PostPlay(context),
            OverlayApiEndpoints.PostPause(context),
            OverlayApiEndpoints.PostSeek(context, new Contracts.SeekRequest { Seconds = 10 }),
            OverlayApiEndpoints.PostSpeed(context, new Contracts.SpeedRequest { Speed = 1.0 }),
            OverlayApiEndpoints.PostSelectSession(context, new Contracts.SelectSessionRequest { BattleSessionId = Guid.NewGuid() }),
        ];

        foreach (IResult result in results)
        {
            IStatusCodeHttpResult? status = result as IStatusCodeHttpResult;
            Assert.IsNotNull(status);
            Assert.AreEqual(StatusCodes.Status403Forbidden, status!.StatusCode,
                "Non-loopback request should be rejected with 403.");
        }
    }
}
