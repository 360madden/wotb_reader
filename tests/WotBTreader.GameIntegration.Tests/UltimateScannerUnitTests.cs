using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.UltimateScanner;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class UltimateScannerUnitTests
{
    [TestMethod]
    public void PatternMatcherTreatsNonZeroMaskBytesAsWildcards()
    {
        Assert.IsTrue(MemoryScanDiscoverer.Matches(
            [0x48, 0xFF, 0x90],
            [0x48, 0x8B, 0x90],
            [0x00, 0xFF, 0x00]));
        Assert.IsFalse(MemoryScanDiscoverer.Matches(
            [0x49, 0xFF, 0x90],
            [0x48, 0x8B, 0x90],
            [0x00, 0xFF, 0x00]));
    }

    [TestMethod]
    public void FloatToleranceMatchesDecodedValuesWithoutWildcardingExponentBytes()
    {
        byte[] expected = BitConverter.GetBytes(100.0f);
        byte[] withinTolerance = BitConverter.GetBytes(100.05f);
        byte[] outsideTolerance = BitConverter.GetBytes(100.2f);

        Assert.IsTrue(MemoryScanDiscoverer.Matches(
            withinTolerance, expected, null, 0.1f, "Float"));
        Assert.IsFalse(MemoryScanDiscoverer.Matches(
            outsideTolerance, expected, null, 0.1f, "Float"));
        Assert.IsFalse(MemoryScanDiscoverer.Matches(
            withinTolerance, expected, null, -0.1f, "Float"));
    }

    [TestMethod]
    public void FloatExpectedValueRejectsNaNAndInfinity()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var request = new MemoryScanRequest(
                "field",
                "Float",
                BitConverter.GetBytes(value),
                null,
                10,
                1,
                ValueKind: MemoryValueKind.FloatValue);

            bool valid = MemoryScanDiscoverer.ValidateScanRequest(
                0x140000000,
                request,
                out string? errorCode,
                out _);

            Assert.IsFalse(valid);
            Assert.AreEqual("discover.invalid_value", errorCode);
        }
    }

    [TestMethod]
    public void FloatToleranceRejectsNonFloatAndMaskConflictRequests()
    {
        var request = new MemoryScanRequest(
            "field",
            "Int32",
            [0, 0, 0, 0],
            [0, 0, 0, 0],
            10,
            1,
            ValueKind: MemoryValueKind.Int32Value,
            FloatTolerance: 0.1f);

        bool valid = MemoryScanDiscoverer.ValidateScanRequest(
            0x140000000,
            request,
            out string? errorCode,
            out _);

        Assert.IsFalse(valid);
        Assert.AreEqual("discover.invalid_options", errorCode);
    }

    [TestMethod]
    public void CompareRejectsUnknownModeBeforeOpeningProcess()
    {
        var engine = new MemoryScanEngine(
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryScanEngine>.Instance);
        var observation = new AuthorizedMemoryObservation(
            1,
            1,
            "C:\\game.exe",
            "test",
            new ContentHash(new string('a', 64)),
            DateTimeOffset.UtcNow.AddMinutes(1));

        OperationResult<MemoryScanEngine.CompareResult> result = engine.Compare(
            observation,
            0x140000000,
            "000001",
            "not-a-mode",
            10,
            false);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.invalid_options", result.Error?.Code);
    }

    [TestMethod]
    public void CompareRejectsOutOfRangeCandidateCapBeforeOpeningProcess()
    {
        var engine = new MemoryScanEngine(
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryScanEngine>.Instance);
        var observation = new AuthorizedMemoryObservation(
            1,
            1,
            "C:\\game.exe",
            "test",
            new ContentHash(new string('a', 64)),
            DateTimeOffset.UtcNow.AddMinutes(1));

        OperationResult<MemoryScanEngine.CompareResult> result = engine.Compare(
            observation,
            0x140000000,
            "000001",
            "changed",
            0,
            false);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.invalid_options", result.Error?.Code);
    }

    [TestMethod]
    public void SnapshotFilterRejectsInvertedTypedRanges()
    {
        var filter = new MemoryScanEngine.SnapshotFilter(
            ValueSize: 4,
            MinAddress: 0,
            MaxAddress: 0,
            FloatMin: 10,
            FloatMax: 1,
            IntMin: null,
            IntMax: null,
            LongMin: null,
            LongMax: null,
            UIntMin: null,
            UIntMax: null,
            ValueKind: MemoryValueKind.FloatValue,
            Alignment: 1,
            RegionSelection: MemoryRegionSelection.Default);

        Assert.IsFalse(MemoryScanEngine.ValidateFilter(filter, out string? error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    [DataRow("Float", 8)]
    [DataRow("Int32", 8)]
    [DataRow("Double", 4)]
    public void TypedScanRejectsInconsistentValueWidth(string fieldType, int valueSize)
    {
        var request = new MemoryScanRequest(
            "field",
            fieldType,
            new byte[valueSize],
            null,
            10,
            1,
            ValueKind: fieldType switch
            {
                "Float" => MemoryValueKind.FloatValue,
                "Int32" => MemoryValueKind.Int32Value,
                _ => MemoryValueKind.DoubleValue,
            });

        bool valid = MemoryScanDiscoverer.ValidateScanRequest(
            0x140000000,
            request,
            out string? errorCode,
            out _);

        Assert.IsFalse(valid);
        Assert.AreEqual("discover.invalid_value_width", errorCode);
    }

    [TestMethod]
    public void SnapshotFilterRejectsUndefinedValueKinds()
    {
        var request = new MemoryScanRequest(
            "field",
            "Bytes",
            [0x01],
            null,
            1,
            1,
            ValueKind: (MemoryValueKind)999);

        bool valid = MemoryScanDiscoverer.ValidateScanRequest(
            0x140000000,
            request,
            out string? errorCode,
            out _);

        Assert.IsFalse(valid);
        Assert.AreEqual("discover.invalid_options", errorCode);
    }

    [TestMethod]
    public void DiscovererRejectsBaseAddressesOutsideSupportedUserRange()
    {
        var request = new MemoryScanRequest(
            "field",
            "Bytes",
            [0x01],
            null,
            1,
            1);

        bool valid = MemoryScanDiscoverer.ValidateScanRequest(
            0x00008000_00000000,
            request,
            out string? errorCode,
            out _);

        Assert.IsFalse(valid);
        Assert.AreEqual("discover.invalid_base", errorCode);
    }

    [TestMethod]
    public void SnapshotEngineRejectsUndefinedValueKinds()
    {
        var filter = new MemoryScanEngine.SnapshotFilter(
            ValueSize: 1,
            MinAddress: 0,
            MaxAddress: 0,
            FloatMin: null,
            FloatMax: null,
            IntMin: null,
            IntMax: null,
            LongMin: null,
            LongMax: null,
            UIntMin: null,
            UIntMax: null,
            ValueKind: (MemoryValueKind)999,
            Alignment: 1,
            RegionSelection: MemoryRegionSelection.Default);

        Assert.IsFalse(MemoryScanEngine.ValidateFilter(filter, out string? error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void SnapshotFilterSupportsFullUInt64Range()
    {
        var filter = new MemoryScanEngine.SnapshotFilter(
            ValueSize: 8,
            MinAddress: 0,
            MaxAddress: 0,
            FloatMin: null,
            FloatMax: null,
            IntMin: null,
            IntMax: null,
            LongMin: null,
            LongMax: null,
            UIntMin: 0,
            UIntMax: ulong.MaxValue,
            ValueKind: MemoryValueKind.UInt64Value,
            Alignment: 8,
            RegionSelection: MemoryRegionSelection.Default);

        Assert.IsTrue(MemoryScanEngine.ValidateFilter(filter, out string? error), error);
    }

    [TestMethod]
    public void SnapshotBaseCompatibilityRequiresTheCapturedModuleBase()
    {
        var snapshot = new MemoryScanEngine.Snapshot(
            "000001",
            DateTimeOffset.UnixEpoch,
            new AuthorizedMemoryObservation(
                1,
                1,
                "C:\\game.exe",
                "test",
                new ContentHash(new string('a', 64)),
                DateTimeOffset.UtcNow.AddMinutes(1)),
            0x140000000,
            new MemoryScanEngine.SnapshotFilter(
                1, 0, 0, null, null, null, null, null, null, null, null,
                MemoryValueKind.Bytes, 1, MemoryRegionSelection.Default),
            [],
            0,
            0,
            null,
            0);

        Assert.IsTrue(MemoryScanEngine.IsSnapshotBaseCompatible(snapshot, 0x140000000));
        Assert.IsFalse(MemoryScanEngine.IsSnapshotBaseCompatible(snapshot, 0x150000000));
    }

    [TestMethod]
    public void SnapshotFilterRejectsAddressesOutsideSupportedUserRange()
    {
        var filter = new MemoryScanEngine.SnapshotFilter(
            ValueSize: 4,
            MinAddress: 0x10000,
            MaxAddress: 0x00008000_00000000,
            FloatMin: null,
            FloatMax: null,
            IntMin: null,
            IntMax: null,
            LongMin: null,
            LongMax: null,
            UIntMin: null,
            UIntMax: null,
            ValueKind: MemoryValueKind.Int32Value,
            Alignment: 4,
            RegionSelection: MemoryRegionSelection.Default);

        Assert.IsFalse(MemoryScanEngine.ValidateFilter(filter, out string? error));
        Assert.IsNotNull(error);
        Assert.IsTrue(MemoryScanEngine.IsSupportedUserAddress(0x10000));
        Assert.IsTrue(MemoryScanEngine.IsSupportedUserAddress(0x00007FFF_FFFF_FFFF));
        Assert.IsFalse(MemoryScanEngine.IsSupportedUserAddress(0x00008000_00000000));
    }

    [TestMethod]
    public void NeighborhoodRejectsAnOutOfRangeBaseBeforeOpeningProcess()
    {
        var discoverer = new MemoryScanDiscoverer(
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MemoryScanDiscoverer>.Instance);
        var request = new MemoryNeighborhoodRequest(
            ReferenceOffset: 0,
            WindowSize: 64,
            IncludeFloat: true,
            IncludeInt32: true,
            IncludeDouble: false,
            FloatMin: null,
            FloatMax: null,
            IntMin: null,
            IntMax: null);
        var observation = new AuthorizedMemoryObservation(
            1,
            1,
            "C:\\game.exe",
            "test",
            new ContentHash(new string('a', 64)),
            DateTimeOffset.UtcNow.AddMinutes(1));

        OperationResult<MemoryScanResult> result = discoverer.ScanNeighborhood(
            observation,
            0x00008000_00000000,
            request,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("discover.neighborhood.invalid_range", result.Error?.Code);
    }

    [TestMethod]
    [DataRow(0x140000001L, 0x140000000L, 4, 0x140000004L)]
    [DataRow(0x140000004L, 0x140000000L, 4, 0x140000004L)]
    [DataRow(0x140000005L, 0x140000003L, 8, 0x14000000BL)]
    public void SnapshotStartAlignsUpFromAnUnalignedMinimum(
        long address,
        long origin,
        int alignment,
        long expected)
    {
        Assert.AreEqual(expected, MemoryScanEngine.AlignAddressUp(address, origin, alignment));
    }

    [TestMethod]
    public void SnapshotStartAlignmentHandlesNegativeDisplacement()
    {
        Assert.AreEqual(
            0x10004L,
            MemoryScanEngine.AlignAddressUp(0x10001, 0x10004, 4));
    }

    [TestMethod]
    public void CopyOnWriteEvidenceDecodesBothWriteCopyProtections()
    {
        const ulong valid = 1UL;
        const ulong shared = 1UL << 15;

        Assert.IsTrue(MemoryScanDiscoverer.HasCopyOnWriteEvidence(valid | (0x08UL << 4)));
        Assert.IsTrue(MemoryScanDiscoverer.HasCopyOnWriteEvidence(valid | (0x80UL << 4)));
        Assert.IsFalse(MemoryScanDiscoverer.HasCopyOnWriteEvidence(
            valid | (0x08UL << 4) | shared));
    }
}
