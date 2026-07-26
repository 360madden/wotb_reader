namespace WotBTreader.GameIntegration.Metadata;

internal sealed record ResolvedGameResource(
    string RelativePath,
    string AbsolutePath,
    string LayerId);

/// <summary>
/// Resolves a fixed relative resource name through local DLC layers before the
/// base install. It rejects rooted and parent-traversing paths before touching disk.
/// </summary>
internal sealed class ResourceOverlay
{
    private readonly string _baseRoot;
    private readonly string[] _dlcRoots;

    public ResourceOverlay(string baseRoot, IReadOnlyList<string> dlcRoots)
    {
        _baseRoot = NormalizeRoot(baseRoot);
        _dlcRoots = dlcRoots.Select(NormalizeRoot).ToArray();
    }

    public ResolvedGameResource? Resolve(string relativePath)
    {
        string safeRelativePath = NormalizeRelativePath(relativePath);
        for (int index = 0; index < _dlcRoots.Length; index++)
        {
            string? path = ResolveUnderRoot(_dlcRoots[index], safeRelativePath);
            if (path is not null && File.Exists(path))
            {
                return new ResolvedGameResource(safeRelativePath, path, $"dlc:{index}");
            }
        }

        string? basePath = ResolveUnderRoot(_baseRoot, safeRelativePath);
        return basePath is not null && File.Exists(basePath)
            ? new ResolvedGameResource(safeRelativePath, basePath, "base")
            : null;
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Game resource paths must be relative.", nameof(relativePath));
        }

        string normalized = relativePath.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        string[] segments = normalized.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 ||
            segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Game resource path traversal is not allowed.", nameof(relativePath));
        }

        return Path.Combine(segments);
    }

    private static string? ResolveUnderRoot(string root, string relativePath)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        string rootPrefix = string.Concat(root, Path.DirectorySeparatorChar);
        return candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }
}
