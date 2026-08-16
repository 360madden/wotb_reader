using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
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
/// Validates schema version, loopback-only base address, expiry, that the record is a
/// regular file (reparse points are rejected), and that it is owned by the current user.
/// </summary>
public sealed class RendezvousLocator
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerOptions.Web;

    private readonly TimeProvider _timeProvider;
    private readonly string _rendezvousPath;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<string, bool> _isReparsePoint;
    private readonly Func<string, bool> _isOwnerOnly;

    public RendezvousLocator(
        TimeProvider? timeProvider = null,
        string? rendezvousPath = null,
        Func<int, bool>? isProcessAlive = null,
        Func<string, bool>? isReparsePoint = null,
        Func<string, bool>? isOwnerOnly = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rendezvousPath = rendezvousPath ?? ResolveDefaultPath();
        _isProcessAlive = isProcessAlive ?? DefaultIsProcessAlive;
        _isReparsePoint = isReparsePoint ?? DefaultIsReparsePoint;
        _isOwnerOnly = isOwnerOnly ?? DefaultIsOwnerOnly;
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

        if (_isReparsePoint(_rendezvousPath))
        {
            return new RendezvousResult(
                RendezvousStatus.Invalid,
                null,
                "record is not a regular file");
        }

        if (!_isOwnerOnly(_rendezvousPath))
        {
            return new RendezvousResult(
                RendezvousStatus.Invalid,
                null,
                "record is not owned by the current user");
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

        // Verify the publishing process is still alive. A PID from a dead host
        // means the record is stale regardless of expiry time.
        if (!_isProcessAlive(record.ProcessId))
        {
            return new RendezvousResult(RendezvousStatus.Stale, null, "host process exited");
        }

        return new RendezvousResult(RendezvousStatus.Found, record, string.Empty);
    }

    private static bool DefaultIsProcessAlive(int processId)
    {
        try
        {
            using Process? process = Process.GetProcessById(processId);
            return process is not null && !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool DefaultIsReparsePoint(string path)
    {
        try
        {
            // LinkTarget is null for a regular file and non-null for a
            // symlink/junction (a reparse point). The publisher already
            // refuses to write a reparse-point record; the reader mirrors
            // that so a substituted path cannot redirect the client to an
            // arbitrary target.
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Fail closed: if the link target cannot be established, the
            // record is untrusted.
            return true;
        }
    }

    private static bool DefaultIsOwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Non-Windows access is already gated by the owner-only directory
            // mode; there is no DACL owner to compare against.
            return true;
        }

        try
        {
            FileSecurity security = new FileInfo(path).GetAccessControl(
                AccessControlSections.Owner);
            SecurityIdentifier? current = WindowsIdentity.GetCurrent().User;
            return current is not null &&
                current.Equals(security.GetOwner(typeof(SecurityIdentifier)));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Fail closed: an unreadable owner means the record is untrusted.
            return false;
        }
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
