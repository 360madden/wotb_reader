using System.IO;
using System.Text.Json;

namespace WotBTreader.Overlay.Discovery;

/// <summary>Outcome of probing for the local web host's rendezvous record.</summary>
public enum RendezvousStatus
{
    Found,
    NotFound,
    Stale,
    Invalid,
}

/// <summary>Result of a rendezvous probe. <see cref="Reason"/> is user-safe and never contains paths, tokens, or file contents.</summary>
public sealed record RendezvousResult(RendezvousStatus Status, RendezvousRecord? Record, string Reason);

/// <summary>
/// Locates the running web host via its rendezvous JSON file under LocalApplicationData.
/// Validates schema version, loopback-only base address, and expiry.
/// </summary>
public sealed class RendezvousLocator
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerOptions.Web;

    private readonly TimeProvider _timeProvider;
    private readonly string _rendezvousPath;

    public RendezvousLocator(TimeProvider? timeProvider = null, string? rendezvousPath = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rendezvousPath = rendezvousPath ?? ResolveDefaultPath();
    }

    private static string ResolveDefaultPath()
    {
        // Rendezvous is always under %LocalAppData% so the overlay and web host
        // agree on the path regardless of custom data roots. This avoids ACL
        // hazards when the custom root is shared, removable, or admin-owned.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WotBTreader",
            "rendezvous",
            "web.json");
    }

    public RendezvousResult Locate()
    {
        if (!File.Exists(_rendezvousPath))
        {
            return new RendezvousResult(RendezvousStatus.NotFound, null, "host not running");
        }

        RendezvousRecord? record;
        try
        {
            string json = File.ReadAllText(_rendezvousPath);
            record = JsonSerializer.Deserialize<RendezvousRecord>(json, SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new RendezvousResult(RendezvousStatus.Invalid, null, "record unreadable or malformed");
        }

        if (record is null)
        {
            return new RendezvousResult(RendezvousStatus.Invalid, null, "record unreadable or malformed");
        }

        if (!string.Equals(record.SchemaVersion, "1.0", StringComparison.Ordinal))
        {
            return new RendezvousResult(RendezvousStatus.Invalid, null, "unsupported schema version");
        }

        if (!IsLoopbackUri(record.BaseUri))
        {
            return new RendezvousResult(RendezvousStatus.Invalid, null, "non-loopback base address rejected");
        }

        if (record.ExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return new RendezvousResult(RendezvousStatus.Stale, null, "record expired");
        }

        return new RendezvousResult(RendezvousStatus.Found, record, string.Empty);
    }

    private static bool IsLoopbackUri(string baseUri)
    {
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            || uri.Host.Equals("[::1]", StringComparison.Ordinal);
    }
}
