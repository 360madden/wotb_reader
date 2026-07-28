using System.Diagnostics;
using WotBTreader.GameHarness;

if (args.Length == 0)
{
    PrintHelp();
    return 0;
}

string command = args[0].ToLowerInvariant();

int exitCode;
switch (command)
{
    case "scan":
        exitCode = await RunScanAsync(args[1..]);
        break;
    case "state":
        exitCode = ShowState();
        break;
    case "probe":
        exitCode = await RunProbeAsync();
        break;
    case "help":
    case "--help":
    case "-h":
        PrintHelp();
        exitCode = 0;
        break;
    default:
        exitCode = UnknownCommand(command);
        break;
}

return exitCode;

// ── Scan command ────────────────────────────────────────────

static async Task<int> RunScanAsync(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: scan <type> <value> [--pid <id>] [--narrow]");
        Console.Error.WriteLine("  type: int32, float, double");
        Console.Error.WriteLine("  --narrow: intersect with previous scan results");
        return 2;
    }

    string type = args[0].ToLowerInvariant();
    if (!double.TryParse(args[1], out double rawValue))
    {
        Console.Error.WriteLine($"Invalid value: {args[1]}");
        return 2;
    }

    int? pid = null;
    bool narrow = false;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--pid" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
        { pid = p; i++; }
        else if (args[i] == "--narrow")
        { narrow = true; }
    }

    // Auto-detect PID
    if (pid is null)
    {
        pid = FindGameProcessId();
        if (pid is null)
        {
            Console.Error.WriteLine("Game process not found. Use --pid <id> to specify.");
            return 3;
        }
        Console.WriteLine($"Found game process: PID {pid}");
    }

    using var scanner = new MemoryOffsetScanner();
    if (!scanner.Attach(pid.Value))
    {
        Console.Error.WriteLine($"Failed to attach to process {pid}.");
        return 3;
    }

    Console.WriteLine($"Attached to PID {pid}. Scanning for {type} = {rawValue}...");

    HashSet<long>? previous = null;
    if (narrow)
    {
        var state = MemoryOffsetScanner.LoadState();
        if (state is { CandidateCount: > 0 })
        {
            previous = [..state.TopCandidates];
            Console.WriteLine($"Narrowing from {previous.Count} previous candidates...");
        }
        else
        {
            Console.WriteLine("No previous state found — doing full scan.");
        }
    }

    var sw = Stopwatch.StartNew();
    int count = type switch
    {
        "int32" => scanner.ScanInt32((int)rawValue, previous),
        "float" => scanner.ScanFloat((float)rawValue, previous),
        "double" => scanner.ScanDouble(rawValue, previous),
        _ => throw new ArgumentException($"Unknown type: {type}"),
    };
    sw.Stop();

    Console.WriteLine($"Found {count} candidate offset(s) in {sw.Elapsed.TotalSeconds:F1}s.");

    if (count is > 0 and <= 20)
    {
        Console.WriteLine("\nTop candidates (base-relative offsets):");
        foreach (long offset in scanner.Candidates.OrderBy(c => c).Take(20))
            Console.WriteLine($"  0x{offset:X}  ({offset})");
    }

    if (count > 0)
    {
        scanner.SaveState();
        Console.WriteLine($"\nState saved. Run again with --narrow and a new value to narrow down.");
        Console.WriteLine($"When only 1-3 candidates remain, those are your offsets.");
    }
    else if (narrow)
    {
        Console.WriteLine("No candidates match. The value changed in an unexpected way.");
        Console.WriteLine("Start a fresh scan (without --narrow) with a new value.");
    }

    return 0;
}

// ── State command ───────────────────────────────────────────

static int ShowState()
{
    var state = MemoryOffsetScanner.LoadState();
    if (state is null)
    {
        Console.WriteLine("No scanner state found.");
        return 0;
    }

    Console.WriteLine($"Scanner State:");
    Console.WriteLine($"  Process ID:    {state.ProcessId}");
    Console.WriteLine($"  Game Version:  {state.ExecutableVersion}");
    Console.WriteLine($"  Base Address:  0x{state.BaseAddress:X}");
    Console.WriteLine($"  Candidates:    {state.CandidateCount}");
    if (state.TopCandidates.Count > 0)
    {
        Console.WriteLine($"  Top offsets:");
        foreach (long c in state.TopCandidates)
            Console.WriteLine($"    0x{c:X}  ({c})");
    }
    return 0;
}

// ── Probe command ───────────────────────────────────────────

static async Task<int> RunProbeAsync()
{
    int? pid = FindGameProcessId();
    if (pid is null)
    {
        Console.WriteLine("Game not running.");
        return 0;
    }

    Console.WriteLine($"Game PID: {pid}");

    using var scanner = new MemoryOffsetScanner();
    if (!scanner.Attach(pid.Value))
    {
        Console.WriteLine("Failed to attach (may need admin rights).");
        return 3;
    }

    var state = MemoryOffsetScanner.LoadState();
    string version = "unknown";
    if (state is not null) version = state.ExecutableVersion;

    Console.WriteLine($"Attached. Version: {version}");
    Console.WriteLine("Ready for scanning. Use: scan int32 <value>");
    return 0;
}

// ── Helpers ─────────────────────────────────────────────────

static int? FindGameProcessId()
{
    Process[] processes = Process.GetProcessesByName("wotblitz");
    return processes.Length > 0 ? processes[0].Id : null;
}

static int UnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    PrintHelp();
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine(@"
WotBTreader GameHarness — offline replay memory scanner

Commands:
  scan <type> <value> [--pid <id>] [--narrow]
    Scan game memory for a value.
    type: int32, float, double
    --narrow: intersect with previous scan to filter candidates

  state
    Show current scanner state (PID, version, candidate count)

  probe
    Check if game is running and attachable

Usage flow (finding HP offset):
  1. Start game replay, note current HP (e.g. 1500)
  2. scan int32 1500               → finds ~5000 candidates
  3. Wait for HP to change (e.g. 1200)
  4. scan int32 1200 --narrow      → narrows to ~50 candidates
  5. Repeat until 1-3 candidates remain
  6. Those are your HP offset(s)!

  Do the same for float (position X/Y/Z) and double (replay time).
");
}
