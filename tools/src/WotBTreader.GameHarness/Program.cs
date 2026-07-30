using System.Diagnostics;
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
        exitCode = await ScanAsync();
        break;
    case "state":
        exitCode = ShowState();
        break;
    case "probe":
        exitCode = await ProbeAsync();
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

static async Task<int> ScanAsync()
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null)
    {
        Console.Error.WriteLine("scan: no web host found. Start the host with 'serve' first, then launch " +
            "a replay via the dashboard or POST /api/v1/game/launch.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    int gateResult = await CheckGateAsync(hostUrl, "scan");
    if (gateResult != 0)
    {
        return gateResult;
    }

    // Gate is satisfied — show offset field status
    ShowOffsetFieldStatus();
    return 0;
}

// ── Probe command ───────────────────────────────────────────

static async Task<int> ProbeAsync()
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null)
    {
        Console.Error.WriteLine("probe: no web host found. Start the host with 'serve' first, then launch " +
            "a replay via the dashboard or POST /api/v1/game/launch.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    int gateResult = await CheckGateAsync(hostUrl, "probe");
    if (gateResult != 0)
    {
        return gateResult;
    }

    // Gate is satisfied — show detailed offset table
    ShowOffsetFieldStatus();
    ShowOffsetTableDetail();
    return 0;
}

static async Task<int> CheckGateAsync(string hostUrl, string command)
{
    // Ensure trailing slash so relative URIs compose correctly with BaseAddress.
    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(5) };

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

// ── Offset field reporting ──────────────────────────────────

static void ShowOffsetFieldStatus()
{
    string? offsetPath = FindOffsetFile();
    if (offsetPath is null)
    {
        Console.WriteLine("  No offset file found for the installed game version.");
        Console.WriteLine("  Run the Ghidra or Cheat Engine pipeline to discover offsets,");
        Console.WriteLine("  then update memory-offsets/<version>.json.");
        return;
    }

    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(offsetPath));
        JsonElement root = doc.RootElement;
        string gameVersion = root.TryGetProperty("gameVersion", out JsonElement gv) ? gv.GetString() ?? "?" : "?";
        string confidence = root.TryGetProperty("confidence", out JsonElement cf) ? cf.GetString() ?? "none" : "none";

        Console.WriteLine($"  Offset file: memory-offsets/{Path.GetFileName(offsetPath)}");
        Console.WriteLine($"  Game version: {gameVersion}");
        Console.WriteLine($"  Confidence:   {confidence}");
        Console.WriteLine();

        if (!root.TryGetProperty("offsets", out JsonElement offsets))
        {
            Console.WriteLine("  No offset fields configured.");
            return;
        }

        var fields = new (string name, string type)[]
        {
            ("replayTime",      "double"),
            ("playerHP",         "int32"),
            ("playerPositionX",  "float"),
            ("playerPositionY",  "float"),
            ("playerPositionZ",  "float"),
            ("playerYaw",        "float"),
            ("cameraPitch",      "float"),
            ("aliveTankCount",   "int32"),
        };

        Console.WriteLine("  Field status:");
        int knownCount = 0;
        foreach ((string name, string type) in fields)
        {
            long value = offsets.TryGetProperty(name, out JsonElement f) && f.TryGetInt64(out long v) ? v : 0;
            string status = value == 0 ? "unknown" : $"0x{value:X}";
            if (value != 0) knownCount++;
            Console.WriteLine($"    {name,-18} {type,-8} {status}");
        }
        Console.WriteLine($"  {knownCount}/{fields.Length} fields have known offsets.");
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        Console.WriteLine($"  Could not read offset file: {ex.Message}");
    }
}

static void ShowOffsetTableDetail()
{
    string? offsetPath = FindOffsetFile();
    if (offsetPath is null)
    {
        return;
    }

    try
    {
        string json = File.ReadAllText(offsetPath);
        // Pretty-print with indentation
        using var doc = JsonDocument.Parse(json);
        Console.WriteLine("  Raw offset table:");
        Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        Console.WriteLine($"  Could not read offset file: {ex.Message}");
    }
}

static string? FindOffsetFile()
{
    // Search for the offsets directory from the current or parent directories,
    // then find the newest offset JSON file (heuristic for installed version).
    string current = Environment.CurrentDirectory;
    for (int level = 0; level < 6; level++)
    {
        string candidate = Path.Combine(current, "memory-offsets");
        if (Directory.Exists(candidate))
        {
            var files = Directory.GetFiles(candidate, "*.json")
                .Where(f => !f.EndsWith("schema.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            return files.FirstOrDefault();
        }

        string? parent = Path.GetDirectoryName(current);
        if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            break;
        current = parent;
    }

    return null;
}

static string? ReadRendezvousUrl()
{
    try
    {
        // Same path as the Overlay's RendezvousLocator.ResolveDefaultPath()
        // and the web host's RendezvousPublisher so both processes agree.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string rendezvousPath = Path.Combine(localAppData, "WotBTreader", "rendezvous", "web.json");
        if (!File.Exists(rendezvousPath))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(rendezvousPath));
        JsonElement root = doc.RootElement;

        // Schema version guard
        if (!root.TryGetProperty("schemaVersion", out JsonElement schemaElement)
            || !string.Equals(schemaElement.GetString(), "1.0", StringComparison.Ordinal))
        {
            return null;
        }

        // Expiry guard
        if (root.TryGetProperty("expiresAtUtc", out JsonElement expiresElement))
        {
            string? expiresStr = expiresElement.GetString();
            if (expiresStr is not null
                && DateTimeOffset.TryParse(expiresStr, out DateTimeOffset expiresAt)
                && expiresAt <= DateTimeOffset.UtcNow)
            {
                return null;
            }
        }

        // The rendezvous record uses "baseUri", not "url"
        string? baseUri = root.TryGetProperty("baseUri", out JsonElement uriElement)
            ? uriElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(baseUri))
        {
            return null;
        }

        // Loopback-only guard
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? uri)
            || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
                || uri.Host.Equals("[::1]", StringComparison.Ordinal)))
        {
            return null;
        }

        // Process-alive guard: reject records from exited hosts
        if (root.TryGetProperty("processId", out JsonElement pidElement)
            && pidElement.TryGetInt32(out int processId))
        {
            try
            {
                using Process? process = Process.GetProcessById(processId);
                if (process is null || process.HasExited)
                {
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        return baseUri;
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
    Check offline-session gate + show offset field status
  probe
    Check offline-session gate + show offset field status + raw table

Gate: scan and probe require the web host to have a verified
      offline replay session (launch one via the dashboard first).
      When the gate is satisfied, scan/probe also report which
      memory-offset fields are known vs unknown.
");
}
