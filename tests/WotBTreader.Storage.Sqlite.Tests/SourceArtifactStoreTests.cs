using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class SourceArtifactStoreTests
{
    [TestMethod]
    public async Task ImportCopiesAtomicallyAndDuplicateIsIdempotent()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        byte[] bytes = "synthetic replay evidence"u8.ToArray();
        string inputDirectory = Path.Combine(scope.Root, "inputs");
        Directory.CreateDirectory(inputDirectory);
        string input = Path.Combine(inputDirectory, "sample.wotbreplay");
        await File.WriteAllBytesAsync(input, bytes);
        SourceImportRequest request = new(
            input,
            "application/vnd.wotblitz.replay",
            "wotbreplay",
            1_024);

        SourceImportOutcome first = StorageTestScope.Success(
            await scope.ArtifactStore.ImportAsync(request, CancellationToken.None));
        await File.WriteAllBytesAsync(input, "changed original"u8.ToArray());
        await File.WriteAllBytesAsync(input, bytes);
        SourceImportOutcome second = StorageTestScope.Success(
            await scope.ArtifactStore.ImportAsync(request, CancellationToken.None));

        Assert.IsFalse(first.AlreadyExisted);
        Assert.IsTrue(second.AlreadyExisted);
        Assert.AreEqual(first.Artifact.Id, second.Artifact.Id);
        Assert.AreEqual(StorageTestScope.Hash(bytes), first.Artifact.Sha256);
        Assert.AreEqual(".wotbreplay", first.Artifact.StoredExtension);

        OperationResult<Stream> open =
            await scope.ArtifactStore.OpenReadAsync(first.Artifact.Id, CancellationToken.None);
        await using Stream managed = StorageTestScope.Success(open);
        using MemoryStream copy = new();
        await managed.CopyToAsync(copy);
        CollectionAssert.AreEqual(bytes, copy.ToArray());

        string[] managedObjects = Directory.GetFiles(
            scope.Paths.ContentRoot,
            "*",
            SearchOption.AllDirectories);
        Assert.HasCount(1, managedObjects);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateImportsConvergeOnOneArtifact()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        byte[] bytes = Enumerable.Range(0, 8_192).Select(index => (byte)(index % 251)).ToArray();
        string inputDirectory = Path.Combine(scope.Root, "inputs");
        Directory.CreateDirectory(inputDirectory);
        string input = Path.Combine(inputDirectory, "concurrent.wotbreplay");
        await File.WriteAllBytesAsync(input, bytes);
        SourceImportRequest request = new(
            input,
            "application/vnd.wotblitz.replay",
            ".wotbreplay",
            16_384);

        OperationResult<SourceImportOutcome>[] results = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(
                _ => scope.ArtifactStore.ImportAsync(request, CancellationToken.None).AsTask()));

        Assert.IsTrue(results.All(result => result.IsSuccess));
        SourceImportOutcome[] outcomes =
            results.Select(StorageTestScope.Success).ToArray();
        Assert.AreEqual(1, outcomes.Select(item => item.Artifact.Id).Distinct().Count());
        Assert.AreEqual(1, outcomes.Count(item => !item.AlreadyExisted));
        Assert.AreEqual(11, outcomes.Count(item => item.AlreadyExisted));
        Assert.HasCount(
            1,
            Directory.GetFiles(scope.Paths.ContentRoot, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task OversizedImportIsRejectedWithoutManagedResidue()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        string inputDirectory = Path.Combine(scope.Root, "inputs");
        Directory.CreateDirectory(inputDirectory);
        string input = Path.Combine(inputDirectory, "oversized.wotbreplay");
        await File.WriteAllBytesAsync(input, [1, 2, 3, 4, 5]);

        OperationResult<SourceImportOutcome> result = await scope.ArtifactStore.ImportAsync(
            new SourceImportRequest(
                input,
                "application/vnd.wotblitz.replay",
                ".wotbreplay",
                4),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("storage.invalid_source", result.Error?.Code);
        Assert.IsEmpty(Directory.GetFiles(scope.Paths.ContentRoot, "*", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetFiles(scope.Paths.StagingRoot));
    }

    [TestMethod]
    public async Task MissingArtifactReturnsStableNotFoundError()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();

        OperationResult<SourceArtifact> result = await scope.ArtifactStore.GetAsync(
            SourceArtifactId.New(),
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("storage.not_found", result.Error?.Code);
    }
}
