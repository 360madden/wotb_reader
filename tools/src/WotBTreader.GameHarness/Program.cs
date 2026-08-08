using System.Diagnostics;
using System.Globalization;
using System.Net;
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
    case "discover-pattern":
    case "pattern":
        exitCode = await DiscoverPatternAsync(args);
        break;
    case "discover-pointer-chain":
    case "pointer-chain":
        exitCode = await DiscoverPointerChainAsync(args);
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
    case "discover-campaign":
    case "campaign":
        exitCode = await RunCampaignAsync(args);
        break;
    case "discover-instruction-snapshot":
    case "instruction-snapshot":
        exitCode = await InstructionSnapshotAsync(args);
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
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null)
    {
        Console.Error.WriteLine("start: no web host found. Start the host with 'serve' first.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(10) };
    AddCapabilityHeader(client, rendezvous!);

    try
    {
        Console.WriteLine("Starting game launcher...");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/game/start", null).ConfigureAwait(false);
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
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null)
    {
        Console.Error.WriteLine("scan: no web host found. Start the host with 'serve' first, then launch " +
            "a replay via the dashboard or POST /api/v1/game/launch.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    int gateResult = await CheckGateAsync(hostUrl, "scan", rendezvous!);
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
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null)
    {
        Console.Error.WriteLine("probe: no web host found. Start the host with 'serve' first, then launch " +
            "a replay via the dashboard or POST /api/v1/game/launch.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    int gateResult = await CheckGateAsync(hostUrl, "probe", rendezvous!);
    if (gateResult != 0)
    {
        return gateResult;
    }

    // Gate is satisfied — show detailed offset table
    ShowOffsetFieldStatus();
    ShowOffsetTableDetail();
    return 0;
}

static async Task<int> CheckGateAsync(
    string hostUrl,
    string command,
    RendezvousConnection rendezvous)
{
    ArgumentNullException.ThrowIfNull(rendezvous);
    // Ensure trailing slash so relative URIs compose correctly with BaseAddress.
    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(5) };
    AddCapabilityHeader(client, rendezvous);

    try
    {
        using HttpResponseMessage stateResponse = await client.GetAsync("/api/v1/game/state").ConfigureAwait(false);
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

// ── Instruction-first position snapshot ────────────────────

static async Task<int> InstructionSnapshotAsync(string[] args)
{
    int durationMilliseconds = 5_000;
    int maxHits = 16;
    for (int index = 1; index < args.Length; index++)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine("instruction-snapshot: option value missing.");
            return (int)HarnessExitCode.InvalidArguments;
        }

        string option = args[index];
        string value = args[++index];
        if (option == "--seconds"
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            && seconds is >= 1 and <= 5)
        {
            durationMilliseconds = seconds * 1_000;
        }
        else if (option == "--max-hits"
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedHits)
            && parsedHits is >= 1 and <= 64)
        {
            maxHits = parsedHits;
        }
        else
        {
            Console.Error.WriteLine("instruction-snapshot: invalid option or bound.");
            return (int)HarnessExitCode.InvalidArguments;
        }
    }

    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null)
    {
        return HostNotFound("instruction-snapshot");
    }

    int gateResult = await CheckGateAsync(hostUrl, "instruction-snapshot", rendezvous!);
    if (gateResult != 0)
    {
        return gateResult;
    }

    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromSeconds(12),
    };
    AddCapabilityHeader(client, rendezvous!);
    try
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/game/discover/instruction-snapshot",
            new { durationMilliseconds, maxHits }).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string error = "discover.instruction_snapshot.failed";
            try
            {
                using JsonDocument errorDocument = JsonDocument.Parse(body);
                if (errorDocument.RootElement.TryGetProperty("error", out JsonElement errorElement))
                {
                    error = errorElement.GetString() ?? error;
                }
            }
            catch (JsonException)
            {
                // Keep the stable fallback; never echo an unbounded response.
            }

            Console.Error.WriteLine($"instruction-snapshot: {error}");
            return (int)HarnessExitCode.ConflictOrBusy;
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string status = root.GetProperty("status").GetString() ?? "unknown";
        int hitCount = root.GetProperty("hitCount").GetInt32();
        bool cleanupProven = root.GetProperty("cleanupProven").GetBoolean();
        bool fingerprintMatched = root.GetProperty("instructionFingerprintMatched").GetBoolean();
        Console.WriteLine(
            $"instruction-snapshot: status={status} hits={hitCount} " +
            $"fingerprint={fingerprintMatched} cleanup={cleanupProven}");
        foreach (JsonElement hit in root.GetProperty("hits").EnumerateArray())
        {
            int sequence = hit.GetProperty("sequence").GetInt32();
            string objectKey = hit.GetProperty("objectKey").GetString() ?? "object-unknown";
            bool readOk = hit.GetProperty("readOk").GetBoolean();
            bool finite = hit.GetProperty("finite").GetBoolean();
            string x = hit.GetProperty("x").ToString();
            string y = hit.GetProperty("y").ToString();
            string z = hit.GetProperty("z").ToString();
            Console.WriteLine(
                $"  hit={sequence} {objectKey} read={readOk} finite={finite} xyz=({x},{y},{z}) " +
                "identity=unknown stable_root=false");
        }

        return cleanupProven && fingerprintMatched
            ? 0
            : (int)HarnessExitCode.ConflictOrBusy;
    }
    catch (Exception exception) when (
        exception is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine("instruction-snapshot: host request failed.");
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
        Console.WriteLine("  Run the Ghidra or x64dbg pipeline to discover offsets,");
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
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null)
    {
        Console.Error.WriteLine("discover: no web host found.");
        return (int)HarnessExitCode.UnsupportedCapability;
    }

    if (args.Length < 4)
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

    int gateResult = await CheckGateAsync(hostUrl, "discover", rendezvous!);
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

    float? floatTolerance = null;
    if (args.Length > 5)
    {
        Console.Error.WriteLine("discover: too many arguments.");
        return (int)HarnessExitCode.InvalidInput;
    }

    if (args.Length >= 5)
    {
        if (fieldType != "Float"
            || !float.TryParse(
                args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float toleranceValue)
            || !float.IsFinite(toleranceValue)
            || toleranceValue < 0)
        {
            Console.Error.WriteLine("Tolerance is only supported for finite, non-negative Float values.");
            return (int)HarnessExitCode.InvalidInput;
        }

        floatTolerance = toleranceValue;
    }

    // Ensure trailing slash.
    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromSeconds(30),
    };
    AddCapabilityHeader(client, rendezvous!);

    var payload = new
    {
        fieldName,
        fieldType,
        expectedValueHex = Convert.ToHexString(expectedValue),
        floatTolerance,
        maxCandidates = 200,
        minRegionSize = 4096L,
    };

    Console.WriteLine($"\u250c Scanning for {fieldName} ({fieldType})...");
    Console.WriteLine($"\u2502 Expected: {FormatExpectedValue(expectedValue, fieldType)}");
    if (floatTolerance.HasValue)
        Console.WriteLine($"\u2502 Tolerance: ±{floatTolerance.Value.ToString(CultureInfo.InvariantCulture)} ({fieldType})");
    Console.WriteLine($"\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");

    try
    {
        using HttpResponseMessage response = await client
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

            string relOffset = c.TryGetProperty("baseDisplacement", out JsonElement ro)
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
        "Float" => float.TryParse(
            input, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
            && float.IsFinite(f)
            ? (value = BitConverter.GetBytes(f)) is not null
            : false,
        "Int32" => int.TryParse(
            input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
            ? (value = BitConverter.GetBytes(i)) is not null
            : false,
        "Double" => double.TryParse(
            input, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            && double.IsFinite(d)
            ? (value = BitConverter.GetBytes(d)) is not null
            : false,
        _ => false,
    };
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

// ── Pattern command ────────────────────────────────────────

static async Task<int> DiscoverPatternAsync(string[] args)
{
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null) return HostNotFound("discover-pattern");
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: discover-pattern <fieldName> <patternHex> [toleranceMaskHex] [--alignment <1|2|4|8>]");
        Console.Error.WriteLine("  Non-zero tolerance-mask bytes are wildcards; use a hex mask such as 00FF00.");
        return (int)HarnessExitCode.InvalidInput;
    }

    int gateResult = await CheckGateAsync(hostUrl, "discover-pattern", rendezvous!);
    if (gateResult != 0) return gateResult;

    string fieldName = args[1];
    string patternHex = args[2];
    string? maskHex = args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)
        ? args[3] : null;
    int alignment = 1;
    int optionStart = maskHex is null ? 3 : 4;
    for (int i = optionStart; i < args.Length; i++)
    {
        if (args[i] == "--alignment")
        {
            if (i + 1 >= args.Length
                || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                || parsed is not (1 or 2 or 4 or 8))
            {
                Console.Error.WriteLine("discover-pattern: --alignment requires one of 1, 2, 4, or 8.");
                return (int)HarnessExitCode.InvalidInput;
            }

            alignment = parsed;
        }
        else
        {
            Console.Error.WriteLine($"discover-pattern: unknown option '{args[i]}'.");
            return (int)HarnessExitCode.InvalidInput;
        }
    }

    if (!TryParseHex(patternHex, out byte[] pattern)
        || pattern.Length is < 1 or > 64
        || (maskHex is not null && (!TryParseHex(maskHex, out byte[] mask)
            || mask.Length != pattern.Length)))
    {
        Console.Error.WriteLine("discover-pattern: pattern and mask must be even-length hexadecimal strings of equal length (1–64 bytes).");
        return (int)HarnessExitCode.InvalidInput;
    }

    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromMinutes(2) };
    AddCapabilityHeader(client, rendezvous!);
    var payload = new
    {
        fieldName,
        expectedValueHex = Convert.ToHexString(pattern),
        toleranceMaskHex = maskHex,
        maxCandidates = 200,
        minRegionSize = 4096L,
        alignment,
    };

    Console.WriteLine($"Scanning AOB pattern {patternHex}...");
    try
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/game/discover/pattern", payload).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"discover-pattern: HTTP {(int)response.StatusCode} — {Truncate(await response.Content.ReadAsStringAsync().ConfigureAwait(false), 200)}");
            return (int)HarnessExitCode.ConflictOrBusy;
        }

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        PrintScanSummary(document.RootElement);
        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine($"discover-pattern: could not reach web host at {hostUrl}.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

// ── Pointer-chain command ──────────────────────────────────

static async Task<int> DiscoverPointerChainAsync(string[] args)
{
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null) return HostNotFound("discover-pointer-chain");
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: discover-pointer-chain <rootOffset> <offset1,offset2,...>");
        Console.Error.WriteLine("Example: discover-pointer-chain 0x0317A810 0x20,0x18,0x34");
        return (int)HarnessExitCode.InvalidInput;
    }

    int gateResult = await CheckGateAsync(hostUrl, "discover-pointer-chain", rendezvous!);
    if (gateResult != 0) return gateResult;

    if (!TryParseAddress(args[1], out long rootOffset))
    {
        Console.Error.WriteLine("discover-pointer-chain: invalid root offset.");
        return (int)HarnessExitCode.InvalidInput;
    }

    string[] parts = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length is < 1 or > 4 || !parts.All(part => TryParseAddress(part, out _)))
    {
        Console.Error.WriteLine("discover-pointer-chain: provide 1–4 comma-separated decimal or hexadecimal offsets.");
        return (int)HarnessExitCode.InvalidInput;
    }

    List<long> offsets = parts.Select(ParseAddress).ToList();
    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(30) };
    AddCapabilityHeader(client, rendezvous!);
    try
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/game/discover/pointer-chain",
            new { rootRelativeOffset = rootOffset, pointerOffsets = offsets, maxDepth = 4 })
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"discover-pointer-chain: HTTP {(int)response.StatusCode}");
            return (int)HarnessExitCode.ConflictOrBusy;
        }

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement root = document.RootElement;
        Console.WriteLine($"Candidates: {(root.TryGetProperty("candidates", out JsonElement candidates) ? candidates.GetArrayLength() : 0)}");
        if (root.TryGetProperty("candidates", out candidates))
        {
            foreach (JsonElement candidate in candidates.EnumerateArray())
            {
                Console.WriteLine($"  {ReadStr(candidate, "rootAddress")} → {ReadStr(candidate, "finalAddress")}");
            }
        }
        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine($"discover-pointer-chain: could not reach web host at {hostUrl}.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

static bool TryParseHex(string text, out byte[] bytes)
{
    try
    {
        bytes = Convert.FromHexString(text);
        return true;
    }
    catch (FormatException)
    {
        bytes = [];
        return false;
    }
}

static bool TryParseAddress(string text, out long value)
{
    try
    {
        value = ParseAddress(text);
        return true;
    }
    catch (Exception exception) when (exception is FormatException or OverflowException)
    {
        value = 0;
        return false;
    }
}

static long ParseAddress(string text) =>
    text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

static void PrintScanSummary(JsonElement root)
{
    int regions = root.TryGetProperty("regionsScanned", out JsonElement regionElement)
        ? regionElement.GetInt32() : 0;
    long bytes = root.TryGetProperty("bytesScanned", out JsonElement bytesElement)
        ? bytesElement.GetInt64() : 0;
    int candidates = root.TryGetProperty("candidates", out JsonElement candidatesElement)
        ? candidatesElement.GetArrayLength() : 0;
    Console.WriteLine($"Scanned {regions} regions ({FormatBytes(bytes)}); candidates: {candidates}");
}

// ── Snapshot command ───────────────────────────────────────

static async Task<int> SnapshotAsync(string[] args)
{
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null) return HostNotFound("snapshot");
    int gateResult = await CheckGateAsync(hostUrl, "snapshot", rendezvous!);
    if (gateResult != 0) return gateResult;

    int valueSize = 4;
    float? floatMin = null, floatMax = null;
    int? intMin = null, intMax = null;
    long maxBytes = 0;

    if (args.Length > 1)
    {
        if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSize)
            || parsedSize != 4)
        {
            Console.Error.WriteLine("snapshot: valueSize must be 4 for the CLI's default Int32 snapshot.");
            return (int)HarnessExitCode.InvalidInput;
        }

        valueSize = parsedSize;
    }
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] is "--float-min" or "--float-max" or "--int-min" or "--int-max" or "--max-bytes")
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"snapshot: {args[i]} requires a value.");
                return (int)HarnessExitCode.InvalidInput;
            }

            string value = args[++i];
            bool parsed = args[i - 1] switch
            {
                "--float-min" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fmin)
                    && float.IsFinite(fmin) && (floatMin = fmin) is not null,
                "--float-max" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fmax)
                    && float.IsFinite(fmax) && (floatMax = fmax) is not null,
                "--int-min" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int imin)
                    && (intMin = imin) is not null,
                "--int-max" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int imax)
                    && (intMax = imax) is not null,
                "--max-bytes" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
                    && bytes >= 0
                    && (maxBytes = bytes) >= 0,
                _ => false,
            };
            if (!parsed)
            {
                Console.Error.WriteLine($"snapshot: invalid value for {args[i - 1]}.");
                return (int)HarnessExitCode.InvalidInput;
            }
        }
        else
        {
            Console.Error.WriteLine($"snapshot: unknown option '{args[i]}'.");
            return (int)HarnessExitCode.InvalidInput;
        }
    }

    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr), Timeout = TimeSpan.FromMinutes(2) };
    AddCapabilityHeader(client, rendezvous!);

    if ((floatMin.HasValue && floatMax.HasValue && floatMin > floatMax)
        || (floatMin.HasValue && !float.IsFinite(floatMin.Value))
        || (floatMax.HasValue && !float.IsFinite(floatMax.Value)))
    {
        Console.Error.WriteLine("snapshot: float bounds must be finite and ordered.");
        return (int)HarnessExitCode.InvalidInput;
    }

    // Float bounds are meaningless unless the host valueKind is Float; the
    // contract defaults to Int32, which silently ignored --float-min/--float-max
    // during OD-RECOVERY-004 until the API path sent valueKind explicitly.
    string valueKind = floatMin.HasValue || floatMax.HasValue ? "Float" : "Int32";
    int alignment = valueKind == "Float" ? 4 : 1;

    Console.WriteLine($"Creating snapshot (valueSize={valueSize}, valueKind={valueKind}, float=[{floatMin},{floatMax}], int=[{intMin},{intMax}], maxBytes={maxBytes})...");
    try
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/game/discover/snapshot",
            new { valueSize, valueKind, alignment, floatMin, floatMax, intMin, intMax, minAddress = 0L, maxAddress = 0L, maxBytes }).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"snapshot: HTTP {(int)response.StatusCode}");
            return (int)HarnessExitCode.ConflictOrBusy;
        }
        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        string? sid = json.RootElement.TryGetProperty("sessionId", out JsonElement e) ? e.GetString() : null;
        if (string.IsNullOrWhiteSpace(sid))
        {
            Console.Error.WriteLine("snapshot: host returned no session id.");
            return (int)HarnessExitCode.InternalFailure;
        }
        Console.WriteLine($"Session: {sid}");
        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine($"snapshot: could not reach web host at {hostUrl}.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

// ── Compare command ────────────────────────────────────────

static async Task<int> CompareSnapshotAsync(string[] args)
{
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null) return HostNotFound("compare");
    int gateResult = await CheckGateAsync(hostUrl, "compare", rendezvous!);
    if (gateResult != 0) return gateResult;

    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: compare <sessionId> [changed|unchanged|increased|decreased]");
        return (int)HarnessExitCode.InvalidInput;
    }

    if (args.Length > 3)
    {
        Console.Error.WriteLine("compare: too many arguments.");
        return (int)HarnessExitCode.InvalidInput;
    }

    string sessionId = args[1];
    string mode = args.Length > 2 ? args[2].ToLowerInvariant() : "changed";
    if (mode is not ("changed" or "unchanged" or "increased" or "decreased"))
    {
        Console.Error.WriteLine("compare: mode must be changed, unchanged, increased, or decreased.");
        return (int)HarnessExitCode.InvalidInput;
    }

    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr), Timeout = TimeSpan.FromMinutes(2) };
    AddCapabilityHeader(client, rendezvous!);

    Console.WriteLine($"Comparing session {sessionId} (mode={mode})...");
    using HttpResponseMessage response = await client.PostAsJsonAsync(
        $"/api/v1/game/discover/compare/{Uri.EscapeDataString(sessionId)}",
        new { compareMode = mode, maxCandidates = 100 }).ConfigureAwait(false);

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"compare: HTTP {(int)response.StatusCode}");
        return (int)HarnessExitCode.ConflictOrBusy;
    }

    using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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
            Console.WriteLine($"  {ReadStr(c, "baseDisplacement")} = {ReadStr(c, "valueSummary")}");
        }
    }
    return 0;
}

// ── Nearby (neighborhood) command ──────────────────────────

static async Task<int> NearbyAsync(string[] args)
{
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null) return HostNotFound("nearby");
    int gateResult = await CheckGateAsync(hostUrl, "nearby", rendezvous!);
    if (gateResult != 0) return gateResult;

    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: nearby <refOffset> [--window <bytes>] [--float-min <f>] [--float-max <f>]");
        Console.Error.WriteLine("Example: nearby 0x0317A810 --window 512");
        return (int)HarnessExitCode.InvalidInput;
    }

    if (!TryParseAddress(args[1], out long refOffset) || refOffset < 0)
    {
        Console.Error.WriteLine("nearby: refOffset must be a non-negative decimal or hexadecimal address.");
        return (int)HarnessExitCode.InvalidInput;
    }
    int window = 512;
    float? floatMin = null, floatMax = null;
    int? intMin = null, intMax = null;

    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] is "--window" or "--float-min" or "--float-max" or "--int-min" or "--int-max")
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"nearby: {args[i]} requires a value.");
                return (int)HarnessExitCode.InvalidInput;
            }

            string value = args[++i];
            bool parsed = args[i - 1] switch
            {
                "--window" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
                    && (window = w) >= 0,
                "--float-min" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fmin)
                    && float.IsFinite(fmin) && (floatMin = fmin) is not null,
                "--float-max" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fmax)
                    && float.IsFinite(fmax) && (floatMax = fmax) is not null,
                "--int-min" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int imin)
                    && (intMin = imin) is not null,
                "--int-max" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int imax)
                    && (intMax = imax) is not null,
                _ => false,
            };
            if (!parsed)
            {
                Console.Error.WriteLine($"nearby: invalid value for {args[i - 1]}.");
                return (int)HarnessExitCode.InvalidInput;
            }
        }
        else
        {
            Console.Error.WriteLine($"nearby: unknown option '{args[i]}'.");
            return (int)HarnessExitCode.InvalidInput;
        }
    }

    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr), Timeout = TimeSpan.FromSeconds(30) };
    AddCapabilityHeader(client, rendezvous!);

    if (window is < 64 or > 4096
        || (floatMin.HasValue && floatMax.HasValue && floatMin > floatMax)
        || (floatMin.HasValue && !float.IsFinite(floatMin.Value))
        || (floatMax.HasValue && !float.IsFinite(floatMax.Value)))
    {
        Console.Error.WriteLine("nearby: window must be 64-4096 and float bounds must be finite and ordered.");
        return (int)HarnessExitCode.InvalidInput;
    }

    Console.WriteLine($"Neighborhood scan at 0x{refOffset:X} (±{window} bytes)...");
    try
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/game/discover/neighborhood",
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

        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement root = json.RootElement;
        if (root.TryGetProperty("candidates", out JsonElement cands))
        {
            Console.WriteLine($"Candidates: {cands.GetArrayLength()}");
            Console.WriteLine($"{"Delta",-8} {"Relative Offset",-16} {"Value",-30}");
            Console.WriteLine(new string('-', 56));
            foreach (var c in cands.EnumerateArray())
            {
                string summary = ReadStr(c, "valueSummary");
                string relOff = ReadStr(c, "baseDisplacement");
                // Extract delta from summary (e.g. "+4: float=0.500")
                Console.WriteLine($"{summary,-56} {relOff}");
            }
        }
        return 0;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine($"nearby: could not reach web host at {hostUrl}.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

// ── Discard command ────────────────────────────────────────

static async Task<int> DiscardSessionAsync(string[] args)
{
    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null) return HostNotFound("discard");
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: discard <sessionId>");
        return (int)HarnessExitCode.InvalidInput;
    }
    string baseAddr = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient { BaseAddress = new Uri(baseAddr) };
    AddCapabilityHeader(client, rendezvous!);
    try
    {
        using HttpResponseMessage response = await client.DeleteAsync($"/api/v1/game/discover/session/{Uri.EscapeDataString(args[1])}").ConfigureAwait(false);
        Console.WriteLine($"Discarded {args[1]}: HTTP {(int)response.StatusCode}");
        return response.IsSuccessStatusCode ? 0 : (int)HarnessExitCode.ConflictOrBusy;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine($"discard: could not reach web host at {hostUrl}.");
        return (int)HarnessExitCode.ConflictOrBusy;
    }
}

// ── Aggregate campaign command ─────────────────────────────

static async Task<int> RunCampaignAsync(string[] args)
{
    if (!OffsetCampaignOptions.TryParse(args, out OffsetCampaignOptions? options, out string? error))
    {
        Console.Error.WriteLine(error);
        return (int)HarnessExitCode.InvalidInput;
    }

    RendezvousConnection? rendezvous = ReadRendezvous();
    string? hostUrl = rendezvous?.BaseUri;
    if (hostUrl is null) return HostNotFound("campaign");
    int gateResult = await CheckGateAsync(hostUrl, "campaign", rendezvous!).ConfigureAwait(false);
    if (gateResult != 0) return gateResult;

    string baseAddress = hostUrl.EndsWith('/') ? hostUrl : hostUrl + "/";
    using var client = new HttpClient
    {
        BaseAddress = new Uri(baseAddress),
        Timeout = TimeSpan.FromMinutes(2),
    };
    AddCapabilityHeader(client, rendezvous!);

    var runner = new OffsetCampaignRunner(client, Console.Out, Console.Error);
    return await runner.RunAsync(options!).ConfigureAwait(false);
}

static int HostNotFound(string cmd)
{
    Console.Error.WriteLine($"{cmd}: no web host found.");
    return (int)HarnessExitCode.UnsupportedCapability;
}

static int ReadInt(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

static string ReadStr(JsonElement e, string prop) =>
    e.TryGetProperty(prop, out JsonElement v) ? v.GetString() ?? "?" : "?";

static RendezvousConnection? ReadRendezvous()
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

        // Expiry and publisher identity are mandatory. A malformed or missing
        // value is rejected rather than treated as an unbounded lease.
        if (!root.TryGetProperty("expiresAtUtc", out JsonElement expiresElement)
            || expiresElement.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                expiresElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        // The rendezvous record uses "baseUri", not "url". The capability
        // is required for every unsafe API call; loopback alone is not auth.
        string? baseUri = root.TryGetProperty("baseUri", out JsonElement uriElement)
            ? uriElement.GetString()
            : null;
        string? capability = root.TryGetProperty("capability", out JsonElement capabilityElement)
            ? capabilityElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(baseUri) || string.IsNullOrWhiteSpace(capability))
        {
            return null;
        }

        // Loopback-only guard
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address)
            || !IPAddress.IsLoopback(address))
        {
            return null;
        }

        // Process-alive guard: the publisher identity is mandatory. Reject
        // missing, malformed, or exited PIDs before sending the capability.
        if (!root.TryGetProperty("processId", out JsonElement pidElement)
            || !pidElement.TryGetInt32(out int processId)
            || processId <= 0)
        {
            return null;
        }

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

        return new RendezvousConnection(baseUri, capability);
    }
    catch
    {
        return null;
    }
}

static void AddCapabilityHeader(HttpClient client, RendezvousConnection rendezvous)
{
    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(rendezvous);
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-WotBTreader-Capability", rendezvous.Capability);
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

  discover-pattern <fieldName> <patternHex> [toleranceMaskHex]
    Scan an AOB/wildcard byte pattern. Non-zero mask bytes are wildcards.
    Example: discover-pattern playerStruct 488B90 00FF00 --alignment 8

  discover-pointer-chain <rootOffset> <offset1,offset2,...>
    Resolve a bounded pointer chain (maximum four dereferences).
    Example: discover-pointer-chain 0x0317A810 0x20,0x18,0x34

  discover-snapshot <valueSize> [--float-min <f>] [--float-max <f>]
    Create a snapshot of all values in committed memory.
    Example: discover-snapshot 4 --float-min -500 --float-max 500
    --max-bytes <n> sets an explicit retained-byte budget (0 = engine ceiling);
    bounded campaigns use this instead of address windows.

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

  discover-campaign [options]
    Run a bounded aggregate-only Float32 rolling comparison campaign.
    Options: --comparisons <1-4> --interval-seconds <1-5>
             --span-mib <1-64> --float-min <f> --float-max <f>
             --mode <changed|unchanged|increased|decreased>
             --max-bytes <n>  (0-512 MiB; 0 = engine ceiling)
    Total configured wait time may not exceed 8 seconds. Candidate addresses,
    values, and scanner session ids are suppressed; the session is discarded.

  discover-instruction-snapshot [--seconds <1-5>] [--max-hits <1-64>]
    Capture register-derived XYZ triples at the server-pinned transform-fill
    instruction. No PID, address, module, register, or displacement is caller
    controlled. A hit proves only same-debug-event register/displacement
    provenance; viewpoint identity and a stable root remain separate evidence.

Gate: all discover commands require the web host to have a
      verified offline replay session (launch one via the
      dashboard first).
");
}
