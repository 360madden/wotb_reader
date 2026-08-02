using System.Text;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;
using WotBTreader.Core;
using WotBTreader.UltimateScanner;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class UltimateScannerUnitTests
{
    [TestMethod]
    [DataRow((ushort)0x014C, "x86", 4, 0x0000_0000_FFFF_FFFFL)]
    [DataRow((ushort)0x0000, "x64", 8, 0x0000_7FFF_FFFF_FFFFL)]
    public void TargetArchitectureResolutionSupportsAmd64AndWow64X86(
        ushort processMachine,
        string expectedArchitecture,
        int expectedPointerSize,
        long expectedMaximumAddress)
    {
        bool supported = AuthorizedProcessLease.TryResolveTargetArchitecture(
            processMachine,
            nativeMachine: 0x8664,
            out string architecture,
            out int pointerSize,
            out long maximumAddress);

        Assert.IsTrue(supported);
        Assert.AreEqual(expectedArchitecture, architecture);
        Assert.AreEqual(expectedPointerSize, pointerSize);
        Assert.AreEqual(expectedMaximumAddress, maximumAddress);
    }

    [TestMethod]
    public void TargetArchitectureResolutionRejectsUnsupportedMachinePairs()
    {
        Assert.IsFalse(AuthorizedProcessLease.TryResolveTargetArchitecture(
            processMachine: 0xAA64,
            nativeMachine: 0xAA64,
            out _,
            out _,
            out _));
        Assert.IsFalse(AuthorizedProcessLease.TryResolveTargetArchitecture(
            processMachine: 0x8664,
            nativeMachine: 0x8664,
            out _,
            out _,
            out _));
    }

    [TestMethod]
    public void SnapshotTargetRangeRejectsAddressesAboveX86UserSpace()
    {
        var filter = new MemoryScanEngine.SnapshotFilter(
            ValueSize: 4,
            MinAddress: 0x1_0000_0000,
            MaxAddress: 0x1_0000_1000,
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

        Assert.IsFalse(MemoryScanEngine.ValidateTargetAddressRange(
            baseAddress: 0x0040_0000,
            filter,
            maximumUserAddress: uint.MaxValue,
            out string? error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void SnapshotTargetRangeAcceptsExclusiveX86UpperBound()
    {
        var filter = new MemoryScanEngine.SnapshotFilter(
            ValueSize: 4,
            MinAddress: uint.MaxValue - 3L,
            MaxAddress: 0x1_0000_0000,
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

        Assert.IsTrue(MemoryScanEngine.ValidateTargetAddressRange(
            baseAddress: 0x0040_0000,
            filter,
            maximumUserAddress: uint.MaxValue,
            out string? error), error);
    }

    [TestMethod]
    public void ScannerDiagnosticsDoNotPersistCallerLabelsOrExpectedBytes()
    {
        const string sentinel = "PRIVATE_PLAYER_SENTINEL";
        byte[] expected = Encoding.UTF8.GetBytes(sentinel);
        byte[] mask = Enumerable.Repeat((byte)0xA5, expected.Length).ToArray();
        var logger = new CollectingLogger<MemoryScanDiscoverer>();
        var discoverer = new MemoryScanDiscoverer(TimeProvider.System, logger);
        var observation = new AuthorizedMemoryObservation(
            ProcessId: 0,
            ProcessStartIdentity: 1,
            CanonicalExecutablePath: @"C:\missing.exe",
            ProductVersion: "test",
            ExecutableSha256: new ContentHash(new string('a', 64)),
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            ReadGate: new AuthorizationReadGate());
        var request = new MemoryScanRequest(
            sentinel,
            "Bytes",
            expected,
            mask,
            MaxCandidates: 10,
            MinRegionSize: 1,
            Alignment: 1);

        OperationResult<MemoryScanResult> result = discoverer.Scan(
            observation,
            baseAddress: 0x0040_0000,
            request,
            CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        string diagnostics = string.Join(Environment.NewLine, logger.Messages);
        Assert.DoesNotContain(sentinel, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(expected),
            diagnostics,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(mask),
            diagnostics,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void PointerDecoderUsesTargetPointerWidth()
    {
        Assert.AreEqual(
            0xF123_4567L,
            MemoryScanDiscoverer.DecodePointer([0x67, 0x45, 0x23, 0xF1], sizeof(uint)));
        Assert.AreEqual(
            0x0000_7FFF_1234_5678L,
            MemoryScanDiscoverer.DecodePointer(
                [0x78, 0x56, 0x34, 0x12, 0xFF, 0x7F, 0x00, 0x00],
                sizeof(ulong)));
        Assert.AreEqual(
            -1L,
            MemoryScanDiscoverer.DecodePointer(
                [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
                sizeof(ulong)));
    }

    [TestMethod]
    public void AuthorizationReadGate_RevocationPreventsSubsequentNativeOperation()
    {
        var gate = new AuthorizationReadGate();
        int operationCount = 0;

        Assert.IsTrue(gate.TryExecute(
            () =>
            {
                operationCount++;
                return true;
            },
            CancellationToken.None));

        gate.Revoke();

        Assert.IsFalse(gate.TryExecute(
            () =>
            {
                operationCount++;
                return true;
            },
            CancellationToken.None));
        Assert.AreEqual(1, operationCount);
    }

    [TestMethod]
    public void AuthorizationReadGate_CancellationPreventsNativeOperation()
    {
        var gate = new AuthorizationReadGate();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            gate.TryExecute(() => true, cancellation.Token));
    }

    [TestMethod]
    public async Task AuthorizationReadGate_RevocationWaitsForAdmittedOperationThenDeniesNewReads()
    {
        var gate = new AuthorizationReadGate();
        using ManualResetEventSlim operationEntered = new();
        using ManualResetEventSlim releaseOperation = new();

        Task<bool> admittedOperation = Task.Run(() => gate.TryExecute(
            () =>
            {
                operationEntered.Set();
                releaseOperation.Wait();
                return true;
            },
            CancellationToken.None));

        Assert.IsTrue(operationEntered.Wait(TimeSpan.FromSeconds(1)));
        gate.Revoke();

        releaseOperation.Set();
        Assert.IsTrue(await admittedOperation);
        Assert.IsFalse(gate.TryExecute(() => true, CancellationToken.None));
    }

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
            DateTimeOffset.UtcNow.AddMinutes(1),
            new AuthorizationReadGate());

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
            DateTimeOffset.UtcNow.AddMinutes(1),
            new AuthorizationReadGate());

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
    public void SnapshotFilterRejectsEqualNonZeroAddressBounds()
    {
        var filter = new MemoryScanEngine.SnapshotFilter(
            ValueSize: 4,
            MinAddress: 0x140000000,
            MaxAddress: 0x140000000,
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
        StringAssert.Contains(error, "exclusive");
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
                DateTimeOffset.UtcNow.AddMinutes(1),
                new AuthorizationReadGate()),
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
            DateTimeOffset.UtcNow.AddMinutes(1),
            new AuthorizationReadGate());

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

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
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
