using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class ReplayLaunchStagingPathsTests
{
    [TestMethod]
    public void IsGuidStageFileName_AcceptsGuidNReplayNames()
    {
        Assert.IsTrue(
            ReplayLaunchStagingPaths.IsGuidStageFileName(
                "1700337b6eef493988b7d484dd2e1760.wotbreplay"));
        Assert.IsFalse(
            ReplayLaunchStagingPaths.IsGuidStageFileName(
                "20260802_1615__player_GB08_Churchill_I_1.wotbreplay"));
        Assert.IsFalse(
            ReplayLaunchStagingPaths.IsGuidStageFileName("short.wotbreplay"));
    }

    [TestMethod]
    public void TryGetFlatReplayClonePath_MapsStagingSibling()
    {
        string staging = Path.Combine(
            Path.GetTempPath(),
            "replays",
            ReplayLaunchStagingPaths.StagingFolderName,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.wotbreplay");
        string? clone = ReplayLaunchStagingPaths.TryGetFlatReplayClonePath(staging);
        Assert.IsNotNull(clone);
        Assert.AreEqual(
            Path.Combine(
                Path.GetTempPath(),
                "replays",
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.wotbreplay"),
            clone,
            ignoreCase: true);
    }

    [TestMethod]
    public void TryGetFlatReplayClonePath_IgnoresNonStagingRoots()
    {
        string launch = Path.Combine(
            Path.GetTempPath(),
            "WotBTreader",
            "launch",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.wotbreplay");
        Assert.IsNull(ReplayLaunchStagingPaths.TryGetFlatReplayClonePath(launch));
    }

    [TestMethod]
    public void Resolve_FallsBackToApplicationLaunchWhenGameTreeMissing()
    {
        string appRoot = Path.Combine(
            Path.GetTempPath(),
            "wotb-staging-resolve-" + Guid.NewGuid().ToString("N"));
        string missingUserData = Path.Combine(appRoot, "no-such-wotblitz");
        try
        {
            Directory.CreateDirectory(appRoot);
            string resolved = ReplayLaunchStagingPaths.Resolve(missingUserData, appRoot);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(appRoot, "launch")),
                resolved,
                ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(appRoot))
            {
                Directory.Delete(appRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Resolve_UsesGameReplaysStagingWhenUserDataPresent()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "wotb-staging-game-" + Guid.NewGuid().ToString("N"));
        string userData = Path.Combine(root, "wotblitz");
        string replays = Path.Combine(userData, "DAVAProject", "replays");
        try
        {
            Directory.CreateDirectory(replays);
            string resolved = ReplayLaunchStagingPaths.Resolve(userData, Path.Combine(root, "app"));
            Assert.AreEqual(
                Path.GetFullPath(
                    Path.Combine(replays, ReplayLaunchStagingPaths.StagingFolderName)),
                resolved,
                ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
