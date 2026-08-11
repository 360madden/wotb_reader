using System.Net.Http;
using System.Text.Json;
using WotBTreader.ApiContracts;

namespace WotBTreader.Overlay.Services;

/// <summary>
/// Loopback API client. GET requests are unauthenticated; POST/PUT/DELETE
/// requests include the loopback capability header when a token is provided.
/// Never logs or surfaces any capability token.
/// </summary>
public sealed class TreaderApiClient : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = JsonSerializerOptions.Web;

    private readonly HttpClient _httpClient;
    private readonly string? _capability;

    /// <summary>
    /// Creates a client for the loopback API.
    /// </summary>
    /// <param name="baseUri">Must be an http(s) loopback address (localhost, 127.0.0.1, or [::1]).</param>
    /// <param name="handler">Optional HttpMessageHandler for test injection.</param>
    /// <param name="capability">
    /// Optional loopback capability token from the rendezvous record.
    /// When non-null, it is sent as the X-WotBTreader-Capability header on
    /// mutation requests (POST/PUT/DELETE). Never included on GET requests.
    /// </param>
    /// <exception cref="ArgumentException">Thrown when baseUri is not a loopback web URI.</exception>
    public TreaderApiClient(Uri baseUri, HttpMessageHandler? handler = null, string? capability = null)
    {
        if (!IsLoopbackWebUri(baseUri))
        {
            throw new ArgumentException("Base URI must be an http(s) loopback address.", nameof(baseUri));
        }

        _httpClient = handler is not null
            ? new HttpClient(handler) { BaseAddress = baseUri }
            : new HttpClient { BaseAddress = baseUri };
        _capability = capability;
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

    /// <summary>
    /// Fetches one overlay frame (viewpoint camera + tanks projected to
    /// viewport pixels) at a replay time for the W2S HUD.
    /// </summary>
    /// <param name="battleSessionId">The session to render.</param>
    /// <param name="replayTimeSeconds">Replay time to build the frame at.</param>
    /// <param name="verticalFovDegrees">Vertical field of view (default 90).</param>
    /// <param name="viewportWidth">Viewport width in pixels (default 1920).</param>
    /// <param name="viewportHeight">Viewport height in pixels (default 1080).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projected frame, or null if deserialization fails.</returns>
    public async Task<OverlayFrameResponse?> GetOverlayFrameAsync(
        Guid battleSessionId,
        double replayTimeSeconds,
        double verticalFovDegrees = 90.0,
        double viewportWidth = 1920.0,
        double viewportHeight = 1080.0,
        CancellationToken cancellationToken = default)
    {
        string json = await _httpClient.GetStringAsync(
            $"api/v1/sessions/{battleSessionId:D}/frame"
            + $"?timeSeconds={replayTimeSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&fov={verticalFovDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&width={viewportWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&height={viewportHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken);
        return JsonSerializer.Deserialize<OverlayFrameResponse>(json, SerializerOptions);
    }

    /// <summary>
    /// Fetches one composed LIVE frame (gated memory read: roster -> batch
    /// ring + entity-base records -> CAM-001 camera pose) projected to
    /// viewport pixels — the LiveFrameSource seam. Same
    /// <see cref="OverlayFrameResponse"/> shape as the replay frame, so the
    /// HUD renders live nameplates without touching its render path. HP
    /// bars/readouts carry the L1 entity-base values when the read
    /// resolved; pips/kills/scoreboard are absent.
    /// </summary>
    /// <param name="verticalFovDegrees">Vertical field of view (default 90).</param>
    /// <param name="viewportWidth">Viewport width in pixels (default 1920).</param>
    /// <param name="viewportHeight">Viewport height in pixels (default 1080).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projected live frame, or null on a non-success response.</returns>
    public async Task<OverlayFrameResponse?> GetLiveFrameAsync(
        double verticalFovDegrees = 90.0,
        double viewportWidth = 1920.0,
        double viewportHeight = 1080.0,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            "api/v1/live/frame"
            + $"?fov={verticalFovDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&width={viewportWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&height={viewportHeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}",
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<OverlayFrameResponse>(json, SerializerOptions);
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

    /// <summary>
    /// Fetches the minimap texture PNG for the given map ID.
    /// Returns the raw PNG bytes, or null if unavailable.
    /// </summary>
    public async Task<byte[]?> GetMinimapPngAsync(string mapId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mapId))
            return null;

        try
        {
            return await _httpClient.GetByteArrayAsync(
                $"api/v1/maps/{Uri.EscapeDataString(mapId)}/minimap",
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Gets the current game and replay lifecycle state from the host.</summary>
    public async Task<GameStateResponse?> GetGameStateAsync(CancellationToken cancellationToken = default)
    {
        string json = await _httpClient.GetStringAsync(
            "api/v1/game/state",
            cancellationToken);
        return JsonSerializer.Deserialize<GameStateResponse>(json, SerializerOptions);
    }

    /// <summary>Requests the host to launch a managed replay artifact through the installed game.</summary>
    public async Task<GameLaunchResponse?> LaunchGameAsync(string sourceArtifactId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "api/v1/game/launch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new GameLaunchRequest { SourceArtifactId = sourceArtifactId }, SerializerOptions),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        AddCapabilityHeader(request);

        HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<GameLaunchResponse>(json, SerializerOptions);
    }

    private void AddCapabilityHeader(HttpRequestMessage request)
    {
        if (_capability is not null
            && request.Method != HttpMethod.Get
            && request.Method != HttpMethod.Head
            && request.Method != HttpMethod.Options)
        {
            request.Headers.TryAddWithoutValidation(
                "X-WotBTreader-Capability", _capability);
        }
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
