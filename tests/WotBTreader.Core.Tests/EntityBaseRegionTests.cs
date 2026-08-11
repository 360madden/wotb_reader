using System.Buffers.Binary;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class EntityBaseRegionTests
{
    private static byte[] Region(int length = EntityBaseRegion.HpMaxOffset + 2)
    {
        var region = new byte[length];
        // Fill with a recognizable non-health byte pattern so only the
        // pinned fields decode.
        Array.Fill(region, (byte)0xCD);
        return region;
    }

    [TestMethod]
    public void TryReadHpCurrent_DecodesInt16At0xB8()
    {
        byte[] region = Region();
        BinaryPrimitives.WriteInt16LittleEndian(
            region.AsSpan(EntityBaseRegion.HpCurrentOffset),
            1228);
        Assert.AreEqual(1228f, EntityBaseRegion.TryReadHpCurrent(region));
    }

    [TestMethod]
    public void TryReadHpCurrent_ZeroIsValid_DeadTank()
    {
        byte[] region = Region();
        BinaryPrimitives.WriteInt16LittleEndian(
            region.AsSpan(EntityBaseRegion.HpCurrentOffset),
            0);
        Assert.AreEqual(0f, EntityBaseRegion.TryReadHpCurrent(region));
    }

    [TestMethod]
    public void TryReadHpCurrent_NegativeFailsClosed()
    {
        byte[] region = Region();
        BinaryPrimitives.WriteInt16LittleEndian(
            region.AsSpan(EntityBaseRegion.HpCurrentOffset),
            -1);
        Assert.IsNull(EntityBaseRegion.TryReadHpCurrent(region));
    }

    [TestMethod]
    public void TryReadHpCurrent_TooShortReturnsNull()
    {
        Assert.IsNull(EntityBaseRegion.TryReadHpCurrent(Region(EntityBaseRegion.HpCurrentOffset + 1)));
    }

    [TestMethod]
    public void TryReadHpMax_DecodesInt16At0x11C()
    {
        byte[] region = Region();
        BinaryPrimitives.WriteInt16LittleEndian(
            region.AsSpan(EntityBaseRegion.HpMaxOffset),
            1550);
        Assert.AreEqual(1550f, EntityBaseRegion.TryReadHpMax(region));
    }

    [TestMethod]
    public void TryReadHpMax_NegativeFailsClosed()
    {
        byte[] region = Region();
        BinaryPrimitives.WriteInt16LittleEndian(
            region.AsSpan(EntityBaseRegion.HpMaxOffset),
            -5);
        Assert.IsNull(EntityBaseRegion.TryReadHpMax(region));
    }

    [TestMethod]
    public void TryReadHpMax_TooShortReturnsNull()
    {
        Assert.IsNull(EntityBaseRegion.TryReadHpMax(Region(EntityBaseRegion.HpMaxOffset + 1)));
    }

    [TestMethod]
    [DataRow((byte)0, false)]
    [DataRow((byte)1, true)]
    public void TryReadAlive_DecodesBoolByteAt0xBA(byte value, bool expected)
    {
        byte[] region = Region();
        region[EntityBaseRegion.AliveOffset] = value;
        Assert.AreEqual(expected, EntityBaseRegion.TryReadAlive(region));
    }

    [TestMethod]
    public void TryReadAlive_NonBoolByteFailsClosed()
    {
        byte[] region = Region();
        region[EntityBaseRegion.AliveOffset] = 2;
        Assert.IsNull(EntityBaseRegion.TryReadAlive(region));
    }

    [TestMethod]
    public void TryReadAlive_TooShortReturnsNull()
    {
        Assert.IsNull(EntityBaseRegion.TryReadAlive(Region(EntityBaseRegion.AliveOffset)));
    }
}
