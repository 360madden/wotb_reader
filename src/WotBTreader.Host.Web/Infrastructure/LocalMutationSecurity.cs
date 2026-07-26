using System.Security.Cryptography;
using System.Text;

namespace WotBTreader.Host.Web.Infrastructure;

internal sealed class LocalMutationSecurity(TimeProvider timeProvider)
{
    public const string CapabilityHeaderName = "X-WotBTreader-Capability";
    public const string AntiforgeryHeaderName = "X-WotBTreader-Antiforgery";

    private readonly Lock gate = new();
    private CapabilityLease current = CapabilityLease.Create(timeProvider);

    public CapabilityLease Snapshot()
    {
        lock (gate)
        {
            if (current.ExpiresAtUtc <= timeProvider.GetUtcNow())
            {
                current = CapabilityLease.Create(timeProvider);
            }

            return current;
        }
    }

    public CapabilityLease Rotate()
    {
        lock (gate)
        {
            current = CapabilityLease.Create(timeProvider);
            return current;
        }
    }

    public bool Validate(string supplied)
    {
        var snapshot = Snapshot();
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(snapshot.Token);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

internal sealed record CapabilityLease(string Token, DateTimeOffset ExpiresAtUtc)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static CapabilityLease Create(TimeProvider timeProvider)
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return new CapabilityLease(
            Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'),
            timeProvider.GetUtcNow().Add(Lifetime));
    }
}
