namespace WotBTreader.Overlay.Discovery;

/// <summary>
/// Rendezvous record written by the local web host so the overlay can find it.
/// Wire format is camelCase JSON; deserialize with <see cref="System.Text.Json.JsonSerializerOptions.Web"/>.
/// </summary>
public sealed record RendezvousRecord(
    string SchemaVersion,
    Guid InstanceId,
    int ProcessId,
    string BaseUri,
    string Capability,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
