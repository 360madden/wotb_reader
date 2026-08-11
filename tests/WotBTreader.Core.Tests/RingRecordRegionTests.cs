using System.Buffers.Binary;
using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class RingRecordRegionTests
{
    private static byte[] RegionWithPosition(float x, float y, float z, int length = 0x40)
    {
        byte[] region = new byte[length];
        if (length >= RingRecordRegion.PositionOffset + 12)
        {
            BinaryPrimitives.WriteSingleLittleEndian(region.AsSpan(RingRecordRegion.PositionOffset), x);
            BinaryPrimitives.WriteSingleLittleEndian(region.AsSpan(RingRecordRegion.PositionOffset + 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(region.AsSpan(RingRecordRegion.PositionOffset + 8), z);
        }

        return region;
    }

    [TestMethod]
    public void TryReadPosition_DecodesFiniteTriple()
    {
        byte[] region = RegionWithPosition(12.5f, -3.25f, 44.75f);

        (float X, float Y, float Z)? position = RingRecordRegion.TryReadPosition(region);

        Assert.IsNotNull(position);
        Assert.AreEqual(12.5f, position!.Value.X);
        Assert.AreEqual(-3.25f, position.Value.Y);
        Assert.AreEqual(44.75f, position.Value.Z);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(0x10)]
    [DataRow(0x1b)]
    public void TryReadPosition_ShortRegion_FailsClosed(int length)
    {
        byte[] region = RegionWithPosition(1f, 2f, 3f, Math.Max(length, 0));

        (float X, float Y, float Z)? position = RingRecordRegion.TryReadPosition(region);

        Assert.IsNull(position);
    }

    [TestMethod]
    public void TryReadPosition_NonFinite_FailsClosed()
    {
        byte[] region = RegionWithPosition(float.NaN, 2f, 3f);
        Assert.IsNull(RingRecordRegion.TryReadPosition(region));

        byte[] infinity = RegionWithPosition(1f, float.PositiveInfinity, 3f);
        Assert.IsNull(RingRecordRegion.TryReadPosition(infinity));
    }

    [TestMethod]
    public void TryReadYaw_DecodesFiniteValue()
    {
        byte[] region = RegionWithPosition(1f, 2f, 3f);
        BinaryPrimitives.WriteSingleLittleEndian(region.AsSpan(RingRecordRegion.YawOffset), 0.567f);

        float? yaw = RingRecordRegion.TryReadYaw(region);

        Assert.IsNotNull(yaw);
        Assert.AreEqual(0.567f, yaw!.Value);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(RingRecordRegion.YawOffset)]
    [DataRow(RingRecordRegion.YawOffset + 2)]
    public void TryReadYaw_ShortRegion_FailsClosed(int length)
    {
        byte[] region = RegionWithPosition(1f, 2f, 3f, Math.Max(length, 0));

        Assert.IsNull(RingRecordRegion.TryReadYaw(region));
    }

    [TestMethod]
    public void TryReadYaw_NonFinite_FailsClosed()
    {
        byte[] region = RegionWithPosition(1f, 2f, 3f);
        BinaryPrimitives.WriteSingleLittleEndian(
            region.AsSpan(RingRecordRegion.YawOffset),
            float.NegativeInfinity);

        Assert.IsNull(RingRecordRegion.TryReadYaw(region));
    }
}
