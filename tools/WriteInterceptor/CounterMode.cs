using System.Globalization;
using System.Runtime.InteropServices;

namespace WotBTreader.WriteInterceptor;

internal static partial class CounterNative
{
    // Real CRT memcpy: the game's coordinate is copied by VCRUNTIME140
    // memcpy, so the offline mimic must fault INSIDE a genuine memcpy whose
    // x86 ABI walks the source with esi / destination with edi. P/Invoke
    // defeats JIT inlining (a 16-byte Buffer.BlockCopy is inlined and the
    // register convention is lost).
    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint memcpy(nint dest, nint src, nuint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint VirtualAlloc(
        nint lpAddress,
        nuint dwSize,
        uint flAllocationType,
        uint flProtect);
}

/// <summary>
/// Synthetic target for offline mechanism tests (no game, no external
/// compiler): publishes the address of a destination float field, writes it
/// in a loop while reporting a progress counter to a file. The interceptor
/// mode attaches to this process and must capture the writes.
///
/// FRESH38+ mimicry: the destination is written by a real CRT memcpy from a
/// separate source buffer (like the game's VCRUNTIME copy of a per-battle
/// heap struct), and the source buffer itself is written directly (the "fill
/// site" one level up). With -ArmSourceOnFirstHit the interceptor arms the
/// source page at hit time and must then capture source-kind hits at the fill
/// site.
///
/// Both buffers are NATIVE allocations (Marshal.AllocHGlobal): managed arrays
/// carry a GC header on the same page, and the JIT's write-barrier / pinning
/// helpers touch that page - the trap would land on JIT code with garbage
/// registers instead of inside msvcrt's memcpy with esi = source. Native
/// buffers have no header, so the armed page traps exactly like the game's
/// native heap struct copy.
/// </summary>
internal static class CounterMode
{
    private const int WriteIntervalMs = 25;
    private const int CopyBytes = 16; // 4 floats - same stride as the game copy

    // Page-aligned separate allocations: PAGE_GUARD traps on ANY access (read
    // OR write), so if Source and Dest shared a 4KB page the memcpy READ of
    // Source would consume the guard before the write landed, and the write
    // discriminator would see no change. VirtualAlloc each buffer on its own
    // page - exactly the game's layout (coordinate page vs copy-source page).
    private static readonly nint Source = AllocateOwnPage();
    private static readonly nint Dest = AllocateOwnPage();

    private static nint AllocateOwnPage()
    {
        nint ptr = CounterNative.VirtualAlloc(nint.Zero, 0x2000, 0x3000 /*MEM_COMMIT|MEM_RESERVE*/, 0x04 /*PAGE_READWRITE*/);
        if (ptr == nint.Zero)
        {
            throw new InvalidOperationException($"VirtualAlloc failed win32={Marshal.GetLastWin32Error()}");
        }

        return ptr;
    }

    public static int Run(string addrFile, string progressFile)
    {
        File.WriteAllText(addrFile, ((nuint)Dest).ToString("X8", CultureInfo.InvariantCulture));
        long iterations = 0;
        while (true)
        {
            // Fill site: the game's own write to the copy source buffer.
            float next = ReadFloat(Source) + 0.5f;
            WriteFloat(Source, next);

            // Copy site: the memcpy-style store the interceptor arms on. The
            // CRT memcpy faults inside msvcrt's copy loop with esi = source /
            // edi = destination, faithfully mimicking the game's VCRUNTIME
            // memcpy and giving the source-arm step the source pointer in the
            // captured registers.
            _ = CounterNative.memcpy(Dest, Source, (nuint)CopyBytes);

            iterations++;
            if (iterations % 8 == 0)
            {
                try
                {
                    File.WriteAllText(progressFile, iterations.ToString(CultureInfo.InvariantCulture));
                }
                catch
                {
                    // Progress file is diagnostic only.
                }
            }

            Thread.Sleep(WriteIntervalMs);
        }
    }

    private static float ReadFloat(nint address)
    {
        unsafe
        {
            return *(float*)address;
        }
    }

    private static void WriteFloat(nint address, float value)
    {
        unsafe
        {
            *(float*)address = value;
        }
    }
}
