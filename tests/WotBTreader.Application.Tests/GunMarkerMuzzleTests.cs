using WotBTreader.Application.Replay;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class GunMarkerMuzzleTests
{
    [TestMethod]
    public void ReconstructStartWhenParam3IsOneUsesHalfScalar()
    {
        // hit = start + dir * 100, param3=1 => scalar = 2*100*1 = 200
        bool ok = GunMarkerMuzzle.TryReconstructStart(
            hitX: 0,
            hitY: 1.5,
            hitZ: 100,
            dirX: 0,
            dirY: 0,
            dirZ: 1,
            scalar: 200,
            param3: 1.0,
            out double startX,
            out double startY,
            out double startZ);

        Assert.IsTrue(ok);
        Assert.AreEqual(0.0, startX, 1e-9);
        Assert.AreEqual(1.5, startY, 1e-9);
        Assert.AreEqual(0.0, startZ, 1e-9);
    }

    [TestMethod]
    public void ReconstructStartWhenParam3IsHalfUsesScalarAsDistance()
    {
        bool ok = GunMarkerMuzzle.TryReconstructStart(
            hitX: 0,
            hitY: 1.5,
            hitZ: 100,
            dirX: 0,
            dirY: 0,
            dirZ: 1,
            scalar: 100,
            param3: 0.5,
            out double startX,
            out double startY,
            out double startZ);

        Assert.IsTrue(ok);
        Assert.AreEqual(0.0, startX, 1e-9);
        Assert.AreEqual(1.5, startY, 1e-9);
        Assert.AreEqual(0.0, startZ, 1e-9);
    }

    [TestMethod]
    public void ReconstructStartRejectsNonPositiveParam3()
    {
        bool ok = GunMarkerMuzzle.TryReconstructStart(
            0, 1.5, 100, 0, 0, 1, 200, 0,
            out _, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void ReconstructStartRejectsNonFiniteScalar()
    {
        bool ok = GunMarkerMuzzle.TryReconstructStart(
            0, 1.5, 100, 0, 0, 1, double.NaN, 1.0,
            out _, out _, out _);

        Assert.IsFalse(ok);
    }
}
