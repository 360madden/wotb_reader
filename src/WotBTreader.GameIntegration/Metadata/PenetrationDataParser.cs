using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WotBTreader.Application.Game;

namespace WotBTreader.GameIntegration.Metadata;

/// <summary>
/// Parses the install's penetration-relevant XML (already decompressed by
/// <see cref="Dvpl.DvplReader"/>): the detailed vehicle armor groups, the
/// per-nation shell stats, and the per-nation gun→shell piercingPower pairs.
/// Pure span-in → records-out; every method is bounded by the caller's
/// <c>maxCharacters</c> and skips (rather than throws on) malformed entries.
/// </summary>
internal static class PenetrationDataParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Parses a detailed vehicle definition XML (e.g. <c>{nation}/{tank}.xml.dvpl</c>)
    /// into its armor profile: the <c>&lt;hull&gt;&lt;armor&gt;</c> groups, the turret's
    /// <c>&lt;armor&gt;</c> groups (the first <c>turrets*</c> section), and the
    /// <c>primaryArmor</c> group names. An armor group's thickness is its FIRST
    /// text node only (later children like <c>vehicleDamageFactor</c> are ignored).
    /// </summary>
    public static VehicleArmorProfile ParseVehicleArmor(
        ReadOnlySpan<byte> payload,
        string vehicleId,
        long maxCharacters)
    {
        XDocument document = Load(payload, maxCharacters);
        XElement? root = document.Root;
        if (root is null)
        {
            return new VehicleArmorProfile(vehicleId, [], [], [], []);
        }

        List<ArmorGroup> hull = [];
        XElement? hullArmor = root.Element("hull")?.Element("armor");
        if (hullArmor is not null)
        {
            foreach (XElement group in hullArmor.Elements())
            {
                if (group.Name.LocalName.StartsWith("armor_", StringComparison.Ordinal) &&
                    TryReadArmorThickness(group, out double thickness))
                {
                    hull.Add(new ArmorGroup(group.Name.LocalName, thickness));
                }
            }
        }

        List<ArmorGroup> turret = [];
        XElement? turretSection = root.Elements().FirstOrDefault(
            element => element.Name.LocalName.StartsWith("turrets", StringComparison.Ordinal));
        XElement? turretArmor = turretSection?.Descendants("armor").FirstOrDefault();
        if (turretArmor is not null)
        {
            foreach (XElement group in turretArmor.Elements())
            {
                if (group.Name.LocalName.StartsWith("armor_", StringComparison.Ordinal) &&
                    TryReadArmorThickness(group, out double thickness))
                {
                    turret.Add(new ArmorGroup(group.Name.LocalName, thickness));
                }
            }
        }

        List<string> primary = [];
        string? primaryArmor = root.Element("hull")?.Element("primaryArmor")?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(primaryArmor))
        {
            primary.AddRange(
                primaryArmor.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // The turret's declared frontal (primary) armor, from the first
        // turret section's <primaryArmor> (the turret front is declared
        // separately from the hull's).
        List<string> turretPrimary = [];
        string? turretPrimaryArmor = turretSection?.Descendants("primaryArmor")
            .FirstOrDefault()?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(turretPrimaryArmor))
        {
            turretPrimary.AddRange(
                turretPrimaryArmor.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return new VehicleArmorProfile(vehicleId, hull, turret, primary, turretPrimary);
    }

    /// <summary>
    /// Parses <c>components/shells.xml.dvpl</c> into shell profiles. A shell
    /// entry is any root child carrying a <c>caliber</c> and a <c>kind</c>;
    /// the <c>icons</c> block is skipped.
    /// </summary>
    public static IReadOnlyList<ShellProfile> ParseShells(
        ReadOnlySpan<byte> payload,
        long maxCharacters)
    {
        XDocument document = Load(payload, maxCharacters);
        List<ShellProfile> shells = [];
        foreach (XElement element in document.Root?.Elements() ?? [])
        {
            if (element.Name.LocalName == "icons")
            {
                continue;
            }

            string? kind = element.Element("kind")?.Value.Trim();
            if (kind is null ||
                !TryParseDouble(element.Element("caliber")?.Value, out double caliber) ||
                caliber <= 0)
            {
                continue;
            }

            _ = TryParseDouble(element.Element("normalizationAngle")?.Value, out double normalization);
            _ = TryParseDouble(element.Element("ricochetAngle")?.Value, out double ricochet);
            shells.Add(new ShellProfile(
                element.Name.LocalName, kind, caliber, normalization, ricochet));
        }

        return shells;
    }

    /// <summary>
    /// Reads a detailed vehicle definition's stock shell name: the first shot
    /// of the first gun of the first <c>&lt;guns&gt;</c> block (the stock
    /// turret's stock gun, in document order). Returns null when the vehicle
    /// declares no gun/shots. This is the viewer's default shell until loaded
    /// shell is decodable (the documented open gap).
    /// </summary>
    public static string? ParseStockGunShellName(
        ReadOnlySpan<byte> payload,
        long maxCharacters)
    {
        XDocument document = Load(payload, maxCharacters);
        XElement? guns = document.Root?.Descendants("guns").FirstOrDefault();
        XElement? gun = guns?.Elements().FirstOrDefault();
        XElement? shot = gun?.Element("shots")?.Elements().FirstOrDefault();
        return shot?.Name.LocalName;
    }

    /// <summary>
    /// Reads a detailed vehicle definition's stock-gun SHOT names — every
    /// shot of the first gun of the first <c>&lt;guns&gt;</c> block (the stock
    /// turret's stock gun), in document order. The first is the stock shell;
    /// the rest are the viewer's alternative ammo (AP/APCR/HE/HEAT) the pen
    /// badge's manual selector cycles through. Empty when the vehicle
    /// declares no gun/shots.
    /// </summary>
    public static IReadOnlyList<string> ParseGunShotNames(
        ReadOnlySpan<byte> payload,
        long maxCharacters)
    {
        XDocument document = Load(payload, maxCharacters);
        XElement? guns = document.Root?.Descendants("guns").FirstOrDefault();
        XElement? gun = guns?.Elements().FirstOrDefault();
        XElement? shots = gun?.Element("shots");
        return shots?.Elements().Select(shot => shot.Name.LocalName).ToList() ?? [];
    }

    /// <summary>
    /// Parses <c>components/guns.xml.dvpl</c> into gun→shell pairings. The
    /// file's layout is <c>&lt;root&gt;&lt;nextAvailableId/&gt;&lt;ids/&gt;&lt;shared&gt;…</c>
    /// — the gun definitions (the only entries carrying a <c>shots</c> list)
    /// live under the <c>&lt;shared&gt;</c> section, NOT as root children. Each shot
    /// carries the two-point <c>piercingPower</c> ("near far"), the shell
    /// muzzle <c>speed</c>, and <c>maxDistance</c>.
    /// </summary>
    public static IReadOnlyList<GunShellProfile> ParseGuns(
        ReadOnlySpan<byte> payload,
        long maxCharacters)
    {
        XDocument document = Load(payload, maxCharacters);
        List<GunShellProfile> guns = [];
        foreach (XElement gun in document.Root?.Element("shared")?.Elements() ?? [])
        {
            XElement? shots = gun.Element("shots");
            if (shots is null)
            {
                continue;
            }

            foreach (XElement shot in shots.Elements())
            {
                if (!TryParsePowerPair(shot.Element("piercingPower")?.Value, out double near, out double far))
                {
                    continue;
                }

                _ = TryParseDouble(shot.Element("maxDistance")?.Value, out double maxDistance);
                _ = TryParseDouble(shot.Element("speed")?.Value, out double speed);
                guns.Add(new GunShellProfile(
                    gun.Name.LocalName, shot.Name.LocalName, near, far, maxDistance, speed));
            }
        }

        return guns;
    }

    private static bool TryReadArmorThickness(XElement group, out double thickness)
    {
        thickness = 0;
        string? firstText = group.Nodes()
            .OfType<XText>()
            .Select(text => text.Value.Trim())
            .FirstOrDefault(text => text.Length > 0);
        return TryParseDouble(firstText, out thickness) && thickness >= 0;
    }

    private static bool TryParsePowerPair(
        string? value,
        out double near,
        out double far)
    {
        near = 0;
        far = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               TryParseDouble(parts[0], out near) &&
               TryParseDouble(parts[1], out far) &&
               near >= 0 && far >= 0;
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0;
            return false;
        }

        return double.TryParse(
            text.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static XDocument Load(ReadOnlySpan<byte> payload, long maxCharacters)
    {
        string xml = StrictUtf8.GetString(payload);
        if (xml.Length > maxCharacters)
        {
            throw new InvalidDataException("A penetration-data resource exceeded the character limit.");
        }

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
        return XDocument.Load(reader, LoadOptions.None);
    }
}
