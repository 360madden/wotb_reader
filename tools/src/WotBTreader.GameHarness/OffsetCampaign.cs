using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace WotBTreader.GameHarness;

internal sealed record OffsetCampaignOptions(
    int Comparisons,
    int IntervalSeconds,
    int SpanMiB,
    float FloatMin,
    float FloatMax,
    string CompareMode,
    long MaxBytes = 0)
{
    private const int MaximumWaitSeconds = 8;

    // Mirrors the scanner engine's 512 MiB retained-byte ceiling; values above
    // it are rejected so a campaign can never widen the privacy-safe bound.
    private const long MaximumSnapshotBytes = 512L * 1024 * 1024;

    public static bool TryParse(
        string[] arguments,
        out OffsetCampaignOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        int comparisons = 2;
        int intervalSeconds = 2;
        int spanMiB = 16;
        float floatMin = -500;
        float floatMax = 500;
        string compareMode = "changed";
        long maxBytes = 0;

        for (int index = 1; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (option is not ("--comparisons" or "--interval-seconds" or "--span-mib"
                or "--float-min" or "--float-max" or "--mode" or "--max-bytes"))
            {
                options = null;
                error = "campaign: unknown option.";
                return false;
            }

            if (++index >= arguments.Length)
            {
                options = null;
                error = "campaign: every option requires a value.";
                return false;
            }

            string value = arguments[index];
            bool parsed = option switch
            {
                "--comparisons" => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out comparisons),
                "--interval-seconds" => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out intervalSeconds),
                "--span-mib" => int.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out spanMiB),
                "--float-min" => float.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out floatMin),
                "--float-max" => float.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out floatMax),
                "--mode" => TryParseMode(value, out compareMode),
                "--max-bytes" => long.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out maxBytes),
                _ => false,
            };

            if (!parsed)
            {
                options = null;
                error = "campaign: invalid option value.";
                return false;
            }
        }

        if (comparisons is < 1 or > 4
            || intervalSeconds is < 1 or > 5
            || spanMiB is < 1 or > 64
            || comparisons * intervalSeconds > MaximumWaitSeconds
            || maxBytes < 0
            || maxBytes > MaximumSnapshotBytes
            || !float.IsFinite(floatMin)
            || !float.IsFinite(floatMax)
            || floatMin > floatMax)
        {
            options = null;
            error =
                "campaign: require 1-4 comparisons, 1-5 second intervals, " +
                "a 1-64 MiB span, at most 8 total wait seconds, a byte budget " +
                "between 0 and 512 MiB, and ordered finite float bounds.";
            return false;
        }

        options = new OffsetCampaignOptions(
            comparisons,
            intervalSeconds,
            spanMiB,
            floatMin,
            floatMax,
            compareMode,
            maxBytes);
        error = null;
        return true;
    }

    private static bool TryParseMode(string value, out string mode)
    {
        mode = value.ToLowerInvariant();
        return mode is "changed" or "unchanged" or "increased" or "decreased";
    }
}

internal sealed class OffsetCampaignRunner(
    HttpClient client,
    TextWriter standardOutput,
    TextWriter standardError,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const int Megabyte = 1024 * 1024;
    private readonly HttpClient _client = client ?? throw new ArgumentNullException(nameof(client));
    private readonly TextWriter _standardOutput = standardOutput
        ?? throw new ArgumentNullException(nameof(standardOutput));
    private readonly TextWriter _standardError = standardError
        ?? throw new ArgumentNullException(nameof(standardError));
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<int> RunAsync(
        OffsetCampaignOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? sessionId = null;
        int exitCode = (int)HarnessExitCode.InternalFailure;
        try
        {
            (long moduleBase, long moduleSize) = await ReadPrivateModuleBoundsAsync(
                cancellationToken).ConfigureAwait(false);
            long configuredSpan = checked((long)options.SpanMiB * Megabyte);
            long scanLength = moduleSize > 0
                ? Math.Min(moduleSize, configuredSpan)
                : configuredSpan;
            if (scanLength < 64 || moduleBase > long.MaxValue - scanLength)
            {
                await _standardError.WriteLineAsync(
                    "campaign: the host returned unusable module bounds.").ConfigureAwait(false);
                return (int)HarnessExitCode.ConflictOrBusy;
            }

            await _standardOutput.WriteLineAsync(
                "Aggregate-only Float32 variability campaign.").ConfigureAwait(false);
            await _standardOutput.WriteLineAsync(
                $"Scope: main-module range, up to {options.SpanMiB} MiB; " +
                $"mode={options.CompareMode}; comparisons={options.Comparisons}; " +
                $"interval={options.IntervalSeconds}s; maxBytes={options.MaxBytes}.").ConfigureAwait(false);
            await _standardOutput.WriteLineAsync(
                "Natural replay changes are reconnaissance only; they do not prove a field or offset.")
                .ConfigureAwait(false);

            using HttpResponseMessage snapshotResponse = await _client.PostAsJsonAsync(
                "/api/v1/game/discover/snapshot",
                new
                {
                    valueSize = 4,
                    floatMin = options.FloatMin,
                    floatMax = options.FloatMax,
                    intMin = (int?)null,
                    intMax = (int?)null,
                    minAddress = moduleBase,
                    maxAddress = checked(moduleBase + scanLength),
                    valueKind = "Float",
                    alignment = 4,
                    includeImageRegions = true,
                    maxBytes = options.MaxBytes,
                },
                cancellationToken).ConfigureAwait(false);
            if (!snapshotResponse.IsSuccessStatusCode)
            {
                await WriteHttpFailureAsync("snapshot", snapshotResponse).ConfigureAwait(false);
                return (int)HarnessExitCode.ConflictOrBusy;
            }

            using (JsonDocument snapshot = JsonDocument.Parse(
                       await snapshotResponse.Content.ReadAsStringAsync(cancellationToken)
                           .ConfigureAwait(false)))
            {
                sessionId = snapshot.RootElement.TryGetProperty("sessionId", out JsonElement id)
                    ? id.GetString()
                    : null;
            }

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                await _standardError.WriteLineAsync(
                    "campaign: the host returned no scanner session.").ConfigureAwait(false);
                return (int)HarnessExitCode.InternalFailure;
            }

            for (int round = 1; round <= options.Comparisons; round++)
            {
                await _delay(
                    TimeSpan.FromSeconds(options.IntervalSeconds),
                    cancellationToken).ConfigureAwait(false);

                using HttpResponseMessage compareResponse = await _client.PostAsJsonAsync(
                    $"/api/v1/game/discover/compare/{Uri.EscapeDataString(sessionId)}",
                    new
                    {
                        compareMode = options.CompareMode,
                        maxCandidates = 1,
                        rollingBaseline = true,
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!compareResponse.IsSuccessStatusCode)
                {
                    await WriteHttpFailureAsync("comparison", compareResponse).ConfigureAwait(false);
                    return (int)HarnessExitCode.ConflictOrBusy;
                }

                using JsonDocument comparison = JsonDocument.Parse(
                    await compareResponse.Content.ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false));
                JsonElement root = comparison.RootElement;
                int previous = ReadInt(root, "previousCount");
                int retained = ReadInt(root, "currentCount");
                int changed = ReadInt(root, "changedCount");
                int unchanged = ReadInt(root, "unchangedCount");
                int increased = ReadInt(root, "increasedCount");
                int decreased = ReadInt(root, "decreasedCount");
                bool truncated = root.TryGetProperty("truncated", out JsonElement truncatedElement)
                    && truncatedElement.ValueKind == JsonValueKind.True;

                await _standardOutput.WriteLineAsync(
                    $"Round {round}: before={previous}, retained={retained}, changed={changed}, " +
                    $"unchanged={unchanged}, increased={increased}, decreased={decreased}, " +
                    $"candidatePayloadTruncated={truncated.ToString().ToLowerInvariant()}.")
                    .ConfigureAwait(false);
            }

            await _standardOutput.WriteLineAsync(
                "Result: aggregate variability evidence only; no candidate address, value, " +
                "address kind, field identity, or promotion claim was produced.").ConfigureAwait(false);
            exitCode = 0;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or FormatException
            or OverflowException)
        {
            await _standardError.WriteLineAsync(
                "campaign: the bounded scanner workflow did not complete.").ConfigureAwait(false);
            exitCode = (int)HarnessExitCode.ConflictOrBusy;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                bool discarded = await TryDiscardAsync(sessionId).ConfigureAwait(false);
                if (!discarded)
                {
                    exitCode = (int)HarnessExitCode.ConflictOrBusy;
                }
            }
        }

        return exitCode;
    }

    private async Task<(long ModuleBase, long ModuleSize)> ReadPrivateModuleBoundsAsync(
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/v1/game/discover/neighborhood",
            new
            {
                // Neighborhood windows are a radius on each side of the
                // reference. Offset one minimum radius into the module so the
                // lower bound lands exactly on the trusted module base.
                referenceOffset = 64L,
                windowSize = 64,
                includeFloat = false,
                includeInt32 = false,
                includeDouble = false,
                includeWorkingSetClassification = false,
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await WriteHttpFailureAsync("module probe", response).ConfigureAwait(false);
            throw new HttpRequestException("Module probe failed.");
        }

        using JsonDocument json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = json.RootElement;
        string baseAddress = root.TryGetProperty("baseAddress", out JsonElement baseElement)
            ? baseElement.GetString() ?? string.Empty
            : string.Empty;
        if (!TryParseHexAddress(baseAddress, out long moduleBase))
        {
            throw new FormatException("Module base was invalid.");
        }

        long moduleSize = root.TryGetProperty("moduleSize", out JsonElement sizeElement)
            && sizeElement.TryGetInt64(out long parsedSize)
            ? parsedSize
            : 0;
        return (moduleBase, moduleSize);
    }

    private async Task<bool> TryDiscardAsync(string sessionId)
    {
        try
        {
            using HttpResponseMessage response = await _client.DeleteAsync(
                $"/api/v1/game/discover/session/{Uri.EscapeDataString(sessionId)}")
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await _standardError.WriteLineAsync(
                    $"campaign: scanner-session discard failed (HTTP {(int)response.StatusCode}).")
                    .ConfigureAwait(false);
                return false;
            }

            await _standardOutput.WriteLineAsync("Scanner session discarded.").ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            await _standardError.WriteLineAsync(
                "campaign: scanner-session discard could not reach the host.").ConfigureAwait(false);
            return false;
        }
    }

    private async Task WriteHttpFailureAsync(string phase, HttpResponseMessage response) =>
        await _standardError.WriteLineAsync(
            $"campaign: {phase} failed (HTTP {(int)response.StatusCode}).").ConfigureAwait(false);

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.TryGetInt32(out int parsed)
            ? parsed
            : 0;

    private static bool TryParseHexAddress(string value, out long address)
    {
        string digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        return long.TryParse(
            digits,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out address)
            && address > 0;
    }
}
