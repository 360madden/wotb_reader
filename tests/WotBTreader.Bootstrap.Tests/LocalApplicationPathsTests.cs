using WotBTreader.Bootstrap.Configuration;

namespace WotBTreader.Bootstrap.Tests;

[TestClass]
public sealed class LocalApplicationPathsTests
{
    [TestMethod]
    public void RendezvousIsAlwaysUnderLocalAppData()
    {
        // The rendezvous is ephemeral coordination data that must live in a
        // per-user location to avoid ACL hazards with shared/removable roots.
        string root = Path.Combine(
            Path.GetTempPath(),
            $"wotbtreader-rdv-{Guid.CreateVersion7():N}");
        LocalApplicationPaths paths = LocalApplicationPaths.Create(root);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.IsTrue(
            paths.Rendezvous.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase),
            "Rendezvous must always live under %LocalAppData% regardless of the chosen data root.");
    }

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

        // Rendezvous is intentionally outside the custom root — see
        // RendezvousIsAlwaysUnderLocalAppData for rationale.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.IsTrue(
            paths.Rendezvous.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase));
    }
}
