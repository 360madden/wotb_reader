using System.Buffers.Binary;
using System.Text;
using WotBTreader.Application.Results;
using WotBTreader.Core.Discovery;
using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// Fail-closed contract tests for the HP-diffing dump file: the machine
/// contract between the (future, gated) live region reader and the offline
/// correlator. Every malformed variant must be rejected with a stable error
/// code, never partially accepted.
/// </summary>
[TestClass]
public sealed class HpDiffSnapshotsFileTests
{
    private const string Schema = "wotbtreader.od.hp-diff.snapshots.v1";

    private static string Region(int hp)
    {
        byte[] bytes = new byte[0x100];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x48), hp);
        return Convert.ToBase64String(bytes);
    }

    private static string FileJson(int regionLength, params (double Seconds, string Bytes)[] snapshots)
    {
        StringBuilder builder = new();
        builder.Append("{\"schema\":\"").Append(Schema)
            .Append("\",\"regionLength\":").Append(regionLength)
            .Append(",\"snapshots\":[");
        for (int index = 0; index < snapshots.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"replayTimeSeconds\":")
                .Append(snapshots[index].Seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\"bytesBase64\":\"")
                .Append(snapshots[index].Bytes)
                .Append("\"}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    [TestMethod]
    public void Parse_AcceptsValidIncreasingSnapshots()
    {
        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse(FileJson(0x100, (0, Region(1000)), (1, Region(550))));

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.HasCount(2, result.Value);
        Assert.AreEqual(TimeSpan.Zero, result.Value[0].ReplayTime);
        Assert.AreEqual(TimeSpan.FromSeconds(1), result.Value[1].ReplayTime);
        Assert.HasCount(0x100, result.Value[0].Bytes);
        Assert.AreEqual(1000, BinaryPrimitives.ReadInt32LittleEndian(result.Value[0].Bytes.AsSpan(0x48)));
    }

    [TestMethod]
    public void Parse_RejectsUnknownSchema()
    {
        string json = FileJson(0x100, (0, Region(1000)))
            .Replace(Schema, "wotbtreader.od.other.v9");

        OperationResult<IReadOnlyList<RecordSnapshot>> result = HpDiffSnapshotsFile.Parse(json);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.schema", result.Error?.Code);
    }

    [TestMethod]
    public void Parse_RejectsMalformedJson()
    {
        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse("{ not json");

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.malformed", result.Error?.Code);
    }

    [TestMethod]
    public void Parse_RejectsInvalidRegionLength()
    {
        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse(FileJson(0x103, (0, Region(1000))));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.region", result.Error?.Code);
    }

    [TestMethod]
    public void Parse_RejectsOversizedRegion()
    {
        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse(FileJson(HpDiffSnapshotsFile.MaxRegionLength + 4, (0, Region(1000))));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.region", result.Error?.Code);
    }

    [TestMethod]
    public void Parse_RejectsNonIncreasingReplayTimes()
    {
        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse(FileJson(0x100, (1, Region(1000)), (1, Region(550))));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.clock", result.Error?.Code);
    }

    [TestMethod]
    public void Parse_RejectsWrongByteLength()
    {
        string shortRegion = Convert.ToBase64String(new byte[0x80]);

        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse(FileJson(0x100, (0, shortRegion)));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.length", result.Error?.Code);
    }

    [TestMethod]
    public void Parse_RejectsInvalidBase64()
    {
        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse(FileJson(0x100, (0, "!!!not-base64!!!")));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.bytes", result.Error?.Code);
    }

    [TestMethod]
    public void Parse_RejectsEmptySnapshots()
    {
        OperationResult<IReadOnlyList<RecordSnapshot>> result =
            HpDiffSnapshotsFile.Parse(FileJson(0x100));

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.hp-diff.snapshots.empty", result.Error?.Code);
    }
}
