using WotBTreader.Overlay.Services;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class TelemetryStreamServiceTests
{
    private static readonly Uri TestUri = new("http://127.0.0.1:8123/");

    [TestMethod]
    public async Task ConnectAsync_NullUri_ThrowsArgumentNullException()
    {
        using TelemetryStreamService service = CreateService();

        try
        {
            await service.ConnectAsync(null!);
            Assert.Fail("Expected ArgumentNullException for null base URI.");
        }
        catch (ArgumentNullException)
        {
            // Expected.
        }
    }

    [TestMethod]
    public async Task ConnectAsync_AfterDispose_IsNoOp()
    {
        TelemetryStreamService service = CreateService();
        service.Dispose();

        // Must not throw — disposed services quietly return.
        await service.ConnectAsync(TestUri);
    }

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        TelemetryStreamService service = CreateService();

        service.Dispose();
        service.Dispose();

        // Must not throw. Disposing twice verifies the _disposed guard.
    }

    private static TelemetryStreamService CreateService() => new();
}
