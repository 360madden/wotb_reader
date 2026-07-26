namespace WotBTreader.Storage.Sqlite;

/// <summary>Configures durable storage paths and SQLite contention behavior.</summary>
public sealed record SqliteStorageOptions
{
    /// <summary>
    /// Gets the private application-data root. Content, database, staging, and
    /// backup paths must resolve beneath this directory.
    /// </summary>
    public required string ApplicationDataRoot { get; init; }

    /// <summary>Gets an optional managed-content root beneath <see cref="ApplicationDataRoot"/>.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>Gets an optional database path beneath <see cref="ApplicationDataRoot"/>.</summary>
    public string? DatabasePath { get; init; }

    /// <summary>Gets the SQLite busy timeout used by every connection.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Creates the production default rooted in the current user's local application data.</summary>
    public static SqliteStorageOptions CreateDefault()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application-data directory is unavailable.");
        }

        return new SqliteStorageOptions
        {
            ApplicationDataRoot = Path.Combine(localApplicationData, "WotBTreader"),
        };
    }
}

/// <summary>Exposes resolved storage paths without leaking adapter implementation details.</summary>
public interface ISqliteStoragePathProvider
{
    string ApplicationDataRoot { get; }

    string ContentRoot { get; }

    string StagingRoot { get; }

    string BackupRoot { get; }

    string DatabasePath { get; }
}
