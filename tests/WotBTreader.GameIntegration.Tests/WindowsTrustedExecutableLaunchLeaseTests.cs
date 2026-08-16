using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class WindowsTrustedExecutableLaunchLeaseTests
{
    [TestMethod]
    public async Task AcquireAsync_PinsExecutableContainingDirectoryAndAncestorUntilDisposed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test exercises a Windows-only code path.");
            return;
        }

        using TemporaryDirectory temporary = new();
        string ancestor = temporary.CreateDirectory("ancestor");
        string gameDirectory = Path.Combine(ancestor, "game");
        Directory.CreateDirectory(gameDirectory);
        string executablePath = Path.Combine(gameDirectory, "synthetic.exe");
        File.Copy(typeof(WindowsTrustedExecutableLaunchLeaseTests).Assembly.Location, executablePath);
        TrustedGameExecutableIdentity identity = await TrustedIdentityAsync(executablePath, gameDirectory);

        OperationResult<WindowsTrustedExecutableLaunchLease> result =
            await WindowsTrustedExecutableLaunchLease.AcquireAsync(identity, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        WindowsTrustedExecutableLaunchLease lease = result.Value!;
        Assert.AreEqual(Path.GetFullPath(executablePath), lease.CanonicalExecutablePath);
        Assert.AreSame(identity, lease.ExecutableIdentity);
        Assert.AreEqual("WindowsTrustedExecutableLaunchLease", lease.ToString());
        Assert.ThrowsExactly<IOException>(() =>
        {
            using FileStream _ = new(executablePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.ThrowsExactly<IOException>(() => File.Delete(executablePath));
        Assert.ThrowsExactly<IOException>(() => File.Move(executablePath, executablePath + ".moved"));
        Assert.ThrowsExactly<IOException>(() => Directory.Move(gameDirectory, gameDirectory + "-moved"));
        Assert.ThrowsExactly<IOException>(() => Directory.Move(ancestor, ancestor + "-moved"));

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        File.Move(executablePath, executablePath + ".moved");
        Directory.Move(ancestor, ancestor + "-moved");
    }

    [TestMethod]
    public async Task AcquireAsync_ExpectedIdentityMismatchFailsAndReleasesAllHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test exercises a Windows-only code path.");
            return;
        }

        using TemporaryDirectory temporary = new();
        string executablePath = Path.Combine(temporary.Path, "synthetic.exe");
        File.Copy(typeof(WindowsTrustedExecutableLaunchLeaseTests).Assembly.Location, executablePath);
        TrustedGameExecutableIdentity actual = await TrustedIdentityAsync(executablePath, temporary.Path);
        var mismatched = new TrustedGameExecutableIdentity(
            actual.Identity with { ExecutableSha256 = new ContentHash(new string('0', ContentHash.Sha256HexLength)) },
            actual.FileIdentity);

        OperationResult<WindowsTrustedExecutableLaunchLease> result =
            await WindowsTrustedExecutableLaunchLease.AcquireAsync(mismatched, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Value);
        Assert.AreEqual("game.launch.executable_unavailable", result.Error!.Code);
        Assert.IsTrue(result.Error.Retryable);
        Assert.DoesNotContain(temporary.Path, result.Error.Message, StringComparison.OrdinalIgnoreCase);
        File.Delete(executablePath);
        Assert.IsFalse(File.Exists(executablePath));
    }

    [TestMethod]
    public async Task AcquireAsync_CancellationPropagatesWithoutRetainingAnyHandle()
    {
        using TemporaryDirectory temporary = new();
        string executablePath = Path.Combine(temporary.Path, "synthetic.exe");
        await File.WriteAllTextAsync(executablePath, "synthetic");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        TrustedGameExecutableIdentity identity = SyntheticIdentity(executablePath, temporary.Path);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await WindowsTrustedExecutableLaunchLease.AcquireAsync(identity, cancellation.Token));

        File.Delete(executablePath);
        Assert.IsFalse(File.Exists(executablePath));
    }

    private static async Task<TrustedGameExecutableIdentity> TrustedIdentityAsync(
        string executablePath,
        string resourceRoot)
    {
        WindowsExecutableFingerprint? fingerprint = await new WindowsExecutableFingerprintReader()
            .ReadAsync(executablePath, CancellationToken.None);
        Assert.IsNotNull(fingerprint);
        return new TrustedGameExecutableIdentity(
            new InstalledGameIdentity(
                fingerprint.CanonicalPath,
                fingerprint.ProductVersion,
                fingerprint.Sha256,
                resourceRoot,
                []),
            fingerprint.FileIdentity);
    }

    private static TrustedGameExecutableIdentity SyntheticIdentity(
        string executablePath,
        string resourceRoot) =>
        new(
            new InstalledGameIdentity(
                executablePath,
                "synthetic",
                new ContentHash(new string('0', ContentHash.Sha256HexLength)),
                resourceRoot,
                []),
            new ExecutableFileIdentity(1, 1));
}
