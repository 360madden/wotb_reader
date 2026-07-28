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
        exitCode = DenyMemoryAccessCommand(command);
        break;
    case "state":
        exitCode = ShowState();
        break;
    case "probe":
        exitCode = DenyMemoryAccessCommand(command);
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

// ── Helpers ─────────────────────────────────────────────────

static int DenyMemoryAccessCommand(string command)
{
    Console.Error.WriteLine(
        $"{command} is disabled pending the centralized offline-replay verification gate.");
    return (int)HarnessExitCode.UnsupportedCapability;
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
WotBTreader GameHarness — offline replay harness

Commands:
  state
    Show saved scanner state (read-only)

Unavailable pending the centralized offline-replay verification gate:
  scan
  probe
");
}
