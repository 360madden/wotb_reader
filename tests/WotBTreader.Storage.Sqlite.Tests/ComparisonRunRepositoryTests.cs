using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class ComparisonRunRepositoryTests
{
    [TestMethod]
    public async Task ImmutableComparisonRoundTripsWithSeparateClassifications()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifact left = await scope.ImportAsync("left.wotbreplay", [1, 2, 3]);
        SourceArtifact right = await scope.ImportAsync("right.wotbreplay", [4, 5, 6]);
        ComparisonRunId runId = ComparisonRunId.New();
        ComparisonRun run = new(
            runId,
            left.Id,
            right.Id,
            "exact-id-window",
            "0.1.0",
            "1",
            TimeSpan.FromMilliseconds(250),
            DateTimeOffset.UtcNow);
        ComparisonItem[] items =
        [
            Item(runId, 1, ComparisonClassification.Exact),
            Item(runId, 2, ComparisonClassification.Tolerant),
            Item(runId, 3, ComparisonClassification.Mismatch),
            Item(runId, 4, ComparisonClassification.Missing),
            Item(runId, 5, ComparisonClassification.Extra),
            Item(runId, 6, ComparisonClassification.Uncomparable),
        ];
        TelemetryComparison expected = new(
            run,
            new ComparisonSummary(1, 1, 1, 1, 1, 1),
            items);

        TelemetryComparison added = StorageTestScope.Success(
            await scope.Comparisons.AddAsync(expected, CancellationToken.None));
        TelemetryComparison actual = StorageTestScope.Success(
            await scope.Comparisons.GetAsync(runId, CancellationToken.None));

        Assert.AreEqual(expected.Summary, added.Summary);
        Assert.AreEqual(expected.Run, actual.Run);
        Assert.AreEqual(expected.Summary, actual.Summary);
        CollectionAssert.AreEqual(items, actual.Items.ToArray());

        OperationResult<TelemetryComparison> duplicate =
            await scope.Comparisons.AddAsync(expected, CancellationToken.None);
        Assert.IsFalse(duplicate.IsSuccess);
        Assert.AreEqual("storage.conflict", duplicate.Error?.Code);
    }

    [TestMethod]
    public async Task SummaryMismatchIsRejectedBeforeWriting()
    {
        await using StorageTestScope scope = await StorageTestScope.CreateAsync();
        SourceArtifact left = await scope.ImportAsync("left-invalid.wotbreplay", [1]);
        SourceArtifact right = await scope.ImportAsync("right-invalid.wotbreplay", [2]);
        ComparisonRunId runId = ComparisonRunId.New();
        TelemetryComparison invalid = new(
            new ComparisonRun(
                runId,
                left.Id,
                right.Id,
                "comparator",
                "1",
                "1",
                TimeSpan.FromMilliseconds(250),
                DateTimeOffset.UtcNow),
            new ComparisonSummary(1, 0, 0, 0, 0, 0),
            [Item(runId, 1, ComparisonClassification.Mismatch)]);

        OperationResult<TelemetryComparison> result =
            await scope.Comparisons.AddAsync(invalid, CancellationToken.None);
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("storage.invalid_input", result.Error?.Code);

        OperationResult<TelemetryComparison> absent =
            await scope.Comparisons.GetAsync(runId, CancellationToken.None);
        Assert.IsFalse(absent.IsSuccess);
        Assert.AreEqual("storage.not_found", absent.Error?.Code);
    }

    private static ComparisonItem Item(
        ComparisonRunId runId,
        long sequence,
        ComparisonClassification classification) =>
        new(
            ComparisonItemId.New(),
            runId,
            sequence,
            classification,
            "position",
            TimeSpan.FromMilliseconds(sequence * 100),
            TimeSpan.FromMilliseconds(sequence * 100 + 10),
            "entity:7001",
            "x",
            "1",
            "1",
            $"Synthetic {classification} result.");
}
