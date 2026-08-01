using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using WotBTreader.Host.Web.Infrastructure;

namespace WotBTreader.Host.Web.Tests;

/// <summary>
/// The capability lease is the inner half of the local trust boundary: it stops
/// another local process that can reach loopback from issuing mutations.
/// </summary>
[TestClass]
public sealed class LocalMutationSecurityTests
{
    [TestMethod]
    public void CurrentTokenValidates()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);

        CapabilityLease lease = security.Snapshot();

        Assert.IsTrue(security.Validate(lease.Token));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-the-token")]
    public void AbsentOrWrongTokenIsRejected(string supplied)
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);

        Assert.IsFalse(security.Validate(supplied));
    }

    [TestMethod]
    public void TokenIsUnguessableAndUrlSafe()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);

        string token = security.Snapshot().Token;

        // 32 random bytes rendered as unpadded base64url.
        Assert.HasCount(43, token.ToCharArray());
        Assert.IsTrue(
            token.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'),
            "The capability must survive URL and header transport unescaped.");
    }

    [TestMethod]
    public async Task MutationMiddlewareRejectsUnsafeRequestWithoutCapability()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);
        DefaultHttpContext context = CreateApiContext(HttpMethods.Post);
        bool nextCalled = false;
        MutationProtectionMiddleware middleware = new(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, security, new StubAntiforgery());

        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.IsFalse(nextCalled);
    }

    [TestMethod]
    public async Task MutationMiddlewareAcceptsCurrentCapabilityHeader()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);
        DefaultHttpContext context = CreateApiContext(HttpMethods.Post);
        context.Request.Headers[LocalMutationSecurity.CapabilityHeaderName] =
            security.Snapshot().Token;
        bool nextCalled = false;
        MutationProtectionMiddleware middleware = new(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, security, new StubAntiforgery());

        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.IsTrue(nextCalled);
    }

    [TestMethod]
    public async Task MutationMiddlewarePassesReadOnlyRequestsWithoutCapability()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);
        DefaultHttpContext context = CreateApiContext(HttpMethods.Get);
        bool nextCalled = false;
        MutationProtectionMiddleware middleware = new(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, security, new StubAntiforgery());

        Assert.IsTrue(nextCalled);
    }

    [TestMethod]
    public void RotationInvalidatesThePreviousToken()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);
        string previous = security.Snapshot().Token;

        string rotated = security.Rotate().Token;

        Assert.AreNotEqual(previous, rotated);
        Assert.IsFalse(security.Validate(previous));
        Assert.IsTrue(security.Validate(rotated));
    }

    [TestMethod]
    public void ExpiredLeaseIsReplacedAndTheStaleTokenIsRejected()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);
        CapabilityLease original = security.Snapshot();

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.IsFalse(
            security.Validate(original.Token),
            "A lease past its expiry must never validate.");
        Assert.IsTrue(security.Snapshot().ExpiresAtUtc > time.GetUtcNow());
    }

    [TestMethod]
    public void LeaseLifetimeIsShort()
    {
        FixedTimeProvider time = new(DateTimeOffset.UnixEpoch);
        LocalMutationSecurity security = new(time);

        CapabilityLease lease = security.Snapshot();

        Assert.IsTrue(
            lease.ExpiresAtUtc - time.GetUtcNow() <= TimeSpan.FromMinutes(5),
            "A local capability must remain short-lived.");
    }

    private static DefaultHttpContext CreateApiContext(string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = "/api/v1/game/discover/snapshot";
        context.Response.StatusCode = StatusCodes.Status200OK;
        return context;
    }

    private sealed class StubAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            new("request", "cookie", "header", "form");

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            GetTokens(httpContext);

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(true);

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }
}
