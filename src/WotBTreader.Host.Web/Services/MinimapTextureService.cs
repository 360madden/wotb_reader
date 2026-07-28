using SkiaSharp;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Dvpl;
using WotBTreader.GameIntegration.Discovery;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// Resolves minimap texture images from the installed WoT Blitz game
/// by reading DVPL-encapsulated WebP files and converting them to PNG.
/// </summary>
internal sealed class MinimapTextureService : IDisposable
{
    private readonly IGameInstallationDiscovery _discovery;
    private readonly IDvplReader _dvplReader;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private InstalledGameIdentity? _lastIdentity;

    public MinimapTextureService(
        IGameInstallationDiscovery discovery,
        IDvplReader dvplReader)
    {
        _discovery = discovery;
        _dvplReader = dvplReader;
    }

    /// <summary>
    /// Returns PNG bytes for the minimap texture of the given map ID,
    /// or null if the texture is unavailable.
    /// </summary>
    public async ValueTask<byte[]?> GetMinimapPngAsync(
        string mapId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mapId))
            return null;

        string folder = MapMinimapFolder(mapId);

        // Check cache, revalidating when the game identity changes.
        InstalledGameIdentity? identity = await ResolveIdentityAsync(cancellationToken);
        lock (_gate)
        {
            if (_cache.TryGetValue(folder, out CacheEntry? entry) &&
                identity is not null &&
                entry.ExecutableSha256 == identity.ExecutableSha256)
            {
                return entry.PngBytes;
            }
        }

        byte[]? pngBytes = await LoadMinimapAsync(folder, identity, cancellationToken);
        if (pngBytes is not null && identity is not null)
        {
            lock (_gate)
            {
                _cache[folder] = new CacheEntry(pngBytes, identity.ExecutableSha256);
            }
        }

        return pngBytes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private async ValueTask<InstalledGameIdentity?> ResolveIdentityAsync(CancellationToken cancellationToken)
    {
        if (_lastIdentity is not null)
            return _lastIdentity;

        OperationResult<InstalledGameIdentity> result =
            await _discovery.DiscoverAsync(cancellationToken);
        if (result.IsSuccess)
        {
            _lastIdentity = result.Value;
        }

        return _lastIdentity;
    }

    private async ValueTask<byte[]?> LoadMinimapAsync(
        string folder,
        InstalledGameIdentity? identity,
        CancellationToken cancellationToken)
    {
        if (identity is null)
            return null;

        string minimapRoot = Path.Combine(
            identity.ResourceRoot,
            "Gfx", "UI", "BattleScreenHUD", "minimap");

        string dvplPath = Path.Combine(minimapRoot, folder, "MiniMapSmall.packed.webp.dvpl");
        if (!File.Exists(dvplPath))
            return null;

        OperationResult<DvplPayload> dvplResult =
            await _dvplReader.ReadAsync(dvplPath, cancellationToken);
        if (!dvplResult.IsSuccess || dvplResult.Value is null)
            return null;

        byte[] webpBytes = dvplResult.Value.Data.ToArray();

        try
        {
            using SKBitmap? bitmap = SKBitmap.Decode(webpBytes);
            if (bitmap is null)
                return null;

            using SKData pngData = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            return pngData.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MinimapTexture] Decode failed for {folder}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Maps a map ID like "02_desert_train_dt" to a minimap folder name like "desert_train".
    /// Strips the leading numeric prefix and the trailing 2-letter suffix. Falls back
    /// to trying progressively shorter suffixes for maps with non-standard naming.
    /// </summary>
    internal static string MapMinimapFolder(string mapId)
    {
        ReadOnlySpan<char> span = mapId.AsSpan().Trim();

        // Skip leading digits and underscore: "02_"
        int start = 0;
        while (start < span.Length && (char.IsDigit(span[start]) || span[start] == '_'))
            start++;

        // Try stripping trailing underscore-separated segments until we hit a
        // reasonable name. Standard maps have a 2-char suffix ("_dt", "_ma"),
        // but some have longer suffixes ("_night", "_old") or extra segments.
        int end = span.Length;

        // Try: strip last underscore-separated segment if it looks like a suffix
        // (short, non-numeric, or a known qualifier).
        while (end > start)
        {
            // Find last underscore before end.
            int lastUnderscore = span[..end].LastIndexOf('_');
            if (lastUnderscore <= start)
                break;

            ReadOnlySpan<char> suffix = span[(lastUnderscore + 1)..end];
            // If the suffix looks like a short code (2-3 chars), numeric variant ("01"),
            // or a known qualifier ("night", "old"), strip it and try again.
            if (suffix.Length <= 3 || IsAllDigits(suffix) || IsQualifier(suffix))
            {
                end = lastUnderscore;
            }
            else
            {
                break;
            }
        }

        if (start >= end)
            return new string(span).ToLowerInvariant();

        return span[start..end].ToString().ToLowerInvariant();
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
            if (!char.IsDigit(c)) return false;
        return s.Length > 0;
    }

    private static bool IsQualifier(ReadOnlySpan<char> s)
    {
        return s is "night" or "old" or "day";
    }

    private sealed record CacheEntry(byte[] PngBytes, ContentHash ExecutableSha256);
}
