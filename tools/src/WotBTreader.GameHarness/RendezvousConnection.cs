namespace WotBTreader.GameHarness;

/// <summary>
/// Loopback host address plus the short-lived capability required for unsafe API calls.
/// </summary>
internal sealed record RendezvousConnection(string BaseUri, string Capability);
