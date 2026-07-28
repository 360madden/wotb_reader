using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;

namespace WotBTreader.Storage.Sqlite;

internal sealed class SqliteStorageInitializer : IStorageInitializer
{
    private readonly SqliteStorageContext _context;
    private readonly ILogger<SqliteStorageInitializer> _logger;

    public SqliteStorageInitializer(
        SqliteStorageContext context,
        ILogger<SqliteStorageInitializer>? logger = null)
    {
        _context = context;
        _logger = logger ?? NullLogger<SqliteStorageInitializer>.Instance;
    }

    public async ValueTask<OperationResult<int>> InitializeAsync(CancellationToken cancellationToken)
    {
        await _context.MigrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool hadDatabase = File.Exists(_context.Paths.DatabasePath) &&
                new FileInfo(_context.Paths.DatabasePath).Length > 0;
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await SetWalModeAsync(connection, cancellationToken).ConfigureAwait(false);
            int currentVersion = await ReadCurrentVersionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            int latestVersion = SqliteMigrations.All[^1].Version;
            if (currentVersion > latestVersion)
            {
                return OperationResult.Failure<int>(
                    new ApplicationError(
                        "storage.schema_too_new",
                        "The storage schema is newer than this application supports."));
            }

            if (hadDatabase && currentVersion < latestVersion)
            {
                await CreateBackupAsync(connection, currentVersion, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (SqliteMigration migration in SqliteMigrations.All.Where(
                         item => item.Version > currentVersion))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // IMMEDIATE prevents independent CLI/web processes from both
                // deciding to apply the same migration from a stale snapshot.
                await using SqliteTransaction transaction =
                    connection.BeginTransaction(deferred: false);
                try
                {
                    int observedVersion = await ReadCurrentVersionAsync(
                        connection,
                        transaction,
                        cancellationToken).ConfigureAwait(false);
                    if (observedVersion >= migration.Version)
                    {
                        currentVersion = observedVersion;
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (observedVersion != migration.Version - 1)
                    {
                        throw new InvalidDataException(
                            "The storage migration history is not contiguous.");
                    }

                    StorageLog.MigrationApplying(_logger, migration.Version, migration.Name);
                    await using SqliteCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = migration.Sql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    await using SqliteCommand record = connection.CreateCommand();
                    record.Transaction = transaction;
                    record.CommandText =
                        """
                        INSERT INTO schema_migrations(version, name, applied_at_utc)
                        VALUES ($version, $name, $applied);
                        """;
                    record.Parameters.AddWithValue("$version", migration.Version);
                    record.Parameters.AddWithValue("$name", migration.Name);
                    record.Parameters.AddWithValue(
                        "$applied",
                        DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    currentVersion = migration.Version;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            return OperationResult.Success(currentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            TreaderDiagnostics.MigrationFailures.Add(1);
            StorageLog.MigrationSqliteFailed(
                _logger,
                exception.SqliteErrorCode);
            return OperationResult.Failure<int>(StorageErrors.From(exception));
        }
        catch (IOException exception)
        {
            TreaderDiagnostics.MigrationFailures.Add(1);
            StorageLog.MigrationIoFailed(_logger, exception.HResult);
            return OperationResult.Failure<int>(StorageErrors.Unavailable());
        }
        catch (InvalidDataException exception)
        {
            TreaderDiagnostics.MigrationFailures.Add(1);
            StorageLog.MigrationUnexpected(_logger, exception.HResult);
            return OperationResult.Failure<int>(StorageErrors.Internal());
        }
        finally
        {
            _context.MigrationGate.Release();
        }
    }

    private static ValueTask<int> ReadCurrentVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken) =>
        ReadCurrentVersionAsync(connection, transaction: null, cancellationToken);

    private static async ValueTask<int> ReadCurrentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand tableCheck = connection.CreateCommand();
        tableCheck.Transaction = transaction;
        tableCheck.CommandText =
            """
            SELECT count(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'schema_migrations';
            """;
        long exists = (long)(await tableCheck.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) ?? 0L);
        if (exists == 0)
        {
            return 0;
        }

        await using SqliteCommand version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(
            await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async ValueTask SetWalModeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                "wal",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SQLite did not enable WAL journal mode.");
        }
    }

    private async ValueTask CreateBackupAsync(
        SqliteConnection source,
        int currentVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string backupName = string.Create(
            CultureInfo.InvariantCulture,
            $"pre-migration-v{currentVersion}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.sqlite3");
        string backupPath = Path.Combine(_context.Paths.BackupRoot, backupName);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        await using SqliteConnection destination = new(builder.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        // BackupDatabase captures the main database consistently even when WAL contains pages.
        source.BackupDatabase(destination);
    }
}
