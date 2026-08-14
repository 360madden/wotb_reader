using WotBTreader.GameIntegration.Session;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class ReplayLaunchStagingScavengerTests
{
    [TestMethod]
    public void Scavenge_RemovesOrphanedStageFilesAndFlatClones_KeepsOriginals()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "wotb-scavenge-" + Guid.NewGuid().ToString("N"));
        string replays = Path.Combine(root, "replays");
        string staging = Path.Combine(replays, ReplayLaunchStagingPaths.StagingFolderName);
        try
        {
            Directory.CreateDirectory(staging);

            string orphanStage = Path.Combine(staging, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.wotbreplay");
            string humanStage = Path.Combine(staging, "my-original-copy.wotbreplay");
            string flatClone = Path.Combine(replays, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.wotbreplay");
            string humanOriginal = Path.Combine(replays, "20260802_1615__player_GB08.wotbreplay");

            foreach (string file in new[] { orphanStage, humanStage, flatClone, humanOriginal })
            {
                File.WriteAllText(file, "replay-bytes");
            }

            ReplayLaunchStagingScavenger.Scavenge(staging);

            Assert.IsFalse(File.Exists(orphanStage), "orphaned GUID stage file must be removed");
            Assert.IsFalse(File.Exists(flatClone), "flat GUID clone must be removed");
            Assert.IsTrue(File.Exists(humanStage), "human-named stage file must survive");
            Assert.IsTrue(File.Exists(humanOriginal), "human-named original must survive");
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
    public void Scavenge_IgnoresNullAndMissingRoots()
    {
        ReplayLaunchStagingScavenger.Scavenge(null);
        ReplayLaunchStagingScavenger.Scavenge(string.Empty);
        ReplayLaunchStagingScavenger.Scavenge(
            Path.Combine(Path.GetTempPath(), "wotb-scavenge-missing-" + Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    public void Scavenge_DoesNotTouchParentWhenRootIsNotStagingFolder()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "wotb-scavenge-app-" + Guid.NewGuid().ToString("N"));
        string launch = Path.Combine(root, "launch");
        try
        {
            Directory.CreateDirectory(launch);

            // A GUID-named file in a non-staging root (the app-data `launch`
            // fallback) must still be scavenged, but its PARENT (the app root)
            // must not be swept — only the staging folder name triggers the
            // flat-clone pass.
            string guidStage = Path.Combine(launch, "cccccccccccccccccccccccccccccccc.wotbreplay");
            string guidSibling = Path.Combine(root, "dddddddddddddddddddddddddddddddd.wotbreplay");
            File.WriteAllText(guidStage, "replay-bytes");
            File.WriteAllText(guidSibling, "replay-bytes");

            ReplayLaunchStagingScavenger.Scavenge(launch);

            Assert.IsFalse(File.Exists(guidStage));
            Assert.IsTrue(File.Exists(guidSibling), "a non-staging root's parent must not be swept");
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
