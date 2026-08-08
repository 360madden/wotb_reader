using WotBTreader.Core;
using WotBTreader.GameIntegration.Discovery;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class InstructionSnapshotTargetPolicyTests
{
    private const string SupportedHash =
        "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d";

    [TestMethod]
    public void ExactVersionAndHashResolveThePinnedTransformFillTarget()
    {
        bool resolved = InstructionSnapshotTargetPolicy.TryResolve(
            "11.19.0.10",
            new ContentHash(SupportedHash),
            out InstructionSnapshotTargetPlan? plan);

        Assert.IsTrue(resolved);
        Assert.IsNotNull(plan);
        Assert.AreEqual("wotblitz.exe", plan!.ModuleName);
        Assert.AreEqual(0x007C39ABu, plan.Rva);
        Assert.AreEqual("8B83A0000000", plan.ExpectedInstructionHex);
        Assert.AreEqual(0x1C, plan.ObjectDisplacement);
        Assert.AreEqual(750, plan.MinimumObjectSampleIntervalMilliseconds);
    }

    [TestMethod]
    public void VersionOrHashDriftFailsClosed()
    {
        Assert.IsFalse(InstructionSnapshotTargetPolicy.TryResolve(
            "11.19.0.11",
            new ContentHash(SupportedHash),
            out _));
        Assert.IsFalse(InstructionSnapshotTargetPolicy.TryResolve(
            "11.19.0.10",
            new ContentHash(new string('0', 64)),
            out _));
    }

    [TestMethod]
    public async Task HelperHashMismatchFailsBeforeAnyTargetAccess()
    {
        string helperPath = Path.Combine(
            Path.GetTempPath(),
            "wotbtreader-helper-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await File.WriteAllBytesAsync(helperPath, [1, 2, 3]);
            var runner = new WindowsInstructionSnapshotRunner(new GameIntegrationOptions
            {
                InstructionSnapshotHelperPath = helperPath,
                InstructionSnapshotHelperSha256 = new string('0', 64),
            });

            InstructionSnapshotRunnerOutcome outcome = await runner.RunAsync(
                new InstructionSnapshotExecutionRequest(
                    ProcessId: int.MaxValue,
                    ProcessStartIdentity: 1,
                    CanonicalExecutablePath: @"C:\missing\wotblitz.exe",
                    ProductVersion: "11.19.0.10",
                    ExecutableSha256: new ContentHash(SupportedHash),
                    DurationMilliseconds: 1_000,
                    MaxHits: 1),
                CancellationToken.None);

            Assert.IsFalse(outcome.IsSuccess);
            Assert.AreEqual(
                "discover.instruction_snapshot.helper_identity_mismatch",
                outcome.Error?.Code);
            Assert.IsTrue(outcome.CleanupProven);
        }
        finally
        {
            File.Delete(helperPath);
        }
    }
}
