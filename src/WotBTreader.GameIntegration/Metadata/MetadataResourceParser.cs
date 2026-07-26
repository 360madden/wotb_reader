using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Metadata;

internal sealed record VehicleDefinition(
    int CompactDescriptor,
    string VehicleId,
    string LocalizationKey,
    TankClass TankClass,
    string Nation,
    ContentHash DefinitionHash);

internal sealed record MapDefinition(
    string MapId,
    int? NumericId,
    string? LocalName,
    ContentHash DefinitionHash);

internal static class MetadataResourceParser
{
    private const int MaxVehicleDefinitionsPerNation = 4096;
    private const int MaxMapDefinitions = 2048;
    private const int MaxLocalizationEntries = 200_000;
    private const int MaxYamlLineCharacters = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<VehicleDefinition> ParseVehicleList(
        ReadOnlySpan<byte> payload,
        string nation,
        int nationId,
        ContentHash sourceHash,
        long maxCharacters)
    {
        string xml = DecodeUtf8(payload, maxCharacters);
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = maxCharacters,
            MaxCharactersFromEntities = 0,
        };

        using StringReader stringReader = new(xml);
        using XmlReader reader = XmlReader.Create(stringReader, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement? root = document.Root;
        if (root is null)
        {
            return [];
        }

        List<VehicleDefinition> definitions = [];
        foreach (XElement element in root.Elements())
        {
            if (definitions.Count >= MaxVehicleDefinitionsPerNation)
            {
                throw new InvalidDataException("The vehicle definition-count limit was exceeded.");
            }

            if (!int.TryParse(
                    element.Element("id")?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int vehicleTypeId) ||
                vehicleTypeId is < 0 or > 0x7FFFFF)
            {
                continue;
            }

            string vehicleName = element.Name.LocalName;
            string? localizationKey = element.Element("userString")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(localizationKey))
            {
                localizationKey = $"#{nation}_vehicles:{vehicleName}";
            }

            string tags = element.Element("tags")?.Value ?? string.Empty;
            int descriptor = checked((vehicleTypeId << 8) | nationId);
            definitions.Add(
                new VehicleDefinition(
                    descriptor,
                    $"{nation}:{vehicleName}",
                    localizationKey,
                    ParseTankClass(tags),
                    nation,
                    sourceHash));
        }

        return definitions;
    }

    public static IReadOnlyList<MapDefinition> ParseMaps(
        ReadOnlySpan<byte> payload,
        ContentHash sourceHash,
        long maxCharacters)
    {
        string yaml = DecodeUtf8(payload, maxCharacters);
        List<MapDefinition> maps = [];
        string? currentId = null;
        int? currentNumericId = null;
        string? currentLocalName = null;

        using StringReader reader = new(yaml);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > MaxYamlLineCharacters)
            {
                throw new InvalidDataException("A map metadata line exceeded the configured limit.");
            }

            int indentation = CountLeadingSpaces(line);
            string trimmed = line.Trim();
            if (indentation == 4 &&
                trimmed.EndsWith(':') &&
                !trimmed.StartsWith('-'))
            {
                AddCurrent();
                currentId = Unquote(trimmed[..^1].Trim());
                currentNumericId = null;
                currentLocalName = null;
                continue;
            }

            if (currentId is null || indentation != 8)
            {
                continue;
            }

            if (TryReadProperty(trimmed, "id", out string? numericValue) &&
                int.TryParse(
                    numericValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedId))
            {
                currentNumericId = parsedId;
            }
            else if (TryReadProperty(trimmed, "localName", out string? localName))
            {
                currentLocalName = Unquote(localName);
            }
        }

        AddCurrent();
        return maps;

        void AddCurrent()
        {
            if (currentId is null)
            {
                return;
            }

            if (maps.Count >= MaxMapDefinitions)
            {
                throw new InvalidDataException("The map definition-count limit was exceeded.");
            }

            maps.Add(new MapDefinition(currentId, currentNumericId, currentLocalName, sourceHash));
        }
    }

    public static IReadOnlyDictionary<string, string> ParseLocalization(
        ReadOnlySpan<byte> payload,
        long maxCharacters)
    {
        string yaml = DecodeUtf8(payload, maxCharacters);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        using StringReader reader = new(yaml);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > MaxYamlLineCharacters)
            {
                throw new InvalidDataException("A localization line exceeded the configured limit.");
            }

            if (values.Count >= MaxLocalizationEntries)
            {
                throw new InvalidDataException("The localization entry-count limit was exceeded.");
            }

            if (!TryParseQuotedKeyValue(line.Trim(), out string? key, out string? value))
            {
                continue;
            }

            values.TryAdd(key, value);
        }

        return values;
    }

    private static TankClass ParseTankClass(string tags)
    {
        HashSet<string> tagSet = tags.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(
                StringComparer.Ordinal);

        if (tagSet.Contains("lightTank"))
        {
            return TankClass.Light;
        }

        if (tagSet.Contains("mediumTank"))
        {
            return TankClass.Medium;
        }

        if (tagSet.Contains("heavyTank"))
        {
            return TankClass.Heavy;
        }

        return tagSet.Contains("AT-SPG") ? TankClass.TankDestroyer : TankClass.Unknown;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> payload, long maxCharacters)
    {
        if (payload.Length > maxCharacters * 4)
        {
            throw new InvalidDataException("The metadata character limit was exceeded.");
        }

        string value = StrictUtf8.GetString(payload);
        if (value.Length > maxCharacters)
        {
            throw new InvalidDataException("The metadata character limit was exceeded.");
        }

        return value;
    }

    private static bool TryParseQuotedKeyValue(
        string line,
        out string key,
        out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (line.Length < 5 || line[0] != '"')
        {
            return false;
        }

        int keyEnd = FindClosingQuote(line, 0);
        if (keyEnd < 0)
        {
            return false;
        }

        int colon = keyEnd + 1;
        while (colon < line.Length && char.IsWhiteSpace(line[colon]))
        {
            colon++;
        }

        if (colon >= line.Length || line[colon] != ':')
        {
            return false;
        }

        string valueToken = line[(colon + 1)..].Trim();
        if (valueToken.Length < 2 || valueToken[0] != '"')
        {
            return false;
        }

        int valueEnd = FindClosingQuote(valueToken, 0);
        if (valueEnd != valueToken.Length - 1)
        {
            return false;
        }

        try
        {
            key = JsonSerializer.Deserialize<string>(line[..(keyEnd + 1)]) ?? string.Empty;
            value = JsonSerializer.Deserialize<string>(valueToken) ?? string.Empty;
            return key.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int FindClosingQuote(string value, int openingIndex)
    {
        bool escaped = false;
        for (int index = openingIndex + 1; index < value.Length; index++)
        {
            char current = value[index];
            if (escaped)
            {
                escaped = false;
            }
            else if (current == '\\')
            {
                escaped = true;
            }
            else if (current == '"')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryReadProperty(string line, string name, out string value)
    {
        string prefix = string.Concat(name, ":");
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = line[prefix.Length..].Trim();
        return value.Length > 0;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            }
            catch (JsonException)
            {
                return value[1..^1];
            }
        }

        return value;
    }

    private static int CountLeadingSpaces(string value)
    {
        int count = 0;
        while (count < value.Length && value[count] == ' ')
        {
            count++;
        }

        return count;
    }
}
