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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }
}
