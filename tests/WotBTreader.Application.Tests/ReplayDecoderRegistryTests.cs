using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class ReplayDecoderRegistryTests
{
    private static readonly ReplayProbeResult SupportedProbe = new(
        IsReplay: true,
        GameVersion: "11.18.0.7",
        FormatVersion: "1",
        ArchiveEntries: [],
        ReplayCapability.Metadata,
        Warnings: []);

    [TestMethod]
    public void SelectWithOneMatchReturnsDecoder()
    {
        StubDecoder decoder = new("strict");
        ReplayDecoderRegistry registry = new([decoder]);

        OperationResult<IReplayDecoder> result = registry.Select(SupportedProbe);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreSame(decoder, result.Value);
    }

    [TestMethod]
    public void SelectWithNoMatchReturnsUnsupported()
    {
        ReplayDecoderRegistry registry = new([]);

        OperationResult<IReplayDecoder> result = registry.Select(SupportedProbe);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.decoder.unsupported", result.Error?.Code);
    }

    [TestMethod]
    public void SelectWithMultipleMatchesReturnsAmbiguous()
    {
        ReplayDecoderRegistry registry = new([new StubDecoder("a"), new StubDecoder("b")]);

        OperationResult<IReplayDecoder> result = registry.Select(SupportedProbe);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.decoder.ambiguous", result.Error?.Code);
    }

    private sealed class StubDecoder(string id) : IReplayDecoder
    {
        public DecoderDescriptor Descriptor { get; } = new(
            id,
            Version: "1",
            SchemaVersion: "1",
            SupportedGameVersions: new HashSet<string>(StringComparer.Ordinal) { "11.18.0.7" });

        public bool CanDecode(ReplayProbeResult probe) => probe.GameVersion == "11.18.0.7";

        public ValueTask<OperationResult<ReplayDecodeProjection>> DecodeAsync(
            ReplayDecodeRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
