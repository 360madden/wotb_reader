using System.Buffers.Binary;
using System.Text;
using WotBTreader.GameIntegration.Metadata;

namespace WotBTreader.GameIntegration.Tests;

/// <summary>
/// CI coverage for <see cref="SceneFileParser"/> using a synthetic SFV2
/// scene (the real .sc2 is pinned by the opt-in test, which skips without the
/// installed game). The binary layout mirrors the real Churchill I collision
/// descriptor, reverse-engineered 2026-08-13: SFV2 header → KeyedArchive v1
/// header → KeyedArchive v2 scene archive with a key table + hash table, then
/// hash-keyed entries whose nested archives are length-prefixed.
/// </summary>
[TestClass]
public sealed class SceneFileParserTests
{
    // DAVA eVariantType codes used by this fixture (see SceneFileParser).
    private const byte TypeFastName = 4;
    private const byte TypeArchive = 8;
    private const byte TypeVec3 = 0x0c;
    private const byte TypeVec4 = 0x0d;
    private const byte TypeVector = 0x1b;

    private const long MaxBytes = 16 * 1024 * 1024;

    [TestMethod]
    public void Parse_OneNodeWithNonIdentityTranslation_ExtractsNameAndTransform()
    {
        byte[] scene = BuildScene(
            (TranslationX: 1.5, TranslationY: -2.0, TranslationZ: 3.25));

        SceneDescription description = SceneFileParser.Parse(scene, MaxBytes);

        Assert.HasCount(1, description.Nodes);
        SceneNodeTransform node = description.Nodes[0];
        Assert.AreEqual("hull", node.Name);
        Assert.AreEqual(1.5, node.TranslationX, 1e-6);
        Assert.AreEqual(-2.0, node.TranslationY, 1e-6);
        Assert.AreEqual(3.25, node.TranslationZ, 1e-6);
        // The fixture carries an identity quaternion + unit scale.
        Assert.AreEqual(0.0, node.RotationX, 1e-9);
        Assert.AreEqual(0.0, node.RotationY, 1e-9);
        Assert.AreEqual(0.0, node.RotationZ, 1e-9);
        Assert.AreEqual(1.0, node.RotationW, 1e-9);
        Assert.AreEqual(1.0, node.ScaleX, 1e-9);
        Assert.AreEqual(1.0, node.ScaleY, 1e-9);
        Assert.AreEqual(1.0, node.ScaleZ, 1e-9);
    }

    [TestMethod]
    public void Parse_TruncatedInput_Throws()
    {
        byte[] scene = BuildScene((TranslationX: 0, TranslationY: 0, TranslationZ: 0));

        for (int cut = 1; cut < scene.Length; cut += 7)
        {
            Assert.ThrowsExactly<InvalidDataException>(
                () => SceneFileParser.Parse(scene.AsSpan(0, cut), MaxBytes),
                $"truncating to {cut} bytes should fail closed");
        }
    }

    [TestMethod]
    public void Parse_EmptyInput_Throws()
    {
        Assert.ThrowsExactly<InvalidDataException>(
            () => SceneFileParser.Parse([], MaxBytes));
    }

    /// <summary>
    /// Builds a minimal one-node scene: the <c>#hierarchy</c> vector holds a
    /// single node archive named "hull" whose TransformComponent carries the
    /// given local translation (identity rotation/scale).
    /// </summary>
    private static byte[] BuildScene(
        (double TranslationX, double TranslationY, double TranslationZ) translation)
    {
        // Key table + hash table (one synthetic hash per key, in order).
        (string Name, uint Hash)[] keys =
        [
            ("#hierarchy", 0x00000001u),
            ("name", 0x00000002u),
            ("components", 0x00000003u),
            ("0000", 0x00000004u),
            ("comp.typename", 0x00000005u),
            ("TransformComponent", 0x00000006u),
            ("tc.localTranslation", 0x00000007u),
            ("tc.localRotation", 0x00000008u),
            ("tc.localScale", 0x00000009u),
            ("hull", 0x0000000Au),
        ];

        // The hierarchy: vector [ node ]. The node's TransformComponent is the
        // only component and carries the translation/rotation/scale.
        byte[] component = BuildArchive(
        [
            (0x00000005u, FastName(0x00000006u)),           // comp.typename → TransformComponent
            (0x00000007u, Vec3(translation)),               // tc.localTranslation
            (0x00000008u, Vec4(0, 0, 0, 1)),                // tc.localRotation (identity)
            (0x00000009u, Vec3((1.0, 1.0, 1.0))),          // tc.localScale (unit)
        ]);
        byte[] components = BuildArchive([(0x00000004u, Archive(component))]);
        byte[] node = BuildArchive(
        [
            (0x00000002u, FastName(0x0000000Au)),           // name → "hull"
            (0x00000003u, Archive(components)),             // components
        ]);
        byte[] hierarchy = Vector([Archive(node)]);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("SFV2"u8);
        writer.Write(1u);           // opaque format counts
        writer.Write(1u);
        writer.Write("KA"u8);       // KeyedArchive v1 header archive
        writer.Write((ushort)1);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write(0.0f);
        writer.Write(0.0f);
        writer.Write("KA"u8);       // KeyedArchive v2 scene archive
        writer.Write((ushort)2);
        writer.Write(keys.Length);  // key count
        foreach ((string name, _) in keys)
        {
            writer.Write((ushort)name.Length);
            writer.Write(Encoding.ASCII.GetBytes(name));
        }

        foreach ((_, uint hash) in keys)
        {
            writer.Write(hash);     // hash table
        }

        writer.Write(1);            // entry count
        writer.Write(0x00000001u);  // "#hierarchy"
        writer.Write(hierarchy);

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] FastName(uint hash)
    {
        byte[] bytes = new byte[5];
        bytes[0] = TypeFastName;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), hash);
        return bytes;
    }

    private static byte[] Vec3((double X, double Y, double Z) value)
    {
        byte[] bytes = new byte[13];
        bytes[0] = TypeVec3;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1, 4), BitConverter.SingleToInt32Bits((float)value.X));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5, 4), BitConverter.SingleToInt32Bits((float)value.Y));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9, 4), BitConverter.SingleToInt32Bits((float)value.Z));
        return bytes;
    }

    private static byte[] Vec4(float x, float y, float z, float w)
    {
        byte[] bytes = new byte[17];
        bytes[0] = TypeVec4;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1, 4), BitConverter.SingleToInt32Bits(x));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(5, 4), BitConverter.SingleToInt32Bits(y));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(9, 4), BitConverter.SingleToInt32Bits(z));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(13, 4), BitConverter.SingleToInt32Bits(w));
        return bytes;
    }

    private static byte[] Archive(byte[] body)
    {
        byte[] bytes = new byte[5 + body.Length];
        bytes[0] = TypeArchive;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), (uint)body.Length);
        body.CopyTo(bytes, 5);
        return bytes;
    }

    private static byte[] Vector(params byte[][] elements)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(TypeVector);
        writer.Write((uint)elements.Length);
        foreach (byte[] element in elements)
        {
            writer.Write(element);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Builds a nested archive body: "KA" + version + flags + key count + hash-keyed entries.</summary>
    private static byte[] BuildArchive(params (uint Hash, byte[] Value)[] entries)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("KA"u8);
        writer.Write((byte)2);   // KeyedArchive version
        writer.Write((byte)1);   // inline flags
        writer.Write((uint)entries.Length);
        foreach ((uint hash, byte[] value) in entries)
        {
            writer.Write(hash);
            writer.Write(value);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
