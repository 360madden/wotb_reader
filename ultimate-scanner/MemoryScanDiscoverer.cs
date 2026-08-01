using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;

namespace WotBTreader.UltimateScanner;

#pragma warning disable CA1873

internal sealed class MemoryScanDiscoverer
{
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint ReadableProtectionMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
    private const int ReadChunkSize = 1_048_576;
    private const int MaximumCandidatesDefault = 500;
    private const int MaximumNeighborhoodBytes = 8_192;
    private const int MaximumPointerDepth = 4;
    private const long MinimumUserAddress = 0x10000;
    private const long MaximumUserAddress = 0x00007FFF_FFFF_FFFF;

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MemoryScanDiscoverer> _logger;

    public MemoryScanDiscoverer(TimeProvider timeProvider, ILogger<MemoryScanDiscoverer> logger)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public OperationResult<MemoryScanResult> Scan(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        MemoryScanRequest request,
        CancellationToken cancellationToken,
        string scanKind = "value")
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidateScanRequest(baseAddress, request, out string? errorCode, out string? errorMessage))
        {
            return Fail(errorCode!, errorMessage!);
        }

        using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(observation, _timeProvider);
        if (lease is null)
        {
            return Fail("discover.identity_mismatch", "The authorized process identity or architecture is invalid.");
        }

        try
        {
            List<MemoryRegion> regions = EnumerateRegions(
                lease.Handle,
                request.MinRegionSize,
                request.RegionSelection,
                cancellationToken);
            List<MemoryScanCandidate> candidates = [];
            long totalMatches = 0;
            long bytesScanned = 0;
            int maxCandidates = request.MaxCandidates > 0
                ? Math.Min(request.MaxCandidates, 10_000)
                : MaximumCandidatesDefault;
            byte[] buffer = GC.AllocateUninitializedArray<byte>(ReadChunkSize + request.ExpectedValue.Length - 1);

            foreach (MemoryRegion region in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long regionOffset = 0;
                while (regionOffset < region.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int requested = (int)Math.Min(
                        buffer.Length,
                        region.Size - regionOffset);
                    long currentAddress = checked(region.BaseAddress + regionOffset);
                    if (!ReadExact(lease, currentAddress, buffer, requested, out int bytesRead))
                    {
                        regionOffset += Math.Min(ReadChunkSize, region.Size - regionOffset);
                        continue;
                    }

                    bytesScanned += bytesRead;
                    int scanLimit = bytesRead - request.ExpectedValue.Length + 1;
                    int chunkOnlyLimit = regionOffset + requested < region.Size
                        ? Math.Min(scanLimit, ReadChunkSize)
                        : scanLimit;
                    for (int offset = 0; offset < chunkOnlyLimit; offset++)
                    {
                        if (request.Alignment > 1 &&
                            ((currentAddress + offset - baseAddress) % request.Alignment + request.Alignment) % request.Alignment != 0)
                        {
                            continue;
                        }

                        if (!Matches(
                                buffer.AsSpan(offset, request.ExpectedValue.Length),
                                request.ExpectedValue,
                                request.ToleranceMask,
                                request.FloatTolerance,
                                request.FieldType))
                        {
                            continue;
                        }

                        totalMatches = Math.Min(int.MaxValue, totalMatches + 1);
                        long absoluteAddress = checked(currentAddress + offset);
                        if (candidates.Count >= maxCandidates)
                        {
                            continue;
                        }

                        bool isCopyOnWrite = request.IncludeWorkingSetClassification
                            && (region.Type & (MemMapped | MemImage)) != 0
                            && TryIsCopyOnWrite(lease.Handle, absoluteAddress);
                        candidates.Add(new MemoryScanCandidate(
                            absoluteAddress,
                            absoluteAddress - baseAddress,
                            buffer.AsSpan(offset, request.ExpectedValue.Length).ToArray(),
                            FormatSummary(buffer.AsSpan(offset, request.ExpectedValue.Length), request.FieldType),
                            GetAddressKind(region.Type),
                            isCopyOnWrite));
                    }

                    regionOffset += Math.Min(ReadChunkSize, region.Size - regionOffset);
                }
            }

            bool truncated = totalMatches > candidates.Count;
            return Success(
                observation,
                baseAddress,
                regions.Count,
                bytesScanned,
                candidates,
                (int)totalMatches,
                request.Alignment,
                truncated,
                scanKind,
                lease.Architecture);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException
            or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        {
            _logger.LogError("Memory scan failed: {ExceptionType}", exception.GetType().Name);
            return Fail("discover.scan_error", $"Scan failed: {exception.GetType().Name}");
        }
    }

    public OperationResult<MemoryScanResult> ScanNeighborhood(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        MemoryNeighborhoodRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedUserAddress(baseAddress) || request.ReferenceOffset < 0)
        {
            return Fail("discover.neighborhood.invalid_range", "The neighborhood range is invalid.");
        }

        int window = Math.Clamp(request.WindowSize, 1, MaximumNeighborhoodBytes / 2);
        long reference;
        long start;
        long end;
        try
        {
            reference = checked(baseAddress + request.ReferenceOffset);
            start = checked(reference - window);
            end = checked(reference + window);
        }
        catch (OverflowException)
        {
            return Fail("discover.neighborhood.invalid_range", "The neighborhood range overflowed.");
        }

        if (start < MinimumUserAddress
            || end <= start
            || end - 1 > MaximumUserAddress)
        {
            return Fail("discover.neighborhood.invalid_range", "The neighborhood range is outside the supported user address space.");
        }

        int length = checked((int)(end - start));
        byte[] bytes = GC.AllocateUninitializedArray<byte>(length);
        using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(observation, _timeProvider);
        if (lease is null)
        {
            return Fail("discover.identity_mismatch", "The authorized process identity or architecture is invalid.");
        }

        if (!ReadWindow(lease, start, bytes, cancellationToken))
        {
            return Fail("discover.neighborhood.read_failed", "The requested neighborhood is not fully readable.");
        }

        List<MemoryScanCandidate> candidates = [];
        for (int offset = 0; offset <= bytes.Length - 4; offset += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long absoluteAddress = start + offset;
            long baseDisplacement = absoluteAddress - baseAddress;
            int delta = offset - window;
            if (request.IncludeFloat)
            {
                float value = BitConverter.ToSingle(bytes, offset);
                if (!float.IsNaN(value) && !float.IsInfinity(value)
                    && InRange(value, request.FloatMin, request.FloatMax))
                {
                    candidates.Add(new MemoryScanCandidate(
                        absoluteAddress, baseDisplacement,
                        bytes.AsSpan(offset, 4).ToArray(),
                        $"{delta:+0;-0;0}: float={value.ToString("F3", CultureInfo.InvariantCulture)}",
                        "member-displacement",
                        request.IncludeWorkingSetClassification
                            && TryIsCopyOnWrite(lease.Handle, absoluteAddress)));
                }
            }

            if (request.IncludeInt32)
            {
                int value = BitConverter.ToInt32(bytes, offset);
                if (InRange(value, request.IntMin, request.IntMax))
                {
                    candidates.Add(new MemoryScanCandidate(
                        absoluteAddress, baseDisplacement,
                        bytes.AsSpan(offset, 4).ToArray(),
                        $"{delta:+0;-0;0}: int32={value}",
                        "member-displacement",
                        request.IncludeWorkingSetClassification
                            && TryIsCopyOnWrite(lease.Handle, absoluteAddress)));
                }
            }
        }

        if (request.IncludeDouble)
        {
            for (int offset = 0; offset <= bytes.Length - 8; offset += 8)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double value = BitConverter.ToDouble(bytes, offset);
                if (!double.IsNaN(value) && !double.IsInfinity(value))
                {
                    long absoluteAddress = start + offset;
                    candidates.Add(new MemoryScanCandidate(
                        absoluteAddress, absoluteAddress - baseAddress,
                        bytes.AsSpan(offset, 8).ToArray(),
                        $"{offset - window:+0;-0;0}: double={value.ToString("F6", CultureInfo.InvariantCulture)}",
                        "member-displacement",
                        request.IncludeWorkingSetClassification
                            && TryIsCopyOnWrite(lease.Handle, absoluteAddress)));
                }
            }
        }

        return Success(
            observation,
            baseAddress,
            1,
            bytes.Length,
            candidates,
            candidates.Count,
            1,
            false,
            "neighborhood",
            lease.Architecture);
    }

    public OperationResult<MemoryPointerChainResult> ResolvePointerChain(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        MemoryPointerChainRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupportedUserAddress(baseAddress) || request.RootRelativeOffset < 0
            || request.PointerOffsets is null
            || request.PointerOffsets.Count is < 1 or > MaximumPointerDepth
            || request.MaxDepth is < 1 or > MaximumPointerDepth
            || request.PointerOffsets.Count > request.MaxDepth)
        {
            return OperationResult.Failure<MemoryPointerChainResult>(new ApplicationError(
                "discover.pointer_chain.invalid_request",
                "Pointer chains are limited to four bounded dereferences.",
                Retryable: false));
        }

        using AuthorizedProcessLease? lease = AuthorizedProcessLease.Open(observation, _timeProvider);
        if (lease is null)
        {
            return OperationResult.Failure<MemoryPointerChainResult>(new ApplicationError(
                "discover.identity_mismatch",
                "The authorized process identity or architecture is invalid.",
                Retryable: false));
        }

        long rootAddress;
        try { rootAddress = checked(baseAddress + request.RootRelativeOffset); }
        catch (OverflowException)
        {
            return OperationResult.Failure<MemoryPointerChainResult>(new ApplicationError(
                "discover.pointer_chain.invalid_request", "The pointer root overflowed.", Retryable: false));
        }

        if (rootAddress < MinimumUserAddress || rootAddress > MaximumUserAddress)
        {
            return OperationResult.Failure<MemoryPointerChainResult>(new ApplicationError(
                "discover.pointer_chain.invalid_request", "The pointer root is outside the supported user address range.", Retryable: false));
        }

        List<MemoryPointerChainCandidate> candidates = [];
        int rejected = 0;
        long current = rootAddress;
        List<long> traversed = [rootAddress];
        HashSet<long> visited = [rootAddress];
        for (int depth = 0; depth < request.PointerOffsets.Count && depth < request.MaxDepth; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { current = checked(current + request.PointerOffsets[depth]); }
            catch (OverflowException) { rejected++; break; }
            if (current < MinimumUserAddress || current > MaximumUserAddress)
            {
                rejected++;
                break;
            }

            byte[] pointerBytes = new byte[IntPtr.Size];
            if (!ReadExact(lease, current, pointerBytes, pointerBytes.Length, out _))
            {
                rejected++;
                break;
            }

            long next = BitConverter.ToInt64(pointerBytes, 0);
            if (next < MinimumUserAddress || next > MaximumUserAddress)
            {
                rejected++;
                break;
            }

            if (!visited.Add(next))
            {
                rejected++;
                break;
            }

            current = next;
            traversed.Add(current);
            if (depth == request.PointerOffsets.Count - 1
                || depth == request.MaxDepth - 1)
            {
                candidates.Add(new MemoryPointerChainCandidate(
                    rootAddress, current, traversed.ToArray(), "pointer-chain"));
            }
        }

        return OperationResult.Success(new MemoryPointerChainResult(
            _timeProvider.GetUtcNow(),
            candidates,
            rejected));
    }

    internal static bool ValidateScanRequest(
        long baseAddress,
        MemoryScanRequest request,
        out string? errorCode,
        out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;
        if (!IsSupportedUserAddress(baseAddress))
        {
            errorCode = "discover.invalid_base";
            errorMessage = "The module base address is outside the supported user address range.";
            return false;
        }

        ArgumentNullException.ThrowIfNull(request);

        if (request.ExpectedValue is not { Length: > 0 and <= 64 })
        {
            errorCode = "discover.invalid_value";
            errorMessage = "Expected value must contain between 1 and 64 bytes.";
            return false;
        }

        if (request.ToleranceMask is not null
            && request.ToleranceMask.Length != request.ExpectedValue.Length)
        {
            errorCode = "discover.tolerance_mismatch";
            errorMessage = "Tolerance mask must match expected value length.";
            return false;
        }

        bool typedWidthValid = request.FieldType switch
        {
            "Float" or "Int32" => request.ExpectedValue.Length == 4,
            "Double" => request.ExpectedValue.Length == 8,
            "Bytes" => true,
            _ => false,
        };

        if (!typedWidthValid)
        {
            errorCode = "discover.invalid_value_width";
            errorMessage = "Float and Int32 values require 4 bytes; Double values require 8 bytes.";
            return false;
        }

        if (request.FieldType == "Float"
            && !float.IsFinite(BitConverter.ToSingle(request.ExpectedValue)))
        {
            errorCode = "discover.invalid_value";
            errorMessage = "Float expected values must be finite.";
            return false;
        }

        if (!Enum.IsDefined(request.ValueKind)
            || request.Alignment is not (1 or 2 or 4 or 8)
            || request.MinRegionSize < 0
            || request.MaxCandidates < 0
            || (request.FloatTolerance.HasValue
                && (!float.IsFinite(request.FloatTolerance.Value)
                    || request.FloatTolerance.Value < 0
                    || request.FieldType != "Float"
                    || request.ToleranceMask is not null)))
        {
            errorCode = "discover.invalid_options";
            errorMessage = "Alignment or scan limits are invalid.";
            return false;
        }

        if (request.RegionSelection == MemoryRegionSelection.None)
        {
            errorCode = "discover.invalid_regions";
            errorMessage = "At least one region type must be selected.";
            return false;
        }

        return true;
    }

    private static bool IsSupportedUserAddress(long address) =>
        address is >= MinimumUserAddress and <= MaximumUserAddress;

    private static List<MemoryRegion> EnumerateRegions(
        Microsoft.Win32.SafeHandles.SafeProcessHandle handle,
        long minSize,
        MemoryRegionSelection selection,
        CancellationToken cancellationToken)
    {
        List<MemoryRegion> regions = [];
        long address = MinimumUserAddress;
        nuint mbiSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nuint result = NativeMethods.VirtualQueryEx(handle, (nint)address, out MemoryBasicInformation mbi, mbiSize);
            if (result == 0 || mbi.RegionSize == 0) break;

            bool committed = (mbi.State & MemCommit) != 0;
            bool readable = (mbi.Protect & ReadableProtectionMask) != 0
                && (mbi.Protect & (PageNoAccess | PageGuard)) == 0;
            bool typeSelected = (mbi.Type & MemPrivate) != 0 && selection.HasFlag(MemoryRegionSelection.Private)
                || (mbi.Type & MemMapped) != 0 && selection.HasFlag(MemoryRegionSelection.Mapped)
                || (mbi.Type & MemImage) != 0 && selection.HasFlag(MemoryRegionSelection.Image);
            long size = checked((long)mbi.RegionSize);
            long baseAddress = mbi.BaseAddress.ToInt64();
            long regionEnd = checked(baseAddress + size);
            long boundedStart = Math.Max(baseAddress, MinimumUserAddress);
            long boundedEnd = Math.Min(regionEnd, MaximumUserAddress + 1);
            long boundedSize = boundedEnd - boundedStart;
            if (committed && readable && typeSelected && boundedSize >= minSize)
            {
                regions.Add(new MemoryRegion(boundedStart, boundedSize, mbi.Type, mbi.Protect));
            }

            if (regionEnd <= baseAddress || regionEnd >= MaximumUserAddress + 1)
            {
                break;
            }

            address = regionEnd;
        }

        return regions;
    }

    private static bool ReadExact(
        AuthorizedProcessLease lease,
        long address,
        byte[] target,
        int length,
        out int bytesRead)
    {
        bytesRead = 0;
        if (length <= 0 || length > target.Length || address <= 0)
        {
            return false;
        }

        if (!lease.TryRead((nint)address, target, 0, length, out nuint read))
        {
            return false;
        }

        bytesRead = checked((int)read);
        return bytesRead == length;
    }

    private static bool ReadWindow(
        AuthorizedProcessLease lease,
        long start,
        byte[] target,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < target.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long current = checked(start + offset);
            nuint querySize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
            if (NativeMethods.VirtualQueryEx(
                    lease.Handle,
                    (nint)current,
                    out MemoryBasicInformation mbi,
                    querySize) == 0
                || mbi.RegionSize == 0
                || (mbi.State & MemCommit) == 0
                || (mbi.Protect & ReadableProtectionMask) == 0
                || (mbi.Protect & (PageNoAccess | PageGuard)) != 0)
            {
                return false;
            }

            long regionEnd = checked(mbi.BaseAddress.ToInt64() + (long)mbi.RegionSize);
            if (current < mbi.BaseAddress.ToInt64() || current >= regionEnd)
            {
                return false;
            }

            int length = (int)Math.Min(
                Math.Min(ReadChunkSize, target.Length - offset),
                regionEnd - current);
            byte[] part = GC.AllocateUninitializedArray<byte>(length);
            if (!lease.TryRead((nint)current, part, 0, length, out nuint read)
                || read != (nuint)length)
            {
                return false;
            }
            part.CopyTo(target, offset);
            offset += length;
        }
        return true;
    }

    internal static bool Matches(ReadOnlySpan<byte> observed, byte[] expected, byte[]? tolerance)
    {
        return Matches(observed, expected, tolerance, null, null);
    }

    internal static bool Matches(
        ReadOnlySpan<byte> observed,
        byte[] expected,
        byte[]? tolerance,
        float? floatTolerance,
        string? fieldType)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (observed.Length < expected.Length
            || (tolerance is not null && tolerance.Length != expected.Length))
        {
            return false;
        }

        if (floatTolerance.HasValue)
        {
            if (fieldType != "Float"
                || expected.Length != 4
                || !float.IsFinite(floatTolerance.Value)
                || floatTolerance.Value < 0)
            {
                return false;
            }

            float expectedValue = BitConverter.ToSingle(expected);
            float observedValue = BitConverter.ToSingle(observed);
            return float.IsFinite(expectedValue)
                && float.IsFinite(observedValue)
                && MathF.Abs(observedValue - expectedValue) <= floatTolerance.Value;
        }

        for (int index = 0; index < expected.Length; index++)
        {
            if (tolerance is not null && tolerance[index] != 0) continue;
            if (observed[index] != expected[index]) return false;
        }
        return true;
    }

    private static string GetAddressKind(uint type) =>
        (type & MemImage) != 0 ? "image-mapping"
        : (type & MemMapped) != 0 ? "mapped-mapping"
        : (type & MemPrivate) != 0 ? "private-mapping"
        : "unknown";

    private static bool TryIsCopyOnWrite(
        Microsoft.Win32.SafeHandles.SafeProcessHandle handle,
        long address)
    {
        // PSAPI_WORKING_SET_EX_INFORMATION is two pointer-sized fields on x64.
        // Use a byte buffer so native writes cannot be lost through boxed structs.
        byte[] information = new byte[IntPtr.Size * 2];
        GCHandle pinned = GCHandle.Alloc(information, GCHandleType.Pinned);
        try
        {
            BitConverter.GetBytes((nint)address).CopyTo(information, 0);
            if (!NativeMethods.QueryWorkingSetEx(
                    handle,
                    pinned.AddrOfPinnedObject(),
                    (uint)information.Length))
            {
                return false;
            }

            return HasCopyOnWriteEvidence(BitConverter.ToUInt64(information, IntPtr.Size));
        }
        finally { pinned.Free(); }
    }

    internal static bool HasCopyOnWriteEvidence(ulong attributes)
    {
        bool valid = (attributes & 1UL) != 0;
        bool shared = (attributes & (1UL << 15)) != 0;
        // PSAPI_WORKING_SET_EX_BLOCK.Win32Protection occupies eleven bits.
        uint protection = (uint)((attributes >> 4) & 0x7FF);
        bool copyOnWriteProtection = protection is 0x08 or 0x80;
        return valid && !shared && copyOnWriteProtection;
    }

    private static string FormatSummary(ReadOnlySpan<byte> bytes, string fieldType) => fieldType switch
    {
        "Float" when bytes.Length >= 4 => BitConverter.ToSingle(bytes).ToString("F3", CultureInfo.InvariantCulture),
        "Int32" when bytes.Length >= 4 => BitConverter.ToInt32(bytes).ToString(CultureInfo.InvariantCulture),
        "Double" when bytes.Length >= 8 => BitConverter.ToDouble(bytes).ToString("F6", CultureInfo.InvariantCulture),
        _ => Convert.ToHexString(bytes),
    };

    private static bool InRange(float value, float? min, float? max) =>
        (!min.HasValue || value >= min.Value) && (!max.HasValue || value <= max.Value);

    private static bool InRange(int value, int? min, int? max) =>
        (!min.HasValue || value >= min.Value) && (!max.HasValue || value <= max.Value);

    private OperationResult<MemoryScanResult> Success(
        AuthorizedMemoryObservation observation,
        long baseAddress,
        int regions,
        long bytes,
        IReadOnlyList<MemoryScanCandidate> candidates,
        int totalMatches,
        int alignment,
        bool truncated,
        string scanKind,
        string leaseArchitecture) =>
        OperationResult.Success(new MemoryScanResult(
            _timeProvider.GetUtcNow(),
            baseAddress,
            regions,
            bytes,
            candidates,
            totalMatches,
            leaseArchitecture,
            Path.GetFileName(observation.CanonicalExecutablePath),
            0,
            alignment,
            truncated,
            scanKind));

    private static OperationResult<MemoryScanResult> Fail(string code, string message) =>
        OperationResult.Failure<MemoryScanResult>(new ApplicationError(code, message));

    private readonly record struct MemoryRegion(long BaseAddress, long Size, uint Type, uint Protection);

}
