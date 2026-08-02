using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class SuspendedGameProcessLaunchTests
{
    [TestMethod]
    public void CreateStartupInfo_UsesNormalDefaultWindowDisplay()
    {
        StartupInfoEx startupInfo = WindowsSuspendedProcessPlatform.CreateStartupInfo();

        Assert.AreEqual(Marshal.SizeOf<StartupInfoEx>(), startupInfo.cb);
        Assert.AreEqual(0, startupInfo.dwFlags);
        Assert.AreEqual(0, startupInfo.wShowWindow);
    }

    [TestMethod]
    public async Task CreateAsync_Success_ReturnsLeaseWithValidHandles()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);

            Assert.IsTrue(result.IsSuccess, result.Error?.Message);
            Assert.IsNotNull(result.Value);
            var lease = result.Value!;
            Assert.IsGreaterThan(0, lease.ProcessId);
            Assert.IsGreaterThan(0L, lease.CreationTimeUtcTicks);
            Assert.IsFalse(lease.HandedOff);

            await lease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CreateAsync_ExecutableMismatch_TerminatesChildAndReturnsFailure()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform(executableMismatch: true);
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("game.launch.child_exe_mismatch", result.Error?.Code);
        }
    }

    [TestMethod]
    public async Task CreateAsync_IdentityMismatch_TerminatesChildAndReturnsFailure()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform(identityMismatch: true);
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("game.launch.child_identity_mismatch", result.Error?.Code);
        }
    }

    [TestMethod]
    public async Task CreateAsync_Cancellation_BeforeCreation_ThrowsOperationCanceled()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await using (executableLease)
        await using (artifactLease)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                async () => await fakePlatform.CreateAsync(executableLease, artifactLease, cts.Token));
        }
    }

    [TestMethod]
    public async Task CreateAsync_DoubleDispose_IsIdempotent()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess);
            var dblLease = result.Value!;

            await dblLease.DisposeAsync();
            await dblLease.DisposeAsync(); // second call should not throw
        }
    }

    [TestMethod]
    public async Task CreateAsync_DisposeWithoutHandoff_HandedOffIsFalse()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess);
            var noHandoffLease = result.Value!;

            Assert.IsFalse(noHandoffLease.HandedOff);
            await noHandoffLease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CreateAsync_HandoffThenDispose_HandedOffIsTrue()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess);
            var handoffLease = result.Value!;

            handoffLease.HandOffLeases();
            Assert.IsTrue(handoffLease.HandedOff);

            await handoffLease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task HandOffLeases_CalledTwice_ThrowsInvalidOperation()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess);
            var twiceLease = result.Value!;

            twiceLease.HandOffLeases();
            Assert.ThrowsExactly<InvalidOperationException>(() => twiceLease.HandOffLeases());

            await twiceLease.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CreateAsync_NullExecutableLease_ThrowsArgumentNull()
    {
        var platform = new WindowsSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (artifactLease)
        {
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(
                async () => await platform.CreateAsync(null!, artifactLease, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task CreateAsync_NullArtifactLease_ThrowsArgumentNull()
    {
        var platform = new WindowsSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);

        await using (executableLease)
        {
            await Assert.ThrowsExactlyAsync<ArgumentNullException>(
                async () => await platform.CreateAsync(executableLease, null!, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task SuspendedGameProcessLease_Failure_HasCorrectError()
    {
        var failure = SuspendedGameProcessLease.Failure();
        Assert.IsFalse(failure.IsSuccess);
        Assert.AreEqual("game.launch.process_creation_failed", failure.Error?.Code);

        var customFailure = SuspendedGameProcessLease.Failure("custom.code", "Custom message");
        Assert.IsFalse(customFailure.IsSuccess);
        Assert.AreEqual("custom.code", customFailure.Error?.Code);
        Assert.AreEqual("Custom message", customFailure.Error?.Message);
    }

    [TestMethod]
    public async Task SuspendedGameProcessLease_ToString_IsSafe()
    {
        var fakePlatform = new FakeSuspendedProcessPlatform();
        using var tempDir = new TemporaryDirectory();
        var executableLease = await CreateFakeExecutableLeaseAsync(tempDir);
        var artifactLease = await CreateFakeArtifactLeaseAsync(tempDir);

        await using (executableLease)
        await using (artifactLease)
        {
            var result = await fakePlatform.CreateAsync(executableLease, artifactLease, CancellationToken.None);
            Assert.IsTrue(result.IsSuccess);

            var toStringLease = result.Value!;
            string toString = toStringLease.ToString();
            Assert.AreEqual(nameof(SuspendedGameProcessLease), toString);

            await toStringLease.DisposeAsync();
        }
    }

    private static async Task<WindowsTrustedExecutableLaunchLease> CreateFakeExecutableLeaseAsync(
        TemporaryDirectory tempDir)
    {
        string exePath = tempDir.GetPath("synthetic.exe");
        File.Copy(typeof(SuspendedGameProcessLaunchTests).Assembly.Location, exePath);

        var fingerprintReader = new WindowsExecutableFingerprintReader();
        var fingerprint = await fingerprintReader.ReadAsync(exePath, CancellationToken.None);
        Assert.IsNotNull(fingerprint);

        var installedIdentity = new InstalledGameIdentity(
            fingerprint.CanonicalPath,
            fingerprint.ProductVersion,
            fingerprint.Sha256,
            string.Empty,
            []);

        var leaseResult = await WindowsTrustedExecutableLaunchLease.AcquireAsync(
            new TrustedGameExecutableIdentity(installedIdentity, fingerprint.FileIdentity),
            CancellationToken.None);
        Assert.IsTrue(leaseResult.IsSuccess, leaseResult.Error?.Message);
        return leaseResult.Value!;
    }

    private static async Task<ManagedReplayArtifactLease> CreateFakeArtifactLeaseAsync(
        TemporaryDirectory tempDir)
    {
        string stagingDir = tempDir.CreateDirectory("staging");
        var nameGenerator = new ReplayLaunchStageNameGenerator();
        var stagingPlatform = new WindowsReplayLaunchStagingPlatform();

        string name = nameGenerator.Generate();
        Assert.IsNotNull(name);

        var stagingFile = await stagingPlatform.CreateNewAsync(stagingDir, name, CancellationToken.None);
        Assert.IsNotNull(stagingFile, "Staging platform could not create a new file");

        byte[] content = new byte[2048];
        new Random(42).NextBytes(content);
        await stagingFile.Stream.WriteAsync(content, CancellationToken.None);
        await stagingFile.Stream.FlushAsync();
        Assert.IsTrue(await stagingFile.SealAsync(CancellationToken.None));

        return new ManagedReplayArtifactLease(
            stagingFile.Path,
            new SourceArtifactId(Guid.NewGuid()),
            new ContentHash("0000000000000000000000000000000000000000000000000000000000000000"),
            content.Length,
            stagingFile);
    }

    private sealed class FakeSuspendedProcessPlatform : ISuspendedProcessPlatform
    {
        private readonly bool _executableMismatch;
        private readonly bool _identityMismatch;
        private int _fakePid = 1234;

        public FakeSuspendedProcessPlatform(bool executableMismatch = false, bool identityMismatch = false)
        {
            _executableMismatch = executableMismatch;
            _identityMismatch = identityMismatch;
        }

        public ValueTask<OperationResult<SuspendedGameProcessLease>> CreateAsync(
            WindowsTrustedExecutableLaunchLease executableLease,
            ManagedReplayArtifactLease artifactLease,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(executableLease);
            ArgumentNullException.ThrowIfNull(artifactLease);

            if (_executableMismatch)
            {
                return ValueTask.FromResult(SuspendedGameProcessLease.Failure(
                    "game.launch.child_exe_mismatch",
                    "Child executable path does not match trusted executable"));
            }

            if (_identityMismatch)
            {
                return ValueTask.FromResult(SuspendedGameProcessLease.Failure(
                    "game.launch.child_identity_mismatch",
                    "Child executable identity does not match trusted identity"));
            }

            int pid = _fakePid++;
            var dummyProcessHandle = new SafeProcessHandle((nint)1, ownsHandle: true);
            var dummyThreadHandle = new SafeThreadHandle((nint)1, ownsHandle: true);

            var lease = new SuspendedGameProcessLease(
                pid,
                DateTime.UtcNow.Ticks,
                executableLease.CanonicalExecutablePath,
                dummyProcessHandle,
                dummyThreadHandle,
                executableLease,
                artifactLease);

            return ValueTask.FromResult(OperationResult.Success(lease));
        }
    }
}
