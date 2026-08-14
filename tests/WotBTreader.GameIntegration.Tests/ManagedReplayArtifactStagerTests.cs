using System.Security.Cryptography;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class ManagedReplayArtifactStagerTests
{
    private static readonly SourceArtifactId ArtifactId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [TestMethod]
    public async Task StageAsync_RejectsEmptyMissingUnsupportedAndInvalidArtifactsWithoutPaths()
    {
        var missing = new Store(error: new ApplicationError("storage.artifact_not_found", "private path"));
        await AssertFailureAsync(Create(missing), default, "game.launch.artifact_invalid", false);
        await AssertFailureAsync(Create(missing), ArtifactId, "game.launch.artifact_not_found", false);
        Assert.AreEqual(0, missing.OpenCalls);

        SourceArtifact unsupported = Artifact([1], mediaType: "application/octet-stream");
        var unsupportedStore = new Store(unsupported, [1]);
        await AssertFailureAsync(Create(unsupportedStore), ArtifactId, "game.launch.artifact_unsupported", false);
        Assert.AreEqual(0, unsupportedStore.OpenCalls);

        SourceArtifact invalid = Artifact([1], length: 0);
        await AssertFailureAsync(Create(new Store(invalid, [1])), ArtifactId, "game.launch.artifact_invalid", false);
    }

    [TestMethod]
    public async Task StageAsync_MapsStoreOpenAndPlatformFailuresToSafeStableErrors()
    {
        await AssertFailureAsync(
            Create(new Store(error: new ApplicationError("storage.unavailable", "C:\\private"))),
            ArtifactId,
            "game.launch.staging_unavailable",
            true);
        await AssertFailureAsync(
            Create(new Store(Artifact([1]), [1], openError: new ApplicationError("storage.unavailable", "C:\\private"))),
            ArtifactId,
            "game.launch.staging_unavailable",
            true);
        await AssertFailureAsync(
            Create(new Store(Artifact([1]), [1]), platform: new Platform { ThrowOnCreate = true }),
            ArtifactId,
            "game.launch.staging_unavailable",
            true);
        await AssertFailureAsync(
            Create(new Store(Artifact([1]), [1]), options: new GameIntegrationOptions { ReplayLaunchStagingRoot = " " }),
            ArtifactId,
            "game.launch.staging_unavailable",
            true);
    }

    [TestMethod]
    public async Task StageAsync_DetectsLengthAndHashInconsistencyAndCleansPartialOutput()
    {
        var shortPlatform = new Platform();
        await AssertFailureAsync(Create(new Store(Artifact([1, 2]), [1]), shortPlatform), ArtifactId, "game.launch.artifact_inconsistent", false);
        Assert.AreEqual(1, shortPlatform.Files.Single().DisposeCalls);

        var hashPlatform = new Platform();
        await AssertFailureAsync(Create(new Store(Artifact([1, 2]), [2, 1]), hashPlatform), ArtifactId, "game.launch.artifact_inconsistent", false);
        Assert.AreEqual(1, hashPlatform.Files.Single().DisposeCalls);
    }

    [TestMethod]
    public async Task StageAsync_CopyFailureAndCancellationCleanPartialOutput()
    {
        var copyPlatform = new Platform();
        await AssertFailureAsync(
            Create(new Store(Artifact([1, 2]), new ThrowingReadStream([1])), copyPlatform),
            ArtifactId,
            "game.launch.staging_unavailable",
            true);
        Assert.AreEqual(1, copyPlatform.Files.Single().DisposeCalls);

        using var cancellation = new CancellationTokenSource();
        var cancellationPlatform = new Platform();
        var stager = Create(new Store(Artifact([1, 2]), new CancellingReadStream([1], cancellation)), cancellationPlatform);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await stager.StageAsync(ArtifactId, cancellation.Token));
        Assert.AreEqual(1, cancellationPlatform.Files.Single().DisposeCalls);
    }

    [TestMethod]
    public async Task StageAsync_SealFailureCleansAndReturnsInconsistent()
    {
        var platform = new Platform { SealSucceeds = false };

        await AssertFailureAsync(
            Create(new Store(Artifact([1, 2]), [1, 2]), platform),
            ArtifactId,
            "game.launch.artifact_inconsistent",
            false);

        Assert.AreEqual(1, platform.Files.Single().SealCalls);
        Assert.AreEqual(1, platform.Files.Single().DisposeCalls);
    }

    [TestMethod]
    public async Task StageAsync_SealedByteChangeIsDetectedBeforeLeaseReturn()
    {
        var platform = new Platform { CorruptOnSeal = true };

        await AssertFailureAsync(
            Create(new Store(Artifact([1, 2]), [1, 2]), platform),
            ArtifactId,
            "game.launch.artifact_inconsistent",
            false);

        Assert.AreEqual(1, platform.Files.Single().DisposeCalls);
    }

    [TestMethod]
    public async Task StageAsync_RetriesCollisionsWithoutOverwriteAndExhaustionIsSafe()
    {
        var platform = new Platform { CollisionsRemaining = 2 };
        var names = new Names("a".PadLeft(32, '0') + ".wotbreplay", "b".PadLeft(32, '0') + ".wotbreplay", "c".PadLeft(32, '0') + ".wotbreplay");
        OperationResult<ManagedReplayArtifactLease> result = await Create(new Store(Artifact([1]), [1]), platform, names).StageAsync(ArtifactId, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(names.Values, platform.Attempts);
        Assert.HasCount(1, platform.Files);
        await result.Value!.DisposeAsync();

        var exhausted = new Platform { CollisionsRemaining = int.MaxValue };
        await AssertFailureAsync(Create(new Store(Artifact([1]), [1]), exhausted), ArtifactId, "game.launch.staging_unavailable", true);
        Assert.HasCount(8, exhausted.Attempts);
        Assert.IsEmpty(exhausted.Files);
    }

    [TestMethod]
    public async Task StageAsync_RejectsInvalidGeneratedNameBeforeCreatingFile()
    {
        var platform = new Platform();
        await AssertFailureAsync(Create(new Store(Artifact([1]), [1]), platform, new Names("A".PadLeft(32, 'A') + ".wotbreplay")), ArtifactId, "game.launch.staging_unavailable", true);
        Assert.IsEmpty(platform.Attempts);
        Assert.IsTrue(ManagedReplayArtifactStager.IsValidStagingName("0123456789abcdef0123456789abcdef.wotbreplay"));
        Assert.IsFalse(ManagedReplayArtifactStager.IsValidStagingName("0123456789abcdef0123456789abcdeF.wotbreplay"));
    }

    [TestMethod]
    public async Task StageAsync_ScavengesOrphansOncePerStagerLifetime()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "wotb-stage-once-" + Guid.NewGuid().ToString("N"));
        string staging = Path.Combine(
            root,
            "replays",
            ReplayLaunchStagingPaths.StagingFolderName);
        Directory.CreateDirectory(staging);
        try
        {
            string orphan1 = Path.Combine(staging, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.wotbreplay");
            System.IO.File.WriteAllText(orphan1, "orphan");

            var platform = new Platform();
            var stager = new ManagedReplayArtifactStager(
                new Store(Artifact([1]), [1]),
                new GameIntegrationOptions { ReplayLaunchStagingRoot = staging, MaxReplayLaunchBytes = 16 },
                platform,
                new Names("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.wotbreplay"));

            // The first stage scavenges the pre-existing orphan.
            OperationResult<ManagedReplayArtifactLease> first = await stager.StageAsync(ArtifactId, CancellationToken.None);
            Assert.IsTrue(first.IsSuccess);
            Assert.IsFalse(System.IO.File.Exists(orphan1));

            // A new orphan appears before the second stage. The once-guard
            // must leave it alone: a second launch in the same process may
            // still hold the first launch's active lease.
            string orphan2 = Path.Combine(staging, "cccccccccccccccccccccccccccccccc.wotbreplay");
            System.IO.File.WriteAllText(orphan2, "orphan2");
            OperationResult<ManagedReplayArtifactLease> second = await stager.StageAsync(ArtifactId, CancellationToken.None);
            Assert.IsTrue(second.IsSuccess, "second failed: " + second.Error?.Code + " " + second.Error?.Message);
            Assert.IsTrue(System.IO.File.Exists(orphan2), "a later stage must not re-scavenge");

            await first.Value!.DisposeAsync();
            await second.Value!.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task StageAsync_ReturnsPinnedLeaseWithMetadataAndIdempotentCleanup()
    {
        byte[] bytes = [1, 2, 3];
        var platform = new Platform();
        OperationResult<ManagedReplayArtifactLease> result = await Create(new Store(Artifact(bytes), bytes), platform).StageAsync(ArtifactId, CancellationToken.None);
        Assert.IsTrue(result.IsSuccess);
        ManagedReplayArtifactLease lease = result.Value!;
        Assert.AreEqual(ArtifactId, lease.SourceArtifactId);
        Assert.AreEqual(3L, lease.ByteLength);
        Assert.AreEqual(Hash(bytes), lease.Sha256);
        Assert.AreEqual("ManagedReplayArtifactLease", lease.ToString());
        Assert.AreEqual(0, platform.Files.Single().DisposeCalls);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.AreEqual(1, platform.Files.Single().DisposeCalls);
    }

    private static ManagedReplayArtifactStager Create(
        Store store,
        Platform? platform = null,
        Names? names = null,
        GameIntegrationOptions? options = null) =>
        new(
            store,
            options ?? new GameIntegrationOptions { ReplayLaunchStagingRoot = "private-root", MaxReplayLaunchBytes = 16 },
            platform ?? new Platform(),
            names ?? new Names("0123456789abcdef0123456789abcdef.wotbreplay"));

    private static SourceArtifact Artifact(byte[] bytes, string mediaType = "application/vnd.wotblitz.replay", long? length = null) =>
        new(ArtifactId, Hash(bytes), length ?? bytes.Length, mediaType, ".wotbreplay", DateTimeOffset.UnixEpoch, "test");

    private static ContentHash Hash(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static async Task AssertFailureAsync(
        ManagedReplayArtifactStager stager,
        SourceArtifactId id,
        string code,
        bool retryable)
    {
        OperationResult<ManagedReplayArtifactLease> result = await stager.StageAsync(id, CancellationToken.None);
        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Value);
        Assert.AreEqual(code, result.Error!.Code);
        Assert.AreEqual(retryable, result.Error.Retryable);
        Assert.DoesNotContain("private", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\\', result.Error.Message);
    }

    private sealed class Store : ISourceArtifactStore
    {
        private readonly OperationResult<SourceArtifact> _get;
        private readonly Stream? _stream;
        private readonly byte[]? _reusableBytes;
        private readonly ApplicationError? _openError;
        public int OpenCalls { get; private set; }
        public Store(SourceArtifact? artifact = null, byte[]? bytes = null, ApplicationError? error = null, ApplicationError? openError = null)
            : this(artifact, stream: null, reusableBytes: bytes, error, openError) { }
        public Store(SourceArtifact artifact, Stream stream) : this(artifact, stream, reusableBytes: null, null, null) { }
        private Store(
            SourceArtifact? artifact,
            Stream? stream,
            byte[]? reusableBytes,
            ApplicationError? error,
            ApplicationError? openError)
        {
            _get = error is null ? OperationResult.Success(artifact!) : OperationResult.Failure<SourceArtifact>(error);
            _stream = stream;
            _reusableBytes = reusableBytes;
            _openError = openError;
        }

        public ValueTask<OperationResult<SourceImportOutcome>> ImportAsync(SourceImportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OperationResult<SourceArtifact>> GetAsync(SourceArtifactId artifactId, CancellationToken cancellationToken) => ValueTask.FromResult(_get);
        public ValueTask<OperationResult<Stream>> OpenReadAsync(SourceArtifactId artifactId, CancellationToken cancellationToken)
        {
            OpenCalls++;
            if (_openError is not null)
            {
                return ValueTask.FromResult(OperationResult.Failure<Stream>(_openError));
            }

            // Byte-backed stores hand out a fresh stream per open (a real
            // store never reuses a consumed stream); Stream-backed stores
            // (throwing/cancelling fixtures) keep their single instance.
            if (_reusableBytes is not null)
            {
                return ValueTask.FromResult(OperationResult.Success<Stream>(new MemoryStream(_reusableBytes)));
            }

            if (_stream?.CanSeek == true)
            {
                _stream.Position = 0;
            }

            return ValueTask.FromResult(OperationResult.Success(_stream!));
        }

        public ValueTask<IReadOnlyList<string>> ListUnreferencedContentHashesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class Names(params string[] values) : IReplayLaunchStageNameGenerator
    {
        private int _index;
        public string[] Values { get; } = values;
        public string Generate() => Values[Math.Min(_index++, Values.Length - 1)];
    }

    private sealed class Platform : IReplayLaunchStagingPlatform
    {
        public List<string> Attempts { get; } = [];
        public List<File> Files { get; } = [];
        public int CollisionsRemaining { get; set; }
        public bool ThrowOnCreate { get; set; }
        public bool SealSucceeds { get; set; } = true;
        public bool CorruptOnSeal { get; set; }
        public ValueTask<IReplayLaunchStagingFile?> CreateNewAsync(string stagingRoot, string fileName, CancellationToken cancellationToken)
        {
            Attempts.Add(fileName);
            if (ThrowOnCreate) throw new IOException("C:\\private");
            if (CollisionsRemaining-- > 0) return ValueTask.FromResult<IReplayLaunchStagingFile?>(null);
            var file = new File(
                "private-root/" + fileName,
                SealSucceeds,
                CorruptOnSeal);
            Files.Add(file);
            return ValueTask.FromResult<IReplayLaunchStagingFile?>(file);
        }
    }

    private sealed class File(
        string path,
        bool sealSucceeds,
        bool corruptOnSeal) : IReplayLaunchStagingFile
    {
        public string Path { get; } = path;
        public Stream Stream { get; } = new MemoryStream();
        public int DisposeCalls { get; private set; }
        public int SealCalls { get; private set; }
        public ValueTask<bool> SealAsync(CancellationToken cancellationToken)
        {
            SealCalls++;
            if (corruptOnSeal && Stream.Length > 0)
            {
                Stream.Position = 0;
                Stream.WriteByte(0xff);
            }

            Stream.Position = 0;
            return ValueTask.FromResult(sealSucceeds);
        }
        public ValueTask DisposeAsync() { DisposeCalls++; Stream.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ThrowingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("private");
    }

    private sealed class CancellingReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(0); }
    }
}
