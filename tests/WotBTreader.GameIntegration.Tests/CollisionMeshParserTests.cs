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
        int vertexFormat = 0x207)
    {
        return BuildMultiGroupScpg(
            [(PartId: 0L, vertices, indices, indexFormat, vertexFormat)]);
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
        int vertexFormat)
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
        WriteBytesValue(writer, "vertices", BuildVertices(vertices, vertexFormat));
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
        int vertexFormat)
    {
        // The test only exercises vertexFormat 0x207 (position + normal +
        // color + joint index, 32-byte stride) and the missing-normal case
        // (0x001, position only — expected to throw before reading vertices).
        int stride = 32;
        byte[] bytes = new byte[vertices.Length * stride];
        for (int i = 0; i < vertices.Length; i++)
        {
            Span<byte> span = bytes.AsSpan(i * stride, stride);
            BinaryPrimitives.WriteInt32LittleEndian(span[0..4], BitConverter.SingleToInt32Bits(vertices[i].X));
            BinaryPrimitives.WriteInt32LittleEndian(span[4..8], BitConverter.SingleToInt32Bits(vertices[i].Y));
            BinaryPrimitives.WriteInt32LittleEndian(span[8..12], BitConverter.SingleToInt32Bits(vertices[i].Z));
            BinaryPrimitives.WriteInt32LittleEndian(span[12..16], BitConverter.SingleToInt32Bits(vertices[i].Nx));
            BinaryPrimitives.WriteInt32LittleEndian(span[16..20], BitConverter.SingleToInt32Bits(vertices[i].Ny));
            BinaryPrimitives.WriteInt32LittleEndian(span[20..24], BitConverter.SingleToInt32Bits(vertices[i].Nz));
        }

        return bytes;
    }
}
