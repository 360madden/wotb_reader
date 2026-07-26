using System.Globalization;
using System.Text.Json;
using WotBTreader.Application.Replay;

namespace WotBTreader.Replays;

internal sealed record WotbReplayMetadata(
    string Version,
    long? ViewpointAccountId,
    string? ViewpointPlayerName,
    string? ViewpointVehicleName,
    string? ArenaIdentity,
    string? MapName,
    int? MapId,
    int? VehicleCompactDescriptor,
    DateTimeOffset? BattleTimeUtc,
    TimeSpan? Duration)
{
    public static WotbReplayMetadata Parse(ReadOnlyMemory<byte> bytes, DecoderLimits limits)
    {
        JsonDocumentOptions options = new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = Math.Clamp(limits.MaximumNestingDepth, 1, 64),
        };

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, options);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ReplayFormatException(
                    "replay.invalid_metadata",
                    "Replay metadata must be a JSON object.");
            }

            string version = RequiredString(root, "version", 128);
            string? databaseId = OptionalScalarText(root, "dbid", 32);
            string? battleStart = OptionalScalarText(root, "battleStartTime", 32);
            double? durationSeconds = OptionalDouble(root, "battleDuration");
            if (durationSeconds is < 0 or > 3_600 || (durationSeconds is not null && !double.IsFinite(durationSeconds.Value)))
            {
                throw new ReplayFormatException(
                    "replay.invalid_metadata_duration",
                    "Replay metadata contains an invalid battle duration.");
            }

            return new WotbReplayMetadata(
                version,
                ParseInt64(databaseId),
                OptionalString(root, "playerName", 512),
                OptionalString(root, "playerVehicleName", 512),
                OptionalScalarText(root, "arenaUniqueId", 64),
                OptionalString(root, "mapName", 512),
                OptionalInt32(root, "mapId"),
                OptionalInt32(root, "vehicleCompDescriptor"),
                ParseUnixTime(battleStart),
                durationSeconds is null ? null : TimeSpan.FromSeconds(durationSeconds.Value));
        }
        catch (JsonException exception)
        {
            throw new ReplayFormatException(
                "replay.invalid_metadata",
                "Replay metadata is not valid bounded JSON.")
            {
                Data = { ["cause"] = exception.GetType().Name },
            };
        }
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength) =>
        OptionalString(root, name, maximumLength) ??
        throw new ReplayFormatException(
            "replay.missing_metadata_field",
            $"Replay metadata is missing required field '{name}'.");

    private static string? OptionalString(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ReplayFormatException(
                "replay.invalid_metadata_field",
                $"Replay metadata field '{name}' has the wrong type.");
        }

        string? text = value.GetString();
        if (text is not null && text.Length > maximumLength)
        {
            throw new ReplayFormatException(
                "replay.metadata_field_limit",
                $"Replay metadata field '{name}' exceeds its character limit.");
        }

        return text;
    }

    private static string? OptionalScalarText(JsonElement root, string name, int maximumLength)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        string text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new ReplayFormatException(
                "replay.invalid_metadata_field",
                $"Replay metadata field '{name}' has the wrong type."),
        };

        if (text.Length > maximumLength)
        {
            throw new ReplayFormatException(
                "replay.metadata_field_limit",
                $"Replay metadata field '{name}' exceeds its character limit.");
        }

        return text;
    }

    private static int? OptionalInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw new ReplayFormatException(
                "replay.invalid_metadata_field",
                $"Replay metadata field '{name}' is not a 32-bit integer.");
        }

        return result;
    }

    private static double? OptionalDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double result))
        {
            throw new ReplayFormatException(
                "replay.invalid_metadata_field",
                $"Replay metadata field '{name}' is not a number.");
        }

        return result;
    }

    private static long? ParseInt64(string? text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;

    private static DateTimeOffset? ParseUnixTime(string? text)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ReplayFormatException(
                "replay.invalid_metadata_time",
                "Replay metadata contains an invalid battle timestamp.");
        }
    }
}
