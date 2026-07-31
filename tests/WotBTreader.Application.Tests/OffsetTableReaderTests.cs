using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Core;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class OffsetTableReaderTests
{
    private const string Version = "11.19.0.10";
    private static readonly string Hash = new string('a', ContentHash.Sha256HexLength);

    [TestMethod]
    public void Load_ValidExactHash_ReturnsTable()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(Hash, result.Value.ExecutableSha256);
        Assert.AreEqual(
            OffsetFieldStatus.Verified,
            result.Value.Fields.Single(field => field.Name == "playerYaw").Status);
    }

    [TestMethod]
    public void Load_EmptyHash_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, string.Empty);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("offset.hash_missing", result.Error?.Code);
    }

    [TestMethod]
    public void Load_MalformedHash_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, new string('z', ContentHash.Sha256HexLength));

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("offset.hash_missing", result.Error?.Code);
    }

    [TestMethod]
    public void Load_DifferentHash_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, new string('b', ContentHash.Sha256HexLength));

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("offset.hash_mismatch", result.Error?.Code);
    }

    [TestMethod]
    public void Load_InvalidObservedHash_ReturnsStableFailure()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash);

        foreach (string? observedHash in new[] { null, string.Empty, " ", "not-a-hash" })
        {
            OperationResult<OffsetTable?> result =
                new OffsetTableReader(directory.Path).Load(Version, observedHash!);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual("offset.invalid_observed_hash", result.Error?.Code);
        }
    }

    [TestMethod]
    public void Load_UnknownValidationField_ReturnsStableFailure()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash);
        string path = System.IO.Path.Combine(directory.Path, $"{Version}.json");
        string json = File.ReadAllText(path).Replace(
            "\"playerYaw\": {",
            "\"unknownField\": {",
            StringComparison.Ordinal);
        File.WriteAllText(path, json);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("offset.field_validation_unknown_field", result.Error?.Code);
    }

    [TestMethod]
    public void Load_VerifiedDeclarationWithIncompleteEvidence_RemainsCandidate()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash, completeEvidence: false);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(
            OffsetFieldStatus.Candidate,
            result.Value!.Fields.Single(field => field.Name == "playerYaw").Status);
    }

    [TestMethod]
    public void Load_CanceledBeforeRead_Throws()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new OffsetTableReader(directory.Path).Load(Version, Hash, cancellation.Token));
    }

    private static void WriteTable(string directory, string hash, bool completeEvidence = true)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{Version}.json"),
            $$"""
            {
              "schemaVersion": 1,
              "gameVersion": "{{Version}}",
              "executableSha256": "{{hash}}",
              "discoveredAtUtc": "2026-07-31T00:00:00Z",
              "fieldValidation": {
                "playerYaw": {
                  "status": "Verified",
                  "evidence": [
                    { "provenanceKind": "StaticAnalysis", "sourceTool": "synthetic-ghidra", "notes": "fixture" },
                    { "provenanceKind": "GameHarness", "sourceTool": "synthetic-harness", "notes": "fixture" }
                  ],
                  "independentProcessLaunches": {{(completeEvidence ? 2 : 1)}},
                  "independentReplays": {{(completeEvidence ? 2 : 1)}},
                  "harnessInvariantsPassed": {{completeEvidence.ToString().ToLowerInvariant()}},
                  "leadApproved": {{completeEvidence.ToString().ToLowerInvariant()}},
                  "decoderAuditorApproved": {{completeEvidence.ToString().ToLowerInvariant()}}
                }
              },
              "offsets": {
                "replayTime": 0,
                "playerHP": 0,
                "playerPositionX": 0,
                "playerPositionY": 0,
                "playerPositionZ": 0,
                "playerYaw": 30552080,
                "cameraPitch": 0,
                "aliveTankCount": 0
              },
              "confidence": "none",
              "notes": "synthetic"
            }
            """);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WotBTreader.Application.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
