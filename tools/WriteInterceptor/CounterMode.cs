using System.Globalization;

namespace WotBTreader.WriteInterceptor;

/// <summary>
/// Synthetic target for offline mechanism tests (no game, no external
/// compiler): publishes the address of a static float field, then writes it
/// in a loop while reporting a progress counter to a file. The interceptor
/// mode attaches to this process and must capture the writes.
/// </summary>
internal static class CounterMode
{
    private const int WriteIntervalMs = 25;

    private static float _shared;

    public static int Run(string addrFile, string progressFile)
    {
        File.WriteAllText(addrFile, PublishAddress());
        long iterations = 0;
        while (true)
        {
            _shared += 0.5f;
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

    private static string PublishAddress()
    {
        unsafe
        {
            fixed (float* pointer = &_shared)
            {
                return ((nuint)pointer).ToString("X8", CultureInfo.InvariantCulture);
            }
        }
    }
}
