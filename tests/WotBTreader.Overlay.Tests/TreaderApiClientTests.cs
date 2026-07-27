using WotBTreader.Overlay.Services;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class TreaderApiClientTests
{
    [TestMethod]
    public void Ctor_NonLoopbackIp_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new TreaderApiClient(new Uri("http://192.168.1.5")));
    }

    [TestMethod]
    public void Ctor_ExternalHost_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new TreaderApiClient(new Uri("http://example.com")));
    }

    [TestMethod]
    public void Ctor_LoopbackHosts_AreAccepted()
    {
        using TreaderApiClient ipv4 = new(new Uri("http://127.0.0.1:8123"));
        using TreaderApiClient localhost = new(new Uri("http://localhost:8123"));
        using TreaderApiClient ipv6 = new(new Uri("http://[::1]:8123"));

        Assert.IsNotNull(ipv4);
        Assert.IsNotNull(localhost);
        Assert.IsNotNull(ipv6);
    }
}
