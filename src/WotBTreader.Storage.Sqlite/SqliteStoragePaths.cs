namespace WotBTreader.Storage.Sqlite;

internal sealed class SqliteStoragePaths : ISqliteStoragePathProvider
{
    public SqliteStoragePaths(SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApplicationDataRoot))
        {
            throw new ArgumentException("An application-data root is required.", nameof(options));
        }

        ApplicationDataRoot = Path.GetFullPath(options.ApplicationDataRoot);
        string volumeRoot = Path.GetPathRoot(ApplicationDataRoot)
            ?? throw new ArgumentException("The application-data root has no volume.", nameof(options));
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(ApplicationDataRoot),
                Path.TrimEndingDirectorySeparator(volumeRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A volume root cannot be used as application data.", nameof(options));
        }

        ContentRoot = ResolveDescendant(options.ContentRoot, "content", nameof(options.ContentRoot));
        StagingRoot = ResolveDescendant(null, "staging", "StagingRoot");
        BackupRoot = ResolveDescendant(null, "backups", "BackupRoot");
        DatabasePath = ResolveDescendant(options.DatabasePath, "data/wotbtreader.sqlite3", nameof(options.DatabasePath));
    }

    public string ApplicationDataRoot { get; }

    public string ContentRoot { get; }

    public string StagingRoot { get; }

    public string BackupRoot { get; }

    public string DatabasePath { get; }

    private string ResolveDescendant(string? configuredPath, string defaultRelativePath, string parameterName)
    {
        string candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(ApplicationDataRoot, defaultRelativePath)
            : Path.IsPathFullyQualified(configuredPath)
                ? configuredPath
                : Path.Combine(ApplicationDataRoot, configuredPath);
        string resolved = Path.GetFullPath(candidate);
        string relative = Path.GetRelativePath(ApplicationDataRoot, resolved);

        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Storage paths must resolve beneath the application-data root.",
                parameterName);
        }

        return resolved;
    }
}
