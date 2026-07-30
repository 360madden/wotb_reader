using System.Net.Http.Json;
using System.Text.Json;
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
        exitCode = await CheckGateAndReportAsync("scan");
        break;
    case "state":
        exitCode = ShowState();
        break;
    case "probe":
        exitCode = await CheckGateAndReportAsync("probe");
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

// ── Gate check ──────────────────────────────────────────────

static async Task<int> CheckGateAndReportAsync(string command)
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null)
    {
        Console.Error.WriteLine(
            $"{command}: no web host found. Start the host with 'serve' first, then launch " +
            "a replay via the dashboard or POST /api/v1/game/launch.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    using var client = new HttpClient { BaseAddress = new Uri(hostUrl), Timeout = TimeSpan.FromSeconds(5) };

    try
    {
        var stateResponse = await client.GetAsync("/api/v1/game/state").ConfigureAwait(false);
        if (!stateResponse.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(
                $"{command}: gate check failed (HTTP {(int)stateResponse.StatusCode}). " +
                "Ensure the web host is running and a replay has been launched.");
            return (int)HarnessExitCode.ConflictOrBusy;
        }

        var state = await stateResponse.Content
            .ReadFromJsonAsync<JsonElement>()
            .ConfigureAwait(false);

        string? verificationState = state.TryGetProperty("verificationState", out JsonElement vs)
            ? vs.GetString()
            : null;

        if (verificationState == "OfflineReplayVerified")
        {
            Console.WriteLine($"{command}: offline-session gate satisfied — memory access permitted.");
            Console.WriteLine("Offset scanning is available. Use the Ghidra → Cheat Engine pipeline");
            Console.WriteLine("to discover candidate offsets, then validate with this harness.");
            Console.WriteLine($"See docs/operations/offset-discovery-guide.md for the full pipeline.");
            return 0;
        }

        string reasonCode = state.TryGetProperty("reasonCode", out JsonElement rc)
            ? rc.GetString() ?? "unknown"
            : "unknown";
        Console.Error.WriteLine(
            $"{command}: gate not satisfied — verification state is '{verificationState ?? "null"}' " +
            $"(reason: {reasonCode}). Launch a replay before scanning.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine(
            $"{command}: could not reach web host at {hostUrl}. Is 'serve' running?");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

static string? ReadRendezvousUrl()
{
    try
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string rendezvousPath = Path.Combine(localAppData, "WotBTreader", "rendezvous", "web.json");
        if (!File.Exists(rendezvousPath))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(rendezvousPath));
        JsonElement root = doc.RootElement;
        string? url = root.TryGetProperty("url", out JsonElement urlElement)
            ? urlElement.GetString()
            : null;
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }
    catch
    {
        return null;
    }
}

// ── State command ───────────────────────────────────────────

static int ShowState()
{
    ScannerState? state = ScannerStateStore.Load();
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
  scan
    Verify offline-session gate and report scan availability
  probe
    Verify offline-session gate and report probe availability

Gate: scan and probe require the web host to have a verified
      offline replay session (launch one via the dashboard first).
      The harness checks GET /api/v1/game/state via the
      rendezvous file to confirm the gate is satisfied.
");
}
