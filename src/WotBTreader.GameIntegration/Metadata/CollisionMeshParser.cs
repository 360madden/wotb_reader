using System.Buffers.Binary;
using WotBTreader.Core.Overlay;

namespace WotBTreader.GameIntegration.Metadata;

/// <summary>
/// Parses the install's collision mesh (<c>CollisionMeshes/{nation}-{tank}.scg.dvpl</c>,
/// already decompressed by <see cref="Dvpl.DvplReader"/>) into per-part
/// <see cref="CollisionMesh"/> values. The container is DAVA <c>SCPG</c>
/// whose header count is the number of <c>PolygonGroup</c>
/// <c>KeyedArchive</c>s — three on the real installs (keyed <c>#id</c> 1/3/5
/// = hull / turret / gun, all in the shared Z-up rest-pose space: +X right,
/// +Y forward, +Z up). Each group contains a vertex array (position + normal,
/// the <c>EVF_VERTEX</c>/<c>EVF_NORMAL</c> attributes) and a triangle index
/// list (<c>indexFormat</c> 0 = uint16, 1 = uint32).
///
/// The parser returns every polygon group; the consumer combines the groups
/// after validating the collision scene's shared rest-pose transform. Pure
/// span-in → mesh-out. Every read is bounds-checked and fail-closed:
/// malformed, truncated, or over-limit input throws
/// <see cref="InvalidDataException"/> rather than producing a partial mesh.
/// </summary>
internal static class CollisionMeshParser
{
    // DAVA eVertexFormat bits; the attribute order in the vertex record is
    // fixed by bit order (lowest first).
    private const int FlagVertex = 0x001;
    private const int FlagNormal = 0x002;
    private const int FlagColor = 0x004;
    private const int FlagTexCoord0 = 0x008;
    private const int FlagTexCoord1 = 0x010;
    private const int FlagTexCoord2 = 0x020;
    private const int FlagTexCoord3 = 0x040;
    private const int FlagTangent = 0x080;
    private const int FlagBinormal = 0x100;
    private const int FlagHardJointIndex = 0x200;
    private const int FlagJointWeight = 0x400;

    private const int MaxVertices = 1 << 20;
    private const int MaxIndices = 1 << 22;

    /// <summary>
    /// Parses the first polygon group (the hull on the real installs) for the
    /// legacy hull-only read surface. Use <see cref="ParseAll"/> for per-part
    /// hull/turret/gun scoring.
    /// </summary>
    public static CollisionMesh Parse(ReadOnlySpan<byte> payload, long maxBytes)
    {
        IReadOnlyList<CollisionMeshPart> parts = ParseAll(payload, maxBytes);
        return parts.Count == 0
            ? throw new InvalidDataException("A collision-mesh resource has no polygon groups.")
            : parts[0].Mesh;
    }

    /// <summary>
    /// Parses EVERY polygon group into per-part meshes. The real installs
    /// carry three groups keyed <c>#id</c> 1/3/5 = hull / turret / gun (the
    /// three <c>hitTester</c> collision models); the collision <c>.sc2</c>
    /// scene descriptor carries identity transforms for them (verified
    /// 2026-08-13), so the consumer raycasts the parts as a union without
    /// per-part placement.
    /// </summary>
    public static IReadOnlyList<CollisionMeshPart> ParseAll(
        ReadOnlySpan<byte> payload,
        long maxBytes)
    {
        if (payload.Length > maxBytes)
        {
            throw new InvalidDataException("A collision-mesh resource exceeded the byte limit.");
        }

        Reader reader = new(payload);

        reader.ExpectMagic("SCPG"u8);
        _ = reader.ReadUInt32(); // version
        uint groupCount = reader.ReadUInt32(); // polygon-group count (3 on real installs)
        _ = reader.ReadUInt32(); // format count (opaque)
        if (groupCount == 0 || groupCount > 32)
        {
            throw new InvalidDataException("A collision-mesh resource has an invalid group count.");
        }

        List<CollisionMeshPart> parts = new(capacity: checked((int)groupCount));
        for (uint group = 0; group < groupCount; group++)
        {
            parts.Add(ReadGroup(ref reader));
        }

        return parts;
    }

    private static CollisionMeshPart ReadGroup(ref Reader reader)
    {
        reader.ExpectMagic("KA"u8);
        _ = reader.ReadUInt16(); // KeyedArchive version
        int keyCount = checked((int)reader.ReadUInt32());
        if (keyCount < 0 || keyCount > 64)
        {
            throw new InvalidDataException("A collision-mesh archive has an invalid key count.");
        }

        long partId = 0;
        int vertexCount = 0;
        int vertexFormat = 0;
        int indexCount = 0;
        int indexFormat = 0;
        ReadOnlyMemory<byte> vertices = default;
        ReadOnlyMemory<byte> indices = default;

        for (int i = 0; i < keyCount; i++)
        {
            int keyType = reader.ReadByte();
            if (keyType != 4)
            {
                throw new InvalidDataException(
                    "A collision-mesh archive key is not a string.");
            }

            string key = reader.ReadStringKey();
            int valueType = reader.ReadByte();
            switch (valueType)
            {
                case 2: // int32
                    int intValue = reader.ReadInt32();
                    switch (key)
                    {
                        case "vertexCount": vertexCount = intValue; break;
                        case "vertexFormat": vertexFormat = intValue; break;
                        case "indexCount": indexCount = intValue; break;
                        case "indexFormat": indexFormat = intValue; break;
                    }

                    break;
                case 4: // string
                    _ = reader.ReadStringValue();
                    break;
                case 6: // byte array
                    ReadOnlyMemory<byte> bytes = reader.ReadBytes();
                    if (key == "vertices")
                    {
                        vertices = bytes;
                    }
                    else if (key == "indices")
                    {
                        indices = bytes;
                    }
                    else if (key == "#id" && bytes.Length == 8)
                    {
                        partId = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span));
                    }

                    break;
                default:
                    throw new InvalidDataException(
                        $"A collision-mesh archive has an unsupported value type {valueType}.");
            }
        }

        return new CollisionMeshPart(
            partId,
            BuildMesh(vertexCount, vertexFormat, vertices, indexCount, indexFormat, indices));
    }

    private static CollisionMesh BuildMesh(
        int vertexCount,
        int vertexFormat,
        ReadOnlyMemory<byte> vertices,
        int indexCount,
        int indexFormat,
        ReadOnlyMemory<byte> indices)
    {
        if (vertexCount <= 0 || vertexCount > MaxVertices)
        {
            throw new InvalidDataException("A collision mesh has an invalid vertex count.");
        }

        if ((vertexFormat & FlagVertex) == 0 || (vertexFormat & FlagNormal) == 0)
        {
            throw new InvalidDataException(
                "A collision mesh lacks the position or normal vertex attribute.");
        }

        int stride = VertexStride(vertexFormat);
        if (stride <= 0 || (long)vertexCount * stride != vertices.Length)
        {
            throw new InvalidDataException("A collision mesh vertex array has an unexpected size.");
        }

        // Position and normal are the first two attributes (bits 0 and 1), so
        // their offsets are fixed: position at 0, normal at 12 bytes. The hard
        // joint index follows every lower-bit attribute that is present; in
        // the observed 0x207 layout that places it after the packed color.
        int? hardJointIndexOffset = (vertexFormat & FlagHardJointIndex) != 0
            ? VertexAttributeOffset(vertexFormat, FlagHardJointIndex)
            : null;
        CollisionVertex[] parsed = new CollisionVertex[vertexCount];
        ReadOnlySpan<byte> vertexSpan = vertices.Span;
        for (int i = 0; i < vertexCount; i++)
        {
            int baseOffset = i * stride;
            parsed[i] = new CollisionVertex(
                BitConverter.ToSingle(vertexSpan.Slice(baseOffset, 4)),
                BitConverter.ToSingle(vertexSpan.Slice(baseOffset + 4, 4)),
                BitConverter.ToSingle(vertexSpan.Slice(baseOffset + 8, 4)),
                BitConverter.ToSingle(vertexSpan.Slice(baseOffset + 12, 4)),
                BitConverter.ToSingle(vertexSpan.Slice(baseOffset + 16, 4)),
                BitConverter.ToSingle(vertexSpan.Slice(baseOffset + 20, 4)),
                hardJointIndexOffset.HasValue
                    ? BitConverter.ToSingle(
                        vertexSpan.Slice(baseOffset + hardJointIndexOffset.Value, 4))
                    : null);
        }

        if (indexCount <= 0 || indexCount > MaxIndices || indexCount % 3 != 0)
        {
            throw new InvalidDataException("A collision mesh has an invalid index count.");
        }

        int elementSize = indexFormat switch
        {
            0 => 2, // uint16
            1 => 4, // uint32
            _ => throw new InvalidDataException("A collision mesh has an unsupported index format."),
        };

        if ((long)indexCount * elementSize != indices.Length)
        {
            throw new InvalidDataException("A collision mesh index array has an unexpected size.");
        }

        int[] triangles = new int[indexCount];
        ReadOnlySpan<byte> indexSpan = indices.Span;
        for (int i = 0; i < indexCount; i++)
        {
            int element = elementSize == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(indexSpan.Slice(i * 2, 2))
                : checked((int)BinaryPrimitives.ReadUInt32LittleEndian(indexSpan.Slice(i * 4, 4)));
            if (element >= vertexCount)
            {
                throw new InvalidDataException("A collision mesh index references a missing vertex.");
            }

            triangles[i] = element;
        }

        return new CollisionMesh(parsed, triangles);
    }

    private static int VertexStride(int vertexFormat)
    {
        (int Flag, int Size)[] attributes =
        [
            (FlagVertex, 12),
            (FlagNormal, 12),
            (FlagColor, 4),
            (FlagTexCoord0, 8),
            (FlagTexCoord1, 8),
            (FlagTexCoord2, 8),
            (FlagTexCoord3, 8),
            (FlagTangent, 12),
            (FlagBinormal, 12),
            (FlagHardJointIndex, 4),
            (FlagJointWeight, 16),
        ];

        int stride = 0;
        foreach ((int flag, int size) in attributes)
        {
            if ((vertexFormat & flag) != 0)
            {
                stride += size;
            }
        }

        return stride;
    }

    private static int VertexAttributeOffset(int vertexFormat, int attributeFlag)
    {
        return VertexStride(vertexFormat & (attributeFlag - 1));
    }

    private ref struct Reader(ReadOnlySpan<byte> span)
    {
        private readonly ReadOnlySpan<byte> _span = span;
        private int _position;

        public void ExpectMagic(ReadOnlySpan<byte> magic)
        {
            if (!_span.Slice(_position).StartsWith(magic))
            {
                throw new InvalidDataException("A collision-mesh resource has an invalid header.");
            }

            _position += magic.Length;
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

        /// <summary>Reads a KeyedArchive string KEY: a length-prefixed ASCII
        /// name (the key type byte was already consumed by the caller).</summary>
        public string ReadStringKey()
        {
            int length = checked((int)ReadUInt32());
            if (length < 0 || length > 256)
            {
                throw new InvalidDataException("A collision-mesh archive key is too long.");
            }

            EnsureAvailable(length);
            string value = System.Text.Encoding.UTF8.GetString(_span.Slice(_position, length));
            _position += length;
            return value;
        }

        /// <summary>Reads a KeyedArchive string VALUE (length-prefixed).</summary>
        public string ReadStringValue()
        {
            int length = checked((int)ReadUInt32());
            if (length < 0 || length > MaxBytes())
            {
                throw new InvalidDataException("A collision-mesh string is too long.");
            }

            EnsureAvailable(length);
            string value = System.Text.Encoding.UTF8.GetString(_span.Slice(_position, length));
            _position += length;
            return value;
        }

        public ReadOnlyMemory<byte> ReadBytes()
        {
            int length = checked((int)ReadUInt32());
            if (length < 0 || length > MaxBytes())
            {
                throw new InvalidDataException("A collision-mesh byte array is too long.");
            }

            EnsureAvailable(length);
            byte[] copy = _span.Slice(_position, length).ToArray();
            _position += length;
            return copy;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || _position + count > _span.Length)
            {
                throw new InvalidDataException("A collision-mesh resource is truncated.");
            }
        }

        private int MaxBytes() => _span.Length - _position;
    }
}
