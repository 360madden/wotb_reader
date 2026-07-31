using System.Diagnostics;
using System.Globalization;
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
    case "start":
    case "start-game":
        exitCode = await StartGameAsync();
        break;
    case "state":
        exitCode = ShowState();
        break;
    case "probe":
        exitCode = await ProbeAsync();
        break;
    case "discover":
        exitCode = await DiscoverAsync(args);
        break;
    case "discover-snapshot":
    case "snapshot":
        exitCode = await SnapshotAsync(args);
        break;
    case "discover-compare":
    case "compare":
        exitCode = await CompareSnapshotAsync(args);
        break;
    case "discover-nearby":
    case "nearby":
        exitCode = await NearbyAsync(args);
        break;
    case "discover-discard":
    case "discard":
        exitCode = await DiscardSessionAsync(args);
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

static async Task<int> StartGameAsync()
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null)
    {
        Console.Error.WriteLine("start: no web host found. Start the host with 'serve' first.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(10) };

    try
    {
        Console.WriteLine("Starting game launcher...");
        HttpResponseMessage response = await client.PostAsync("/api/v1/game/start", null).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(body);
            int pid = doc.RootElement.TryGetProperty("pid", out JsonElement p) && p.TryGetInt32(out int id) ? id : 0;
            Console.WriteLine($"Game launched (PID {pid}).");
            return 0;
        }

        Console.Error.WriteLine($"start: HTTP {(int)response.StatusCode} — {Truncate(body, 200)}");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"start: could not reach web host at {hostUrl}.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

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

// ── Discover command ────────────────────────────────────────

static async Task<int> DiscoverAsync(string[] args)
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null)
    {
        Console.Error.WriteLine("discover: no web host found.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: discover <fieldName> <fieldType> <expectedValue> [tolerance]");
        Console.Error.WriteLine("  fieldType: Float, Int32, or Double");
        Console.Error.WriteLine("  expectedValue: the value to search for (e.g. 42.5 or 1200)");
        Console.Error.WriteLine("  tolerance: optional +/- tolerance for floats (e.g. 1.0)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Examples:");
        Console.Error.WriteLine("  discover playerPositionX Float 42.5 1.0");
        Console.Error.WriteLine("  discover playerHP Int32 1200");
        Console.Error.WriteLine("  discover playerYaw Float 1.57 0.1");
        return (int)HarnessExitCode.InvalidInput;
    }

    int gateResult = await CheckGateAsync(hostUrl, "discover");
    if (gateResult != 0)
        return gateResult;

    string fieldName = args[1];
    string? fieldType = NormaliseFieldType(args[2]);
    if (fieldType is null)
    {
        Console.Error.WriteLine($"Invalid fieldType '{args[2]}'. Use Float, Int32, or Double.");
        return (int)HarnessExitCode.InvalidInput;
    }

    if (!TryParseExpectedValue(args[3], fieldType, out byte[] expectedValue))
    {
        Console.Error.WriteLine($"Cannot parse '{args[3]}' as {fieldType}.");
        return (int)HarnessExitCode.InvalidInput;
    }

    byte[]? tolerance = null;
    if (args.Length >= 5 && fieldType == "Float"
        && float.TryParse(args[4], out float tol) && tol > 0)
    {
        tolerance = BuildFloatTolerance(expectedValue, tol);
    }

    // Ensure trailing slash.
    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromSeconds(30),
    };

    var payload = new
    {
        fieldName,
        fieldType,
        expectedValueHex = Convert.ToHexString(expectedValue),
        toleranceMaskHex = tolerance is not null
            ? Convert.ToHexString(tolerance) : null,
        maxCandidates = 200,
        minRegionSize = 4096L,
    };

    Console.WriteLine($"\u250c Scanning for {fieldName} ({fieldType})...");
    Console.WriteLine($"\u2502 Expected: {FormatExpectedValue(expectedValue, fieldType)}");
    if (tolerance is not null)
        Console.WriteLine($"\u2502 Tolerance: ±{args[4]} ({fieldType})");
    Console.WriteLine($"\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");

    try
    {
        HttpResponseMessage response = await client
            .PostAsJsonAsync("/api/v1/game/discover", payload)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.Error.WriteLine(
                $"discover: HTTP {(int)response.StatusCode} — {Truncate(body, 200)}");
            return (int)HarnessExitCode.ConflictOrBusy;
        }

        using var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement root = doc.RootElement;

        int regions = root.TryGetProperty("regionsScanned", out JsonElement rs)
            ? rs.GetInt32() : 0;
        long bytes = root.TryGetProperty("bytesScanned", out JsonElement bs)
            ? bs.GetInt64() : 0;

        Console.WriteLine($"Scanned {regions} regions ({FormatBytes(bytes)})");

        if (!root.TryGetProperty("candidates", out JsonElement candidates)
            || candidates.GetArrayLength() == 0)
        {
            Console.WriteLine("No candidates found.");

            int total = root.TryGetProperty("totalMatchesBeforeTruncation", out JsonElement tm)
                ? tm.GetInt32() : 0;
            if (total > 0)
                Console.WriteLine($"({total} matches before 200-candidate cap)");

            return 0;
        }

        Console.WriteLine($"Candidates: {candidates.GetArrayLength()}");
        Console.WriteLine();
        Console.WriteLine($"{"Relative Offset",-20} {"Absolute",-20} {"Value",-16}");
        Console.WriteLine(new string('─', 58));

        int shown = 0;
        foreach (JsonElement c in candidates.EnumerateArray())
        {
            if (shown >= 50)
            {
                Console.WriteLine($"... and {candidates.GetArrayLength() - 50} more");
                break;
            }

            string relOffset = c.TryGetProperty("relativeOffset", out JsonElement ro)
                ? ro.GetString() ?? "?" : "?";
            string absAddr = c.TryGetProperty("absoluteAddress", out JsonElement aa)
                ? aa.GetString() ?? "?" : "?";
            string summary = c.TryGetProperty("valueSummary", out JsonElement vs)
                ? vs.GetString() ?? "?" : "?";

            Console.WriteLine($"{relOffset,-20} {absAddr,-20} {summary,-16}");
            shown++;
        }

        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine(
            $"discover: could not reach web host at {hostUrl}.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

static string? NormaliseFieldType(string input) => input.ToLowerInvariant() switch
{
    "float" => "Float",
    "int32" => "Int32",
    "int" => "Int32",
    "double" => "Double",
    _ => null,
};

static bool TryParseExpectedValue(
    string input, string fieldType, out byte[] value)
{
    value = [];
    return fieldType switch
    {
        "Float" => float.TryParse(input, out float f)
            ? (value = BitConverter.GetBytes(f)) is not null
            : false,
        "Int32" => int.TryParse(input, out int i)
            ? (value = BitConverter.GetBytes(i)) is not null
            : false,
        "Double" => double.TryParse(input, out double d)
            ? (value = BitConverter.GetBytes(d)) is not null
            : false,
        _ => false,
    };
}

static byte[] BuildFloatTolerance(byte[] expected, float tolerance)
{
    // IEEE 754 single-precision float, little-endian:
    //   byte 0: mantissa bits 0-7   (LSB)
    //   byte 1: mantissa bits 8-15
    //   byte 2: mantissa bits 16-22 + exponent LSB
    //   byte 3: exponent bits 24-30 + sign bit  (MSB)
    //
    // Map tolerance to wildcard byte count:
    //   ±0.01 → 1 wildcard (LSB mantissa only)
    //   ±0.1  → 2 wildcards
    //   ±1.0+ → 3 wildcards (allows any value with same sign/exp)
    int wildcards = tolerance switch
    {
        <= 0.01f => 1,
        <= 0.1f => 2,
        _ => 3,
    };

    byte[] mask = new byte[4];
    // Little-endian: wildcard the least significant bytes first.
    // The bytes we keep (non-zero mask) are the most significant ones.
    for (int i = wildcards; i < 4; i++)
        mask[i] = 0xFF;

    return mask;
}

static string FormatExpectedValue(byte[] bytes, string fieldType) => fieldType switch
{
    "Float" when bytes.Length >= 4 =>
        $"{BitConverter.ToSingle(bytes, 0):F3}",
    "Int32" when bytes.Length >= 4 =>
        $"{BitConverter.ToInt32(bytes, 0)}",
    "Double" when bytes.Length >= 8 =>
        $"{BitConverter.ToDouble(bytes, 0):F6}",
    _ => Convert.ToHexString(bytes),
};

static string FormatBytes(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
};

static string Truncate(string value, int maxLength) =>
    value.Length <= maxLength ? value : value[..maxLength] + "...";

// ── Snapshot command ───────────────────────────────────────

static async Task<int> SnapshotAsync(string[] args)
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null) return HostNotFound("snapshot");
    int gateResult = await CheckGateAsync(hostUrl, "snapshot");
    if (gateResult != 0) return gateResult;

    int valueSize = 4;
    float? floatMin = null, floatMax = null;
    int? intMin = null, intMax = null;

    if (args.Length > 1 && int.TryParse(args[1], out int sz))
        valueSize = Math.Clamp(sz, 2, 8);
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--float-min" && i + 1 < args.Length
            && float.TryParse(args[i + 1], out float fmin)) { floatMin = fmin; i++; }
        else if (args[i] == "--float-max" && i + 1 < args.Length
            && float.TryParse(args[i + 1], out float fmax)) { floatMax = fmax; i++; }
        else if (args[i] == "--int-min" && i + 1 < args.Length
            && int.TryParse(args[i + 1], out int imin)) { intMin = imin; i++; }
        else if (args[i] == "--int-max" && i + 1 < args.Length
            && int.TryParse(args[i + 1], out int imax)) { intMax = imax; i++; }
    }

    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr), Timeout = TimeSpan.FromMinutes(2) };

    Console.WriteLine($"Creating snapshot (valueSize={valueSize}, float=[{floatMin},{floatMax}], int=[{intMin},{intMax}])...");
    var response = await client.PostAsJsonAsync("/api/v1/game/discover/snapshot",
        new { valueSize, floatMin, floatMax, intMin, intMax, minAddress = 0L, maxAddress = 0L }).ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"snapshot: HTTP {(int)response.StatusCode}");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    string? sid = json.RootElement.TryGetProperty("sessionId", out JsonElement e) ? e.GetString() : null;
    Console.WriteLine($"Session: {sid}");
    return 0;
}

// ── Compare command ────────────────────────────────────────

static async Task<int> CompareSnapshotAsync(string[] args)
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null) return HostNotFound("compare");
    int gateResult = await CheckGateAsync(hostUrl, "compare");
    if (gateResult != 0) return gateResult;

    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: compare <sessionId> [changed|unchanged|increased|decreased]");
        return (int)HarnessExitCode.InvalidInput;
    }

    string sessionId = args[1];
    string mode = args.Length > 2 ? args[2] : "changed";

    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr), Timeout = TimeSpan.FromMinutes(2) };

    Console.WriteLine($"Comparing session {sessionId} (mode={mode})...");
    var response = await client.PostAsJsonAsync(
        $"/api/v1/game/discover/compare/{sessionId}",
        new { compareMode = mode, maxCandidates = 100 }).ConfigureAwait(false);

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"compare: HTTP {(int)response.StatusCode}");
        return (int)HarnessExitCode.ConflictOrBusy;
    }

    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    JsonElement root = json.RootElement;
    Console.WriteLine($"Changed={ReadInt(root, "changedCount")}, " +
        $"Unchanged={ReadInt(root, "unchangedCount")}, " +
        $"Increased={ReadInt(root, "increasedCount")}, " +
        $"Decreased={ReadInt(root, "decreasedCount")}");
    if (root.TryGetProperty("candidates", out JsonElement cands))
    {
        int shown = 0;
        foreach (var c in cands.EnumerateArray())
        {
            if (shown++ >= 20) { Console.WriteLine("... and more"); break; }
            Console.WriteLine($"  {ReadStr(c, "relativeOffset")} = {ReadStr(c, "valueSummary")}");
        }
    }
    return 0;
}

// ── Nearby (neighborhood) command ──────────────────────────

static async Task<int> NearbyAsync(string[] args)
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null) return HostNotFound("nearby");
    int gateResult = await CheckGateAsync(hostUrl, "nearby");
    if (gateResult != 0) return gateResult;

    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: nearby <refOffset> [--window <bytes>] [--float-min <f>] [--float-max <f>]");
        Console.Error.WriteLine("Example: nearby 0x0317A810 --window 512");
        return (int)HarnessExitCode.InvalidInput;
    }

    long refOffset = ParseHexOrDecimal(args[1]);
    int window = 512;
    float? floatMin = null, floatMax = null;
    int? intMin = null, intMax = null;

    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--window" && i + 1 < args.Length
            && int.TryParse(args[i + 1], out int w)) { window = w; i++; }
        else if (args[i] == "--float-min" && i + 1 < args.Length
            && float.TryParse(args[i + 1], out float fmin)) { floatMin = fmin; i++; }
        else if (args[i] == "--float-max" && i + 1 < args.Length
            && float.TryParse(args[i + 1], out float fmax)) { floatMax = fmax; i++; }
        else if (args[i] == "--int-min" && i + 1 < args.Length
            && int.TryParse(args[i + 1], out int imin)) { intMin = imin; i++; }
        else if (args[i] == "--int-max" && i + 1 < args.Length
            && int.TryParse(args[i + 1], out int imax)) { intMax = imax; i++; }
    }

    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr), Timeout = TimeSpan.FromSeconds(30) };

    Console.WriteLine($"Neighborhood scan at 0x{refOffset:X} (±{window} bytes)...");
    var response = await client.PostAsJsonAsync("/api/v1/game/discover/neighborhood",
        new
        {
            referenceOffset = refOffset,
            windowSize = window,
            includeFloat = true,
            includeInt32 = true,
            includeDouble = false,
            floatMin,
            floatMax,
            intMin,
            intMax
        }).ConfigureAwait(false);

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"nearby: HTTP {(int)response.StatusCode}");
        return (int)HarnessExitCode.ConflictOrBusy;
    }

    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    JsonElement root = json.RootElement;
    if (root.TryGetProperty("candidates", out JsonElement cands))
    {
        Console.WriteLine($"Candidates: {cands.GetArrayLength()}");
        Console.WriteLine($"{"Delta",-8} {"Relative Offset",-16} {"Value",-30}");
        Console.WriteLine(new string('-', 56));
        foreach (var c in cands.EnumerateArray())
        {
            string summary = ReadStr(c, "valueSummary");
            string relOff = ReadStr(c, "relativeOffset");
            // Extract delta from summary (e.g. "+4: float=0.500")
            Console.WriteLine($"{summary,-56} {relOff}");
        }
    }
    return 0;
}

// ── Discard command ────────────────────────────────────────

static async Task<int> DiscardSessionAsync(string[] args)
{
    string? hostUrl = ReadRendezvousUrl();
    if (hostUrl is null) return HostNotFound("discard");
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: discard <sessionId>");
        return (int)HarnessExitCode.InvalidInput;
    }
    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr) };
    var response = await client.DeleteAsync($"/api/v1/game/discover/session/{args[1]}").ConfigureAwait(false);
    Console.WriteLine($"Discarded {args[1]}: HTTP {(int)response.StatusCode}");
    return 0;
}

static int HostNotFound(string cmd)
{
    Console.Error.WriteLine($"{cmd}: no web host found.");
    return (int)HarnessExitCode.UnsupportedCapability;
}

static long ParseHexOrDecimal(string s) =>
    s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : long.Parse(s, CultureInfo.InvariantCulture);

static int ReadInt(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

static string ReadStr(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out JsonElement v) ? v.GetString() ?? "?" : "?";

static string? ReadRendezvousUrl()
{
    try
    {
        // Same path as the Overlay's RendezvousLocator.ResolveDefaultPath()
        // and the web host's RendezvousPublisher so both processes agree.
        // WOTB_TREADER_RENDEZVOUS_PATH overrides it for hermetic black-box
        // tests so a stray local host never leaks into them.
        string? overridePath = Environment.GetEnvironmentVariable("WOTB_TREADER_RENDEZVOUS_PATH");
        string rendezvousPath = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WotBTreader",
                "rendezvous",
                "web.json")
            : overridePath;
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
  start, start-game
    Launch the installed WoT Blitz executable (no replay required).
  state
    Show saved scanner state (read-only)
  scan
    Check offline-session gate + show offset field status
  probe
    Check offline-session gate + show offset field status + raw table

  discover <fieldName> <fieldType> <value> [tolerance]
    Scan game process memory for a known value to discover
    the offset of an unknown memory field.

  discover-snapshot <valueSize> [--float-min <f>] [--float-max <f>]
    Create a snapshot of all values in committed memory.
    Example: discover-snapshot 4 --float-min -500 --float-max 500

  discover-compare <sessionId> <mode>
    Compare current memory against a stored snapshot.
    Modes: changed, unchanged, increased, decreased
    Example: discover-compare 000001 changed

  discover-nearby <refOffset> [--window <bytes>]
    Read memory around a known offset and report all
    float/int/double values as candidates.
    Example: discover-nearby 0x0317A810 --window 256

  discover-discard <sessionId>
    Discard a stored snapshot session.

Gate: all discover commands require the web host to have a
      verified offline replay session (launch one via the
      dashboard first).
");
}
