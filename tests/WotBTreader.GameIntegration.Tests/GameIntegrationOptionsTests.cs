namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class GameIntegrationOptionsTests
{
    [TestMethod]
    public void DefaultLifecycleEvidenceTimeout_IsValid()
    {
        var options = new GameIntegrationOptions();

        options.Validate();

        Assert.AreEqual(TimeSpan.FromSeconds(45), options.LifecycleEvidenceTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(15), options.OfflineReplayEvidenceLifetime);
    }

    [TestMethod]
    public void LifecycleEvidenceTimeout_BelowMinimum_IsRejected()
    {
        var options = new GameIntegrationOptions
        {
            LifecycleEvidenceTimeout = TimeSpan.FromSeconds(4.999),
        };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(options.Validate);
    }

    [TestMethod]
    public void LifecycleEvidenceTimeout_AboveMaximum_IsRejected()
    {
        var options = new GameIntegrationOptions
        {
            LifecycleEvidenceTimeout = TimeSpan.FromMinutes(5).Add(TimeSpan.FromMilliseconds(1)),
        };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(options.Validate);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(300)]
    public void LifecycleEvidenceTimeout_BoundsAreAccepted(int seconds)
    {
        var options = new GameIntegrationOptions
        {
            LifecycleEvidenceTimeout = TimeSpan.FromSeconds(seconds),
        };

        options.Validate();
    }

    [TestMethod]
    [DataRow(4.999)]
    [DataRow(120.001)]
    public void OfflineReplayEvidenceLifetime_OutsideBounds_IsRejected(double seconds)
    {
        var options = new GameIntegrationOptions
        {
            OfflineReplayEvidenceLifetime = TimeSpan.FromSeconds(seconds),
        };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(options.Validate);
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(120)]
    public void OfflineReplayEvidenceLifetime_BoundsAreAccepted(int seconds)
    {
        var options = new GameIntegrationOptions
        {
            OfflineReplayEvidenceLifetime = TimeSpan.FromSeconds(seconds),
        };

        options.Validate();
    }

    [TestMethod]
    public void InstructionSnapshotHelper_RequiresAbsolutePathAndHashTogether()
    {
        var missingHash = new GameIntegrationOptions
        {
            InstructionSnapshotHelperPath = @"C:\tools\snapshot.exe",
        };
        var relativePath = new GameIntegrationOptions
        {
            InstructionSnapshotHelperPath = @"tools\snapshot.exe",
            InstructionSnapshotHelperSha256 = new string('a', 64),
        };

        Assert.ThrowsExactly<ArgumentException>(missingHash.Validate);
        Assert.ThrowsExactly<ArgumentException>(relativePath.Validate);
    }

    [TestMethod]
    public void InstructionSnapshotHelper_ExactIdentityPairIsAccepted()
    {
        var options = new GameIntegrationOptions
        {
            InstructionSnapshotHelperPath = @"C:\tools\snapshot.exe",
            InstructionSnapshotHelperSha256 = new string('a', 64),
        };

        options.Validate();
    }
}
