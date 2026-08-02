using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Game;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Logs;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
#pragma warning disable CA1001 // _tempDir is managed by [TestInitialize]/[TestCleanup]
public sealed class ManagedLaunchCorrelationRegistrarTests
{
    private const string Correlation =
        "test-correlation-00000000000000000000000000";
    private static readonly ContentHash SourceId =
        new("abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd");
    private static readonly ContentHash SecondSourceId =
        new("1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");

    private TemporaryDirectory? _tempDir;

    [TestInitialize]
    public void Setup() => _tempDir = new TemporaryDirectory();

    [TestCleanup]
    public void Cleanup() => _tempDir?.Dispose();

    [TestMethod]
    public async Task Register_ValidInputs_ReturnsContext()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation();
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(Correlation, result.Value.LaunchCorrelation);
        Assert.AreEqual(VerifiedPath, result.Value.TrustedGameIdentity.ExecutablePath);
        Assert.AreEqual(1234, result.Value.ProcessId);
        Assert.AreEqual(638_000_000_000_000_000, result.Value.ProcessStartIdentity);
        Assert.HasCount(1, result.Value.LifecycleSourceBaselines);
        Assert.AreEqual(SourceId, result.Value.LifecycleSourceBaselines[0].SourceId);
        Assert.AreEqual(3, result.Value.LifecycleSourceBaselines[0].Generation);
        Assert.AreEqual(100, result.Value.LifecycleSourceBaselines[0].LastByteOffset);
        Assert.AreEqual(42, result.Value.SourceSequenceBaseline);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_MultipleSources_PreservesEveryBaselineCursor()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        LifecycleSourceCursor[] sources =
        [
            new(SourceId, 3, 100),
            new(SecondSourceId, 5, 200),
        ];
        var preparation = CreatePreparation(sources: sources);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.HasCount(2, result.Value.LifecycleSourceBaselines);
        CollectionAssert.AreEqual(sources, result.Value.LifecycleSourceBaselines.ToArray());

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_DuplicateSource_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(
            sources:
            [
                new LifecycleSourceCursor(SourceId, 3, 100),
                new LifecycleSourceCursor(SourceId, 3, 200),
            ]);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_duplicate_source",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_ExecutablePathMismatch_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(
            executablePath: @"C:\Different\wotblitz.exe");
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_executable_mismatch",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_NegativePid_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation();
        var lease = await CreateSuspendedLeaseAsync(pid: -1);

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.launch.correlation_invalid_pid", result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_ZeroCreationTime_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation();
        var lease = await CreateSuspendedLeaseAsync(creationTimeTicks: 0);

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_invalid_creation_time",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_AlreadyHandedOff_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation();
        var lease = await CreateSuspendedLeaseAsync();
        var (exeLease, artifactLease) = lease.HandOffLeases();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_already_handed_off",
            result.Error?.Code);

        await lease.DisposeAsync();
        await exeLease.DisposeAsync();
        await artifactLease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_UnhealthyBaseline_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(
            health: LifecycleFeedHealth.Uninitialized);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_unhealthy_lifecycle",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_DegradedBaseline_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(
            health: LifecycleFeedHealth.Degraded);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_unhealthy_lifecycle",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_HealthyEmptySources_PreservesTimestampedBaseline()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(sources: []);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.IsEmpty(result.Value.LifecycleSourceBaselines);
        Assert.AreEqual(
            DateTimeOffset.UnixEpoch,
            result.Value.LifecycleBaselineCapturedAtUtc);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_MissingBaselineTimestamp_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(capturedAtUtc: DateTimeOffset.MinValue);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_invalid_baseline_time",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_InvalidSourceGeneration_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(generation: 0);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_invalid_generation",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_NegativeSequence_ReturnsFailure()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation(sequence: -1);
        var lease = await CreateSuspendedLeaseAsync();

        var result = registrar.Register(preparation, lease);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(
            "game.launch.correlation_invalid_sequence",
            result.Error?.Code);

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_NullPreparation_ThrowsArgumentNull()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var lease = await CreateSuspendedLeaseAsync();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => registrar.Register(null!, lease));

        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task Register_NullLease_ThrowsArgumentNull()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        var preparation = CreatePreparation();

        Assert.ThrowsExactly<ArgumentNullException>(
            () => registrar.Register(preparation, null!));
    }

    [TestMethod]
    public void Register_ToString_IsSafe()
    {
        var registrar = new ManagedLaunchCorrelationRegistrar();
        Assert.AreEqual(
            nameof(ManagedLaunchCorrelationRegistrar),
            registrar.ToString());
    }

    private static string VerifiedPath =>
        typeof(ManagedLaunchCorrelationRegistrarTests).Assembly.Location;

    private static ManagedLaunchPreparation CreatePreparation(
        string? executablePath = null,
        LifecycleFeedHealth health = LifecycleFeedHealth.Healthy,
        IReadOnlyList<LifecycleSourceCursor>? sources = null,
        long generation = 3,
        long sequence = 42,
        DateTimeOffset? capturedAtUtc = null)
    {
        sources ??= [new LifecycleSourceCursor(SourceId, generation, 100)];
        var baseline = new LifecycleFeedBaseline(sequence, 1, health, sources)
        {
            CapturedAtUtc = capturedAtUtc ?? DateTimeOffset.UnixEpoch,
        };
        return new ManagedLaunchPreparation(
            new TrustedGameExecutableIdentity(
                new InstalledGameIdentity(
                    executablePath ?? VerifiedPath,
                    "11.18.0.7",
                    new ContentHash(new string('a', 64)),
                    @"C:\Games",
                    []),
                new ExecutableFileIdentity(1, 2)),
            Correlation,
            baseline);
    }

    private async Task<SuspendedGameProcessLease> CreateSuspendedLeaseAsync(
        int pid = 1234,
        long creationTimeTicks = 638_000_000_000_000_000)
    {
        string exePath = _tempDir!.GetPath("synthetic.exe");
        File.Copy(typeof(ManagedLaunchCorrelationRegistrarTests).Assembly.Location, exePath);

        var reader = new WindowsExecutableFingerprintReader();
        var fingerprint = await reader.ReadAsync(exePath, CancellationToken.None);
        Assert.IsNotNull(fingerprint);

        var installedIdentity = new InstalledGameIdentity(
            fingerprint.CanonicalPath,
            fingerprint.ProductVersion,
            fingerprint.Sha256,
            string.Empty,
            []);

        var trustedIdentity = new TrustedGameExecutableIdentity(
            installedIdentity, fingerprint.FileIdentity);

        var exeLeaseResult = await WindowsTrustedExecutableLaunchLease.AcquireAsync(
            trustedIdentity, CancellationToken.None);
        Assert.IsTrue(exeLeaseResult.IsSuccess);
        var exeLease = exeLeaseResult.Value!;

        var artifactLease = await CreateArtifactLeaseAsync();

        return new SuspendedGameProcessLease(
            pid,
            creationTimeTicks,
            VerifiedPath,
            new SafeProcessHandle((nint)1, ownsHandle: true),
            new SafeThreadHandle((nint)1, ownsHandle: true),
            exeLease,
            artifactLease);
    }

    private async Task<ManagedReplayArtifactLease> CreateArtifactLeaseAsync()
    {
        string stagingDir = _tempDir!.CreateDirectory("staging");
        var nameGenerator = new ReplayLaunchStageNameGenerator();
        var stagingPlatform = new WindowsReplayLaunchStagingPlatform();

        string name = nameGenerator.Generate();
        Assert.IsNotNull(name);

        var stagingFile = await stagingPlatform.CreateNewAsync(
            stagingDir, name, CancellationToken.None);
        Assert.IsNotNull(stagingFile);

        byte[] content = new byte[2048];
        new Random(42).NextBytes(content);
        await stagingFile.Stream.WriteAsync(content, CancellationToken.None);
        await stagingFile.Stream.FlushAsync();
        Assert.IsTrue(await stagingFile.SealAsync(CancellationToken.None));

        return new ManagedReplayArtifactLease(
            stagingFile.Path,
            new SourceArtifactId(Guid.NewGuid()),
            new ContentHash(
                "0000000000000000000000000000000000000000000000000000000000000000"),
            content.Length,
            stagingFile);
    }
}
