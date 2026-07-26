using System.Text;
using WotBTreader.Application.Capture;
using WotBTreader.CaptureLogs.Ndjson;

namespace WotBTreader.CaptureLogs.Tests;

[TestClass]
public sealed class NdjsonTelemetrySourceTests
{
    [TestMethod]
    public async Task ValidLinePreservesIdentityValuesAndProvenance()
    {
        const string Line =
            """
            {"schemaVersion":"1","sourceSequence":7,"sourceTimeUtc":"2026-07-26T20:00:00Z","replayTimeMs":1250,"eventType":"position","participantIdentity":"p-1","entityId":42,"values":{"x":1.5},"provenance":{"sourceVersion":"capture-1","detail":"test"}}
            """;
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(Line));
        NdjsonTelemetrySource source = new();

        var results = await ReadAllAsync(source.ReadAsync(
            stream,
            TelemetryReadOptions.Default,
            CancellationToken.None));

        Assert.HasCount(1, results);
        Assert.IsTrue(results[0].IsSuccess);
        Assert.AreEqual(42L, results[0].Value?.EntityId);
        Assert.AreEqual("capture-1", results[0].Value?.Provenance.SourceVersion);
    }

    [TestMethod]
    public async Task OversizedLineReturnsBoundedFailure()
    {
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(new string('x', 33)));
        NdjsonTelemetrySource source = new();
        TelemetryReadOptions options = TelemetryReadOptions.Default with { MaximumLineBytes = 32 };

        var results = await ReadAllAsync(source.ReadAsync(stream, options, CancellationToken.None));

        Assert.HasCount(1, results);
        Assert.AreEqual("capture.limit.line", results[0].Error?.Code);
    }

    [TestMethod]
    public async Task UnsupportedSchemaReturnsTypedFailure()
    {
        const string Line = """{"schemaVersion":"2","sourceSequence":1,"eventType":"position","values":{}}""";
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(Line));
        NdjsonTelemetrySource source = new();

        var results = await ReadAllAsync(source.ReadAsync(
            stream,
            TelemetryReadOptions.Default,
            CancellationToken.None));

        Assert.AreEqual("capture.schema.unsupported", results[0].Error?.Code);
    }

    private static async Task<List<Application.Results.OperationResult<Core.TelemetryEvent>>> ReadAllAsync(
        IAsyncEnumerable<Application.Results.OperationResult<Core.TelemetryEvent>> source)
    {
        List<Application.Results.OperationResult<Core.TelemetryEvent>> results = [];
        await foreach (var result in source)
        {
            results.Add(result);
        }

        return results;
    }
}
