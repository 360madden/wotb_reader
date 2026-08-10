using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class MigrationTests
{
    [TestMethod]
    public async Task InitializeCreatesNumberedSchemaWithRequiredPragmas()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync(initialize: false);
        OperationResult<int>[] results = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => scope.Initializer.InitializeAsync(CancellationToken.None).AsTask()));

        Assert.IsTrue(results.All(result => result.IsSuccess));
        Assert.IsTrue(results.All(result => result.Value == 6));

        await using SqliteConnection connection =
            await scope.Context.OpenConnectionAsync(CancellationToken.None);
        Assert.AreEqual(6L, await ScalarAsync(connection, "SELECT count(*) FROM schema_migrations;"));
        Assert.AreEqual("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
        Assert.AreEqual(1L, await ScalarAsync(connection, "PRAGMA foreign_keys;"));
        long busyTimeout =
            (long)(await ScalarAsync(connection, "PRAGMA busy_timeout;"))!;
        Assert.IsGreaterThanOrEqualTo(1_000L, busyTimeout);
    }

    [TestMethod]
    public async Task PendingMigrationCreatesConsistentPreMigrationBackup()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifactFixture fixture = await SourceArtifactFixture.CreateAsync(scope);

        await using (SqliteConnection connection =
                     await scope.Context.OpenConnectionAsync(CancellationToken.None))
        {
            await ExecuteAsync(
                connection,
                """
                DROP TABLE replay_clock_segments;
                DELETE FROM schema_migrations WHERE version >= 2;
                """);
        }

        OperationResult<int> result =
            await scope.Initializer.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(6, StorageTestScope.Success(result));

        string[] backups = Directory.GetFiles(scope.Paths.BackupRoot, "*.sqlite3");
        Assert.HasCount(1, backups);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = backups[0],
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using SqliteConnection backup = new(builder.ToString());
        await backup.OpenAsync();
        Assert.AreEqual(
            fixture.Artifact.Id.ToString(),
            await ScalarAsync(backup, "SELECT id FROM source_artifacts LIMIT 1;"));
        Assert.AreEqual(1L, await ScalarAsync(backup, "SELECT MAX(version) FROM schema_migrations;"));
    }

    [TestMethod]
    public void PathsOutsideApplicationRootAreRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "WotBTreader.Storage.Tests", "root");
        string outside = Path.Combine(Path.GetTempPath(), "outside.sqlite3");
        SqliteStorageOptions options = new()
        {
            ApplicationDataRoot = root,
            DatabasePath = outside,
        };

        Assert.Throws<ArgumentException>(() => _ = new SqliteStoragePaths(options));
    }

    private static async ValueTask<object?> ScalarAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SourceArtifactFixture(WotBTreader.Core.SourceArtifact Artifact)
    {
        public static async ValueTask<SourceArtifactFixture> CreateAsync(StorageTestScope scope) =>
            new(await scope.ImportAsync("backup-source.wotbreplay", [1, 2, 3, 4]));
    }
}
