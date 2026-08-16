using System.IO;
using WotBTreader.Overlay.Discovery;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class RendezvousLocatorTests
{
    private const string CapabilityToken = "cap-token-9f4e7c-secret";
    private const string LoopbackBaseUri = "http://127.0.0.1:8123";

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ExpectedInstanceId = new("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private string _tempDir = null!;
    private string _rendezvousPath = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wotb-overlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _rendezvousPath = Path.Combine(_tempDir, "rendezvous.json");
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void Locate_MissingFile_ReturnsNotFound()
    {
        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.NotFound, result.Status);
        Assert.IsNull(result.Record);
        AssertReasonContainsNoSecrets(result);
    }

    [TestMethod]
    public void Locate_ValidRecord_ReturnsFoundWithRecordFields()
    {
        DateTimeOffset issuedAtUtc = Now.AddMinutes(-1);
        DateTimeOffset expiresAtUtc = Now.AddMinutes(5);
        WriteRecord("1.0", LoopbackBaseUri, issuedAtUtc, expiresAtUtc);
        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Found, result.Status);
        Assert.IsNotNull(result.Record);
        Assert.AreEqual("1.0", result.Record.SchemaVersion);
        Assert.AreEqual(ExpectedInstanceId, result.Record.InstanceId);
        Assert.AreEqual(1234, result.Record.ProcessId);
        Assert.AreEqual(LoopbackBaseUri, result.Record.BaseUri);
        Assert.AreEqual(CapabilityToken, result.Record.Capability);
        Assert.AreEqual(issuedAtUtc, result.Record.IssuedAtUtc);
        Assert.AreEqual(expiresAtUtc, result.Record.ExpiresAtUtc);
    }

    [TestMethod]
    public void Locate_ExpiredRecord_ReturnsStale()
    {
        WriteRecord("1.0", LoopbackBaseUri, Now.AddMinutes(-10), Now.AddMinutes(-5));
        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Stale, result.Status);
        AssertReasonContainsNoSecrets(result);
    }

    [TestMethod]
    public void Locate_UnknownSchemaVersion_ReturnsInvalid()
    {
        WriteRecord("2.0", LoopbackBaseUri, Now.AddMinutes(-1), Now.AddMinutes(5));
        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Invalid, result.Status);
        AssertReasonContainsNoSecrets(result);
    }

    [TestMethod]
    public void Locate_NonLoopbackIpBaseUri_ReturnsInvalid()
    {
        WriteRecord("1.0", "http://192.168.1.10:8123", Now.AddMinutes(-1), Now.AddMinutes(5));
        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Invalid, result.Status);
        AssertReasonContainsNoSecrets(result);
    }

    [TestMethod]
    public void Locate_ExternalHostBaseUri_ReturnsInvalid()
    {
        WriteRecord("1.0", "http://example.com", Now.AddMinutes(-1), Now.AddMinutes(5));
        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Invalid, result.Status);
        AssertReasonContainsNoSecrets(result);
    }

    [TestMethod]
    public void Locate_MalformedJson_ReturnsInvalid()
    {
        File.WriteAllText(_rendezvousPath, "{ \"schemaVersion\": \"1.0\", broken");
        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Invalid, result.Status);
        AssertReasonContainsNoSecrets(result);
    }

    [TestMethod]
    public void Locate_ReparsePointRecord_ReturnsInvalid()
    {
        WriteRecord("1.0", LoopbackBaseUri, Now.AddMinutes(-1), Now.AddMinutes(5));
        RendezvousLocator locator = CreateLocator(isReparsePoint: _ => true);

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Invalid, result.Status);
        Assert.IsNull(result.Record);
        AssertReasonContainsNoSecrets(result);
    }

    [TestMethod]
    public void Locate_RealSymlinkRecord_ReturnsInvalid()
    {
        string target = Path.Combine(_tempDir, "target.json");
        WriteRecord("1.0", LoopbackBaseUri, Now.AddMinutes(-1), Now.AddMinutes(5));
        File.Move(_rendezvousPath, target);
        try
        {
            File.CreateSymbolicLink(_rendezvousPath, target);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            Assert.Inconclusive(
                "Symbolic-link creation is unavailable; the reparse branch cannot be exercised.");
            return;
        }

        RendezvousLocator locator = CreateLocator();

        RendezvousResult result = locator.Locate();

        Assert.AreEqual(RendezvousStatus.Invalid, result.Status);
        AssertReasonContainsNoSecrets(result);
    }

    private RendezvousLocator CreateLocator(Func<string, bool>? isReparsePoint = null) =>
        new(
            new FakeTimeProvider(Now),
            _rendezvousPath,
            isProcessAlive: _ => true,
            isReparsePoint: isReparsePoint);

    private void WriteRecord(string schemaVersion, string baseUri, DateTimeOffset issuedAtUtc, DateTimeOffset expiresAtUtc)
    {
        string json = $$"""
            {
              "schemaVersion": "{{schemaVersion}}",
              "instanceId": "{{ExpectedInstanceId}}",
              "processId": 1234,
              "baseUri": "{{baseUri}}",
              "capability": "{{CapabilityToken}}",
              "issuedAtUtc": "{{issuedAtUtc:O}}",
              "expiresAtUtc": "{{expiresAtUtc:O}}"
            }
            """;
        File.WriteAllText(_rendezvousPath, json);
    }

    private void AssertReasonContainsNoSecrets(RendezvousResult result)
    {
        Assert.IsFalse(result.Reason.Contains(CapabilityToken, StringComparison.Ordinal), "Reason must not contain the capability token.");
        Assert.IsFalse(result.Reason.Contains(_rendezvousPath, StringComparison.OrdinalIgnoreCase), "Reason must not contain the rendezvous file path.");
        Assert.IsFalse(result.Reason.Contains(_tempDir, StringComparison.OrdinalIgnoreCase), "Reason must not contain the rendezvous directory path.");
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
