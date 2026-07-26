using WotBTreader.Application.Replay;
using WotBTreader.Core;

namespace WotBTreader.Replays.Tests;

[TestClass]
public sealed class ReplayProbeSecurityTests
{
    [TestMethod]
    public async Task ProbeRejectsPathTraversalEntry()
    {
        byte[] archive = SyntheticReplayFactory.CreateArchive(
            ("../meta.json", "{}"u8.ToArray()),
            (ReplayFormatConstants.BattleResultsEntry, [1]),
            (ReplayFormatConstants.EventStreamEntry, [1]));

        var result = await new WotbReplayProbe().ProbeAsync(
            SyntheticReplayFactory.CreateInput(archive),
            DecoderLimits.Default,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.unsafe_entry_name", result.Error?.Code);
    }

    [TestMethod]
    public async Task ProbeRejectsDuplicateEntryCaseInsensitively()
    {
        byte[] archive = SyntheticReplayFactory.CreateArchive(
            (ReplayFormatConstants.MetadataEntry, "{}"u8.ToArray()),
            ("META.JSON", "{}"u8.ToArray()),
            (ReplayFormatConstants.BattleResultsEntry, [1]));

        var result = await new WotbReplayProbe().ProbeAsync(
            SyntheticReplayFactory.CreateInput(archive),
            DecoderLimits.Default,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.duplicate_entry", result.Error?.Code);
    }

    [TestMethod]
    public async Task ProbeRejectsMissingRequiredEntry()
    {
        byte[] archive = SyntheticReplayFactory.CreateArchive(
            (ReplayFormatConstants.MetadataEntry, "{}"u8.ToArray()),
            (ReplayFormatConstants.BattleResultsEntry, [1]));

        var result = await new WotbReplayProbe().ProbeAsync(
            SyntheticReplayFactory.CreateInput(archive),
            DecoderLimits.Default,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.missing_entry", result.Error?.Code);
    }

    [TestMethod]
    public async Task ProbeRejectsCompressionRatioAboveCallerBudget()
    {
        byte[] archive = SyntheticReplayFactory.CreateReplay();
        DecoderLimits limits = DecoderLimits.Default with
        {
            MaximumCompressionRatio = 0.5,
        };

        var result = await new WotbReplayProbe().ProbeAsync(
            SyntheticReplayFactory.CreateInput(archive),
            limits,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.compression_ratio_limit", result.Error?.Code);
    }

    [TestMethod]
    public async Task ProbeRejectsArchiveAboveCallerBudget()
    {
        byte[] archive = SyntheticReplayFactory.CreateReplay();
        DecoderLimits limits = DecoderLimits.Default with
        {
            MaximumArchiveBytes = archive.Length - 1,
        };

        var result = await new WotbReplayProbe().ProbeAsync(
            SyntheticReplayFactory.CreateInput(archive),
            limits,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.archive_size_limit", result.Error?.Code);
    }

    [TestMethod]
    public async Task CancelledProbeReturnsTypedFailure()
    {
        byte[] archive = SyntheticReplayFactory.CreateReplay();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        var result = await new WotbReplayProbe().ProbeAsync(
            SyntheticReplayFactory.CreateInput(archive),
            DecoderLimits.Default,
            cancellation.Token);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("replay.cancelled", result.Error?.Code);
    }

    [TestMethod]
    public async Task DeterministicMutationsNeverEscapeProbeOrDecoderBoundary()
    {
        byte[] valid = SyntheticReplayFactory.CreateReplay();
        for (int iteration = 1; iteration <= 32; iteration++)
        {
            int length = Math.Max(1, valid.Length - (iteration * valid.Length / 37));
            byte[] mutation = valid[..length];
            if (mutation.Length > 8)
            {
                int index = (iteration * 7919) % mutation.Length;
                mutation[index] ^= checked((byte)(iteration | 1));
            }

            ReplayInput input = SyntheticReplayFactory.CreateInput(mutation);
            var probe = await new WotbReplayProbe().ProbeAsync(
                input,
                DecoderLimits.Default,
                CancellationToken.None);
            if (probe.IsSuccess && probe.Value is not null)
            {
                _ = await new WotbReplayDecoder().DecodeAsync(
                    new ReplayDecodeRequest(
                        input,
                        DecodeRunId.New(),
                        probe.Value,
                        DecoderLimits.Default),
                    CancellationToken.None);
            }
        }
    }
}
