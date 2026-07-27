using System.Net.Http;
using System.Text.Json;
using WotBTreader.Overlay.Contracts;

namespace WotBTreader.Overlay.Services;

/// <summary>
/// Read-only client for the loopback read API. Sends no auth headers and never
/// logs or surfaces any capability token.
/// </summary>
public sealed class TreaderApiClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerOptions.Web;

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates a read-only client for the loopback API.
    /// </summary>
    /// <param name="baseUri">Must be an http(s) loopback address (localhost, 127.0.0.1, or [::1]).</param>
    /// <param name="handler">Optional HttpMessageHandler for test injection.</param>
    /// <exception cref="ArgumentException">Thrown when baseUri is not a loopback web URI.</exception>
    public TreaderApiClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        if (!IsLoopbackWebUri(baseUri))
        {
            throw new ArgumentException("Base URI must be an http(s) loopback address.", nameof(baseUri));
        }

        _httpClient = handler is not null
            ? new HttpClient(handler) { BaseAddress = baseUri }
            : new HttpClient { BaseAddress = baseUri };
    }

    /// <summary>Fetches a paginated list of session summaries.</summary>
    /// <param name="offset">Zero-based page offset.</param>
    /// <param name="limit">Maximum items to return (1–200).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page response, or null if deserialization fails.</returns>
    public async Task<SessionPageResponse?> GetSessionsAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        string json = await _httpClient.GetStringAsync(
            $"api/v1/sessions?offset={offset}&limit={limit}",
            cancellationToken);
        return JsonSerializer.Deserialize<SessionPageResponse>(json, SerializerOptions);
    }

    /// <summary>Fetches the full detail projection for a single battle session.</summary>
    /// <param name="battleSessionId">The session to load.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detail response, or null if deserialization fails or the session is not found.</returns>
    public async Task<SessionDetailResponse?> GetSessionDetailAsync(Guid battleSessionId, CancellationToken cancellationToken = default)
    {
        string json = await _httpClient.GetStringAsync(
            $"api/v1/sessions/{battleSessionId:D}",
            cancellationToken);
        return JsonSerializer.Deserialize<SessionDetailResponse>(json, SerializerOptions);
    }

    /// <summary>Fetches the complete map boundary catalogue for minimap projection.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of map boundaries; empty list on failure.</returns>
    public async Task<IReadOnlyList<MapBoundaryResponse>> GetMapBoundariesAsync(CancellationToken cancellationToken = default)
    {
        string json = await _httpClient.GetStringAsync(
            "api/v1/maps/boundaries",
            cancellationToken);
        return JsonSerializer.Deserialize<IReadOnlyList<MapBoundaryResponse>>(json, SerializerOptions) ?? [];
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool IsLoopbackWebUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        bool loopbackHost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            || uri.Host.Equals("[::1]", StringComparison.Ordinal);
        bool webScheme = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        return loopbackHost && webScheme;
    }
}
