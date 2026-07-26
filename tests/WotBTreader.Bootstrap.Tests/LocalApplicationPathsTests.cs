using WotBTreader.Bootstrap.Configuration;

namespace WotBTreader.Bootstrap.Tests;

[TestClass]
public sealed class LocalApplicationPathsTests
{
    [TestMethod]
    public void OverrideKeepsAllRuntimeDataUnderChosenRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wotbtreader-path-test"));

        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);

        Assert.AreEqual(root, paths.Root);
        Assert.IsTrue(paths.ContentStore.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Database.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Logs.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Diagnostics.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(paths.Rendezvous.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
