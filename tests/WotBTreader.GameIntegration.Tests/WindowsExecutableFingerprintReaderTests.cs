using System.Security.Cryptography;
using WotBTreader.Core;
using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class WindowsExecutableFingerprintReaderTests
{
    [TestMethod]
    public async Task ReadAsync_CopiedManagedPeUsesOnePinnedIdentityAndReleasesHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This test exercises a Windows-only code path.");
            return;
        }

        using TemporaryDirectory temporary = new();
        string sourcePath = typeof(WindowsExecutableFingerprintReaderTests)
            .Assembly.Location;
        string executablePath = Path.Combine(temporary.Path, "synthetic.exe");
        File.Copy(sourcePath, executablePath);
        ContentHash expectedHash;
        await using (FileStream expectedStream = new(
                         executablePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            expectedHash = new ContentHash(Convert.ToHexString(
                await SHA256.HashDataAsync(expectedStream)));
        }
        var reader = new WindowsExecutableFingerprintReader();

        WindowsExecutableFingerprint? fingerprint =
            await reader.ReadAsync(executablePath, CancellationToken.None);

        Assert.IsNotNull(fingerprint);
        Assert.AreEqual(Path.GetFullPath(executablePath), fingerprint.CanonicalPath);
        Assert.AreEqual(expectedHash, fingerprint.Sha256);
        Assert.IsFalse(string.IsNullOrWhiteSpace(fingerprint.ProductVersion));
        Assert.IsGreaterThan(0UL, fingerprint.FileIdentity.FileIndex);

        File.Delete(executablePath);
        Assert.IsFalse(File.Exists(executablePath));
    }

    [TestMethod]
    public async Task ReadAsync_MissingVersionReturnsNullAndReleasesHandle()
    {
        using TemporaryDirectory temporary = new();
        string executablePath = Path.Combine(temporary.Path, "synthetic.exe");
        await File.WriteAllTextAsync(executablePath, "not a versioned PE");
        var reader = new WindowsExecutableFingerprintReader();

        WindowsExecutableFingerprint? fingerprint =
            await reader.ReadAsync(executablePath, CancellationToken.None);

        Assert.IsNull(fingerprint);
        File.Delete(executablePath);
        Assert.IsFalse(File.Exists(executablePath));
    }

    [TestMethod]
    public async Task ReadAsync_PreCanceledRequestDoesNotRetainFile()
    {
        using TemporaryDirectory temporary = new();
        string executablePath = Path.Combine(temporary.Path, "synthetic.exe");
        await File.WriteAllTextAsync(executablePath, "synthetic");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new WindowsExecutableFingerprintReader();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await reader.ReadAsync(executablePath, cancellation.Token));

        File.Delete(executablePath);
        Assert.IsFalse(File.Exists(executablePath));
    }
}
