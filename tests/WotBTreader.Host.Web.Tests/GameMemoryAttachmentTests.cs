using WotBTreader.Host.Web.Services;

namespace WotBTreader.Host.Web.Tests;

[TestClass]
public sealed class GameMemoryAttachmentTests
{
    [TestMethod]
    public void Attach_BeforeOfflineVerificationGateExists_IsDenied()
    {
        using var reader = new GameMemoryReader();

        bool attached = reader.Attach(Environment.ProcessId, "test-version");

        Assert.IsFalse(attached);
        Assert.IsFalse(reader.IsAttached);
        Assert.AreEqual(0, reader.AttachedProcessId);
    }

    [TestMethod]
    public void Poll_AfterDeniedAttach_ReportsProcessInaccessible()
    {
        using var reader = new GameMemoryReader();
        _ = reader.Attach(Environment.ProcessId, "test-version");

        GameMemorySnapshot snapshot = reader.Poll();

        Assert.IsFalse(snapshot.ProcessAccessible);
        Assert.AreEqual(0, snapshot.ProcessId);
        Assert.IsFalse(snapshot.AnyOffsetsValidated);
    }
}
