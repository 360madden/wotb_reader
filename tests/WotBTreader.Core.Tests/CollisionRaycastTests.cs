using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class CollisionRaycastTests
{
    // A unit triangle in the Z=0 plane facing +Z (outward vertex normals +Z).
    private static CollisionMesh FrontTriangle()
    {
        CollisionVertex[] vertices =
        [
            new(0, 0, 0, 0, 0, 1),
            new(1, 0, 0, 0, 0, 1),
            new(0, 1, 0, 0, 0, 1),
        ];
        return new CollisionMesh(vertices, [0, 1, 2]);
    }

    // The same triangle with reversed winding — the geometric face normal is
    // -Z but the vertex normals stay +Z, so the raycast must still orient it
    // outward (+Z).
    private static CollisionMesh ReversedTriangle()
    {
        CollisionVertex[] vertices =
        [
            new(0, 0, 0, 0, 0, 1),
            new(0, 1, 0, 0, 0, 1),
            new(1, 0, 0, 0, 0, 1),
        ];
        return new CollisionMesh(vertices, [0, 1, 2]);
    }

    [TestMethod]
    public void Raycast_HeadOn_ReturnsHitWithOutwardNormal()
    {
        AimRay ray = new(0, 0, -1, 0, 0, 1);

        MeshHit? hit = CollisionRaycast.Raycast(ray, FrontTriangle());

        Assert.IsNotNull(hit);
        Assert.AreEqual(1.0, hit.Value.Distance, 1e-9);
        Assert.AreEqual(0.0, hit.Value.HitX, 1e-9);
        Assert.AreEqual(0.0, hit.Value.HitY, 1e-9);
        Assert.AreEqual(0.0, hit.Value.HitZ, 1e-9);
        Assert.AreEqual(0.0, hit.Value.NormalX, 1e-9);
        Assert.AreEqual(0.0, hit.Value.NormalY, 1e-9);
        Assert.AreEqual(1.0, hit.Value.NormalZ, 1e-9);
    }

    [TestMethod]
    public void Raycast_OffTarget_ReturnsNull()
    {
        // Ray passes through x=2, outside the triangle.
        AimRay ray = new(2, 0, -1, 0, 0, 1);

        Assert.IsNull(CollisionRaycast.Raycast(ray, FrontTriangle()));
    }

    [TestMethod]
    public void Raycast_BehindTriangle_ReturnsNull()
    {
        // Ray travels away from the triangle.
        AimRay ray = new(0, 0, 1, 0, 0, 1);

        Assert.IsNull(CollisionRaycast.Raycast(ray, FrontTriangle()));
    }

    [TestMethod]
    public void Raycast_ReversedWinding_NormalStillOutward()
    {
        AimRay ray = new(0, 0, -1, 0, 0, 1);

        MeshHit? hit = CollisionRaycast.Raycast(ray, ReversedTriangle());

        Assert.IsNotNull(hit);
        Assert.AreEqual(1.0, hit.Value.NormalZ, 1e-9);
    }

    [TestMethod]
    public void Raycast_TwoTriangles_PicksNearest()
    {
        CollisionVertex[] vertices =
        [
            new(0, 0, 0, 0, 0, 1),
            new(1, 0, 0, 0, 0, 1),
            new(0, 1, 0, 0, 0, 1),
            new(0, 0, 2, 0, 0, 1),
            new(1, 0, 2, 0, 0, 1),
            new(0, 1, 2, 0, 0, 1),
        ];
        CollisionMesh mesh = new(vertices, [0, 1, 2, 3, 4, 5]);

        AimRay ray = new(0, 0, -1, 0, 0, 1);

        MeshHit? hit = CollisionRaycast.Raycast(ray, mesh);

        Assert.IsNotNull(hit);
        Assert.AreEqual(1.0, hit.Value.Distance, 1e-9);
    }

    [TestMethod]
    public void Raycast_EmptyMesh_ReturnsNull()
    {
        CollisionMesh mesh = new([], []);

        Assert.IsNull(CollisionRaycast.Raycast(new AimRay(0, 0, -1, 0, 0, 1), mesh));
    }

    [TestMethod]
    public void Raycast_NonFiniteRay_ReturnsNull()
    {
        AimRay ray = new(double.NaN, 0, -1, 0, 0, 1);

        Assert.IsNull(CollisionRaycast.Raycast(ray, FrontTriangle()));
    }
}
