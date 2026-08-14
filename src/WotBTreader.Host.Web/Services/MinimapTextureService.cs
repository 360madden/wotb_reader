using SkiaSharp;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Dvpl;

namespace WotBTreader.Host.Web.Services;

/// <summary>
/// Resolves minimap texture images from the installed WoT Blitz game
/// by reading DVPL-encapsulated WebP files and converting them to PNG.
/// </summary>
internal sealed class MinimapTextureService : IDisposable
{
    private const int MaximumFolderLength = 128;

    private readonly IInstalledGameMetadataProvider _metadataProvider;
    private readonly IDvplReader _dvplReader;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private GameMetadataContext? _lastContext;

    public MinimapTextureService(
        IInstalledGameMetadataProvider metadataProvider,
        IDvplReader dvplReader)
    {
        ArgumentNullException.ThrowIfNull(metadataProvider);
        ArgumentNullException.ThrowIfNull(dvplReader);
        _metadataProvider = metadataProvider;
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

        ResolvedMinimap? resolved = await ResolveMinimapAsync(mapId, cancellationToken);
        if (resolved is null)
            return null;

        lock (_gate)
        {
            if (_cache.TryGetValue(resolved.Folder, out CacheEntry? entry) &&
                entry.ExecutableSha256 == resolved.Identity.ExecutableSha256)
            {
                return entry.PngBytes;
            }
        }

        byte[]? pngBytes = await LoadMinimapAsync(
            resolved.Folder,
            resolved.Identity,
            cancellationToken);
        if (pngBytes is not null)
        {
            lock (_gate)
            {
                _cache[resolved.Folder] = new CacheEntry(
                    pngBytes,
                    resolved.Identity.ExecutableSha256);
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

    private async ValueTask<ResolvedMinimap?> ResolveMinimapAsync(
        string mapId,
        CancellationToken cancellationToken)
    {
        GameMetadataContext? context;
        lock (_gate)
        {
            context = _lastContext;
        }

        if (context is null)
        {
            context = await ProbeContextAsync(cancellationToken);
            if (context is null)
                return null;
        }

        OperationResult<MapMetadata> mapResult = await _metadataProvider
            .ResolveMapAsync(context, mapId, cancellationToken)
            .ConfigureAwait(false);

        if (!mapResult.IsSuccess &&
            string.Equals(
                mapResult.Error?.Code,
                "game.metadata.context_stale",
                StringComparison.Ordinal))
        {
            context = await ProbeContextAsync(cancellationToken, replaceExisting: true);
            if (context is null)
                return null;

            mapResult = await _metadataProvider
                .ResolveMapAsync(context, mapId, cancellationToken)
                .ConfigureAwait(false);
        }

        string folderSource;
        if (mapResult.IsSuccess && mapResult.Value is MapMetadata metadata)
        {
            folderSource = metadata.SceneResourcePath ?? metadata.MapId;
        }
        else
        {
            // Preserve support for already-name-based callers, but never guess
            // a numeric arena mapping when installed metadata cannot resolve it.
            string trimmed = mapId.Trim();
            if (trimmed.Length == 0 || trimmed.All(IsAsciiDigit))
                return null;

            folderSource = trimmed;
        }

        string? folder = MapMinimapFolder(folderSource);
        return folder is null
            ? null
            : new ResolvedMinimap(folder, context.Identity);
    }

    private async ValueTask<GameMetadataContext?> ProbeContextAsync(
        CancellationToken cancellationToken,
        bool replaceExisting = false)
    {
        if (!replaceExisting)
        {
            lock (_gate)
            {
                if (_lastContext is not null)
                    return _lastContext;
            }
        }

        OperationResult<GameMetadataContext> result = await _metadataProvider
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            lock (_gate)
            {
                _lastContext = result.Value;
            }
        }

        return result.IsSuccess ? result.Value : null;
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
    /// Maps an installed scene path like
    /// <c>02_desert_train_dt/02_desert_train_dt.sc2</c> to the matching minimap
    /// folder. Numeric variants are preserved (for example,
    /// <c>desert_train_02</c>), and invalid path components fail closed.
    /// </summary>
    internal static string? MapMinimapFolder(string sceneResourcePath)
    {
        string normalized = sceneResourcePath.Trim().Replace('\\', '/');
        if (normalized.Length == 0)
            return null;

        int separator = normalized.LastIndexOf('/');
        ReadOnlySpan<char> span = normalized.AsSpan(separator + 1);
        int extension = span.LastIndexOf('.');
        if (extension > 0)
            span = span[..extension];

        // Skip leading digits and underscore: "02_"
        int start = 0;
        while (start < span.Length && (IsAsciiDigit(span[start]) || span[start] == '_'))
            start++;

        if (start >= span.Length)
            return null;

        // Strip localization/scene suffixes while preserving a numeric map
        // variant such as "_02" because the installed minimap may use it.
        int end = span.Length;
        while (end > start)
        {
            int lastUnderscore = span[..end].LastIndexOf('_');
            if (lastUnderscore <= start)
                break;

            ReadOnlySpan<char> suffix = span[(lastUnderscore + 1)..end];
            if (IsSceneSuffix(suffix) || IsQualifier(suffix))
            {
                end = lastUnderscore;
            }
            else
            {
                break;
            }
        }

        ReadOnlySpan<char> folder = span[start..end];
        if (folder.Length is 0 or > MaximumFolderLength)
            return null;

        foreach (char character in folder)
        {
            if (!IsAsciiLetter(character) &&
                !IsAsciiDigit(character) &&
                character is not '_' and not '-')
            {
                return null;
            }
        }

        return folder.ToString().ToLowerInvariant();
    }

    private static bool IsSceneSuffix(ReadOnlySpan<char> suffix)
    {
        if (suffix.Length is < 2 or > 3)
            return false;

        foreach (char character in suffix)
        {
            if (!IsAsciiLetter(character))
                return false;
        }

        return true;
    }

    private static bool IsQualifier(ReadOnlySpan<char> s)
    {
        return s is "night" or "old" or "day";
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsAsciiDigit(char character) =>
        character is >= '0' and <= '9';

    private sealed record ResolvedMinimap(string Folder, InstalledGameIdentity Identity);
    private sealed record CacheEntry(byte[] PngBytes, ContentHash ExecutableSha256);
}
