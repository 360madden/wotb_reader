using System.Buffers.Binary;

namespace WotBTreader.GameIntegration.Metadata;

/// <summary>
/// One scene node's placement: its name and the local translation / rotation /
/// scale from its <c>TransformComponent</c>. Rotation is a quaternion
/// (x, y, z, w) in the DAVA convention.
/// </summary>
public readonly record struct SceneNodeTransform(
    string Name,
    double TranslationX,
    double TranslationY,
    double TranslationZ,
    double RotationX,
    double RotationY,
    double RotationZ,
    double RotationW,
    double ScaleX,
    double ScaleY,
    double ScaleZ);

/// <summary>
/// The decoded <c>.sc2</c> SFV2 scene descriptor: every hierarchy node's name
/// and its local <c>TransformComponent</c> placement, in hierarchy order.
/// </summary>
public sealed record SceneDescription(
    IReadOnlyList<SceneNodeTransform> Nodes);

/// <summary>
/// Parses the install's <c>.sc2</c> SFV2 scene descriptor (already
/// decompressed by <see cref="Dvpl.DvplReader"/>) into a
/// <see cref="SceneDescription"/>. This is the DAVA <c>KeyedArchive</c>
/// version-2 value walk, reverse-engineered on the real Churchill and pinned
/// by the opt-in test: the container is an SFV2 header + a v1 header archive,
/// then a v2 scene archive whose value section is a leading uint32 entry count
/// followed by <c>&lt;hash, type byte, value&gt;</c> entries; nested archives
/// drop the key table and hash table and write the same
/// <c>&lt;hash, type, value&gt;</c> entries directly (a single
/// <c>KA 02 01</c> header + key count).
///
/// Pure span-in → description-out. Every read is bounds-checked and
/// fail-closed: malformed, truncated, or over-limit input throws
/// <see cref="InvalidDataException"/> rather than producing a partial result.
/// </summary>
internal static class SceneFileParser
{
    // DAVA eVariantType codes observed in the WoT Blitz .sc2 stream (the
    // newer DAVA stores FastName at type 4, not string):
    private const byte TypeBool = 1;
    private const byte TypeInt32 = 2;
    private const byte TypeFloat32 = 3;
    private const byte TypeFastName = 4;
    private const byte TypeString = 5;
    private const byte TypeBytes = 6;
    private const byte TypeUInt32 = 7;
    private const byte TypeArchive = 8;
    private const byte TypeInt64 = 9;
    private const byte TypeUInt64 = 10;
    private const byte TypeVec2 = 0x0b;
    private const byte TypeVec3 = 0x0c;
    private const byte TypeVec4 = 0x0d;
    private const byte TypeAabbox3 = 0x13;
    private const byte TypeFloat64 = 0x15;
    private const byte TypeVector = 0x1b;

    private const int MaxKeys = 256;
    private const int MaxVectorElements = 4096;

    /// <summary>
    /// Parses the scene descriptor and returns its hierarchy nodes' names and
    /// local transforms. Nodes without a <c>TransformComponent</c> are omitted
    /// (a node name alone carries no placement).
    /// </summary>
    public static SceneDescription Parse(ReadOnlySpan<byte> payload, long maxBytes)
    {
        if (payload.Length > maxBytes)
        {
            throw new InvalidDataException("A scene resource exceeded the byte limit.");
        }

        Reader reader = new(payload);

        reader.ExpectMagic("SFV2"u8);
        _ = reader.ReadUInt32(); // version / format counts (opaque)
        _ = reader.ReadUInt32();

        // SFV2 header archive (KeyedArchive v1): KA + version + three uint32
        // + two floats — opaque, only skipped to reach the scene archive.
        reader.ExpectMagic("KA"u8);
        _ = reader.ReadUInt16();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        reader.Skip(8);

        reader.ExpectMagic("KA"u8);
        ushort sceneVersion = reader.ReadUInt16();
        if (sceneVersion != 2)
        {
            throw new InvalidDataException("A scene archive has an unsupported version.");
        }

        int keyCount = checked((int)reader.ReadUInt32());
        if (keyCount < 0 || keyCount > MaxKeys)
        {
            throw new InvalidDataException("A scene archive has an invalid key count.");
        }

        // Key table (uint16-length-prefixed strings) + hash table (the FastName
        // hash of each key, in order) give hash → name resolution.
        string[] keys = new string[keyCount];
        for (int i = 0; i < keyCount; i++)
        {
            int length = reader.ReadUInt16();
            keys[i] = reader.ReadAscii(length);
        }

        Dictionary<uint, string> hashToName = new();
        for (int i = 0; i < keyCount; i++)
        {
            hashToName[reader.ReadUInt32()] = keys[i];
        }

        List<SceneNodeTransform> nodes = [];
        uint entryCount = reader.ReadUInt32();
        if (entryCount > MaxKeys)
        {
            throw new InvalidDataException("A scene archive has an invalid entry count.");
        }

        for (uint i = 0; i < entryCount; i++)
        {
            uint keyHash = reader.ReadUInt32();
            string? keyName = hashToName.TryGetValue(keyHash, out string? name) ? name : null;
            if (string.Equals(keyName, "#hierarchy", StringComparison.Ordinal))
            {
                ReadHierarchy(ref reader, hashToName, nodes);
            }
            else
            {
                SkipValue(ref reader, hashToName);
            }
        }

        return new SceneDescription(nodes);
    }

    /// <summary>
    /// Reads the <c>#hierarchy</c> vector: a list of node archives, each with
    /// a <c>name</c> FastName and a <c>components</c> archive whose
    /// <c>TransformComponent</c> entry carries the placement.
    /// </summary>
    private static void ReadHierarchy(
        ref Reader reader,
        IReadOnlyDictionary<uint, string> hashToName,
        List<SceneNodeTransform> nodes)
    {
        reader.ExpectValueType(TypeVector, "the scene hierarchy");
        int nodeCount = checked((int)reader.ReadUInt32());
        if (nodeCount < 0 || nodeCount > MaxVectorElements)
        {
            throw new InvalidDataException("A scene hierarchy has an invalid node count.");
        }

        for (int i = 0; i < nodeCount; i++)
        {
            ReadNode(ref reader, hashToName, nodes);
        }
    }

    private static void ReadNode(
        ref Reader reader,
        IReadOnlyDictionary<uint, string> hashToName,
        List<SceneNodeTransform> nodes)
    {
        // A hierarchy node is a VALUE (type 8 = length-prefixed archive), not
        // a raw archive: the vector's elements carry a type byte + payload.
        SceneValue nodeValue = ReadValue(ref reader, hashToName);
        if (nodeValue is not SceneArchive node)
        {
            return;
        }

        string nodeName = node.GetFastName("name", hashToName) ?? string.Empty;
        if (!node.Entries.TryGetValue("components", out SceneValue? components)
            || components is not SceneArchive componentsArchive)
        {
            return;
        }

        // The components archive maps "0000"/"0001"/… to component archives;
        // the TransformComponent is the one carrying tc.localTranslation.
        foreach (SceneValue component in componentsArchive.Entries.Values)
        {
            if (component is not SceneArchive componentArchive
                || !componentArchive.Entries.TryGetValue(
                    "tc.localTranslation", out SceneValue? translation)
                || translation is not SceneVec3 position)
            {
                continue;
            }

            SceneVec3 rotation = componentArchive.TryGetVec3(
                "tc.localRotation", out double rX, out double rY, out double rZ, out double rW)
                ? new SceneVec3(rX, rY, rZ, rW)
                : new SceneVec3(0, 0, 0, 1);
            SceneVec3 scale = componentArchive.TryGetVec3(
                "tc.localScale", out double sX, out double sY, out double sZ, out _)
                ? new SceneVec3(sX, sY, sZ, 0)
                : new SceneVec3(1, 1, 1, 0);

            nodes.Add(new SceneNodeTransform(
                nodeName,
                position.X, position.Y, position.Z,
                rotation.X, rotation.Y, rotation.Z, rotation.W,
                scale.X, scale.Y, scale.Z));
            return;
        }
    }

    // ---- Value model (a shallow tree just deep enough to extract nodes) ----

    private abstract record SceneValue;

    private sealed record SceneBool(bool Value) : SceneValue;

    private sealed record SceneLong(long Value) : SceneValue;

    private sealed record SceneDouble(double Value) : SceneValue;

    private sealed record SceneString(string Value) : SceneValue;

    private sealed record SceneFastName(uint Hash, string? Name) : SceneValue;

    private sealed record SceneVec3(double X, double Y, double Z, double W) : SceneValue;

    private sealed record SceneArchive(IReadOnlyDictionary<string, SceneValue> Entries) : SceneValue;

    private sealed record SceneVector(IReadOnlyList<SceneValue> Elements) : SceneValue;

    /// <summary>
    /// Reads one value of any supported type and returns its representation.
    /// </summary>
    private static SceneValue ReadValue(
        ref Reader reader,
        IReadOnlyDictionary<uint, string> hashToName,
        int depth = 0)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("A scene archive nests too deeply.");
        }

        byte type = reader.ReadByte();
        switch (type)
        {
            case TypeBool: return new SceneBool(reader.ReadByte() != 0);
            case TypeInt32: return new SceneLong(reader.ReadInt32());
            case TypeFloat32: return new SceneDouble(reader.ReadFloat32());
            case TypeFastName:
                uint hash = reader.ReadUInt32();
                hashToName.TryGetValue(hash, out string? name);
                return new SceneFastName(hash, name);
            case TypeString: return new SceneString(reader.ReadAscii(checked((int)reader.ReadUInt32())));
            case TypeBytes: reader.Skip(checked((int)reader.ReadUInt32())); return new SceneString(string.Empty);
            case TypeUInt32: return new SceneLong(reader.ReadUInt32());
            case TypeArchive: return ReadArchive(ref reader, hashToName, depth + 1);
            case TypeInt64: return new SceneLong(reader.ReadInt64());
            case TypeUInt64: return new SceneLong(checked((long)reader.ReadUInt64()));
            case TypeVec2:
                return new SceneVec3(reader.ReadFloat32(), reader.ReadFloat32(), 0, 0);
            case TypeVec3:
                return new SceneVec3(reader.ReadFloat32(), reader.ReadFloat32(), reader.ReadFloat32(), 0);
            case TypeVec4:
                return new SceneVec3(
                    reader.ReadFloat32(), reader.ReadFloat32(), reader.ReadFloat32(), reader.ReadFloat32());
            case TypeAabbox3:
                reader.Skip(6 * sizeof(float));
                return new SceneVec3(0, 0, 0, 0);
            case TypeFloat64:
                return new SceneDouble(reader.ReadFloat64());
            case TypeVector:
                return ReadVector(ref reader, hashToName, depth);
            default:
                throw new InvalidDataException(
                    $"A scene archive has an unsupported value type {type} at offset {reader.Position - 1}.");
        }
    }

    private static SceneVector ReadVector(
        ref Reader reader,
        IReadOnlyDictionary<uint, string> hashToName,
        int depth)
    {
        int count = checked((int)reader.ReadUInt32());
        if (count < 0 || count > MaxVectorElements)
        {
            throw new InvalidDataException("A scene vector has an invalid element count.");
        }

        List<SceneValue> elements = new(capacity: count);
        for (int i = 0; i < count; i++)
        {
            elements.Add(ReadValue(ref reader, hashToName, depth + 1));
        }

        return new SceneVector(elements);
    }

    /// <summary>
    /// Reads a nested archive: a leading uint32 byte LENGTH (from the
    /// <c>KA</c> magic through the last entry), then <c>KA 02 01</c> + key
    /// count + hash-keyed entries, into a name-keyed dictionary. The length
    /// is validated so a corrupt archive fails closed instead of drifting.
    /// </summary>
    private static SceneArchive ReadArchive(
        ref Reader reader,
        IReadOnlyDictionary<uint, string> hashToName,
        int depth = 0)
    {
        int length = checked((int)reader.ReadUInt32());
        if (length < 8)
        {
            throw new InvalidDataException("A nested scene archive has an invalid length.");
        }

        int start = reader.Position;
        reader.ExpectMagic("KA"u8);
        byte version = reader.ReadByte();
        if (version != 2)
        {
            throw new InvalidDataException("A nested scene archive has an unsupported version.");
        }

        reader.Skip(1); // inline flags
        int keyCount = checked((int)reader.ReadUInt32());
        if (keyCount < 0 || keyCount > MaxKeys)
        {
            throw new InvalidDataException("A nested scene archive has an invalid key count.");
        }

        Dictionary<string, SceneValue> entries = new(StringComparer.Ordinal);
        for (int i = 0; i < keyCount; i++)
        {
            uint hash = reader.ReadUInt32();
            string keyName = hashToName.TryGetValue(hash, out string? name) ? name : $"#{hash:x8}";
            entries[keyName] = ReadValue(ref reader, hashToName, depth + 1);
        }

        if (reader.Position - start != length)
        {
            throw new InvalidDataException("A nested scene archive has an invalid length.");
        }

        return new SceneArchive(entries);
    }

    /// <summary>Skips one value of any supported type (no capture).</summary>
    private static void SkipValue(
        ref Reader reader,
        IReadOnlyDictionary<uint, string> hashToName,
        int depth = 0)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("A scene archive nests too deeply.");
        }

        byte type = reader.ReadByte();
        switch (type)
        {
            case TypeBool: reader.Skip(1); break;
            case TypeInt32: reader.Skip(4); break;
            case TypeFloat32: reader.Skip(4); break;
            case TypeFastName: reader.Skip(4); break;
            case TypeString: reader.Skip(checked((int)reader.ReadUInt32())); break;
            case TypeBytes: reader.Skip(checked((int)reader.ReadUInt32())); break;
            case TypeUInt32: reader.Skip(4); break;
            case TypeArchive: reader.Skip(checked((int)reader.ReadUInt32())); break;
            case TypeInt64: reader.Skip(8); break;
            case TypeUInt64: reader.Skip(8); break;
            case TypeVec2: reader.Skip(8); break;
            case TypeVec3: reader.Skip(12); break;
            case TypeVec4: reader.Skip(16); break;
            case TypeAabbox3: reader.Skip(6 * sizeof(float)); break;
            case TypeFloat64: reader.Skip(8); break;
            case TypeVector:
                int count = checked((int)reader.ReadUInt32());
                if (count < 0 || count > MaxVectorElements)
                {
                    throw new InvalidDataException("A scene vector has an invalid element count.");
                }

                for (int i = 0; i < count; i++)
                {
                    SkipValue(ref reader, hashToName, depth + 1);
                }

                break;
            default:
                throw new InvalidDataException($"A scene archive has an unsupported value type {type}.");
        }
    }

    // ---- Bounds-checked reader ----

    private ref struct Reader(ReadOnlySpan<byte> span)
    {
        private readonly ReadOnlySpan<byte> _span = span;
        private int _position;

        public void ExpectMagic(ReadOnlySpan<byte> magic)
        {
            if (!_span.Slice(_position).StartsWith(magic))
            {
                throw new InvalidDataException("A scene resource has an invalid header.");
            }

            _position += magic.Length;
        }

        public void ExpectValueType(byte type, string what)
        {
            byte actual = ReadByte();
            if (actual != type)
            {
                throw new InvalidDataException($"{what} is not the expected value type.");
            }
        }

        public byte ReadByte()
        {
            EnsureAvailable(1);
            return _span[_position++];
        }

        public ushort ReadUInt16()
        {
            EnsureAvailable(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_span.Slice(_position, 2));
            _position += 2;
            return value;
        }

        public int ReadInt32()
        {
            EnsureAvailable(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_span.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_span.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public long ReadInt64()
        {
            EnsureAvailable(8);
            long value = BinaryPrimitives.ReadInt64LittleEndian(_span.Slice(_position, 8));
            _position += 8;
            return value;
        }

        public ulong ReadUInt64()
        {
            EnsureAvailable(8);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_span.Slice(_position, 8));
            _position += 8;
            return value;
        }

        public float ReadFloat32()
        {
            EnsureAvailable(4);
            float value = BinaryPrimitives.ReadSingleLittleEndian(_span.Slice(_position, 4));
            _position += 4;
            return value;
        }

        public double ReadFloat64()
        {
            EnsureAvailable(8);
            double value = BinaryPrimitives.ReadDoubleLittleEndian(_span.Slice(_position, 8));
            _position += 8;
            return value;
        }

        public int Position => _position;

        public string ReadAscii(int length)
        {
            if (length < 0 || length > MaxBytes())
            {
                throw new InvalidDataException("A scene string is too long.");
            }

            EnsureAvailable(length);
            string value = System.Text.Encoding.UTF8.GetString(_span.Slice(_position, length));
            _position += length;
            return value;
        }

        public void Skip(int count)
        {
            EnsureAvailable(count);
            _position += count;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || _position + count > _span.Length)
            {
                throw new InvalidDataException("A scene resource is truncated.");
            }
        }

        private int MaxBytes() => _span.Length - _position;
    }

    // ---- Extraction helpers over the value tree ----

    private static string? GetFastName(
        this SceneArchive archive,
        string key,
        IReadOnlyDictionary<uint, string> hashToName)
    {
        if (!archive.Entries.TryGetValue(key, out SceneValue? value))
        {
            return null;
        }

        return value switch
        {
            SceneFastName fastName => fastName.Name ?? (hashToName.TryGetValue(fastName.Hash, out string? n) ? n : null),
            SceneString text => text.Value,
            _ => null,
        };
    }

    private static bool TryGetVec3(
        this SceneArchive archive,
        string key,
        out double x,
        out double y,
        out double z,
        out double w)
    {
        x = y = z = w = 0;
        if (!archive.Entries.TryGetValue(key, out SceneValue? value) || value is not SceneVec3 vec)
        {
            return false;
        }

        (x, y, z, w) = (vec.X, vec.Y, vec.Z, vec.W);
        return true;
    }
}
