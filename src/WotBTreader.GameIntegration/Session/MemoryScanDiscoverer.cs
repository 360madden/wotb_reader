using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WotBTreader.Application.Game;
using WotBTreader.Application.Results;

namespace WotBTreader.GameIntegration.Session;

/// <summary>
/// Scans the game process's committed memory regions for specific byte patterns
/// (float, int32, double values). Uses ReadProcessMemory for each committed
/// private/mapped region that is readable and avoids image sections (EXE/DLL
/// mappings are excluded as they don't hold dynamic game state).
/// </summary>
internal sealed class MemoryScanDiscoverer
{
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;
    private const uint PageReadonly = 0x02;
    private const uint PageReadwrite = 0x04;
    private const uint PageWritecopy = 0x08;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadwrite = 0x40;

    private const int MaxScanValueLength = 8;
    private const int ReadChunkSize = 65_536;
    private const int MaximumCandidatesDefault = 500;

    private readonly TimeProvider _timeProvider;

    public MemoryScanDiscoverer(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public OperationResult<MemoryScanResult> Scan(
        int processId,
        long baseAddress,
        MemoryScanRequest request)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        if (baseAddress == 0)
            return Fail("discover.invalid_base", "The main module base address must be non-zero.");
        if (request.ExpectedValue is not { Length: > 0 and <= MaxScanValueLength })
            return Fail("discover.invalid_value", $"Expected value must be 1–{MaxScanValueLength} bytes.");
        if (request.ToleranceMask is not null
            && request.ToleranceMask.Length != request.ExpectedValue.Length)
            return Fail("discover.tolerance_mismatch", "Tolerance mask must match expected value length.");

        int maxCandidates = request.MaxCandidates > 0 ? request.MaxCandidates : MaximumCandidatesDefault;
        int valueLen = request.ExpectedValue.Length;
        byte[] tolerance = request.ToleranceMask ?? new byte[valueLen];

        SafeProcessHandle? handle = null;
        try
        {
            handle = OpenScanHandle(processId);
            if (handle is null || handle.IsInvalid)
                return Fail("discover.open_failed", "Could not open game process for scanning.");

            List<MemoryRegion> regions = EnumerateScanRegions(handle, request.MinRegionSize);
            if (regions.Count == 0)
                return Success(baseAddress, 0, 0, [], 0);

            List<MemoryScanCandidate> candidates = [];
            int totalMatches = 0;
            long bytesScanned = 0;
            byte[] readBuffer = new byte[ReadChunkSize + valueLen];

            foreach (MemoryRegion region in regions)
            {
                if (candidates.Count >= maxCandidates) break;

                long remaining = region.RegionSize;
                long currentAddr = region.BaseAddress;

                while (remaining > 0 && candidates.Count < maxCandidates)
                {
                    int chunkSize = remaining >= ReadChunkSize ? ReadChunkSize : (int)remaining;
                    int bytesToRead = chunkSize + valueLen;

                    GCHandle pinned = GCHandle.Alloc(readBuffer, GCHandleType.Pinned);
                    try
                    {
                        if (!NativeMethods.ReadProcessMemory(handle,
                                (nint)currentAddr,
                                pinned.AddrOfPinnedObject(),
                                (nuint)bytesToRead,
                                out nuint bytesRead)
                            || bytesRead == 0)
                        {
                            currentAddr += ReadChunkSize;
                            remaining -= ReadChunkSize;
                            continue;
                        }

                        bytesScanned += (long)bytesRead;
                        int scannedBytes = (int)bytesRead;
                        int matchEnd = scannedBytes - valueLen + 1;

                        for (int i = 0; i < matchEnd && candidates.Count < maxCandidates; i++)
                        {
                            if (!Matches(readBuffer, i, request.ExpectedValue, tolerance))
                                continue;

                            totalMatches++;
                            long absAddr = currentAddr + i;
                            byte[] observed = new byte[valueLen];
                            Array.Copy(readBuffer, i, observed, 0, valueLen);

                            candidates.Add(new MemoryScanCandidate(
                                absAddr,
                                absAddr - baseAddress,
                                observed,
                                FormatSummary(observed, request.FieldType)));
                        }
                    }
                    finally
                    {
                        pinned.Free();
                    }

                    currentAddr += ReadChunkSize;
                    remaining -= ReadChunkSize;
                }
            }

            return Success(baseAddress, regions.Count, bytesScanned, candidates,
                totalMatches > maxCandidates ? totalMatches : 0);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail("discover.scan_error", $"Scan failed: {ex.GetType().Name}");
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static SafeProcessHandle? OpenScanHandle(int processId)
    {
        const uint VmRead = 0x0010;
        const uint QueryInfo = 0x0400;
        var h = NativeMethods.OpenProcess(VmRead | QueryInfo, false, checked((uint)processId));
        return h.IsInvalid ? null : h;
    }

    private static List<MemoryRegion> EnumerateScanRegions(SafeProcessHandle handle, long minSize)
    {
        List<MemoryRegion> regions = [];
        long address = 0;

        while (true)
        {
            int result = NativeMethods.VirtualQueryEx(handle, (nint)address,
                out MemoryBasicInformation mbi,
                (uint)Marshal.SizeOf<MemoryBasicInformation>());
            if (result == 0) break;

            bool isCommitted = (mbi.State & MemCommit) != 0;
            bool isPrivate = (mbi.Type & MemPrivate) != 0;
            bool isMapped = (mbi.Type & MemMapped) != 0;
            bool isImage = (mbi.Type & MemImage) != 0;
            bool isReadable = (mbi.Protect & (PageReadonly | PageReadwrite
                | PageWritecopy | PageExecuteRead | PageExecuteReadwrite)) != 0;

            if (isCommitted && !isImage && (isPrivate || isMapped)
                && isReadable && mbi.RegionSize >= (ulong)minSize)
            {
                regions.Add(new MemoryRegion(
                    mbi.BaseAddress.ToInt64(), checked((long)mbi.RegionSize)));
            }

            address = mbi.BaseAddress + checked((long)mbi.RegionSize);
            if (address < 0) break;
        }

        return regions;
    }

    private static bool Matches(byte[] buf, int offset, byte[] expected, byte[] tolerance)
    {
        for (int j = 0; j < expected.Length; j++)
        {
            if (tolerance[j] != 0) continue;
            if (buf[offset + j] != expected[j]) return false;
        }
        return true;
    }

    private static string FormatSummary(byte[] bytes, string fieldType) => fieldType switch
    {
        "Float" when bytes.Length >= 4 =>
            BitConverter.ToSingle(bytes, 0).ToString("F3", CultureInfo.InvariantCulture),
        "Int32" when bytes.Length >= 4 =>
            BitConverter.ToInt32(bytes, 0).ToString(CultureInfo.InvariantCulture),
        "Double" when bytes.Length >= 8 =>
            BitConverter.ToDouble(bytes, 0).ToString("F6", CultureInfo.InvariantCulture),
        _ => Convert.ToHexString(bytes),
    };

    private OperationResult<MemoryScanResult> Success(
        long baseAddress, int regions, long bytes,
        IReadOnlyList<MemoryScanCandidate> candidates, int truncated) =>
        OperationResult.Success(new MemoryScanResult(
            _timeProvider.GetUtcNow(), baseAddress, regions, bytes,
            candidates, truncated));

    private static OperationResult<MemoryScanResult> Fail(
        string code, string message) =>
        OperationResult.Failure<MemoryScanResult>(new ApplicationError(code, message));

    private readonly record struct MemoryRegion(long BaseAddress, long RegionSize);
}
