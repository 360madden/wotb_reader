using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace WotBTreader.Storage.Sqlite;

internal sealed class SqliteStorageContext
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MigrationGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SqliteStorageOptions _options;

    public SqliteStorageContext(SqliteStorageOptions options, ISqliteStoragePathProvider paths)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        MigrationGate = MigrationGates.GetOrAdd(
            Paths.DatabasePath,
            static _ => new SemaphoreSlim(1, 1));
    }

    public ISqliteStoragePathProvider Paths { get; }

    public SemaphoreSlim MigrationGate { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Paths.ApplicationDataRoot);
        Directory.CreateDirectory(Paths.ContentRoot);
        Directory.CreateDirectory(Paths.StagingRoot);
        Directory.CreateDirectory(Paths.BackupRoot);
        Directory.CreateDirectory(
            Path.GetDirectoryName(Paths.DatabasePath)
            ?? throw new InvalidOperationException("The database directory is unavailable."));
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureDirectories();
        int timeoutSeconds = checked((int)Math.Clamp(
            Math.Ceiling(_options.BusyTimeout.TotalSeconds),
            1,
            300));
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = Paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = timeoutSeconds,
        };

        SqliteConnection connection = new(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(
                connection,
                $"PRAGMA busy_timeout={timeoutSeconds * 1000};",
                cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(
                connection,
                "PRAGMA foreign_keys=ON;",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
