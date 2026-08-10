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
    public void Load_WithChains_ParsesChains()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash, withChains: true);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.IsTrue(
            result.Value.Chains!.TryGetValue("playerPositionX", out IReadOnlyList<OffsetChainHop>? hops));
        Assert.HasCount(3, hops!);
        Assert.AreEqual(OffsetChainHopKind.RootRva, hops[0].Kind);
        Assert.AreEqual(67722376, hops[0].Value);
        Assert.AreEqual(OffsetChainHopKind.MemberOffset, hops[1].Kind);
        Assert.AreEqual(12, hops[1].Value);
        Assert.AreEqual(OffsetChainHopKind.RecordOffset, hops[2].Kind);
        Assert.AreEqual(16, hops[2].Value);

        Assert.IsTrue(
            result.Value.Chains!.TryGetValue("playerPositionY", out IReadOnlyList<OffsetChainHop>? yHops));
        Assert.HasCount(4, yHops!);
        Assert.AreEqual(OffsetChainHopKind.RingIndex, yHops[2].Kind);
        Assert.AreEqual(456, yHops[2].IndexOffset);
        Assert.AreEqual(56, yHops[2].Stride);
        Assert.AreEqual(OffsetChainHopKind.RecordOffset, yHops[3].Kind);

        Assert.IsTrue(
            result.Value.Chains!.TryGetValue("playerPositionZ", out IReadOnlyList<OffsetChainHop>? zHops));
        Assert.HasCount(5, zHops!);
        Assert.AreEqual(OffsetChainHopKind.InlineOffset, zHops[2].Kind);
        Assert.AreEqual(4, zHops[2].Value);
        Assert.AreEqual(OffsetChainHopKind.EntityLookup, zHops[3].Kind);
        OffsetEntityLookupDescriptor lookup = zHops[3].EntityLookup!;
        Assert.AreEqual(72, lookup.CachedEntityOffset);
        Assert.AreEqual(28, lookup.EntityIdOffset);
        Assert.HasCount(3, lookup.TreeRootOffsets);
        Assert.AreEqual(24, lookup.TreeNodeSize);
        Assert.AreEqual(1024, lookup.MaxTreeNodes);
        Assert.AreEqual(OffsetChainHopKind.RecordOffset, zHops[4].Kind);
    }

    [TestMethod]
    public void Load_MalformedEntityLookup_DropsChainButKeepsOthers()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash, withChains: true);
        string path = System.IO.Path.Combine(directory.Path, $"{Version}.json");
        string json = File.ReadAllText(path).Replace(
            "\"maxTreeNodes\": 1024",
            "\"maxTreeNodes\": 0",
            StringComparison.Ordinal);
        File.WriteAllText(path, json);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        // The malformed entityLookup chain is dropped fail-closed.
        Assert.IsFalse(result.Value.Chains!.ContainsKey("playerPositionZ"));
        // The well-formed chains survive.
        Assert.IsTrue(result.Value.Chains.ContainsKey("playerPositionX"));
        Assert.IsTrue(result.Value.Chains.ContainsKey("playerPositionY"));
    }

    [TestMethod]
    public void Load_MalformedChain_DropsChainButLoadsTable()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash, withChains: true);
        string path = System.IO.Path.Combine(directory.Path, $"{Version}.json");
        string json = File.ReadAllText(path).Replace(
            "\"kind\": \"recordOffset\"",
            "\"kind\": \"bogusKind\"",
            StringComparison.Ordinal);
        File.WriteAllText(path, json);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.IsFalse(result.Value.Chains!.ContainsKey("playerPositionX"));
        // The legacy fields are unaffected by the dropped chain.
        Assert.HasCount(8, result.Value.Fields);
    }

    [TestMethod]
    public void Load_WithoutChains_ReturnsEmptyChains()
    {
        using var directory = new TemporaryDirectory();
        WriteTable(directory.Path, Hash);

        OperationResult<OffsetTable?> result =
            new OffsetTableReader(directory.Path).Load(Version, Hash);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.IsNotNull(result.Value);
        Assert.IsNotNull(result.Value.Chains);
        Assert.IsEmpty(result.Value.Chains);
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

    private static void WriteTable(
        string directory,
        string hash,
        bool completeEvidence = true,
        bool withChains = false)
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
              {{(withChains ? ChainsJson : string.Empty)}}
              "confidence": "none",
              "notes": "synthetic"
            }
            """);
    }

    private const string ChainsJson =
        "\"chains\": {\n"
        + "  \"playerPositionX\": [\n"
        + "    { \"kind\": \"rootRva\", \"value\": 67722376, \"note\": \"GameCoreRootRva 0x04095C88\" },\n"
        + "    { \"kind\": \"memberOffset\", \"value\": 12, \"note\": \"GameCoreAppControllerOffset\" },\n"
        + "    { \"kind\": \"recordOffset\", \"value\": 16, \"note\": \"X\" }\n"
        + "  ],\n"
        + "  \"playerPositionY\": [\n"
        + "    { \"kind\": \"rootRva\", \"value\": 67722376, \"note\": \"GameCoreRootRva 0x04095C88\" },\n"
        + "    { \"kind\": \"memberOffset\", \"value\": 8, \"note\": \"AvatarFilterHelperOffset 0x08\" },\n"
        + "    { \"kind\": \"ringIndex\", \"value\": 8, \"indexOffset\": 456, \"stride\": 56, \"note\": \"AvatarHelperRingOffset 0x08 (stride 0x38)\" },\n"
        + "    { \"kind\": \"recordOffset\", \"value\": 20, \"note\": \"Y\" }\n"
        + "  ],\n"
        + "  \"playerPositionZ\": [\n"
        + "    { \"kind\": \"rootRva\", \"value\": 67722376, \"note\": \"GameCoreRootRva 0x04095C88\" },\n"
        + "    { \"kind\": \"memberOffset\", \"value\": 12, \"note\": \"GameCoreAppControllerOffset\" },\n"
        + "    { \"kind\": \"inlineOffset\", \"value\": 4, \"note\": \"ConnectionEntitiesOffset 0x04\" },\n"
        + "    { \"kind\": \"entityLookup\", \"value\": 0, \"cachedEntityOffset\": 72, \"entityIdOffset\": 28, \"treeRootOffsets\": [28, 64, 52], \"treeNodeSize\": 24, \"treeNodeNilOffset\": 13, \"treeNodeKeyOffset\": 16, \"treeNodeValueOffset\": 20, \"treeNodeChildLessOffset\": 0, \"treeNodeChildGreaterOffset\": 8, \"treeSentinelFirstNodeOffset\": 4, \"maxTreeNodes\": 1024, \"note\": \"Entity lookup cached 0x48 roots 0x1C/0x40/0x34\" },\n"
        + "    { \"kind\": \"recordOffset\", \"value\": 24, \"note\": \"Z\" }\n"
        + "  ]\n"
        + "},\n";

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
