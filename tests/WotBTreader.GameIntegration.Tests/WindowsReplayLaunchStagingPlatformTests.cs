using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class WindowsReplayLaunchStagingPlatformTests
{
    [TestMethod]
    public async Task CreateNewAsync_PinsAgainstMutationAndDeletesByIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "launch");
        const string name = "0123456789abcdef0123456789abcdef.wotbreplay";
        var platform = new WindowsReplayLaunchStagingPlatform();
        IReplayLaunchStagingFile? staged = await platform.CreateNewAsync(
            root,
            name,
            CancellationToken.None);
        Assert.IsNotNull(staged);
        string path = staged.Path;
        byte[] contents = [1, 2, 3, 4];
        await staged.Stream.WriteAsync(contents);
        await staged.Stream.FlushAsync();
        Assert.IsTrue(await staged.SealAsync(CancellationToken.None));
        Assert.IsFalse(await staged.SealAsync(CancellationToken.None));
        Assert.IsFalse(staged.Stream.CanWrite);

        await using (FileStream reader = File.OpenRead(path))
        {
            byte[] actual = new byte[contents.Length];
            await reader.ReadExactlyAsync(actual);
            CollectionAssert.AreEqual(contents, actual);
        }

        Assert.ThrowsExactly<IOException>(() =>
        {
            using FileStream _ = new(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.ThrowsExactly<IOException>(() => File.Delete(path));
        Assert.ThrowsExactly<IOException>(() =>
            Directory.Move(root, root + "-moved"));

        await staged.DisposeAsync();
        await staged.DisposeAsync();

        Assert.IsFalse(File.Exists(path));
        Directory.Move(root, root + "-moved");
    }

    [TestMethod]
    public async Task CreateNewAsync_CollisionNeverOverwritesPinnedFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "launch");
        const string name = "fedcba9876543210fedcba9876543210.wotbreplay";
        var platform = new WindowsReplayLaunchStagingPlatform();
        await using IReplayLaunchStagingFile? first = await platform.CreateNewAsync(
            root,
            name,
            CancellationToken.None);

        IReplayLaunchStagingFile? collision = await platform.CreateNewAsync(
            root,
            name,
            CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.IsNull(collision);
    }

    [TestMethod]
    public async Task CreateNewAsync_PreCanceledRequestCreatesNothing()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "launch");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var platform = new WindowsReplayLaunchStagingPlatform();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await platform.CreateNewAsync(
                root,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.wotbreplay",
                cancellation.Token));

        Assert.IsFalse(Directory.Exists(root));
    }
}
