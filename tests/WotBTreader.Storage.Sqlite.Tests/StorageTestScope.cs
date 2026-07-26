using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite.Tests;

internal sealed class StorageTestScope : IAsyncDisposable
{
    private StorageTestScope(string root)
    {
        Root = root;
        Options = new SqliteStorageOptions
        {
            ApplicationDataRoot = root,
            BusyTimeout = TimeSpan.FromSeconds(15),
        };
        Paths = new SqliteStoragePaths(Options);
        Context = new SqliteStorageContext(Options, Paths);
        Initializer = new SqliteStorageInitializer(Context);
        ArtifactStore = new ContentAddressedSourceArtifactStore(Context);
        DecodeRuns = new SqliteDecodeRunRepository(Context);
        Sessions = new SqliteSessionQueryRepository(Context);
        Comparisons = new SqliteComparisonRunRepository(Context);
        ClockSegments = new SqliteReplayClockSegmentRepository(Context);
    }

    public string Root { get; }

    public SqliteStorageOptions Options { get; }

    public SqliteStoragePaths Paths { get; }

    public SqliteStorageContext Context { get; }

    public SqliteStorageInitializer Initializer { get; }

    public ContentAddressedSourceArtifactStore ArtifactStore { get; }

    public SqliteDecodeRunRepository DecodeRuns { get; }

    public SqliteSessionQueryRepository Sessions { get; }

    public SqliteComparisonRunRepository Comparisons { get; }

    public SqliteReplayClockSegmentRepository ClockSegments { get; }

    public static async ValueTask<StorageTestScope> CreateAsync(bool initialize = true)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "WotBTreader.Storage.Tests",
            Guid.NewGuid().ToString("N"));
        StorageTestScope scope = new(root);
        if (initialize)
        {
            OperationResult<int> initialized =
                await scope.Initializer.InitializeAsync(CancellationToken.None);
            Success(initialized);
        }

        return scope;
    }

    public async ValueTask<SourceArtifact> ImportAsync(
        string name,
        byte[] bytes,
        long? maximumBytes = null)
    {
        Directory.CreateDirectory(Path.Combine(Root, "inputs"));
        string path = Path.Combine(Root, "inputs", name);
        await File.WriteAllBytesAsync(path, bytes);
        OperationResult<SourceImportOutcome> result = await ArtifactStore.ImportAsync(
            new SourceImportRequest(
                path,
                "application/vnd.wotblitz.replay",
                ".wotbreplay",
                maximumBytes ?? bytes.LongLength + 1),
            CancellationToken.None);
        return Success(result).Artifact;
    }

    public static T Success<T>(OperationResult<T> result)
    {
        Assert.IsTrue(
            result.IsSuccess,
            result.Error is null
                ? "Expected the operation to succeed."
                : $"{result.Error.Code}: {result.Error.Message}");
        Assert.IsNotNull(result.Value);
        return result.Value;
    }

    public static EvidenceReference Evidence(SourceArtifact artifact, int length = 1) =>
        new(
            artifact.Id,
            "data.wotreplay",
            0,
            length,
            artifact.Sha256);

    public static ContentHash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(Root))
        {
            return;
        }

        string expectedParent = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "WotBTreader.Storage.Tests"));
        string resolved = Path.GetFullPath(Root);
        string relative = Path.GetRelativePath(expectedParent, resolved);
        if (Path.IsPathFullyQualified(relative) ||
            relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to remove an unexpected test path.");
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(resolved, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)));
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
