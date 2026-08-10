using System.Globalization;

namespace WotBTreader.WriteInterceptor;

internal static class Program
{
    private const string Usage = """
        WotBTreader.WriteInterceptor - C# guard-page write interceptor (M2 successor)

        Modes:
          --counter -AddrFile <path> -ProgressFile <path> [--double]
              Synthetic target: publishes a static float's address, writes it
              in a loop, reports progress. For offline mechanism tests only.
              --double publishes an 8-byte Double replayTime-mimic instead
              (advances 0.016s/frame) - the -ValueSize 8 discriminator proof.

          --interceptor -Pid <n> -Addresses <0x..,0x..> -Seconds <n> -Out <path>
              [-ValueSize 4|8] [-ArmSourceOnFirstHit]
              Attach to the process, arm PAGE_GUARD on the pages holding the
              addresses, and capture every write (RIP, value, registers, RVA).
              -ValueSize selects the tracked field width (4 = float, the
              position default; 8 = Double, e.g. replayTime). The write
              discriminator is BYTE-EXACT on the tracked bytes - never a
              float-epsilon compare (a monotonic Double's low dword is a
              tiny denormal as float and would read as unchanged).
              -ArmSourceOnFirstHit arms the page holding the esi copy-source
              pointer captured at hit time, so the game's own fill write site
              (one level above a VCRUNTIME memcpy) can trap in the same window.

        Exit codes: 0 ok; 2 usage; 3 no pages armed; 4 attach failed; 5 error.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                Console.WriteLine(Usage);
                return 2;
            }

            string mode = args[0];
            return mode switch
            {
                "--counter" => RunCounter(args),
                "--interceptor" => RunInterceptor(args),
                "-h" or "--help" => PrintUsage(),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected_error:{ex.GetType().Name}:{ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 5;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static int RunCounter(string[] args)
    {
        string? addrFile = GetArg(args, "-AddrFile");
        string? progressFile = GetArg(args, "-ProgressFile");
        if (addrFile is null || progressFile is null)
        {
            Console.Error.WriteLine("--counter requires -AddrFile and -ProgressFile");
            return 2;
        }

        return CounterMode.Run(addrFile, progressFile, HasArg(args, "--double"));
    }

    private static int RunInterceptor(string[] args)
    {
        if (!uint.TryParse(GetArg(args, "-Pid"), out uint pid))
        {
            Console.Error.WriteLine("--interceptor requires -Pid <number>");
            return 2;
        }

        string? addressesArg = GetArg(args, "-Addresses");
        nuint[]? addresses = ParseAddresses(addressesArg);
        if (addresses is null || addresses.Length == 0)
        {
            Console.Error.WriteLine("--interceptor requires -Addresses <0x..,0x..>");
            return 2;
        }

        if (!double.TryParse(GetArg(args, "-Seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
        {
            Console.Error.WriteLine("--interceptor requires -Seconds > 0");
            return 2;
        }

        string? outPath = GetArg(args, "-Out");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            Console.Error.WriteLine("--interceptor requires -Out <path>");
            return 2;
        }

        int valueSize = 4;
        if (GetArg(args, "-ValueSize") is { } valueSizeText
            && (!int.TryParse(valueSizeText, out valueSize) || valueSize is not (4 or 8)))
        {
            Console.Error.WriteLine("--interceptor -ValueSize must be 4 or 8");
            return 2;
        }

        return new WriteInterceptor(
            pid,
            addresses,
            seconds,
            outPath,
            valueSize,
            HasArg(args, "-ArmSourceOnFirstHit")).Run();
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasArg(string[] args, string name)
    {
        return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }

    private static nuint[]? ParseAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var result = new List<nuint>();
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!nuint.TryParse(token.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nuint address))
            {
                return null;
            }

            result.Add(address);
        }

        return result.ToArray();
    }
}
