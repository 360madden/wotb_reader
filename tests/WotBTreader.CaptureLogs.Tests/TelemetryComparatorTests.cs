using WotBTreader.Application.Capture;
using WotBTreader.CaptureLogs.Comparison;
using WotBTreader.Core;

namespace WotBTreader.CaptureLogs.Tests;

[TestClass]
public sealed class TelemetryComparatorTests
{
    [TestMethod]
    public async Task ExactIdentityTimeAndValuesClassifyExact()
    {
        TelemetryComparator comparator = new(TimeProvider.System);
        TelemetryEvent left = CreateEvent(1, TimeSpan.FromSeconds(1), """{"x":1}""");
        TelemetryEvent right = CreateEvent(2, TimeSpan.FromSeconds(1), """{"x":1}""");

        var result = await comparator.CompareAsync(
            SourceArtifactId.New(),
            [left],
            SourceArtifactId.New(),
            [right],
            ComparisonOptions.Default,
            CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, result.Value?.Summary.Exact);
    }

    [TestMethod]
    public async Task ConfiguredNumericToleranceClassifiesTolerant()
    {
        TelemetryComparator comparator = new(TimeProvider.System);
        ComparisonOptions options = new(
            TimeSpan.FromMilliseconds(250),
            new Dictionary<string, double> { ["x"] = 0.1 });

        var result = await comparator.CompareAsync(
            SourceArtifactId.New(),
            [CreateEvent(1, TimeSpan.FromSeconds(1), """{"x":1.0}""")],
            SourceArtifactId.New(),
            [CreateEvent(2, TimeSpan.FromSeconds(1), """{"x":1.05}""")],
            options,
            CancellationToken.None);

        Assert.AreEqual(1, result.Value?.Summary.Tolerant);
        Assert.AreEqual(0, result.Value?.Summary.Mismatch);
    }

    [TestMethod]
    public async Task UnmatchedSidesReportMissingAndExtraSeparately()
    {
        TelemetryComparator comparator = new(TimeProvider.System);

        var result = await comparator.CompareAsync(
            SourceArtifactId.New(),
            [CreateEvent(1, TimeSpan.FromSeconds(1), "{}")],
            SourceArtifactId.New(),
            [CreateEvent(2, TimeSpan.FromSeconds(5), "{}")],
            ComparisonOptions.Default,
            CancellationToken.None);

        Assert.AreEqual(1, result.Value?.Summary.Missing);
        Assert.AreEqual(1, result.Value?.Summary.Extra);
    }

    private static TelemetryEvent CreateEvent(long sequence, TimeSpan replayTime, string values) =>
        new(
            sequence,
            SourceTimeUtc: null,
            replayTime,
            EventType: "position",
            ParticipantIdentity: "participant-1",
            EntityId: 42,
            values,
            new TelemetryProvenance(
                TelemetrySourceKind.CaptureLog,
                "1",
                SourceArtifactId: null,
                Evidence: null,
                Detail: null));
}
