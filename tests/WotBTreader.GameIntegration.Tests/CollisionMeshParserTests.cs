using System.Buffers.Binary;
using System.Text;
using WotBTreader.Core.Overlay;
using WotBTreader.GameIntegration.Metadata;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class CollisionMeshParserTests
{
    private const long MaxBytes = 16 * 1024 * 1024;

    [TestMethod]
    public void Parse_TriangleMesh_ExtractsPositionsNormalsAndIndices()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (-1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f),
        ];
        int[] indices = [0, 2, 1];

        CollisionMesh mesh = CollisionMeshParser.Parse(
            BuildScpg(vertices, indices, indexFormat: 0),
            MaxBytes);

        Assert.HasCount(3, mesh.Vertices);
        Assert.AreEqual(-1.0, mesh.Vertices[0].X, 1e-6);
        Assert.AreEqual(0.0, mesh.Vertices[0].Y, 1e-6);
        Assert.AreEqual(1.0, mesh.Vertices[0].NormalY, 1e-6);
        Assert.AreEqual(1, mesh.TriangleCount);
        CollectionAssert.AreEqual(indices, mesh.TriangleIndices.ToArray());
    }

    [TestMethod]
    public void Parse_PositionNormalColorAndHardJoint_PreservesJointValue()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (-1.0f, 2.0f, 3.0f, 0.25f, 0.5f, 0.75f),
            (1.0f, 2.0f, 3.0f, 0.25f, 0.5f, 0.75f),
            (0.0f, 4.0f, 3.0f, 0.25f, 0.5f, 0.75f),
        ];

        CollisionMesh mesh = CollisionMeshParser.Parse(
            BuildScpg(
                vertices,
                [0, 1, 2],
                indexFormat: 0,
                vertexFormat: 0x207,
                hardJointIndices: [17.0f, 23.0f, 42.0f],
                colors: [0x11223344, 0x55667788, 0x99AABBCC]),
            MaxBytes);

        Assert.AreEqual(-1.0, mesh.Vertices[0].X, 1e-6);
        Assert.AreEqual(0.75, mesh.Vertices[0].NormalZ, 1e-6);
        Assert.AreEqual(17.0, mesh.Vertices[0].HardJointIndex);
        Assert.AreEqual(23.0, mesh.Vertices[1].HardJointIndex);
        Assert.AreEqual(42.0, mesh.Vertices[2].HardJointIndex);
    }

    [TestMethod]
    public void Parse_NoHardJointAttribute_PreservesBackwardCompatibleNull()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f),
        ];

        CollisionMesh mesh = CollisionMeshParser.Parse(
            BuildScpg(vertices, [0, 1, 2], indexFormat: 0, vertexFormat: 0x007),
            MaxBytes);

        Assert.IsNull(mesh.Vertices[0].HardJointIndex);
        Assert.IsNull(new CollisionVertex(0, 0, 0, 0, 1, 0).HardJointIndex);
    }

    [TestMethod]
    public void HardJointAnalysis_ReportsStableMixedAndNoKeyTrianglesAndPartDomain()
    {
        CollisionVertex[] vertices =
        [
            Vertex(2),
            Vertex(2),
            Vertex(2),
            Vertex(3),
            Vertex(3),
            Vertex(null),
            Vertex(4.5),
        ];
        CollisionMeshPart part = new(
            PartId: 3,
            new CollisionMesh(vertices, [0, 1, 2, 2, 3, 4, 4, 5, 6]));

        CollisionPartHardJointAnalysis analysis = CollisionHardJointAnalyzer.Analyze(part);
        int[] expectedDomain = [2, 3];

        Assert.AreEqual(3, analysis.PartId);
        Assert.AreEqual(2, analysis.KeyCardinality);
        CollectionAssert.AreEqual(expectedDomain, analysis.KeyDomain.ToArray());
        Assert.HasCount(3, analysis.Triangles);
        Assert.AreEqual(TriangleHardJointKeyKind.StableKey, analysis.Triangles[0].Kind);
        Assert.AreEqual(2, analysis.Triangles[0].StableKey);
        Assert.AreEqual(TriangleHardJointKeyKind.MixedKeys, analysis.Triangles[1].Kind);
        Assert.IsNull(analysis.Triangles[1].StableKey);
        Assert.AreEqual(TriangleHardJointKeyKind.NoKey, analysis.Triangles[2].Kind);
        Assert.IsNull(analysis.Triangles[2].StableKey);
    }

    [TestMethod]
    public void Parse_Uint32Indices_Unpacks()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f),
        ];
        int[] indices = [1, 0, 2];

        CollisionMesh mesh = CollisionMeshParser.Parse(
            BuildScpg(vertices, indices, indexFormat: 1),
            MaxBytes);

        CollectionAssert.AreEqual(indices, mesh.TriangleIndices.ToArray());
    }

    [TestMethod]
    public void Parse_TruncatedInput_Throws()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f),
        ];
        byte[] full = BuildScpg(vertices, [0, 1, 2], indexFormat: 0);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            CollisionMeshParser.Parse(full.AsSpan(0, full.Length - 8), MaxBytes));
    }

    [TestMethod]
    public void Parse_IndexReferencesMissingVertex_Throws()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f),
        ];

        Assert.ThrowsExactly<InvalidDataException>(() =>
            CollisionMeshParser.Parse(
                BuildScpg(vertices, [0, 1, 99], indexFormat: 0),
                MaxBytes));
    }

    [TestMethod]
    public void ParseAll_MultipleGroups_ReturnsPerPartIdAndMesh()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f),
        ];
        byte[] bytes = BuildMultiGroupScpg(
        [
            (PartId: 1L, vertices, [0, 1, 2], IndexFormat: 0, VertexFormat: 0x207),
            (PartId: 3L, vertices, [0, 2, 1], IndexFormat: 0, VertexFormat: 0x207),
            (PartId: 5L, vertices, [0, 1, 2], IndexFormat: 0, VertexFormat: 0x207),
        ]);

        IReadOnlyList<CollisionMeshPart> parts = CollisionMeshParser.ParseAll(bytes, MaxBytes);

        Assert.HasCount(3, parts);
        Assert.AreEqual(1, parts[0].PartId);
        Assert.AreEqual(3, parts[1].PartId);
        Assert.AreEqual(5, parts[2].PartId);
        Assert.AreEqual(1, parts[1].Mesh.TriangleCount);
    }

    [TestMethod]
    public void Parse_MissingNormalAttribute_Throws()
    {
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices =
        [
            (0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f),
            (0.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f),
        ];
        byte[] bytes = BuildScpg(vertices, [0, 1, 2], indexFormat: 0, vertexFormat: 0x001);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            CollisionMeshParser.Parse(bytes, MaxBytes));
    }

    private static byte[] BuildScpg(
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices,
        int[] indices,
        int indexFormat,
        int vertexFormat = 0x207,
        float[]? hardJointIndices = null,
        uint[]? colors = null)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("SCPG"u8);
        writer.Write(1);
        writer.Write(1);
        writer.Write(3);
        WriteGroup(
            writer,
            partId: 0,
            vertices,
            indices,
            indexFormat,
            vertexFormat,
            hardJointIndices,
            colors);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildMultiGroupScpg(
        IReadOnlyList<(
            long PartId,
            (float X, float Y, float Z, float Nx, float Ny, float Nz)[] Vertices,
            int[] Indices,
            int IndexFormat,
            int VertexFormat)> groups)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("SCPG"u8);
        writer.Write(1);                    // version
        writer.Write(groups.Count);          // polygon-group count
        writer.Write(3);                     // opaque format count
        foreach ((long partId,
            (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices,
            int[] indices,
            int indexFormat,
            int vertexFormat) in groups)
        {
            WriteGroup(writer, partId, vertices, indices, indexFormat, vertexFormat);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteGroup(
        BinaryWriter writer,
        long partId,
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices,
        int[] indices,
        int indexFormat,
        int vertexFormat,
        float[]? hardJointIndices = null,
        uint[]? colors = null)
    {
        writer.Write("KA"u8);
        writer.Write((ushort)1); // KeyedArchive version
        writer.Write(13);  // key count

        WriteStringValue(writer, "##name", "PolygonGroup");
        byte[] idBytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(idBytes, (ulong)partId);
        WriteBytesValue(writer, "#id", idBytes);
        WriteIntValue(writer, "cubeTextureCoordCount", 0);
        WriteIntValue(writer, "indexCount", indices.Length);
        WriteIntValue(writer, "indexFormat", indexFormat);
        WriteBytesValue(writer, "indices", BuildIndices(indices, indexFormat));
        WriteIntValue(writer, "packing", 0);
        WriteIntValue(writer, "primitiveCount", indices.Length / 3);
        WriteIntValue(writer, "rhi_primitiveType", 1);
        WriteIntValue(writer, "textureCoordCount", 0);
        WriteIntValue(writer, "vertexCount", vertices.Length);
        WriteIntValue(writer, "vertexFormat", vertexFormat);
        WriteBytesValue(
            writer,
            "vertices",
            BuildVertices(vertices, vertexFormat, hardJointIndices, colors));
    }

    private static void WriteIntValue(BinaryWriter writer, string key, int value)
    {
        WriteStringKey(writer, key);
        writer.Write((byte)2);
        writer.Write(value);
    }

    private static void WriteStringValue(BinaryWriter writer, string key, string value)
    {
        WriteStringKey(writer, key);
        writer.Write((byte)4);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteBytesValue(BinaryWriter writer, string key, byte[] value)
    {
        WriteStringKey(writer, key);
        writer.Write((byte)6);
        writer.Write(value.Length);
        writer.Write(value);
    }

    private static void WriteStringKey(BinaryWriter writer, string key)
    {
        writer.Write((byte)4);
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] BuildIndices(int[] indices, int indexFormat)
    {
        byte[] bytes = new byte[indices.Length * (indexFormat == 0 ? 2 : 4)];
        for (int i = 0; i < indices.Length; i++)
        {
            if (indexFormat == 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), (ushort)indices[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), (uint)indices[i]);
            }
        }

        return bytes;
    }

    private static byte[] BuildVertices(
        (float X, float Y, float Z, float Nx, float Ny, float Nz)[] vertices,
        int vertexFormat,
        float[]? hardJointIndices,
        uint[]? colors)
    {
        if (hardJointIndices is not null && hardJointIndices.Length != vertices.Length)
        {
            throw new ArgumentException("Hard-joint values must match the vertex count.", nameof(hardJointIndices));
        }

        if (colors is not null && colors.Length != vertices.Length)
        {
            throw new ArgumentException("Colors must match the vertex count.", nameof(colors));
        }

        int stride = TestVertexStride(vertexFormat);
        byte[] bytes = new byte[vertices.Length * stride];
        for (int i = 0; i < vertices.Length; i++)
        {
            Span<byte> span = bytes.AsSpan(i * stride, stride);
            if ((vertexFormat & 0x001) != 0)
            {
                WriteFloat(span, 0, vertices[i].X);
                WriteFloat(span, 4, vertices[i].Y);
                WriteFloat(span, 8, vertices[i].Z);
            }

            if ((vertexFormat & 0x002) != 0)
            {
                int normalOffset = TestAttributeOffset(vertexFormat, 0x002);
                WriteFloat(span, normalOffset, vertices[i].Nx);
                WriteFloat(span, normalOffset + 4, vertices[i].Ny);
                WriteFloat(span, normalOffset + 8, vertices[i].Nz);
            }

            if ((vertexFormat & 0x004) != 0)
            {
                int colorOffset = TestAttributeOffset(vertexFormat, 0x004);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span.Slice(colorOffset, 4),
                    colors?[i] ?? 0u);
            }

            if ((vertexFormat & 0x200) != 0)
            {
                int jointOffset = TestAttributeOffset(vertexFormat, 0x200);
                WriteFloat(span, jointOffset, hardJointIndices?[i] ?? 0.0f);
            }
        }

        return bytes;
    }

    private static CollisionVertex Vertex(double? hardJointIndex) =>
        new(0, 0, 0, 0, 1, 0, hardJointIndex);

    private static void WriteFloat(Span<byte> bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.Slice(offset, 4),
            BitConverter.SingleToInt32Bits(value));

    private static int TestAttributeOffset(int vertexFormat, int attributeFlag) =>
        TestVertexStride(vertexFormat & (attributeFlag - 1));

    private static int TestVertexStride(int vertexFormat)
    {
        (int Flag, int Size)[] attributes =
        [
            (0x001, 12),
            (0x002, 12),
            (0x004, 4),
            (0x008, 8),
            (0x010, 8),
            (0x020, 8),
            (0x040, 8),
            (0x080, 12),
            (0x100, 12),
            (0x200, 4),
            (0x400, 16),
        ];

        return attributes
            .Where(attribute => (vertexFormat & attribute.Flag) != 0)
            .Sum(attribute => attribute.Size);
    }
}
