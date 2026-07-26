using Microsoft.Data.Sqlite;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// An isolated application-data root that is removed when the test finishes.
/// </summary>
internal sealed class TemporaryDataRoot : IDisposable
{
    public TemporaryDataRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"wotbtreader-cli-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        // Pooled SQLite connections keep the database file handle open after the
        // owning host is disposed, so the pool must be drained first.
        SqliteConnection.ClearAllPools();
        for (int attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(20 * (attempt + 1)));
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
