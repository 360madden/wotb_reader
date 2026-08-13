namespace WotBTreader.Core.Overlay;

/// <summary>
/// The nearest collision-surface hit along a ray: the distance, the world/
/// local hit point, and the OUTWARD surface normal (unit). The normal comes
/// from the struck triangle's face, oriented outward using the per-vertex
/// normals (so the winding order cannot invert it).
/// </summary>
public readonly record struct MeshHit(
    double Distance,
    double HitX,
    double HitY,
    double HitZ,
    double NormalX,
    double NormalY,
    double NormalZ);

/// <summary>
/// Pure ray→triangle-mesh intersection (Möller–Trumbore), returning the
/// nearest hit with its outward surface normal. The ray and mesh must share a
/// coordinate space; callers transform the world aim ray into the tank's
/// local collision space first. Fail-closed: no hit or a degenerate mesh
/// yields null.
/// </summary>
public static class CollisionRaycast
{
    private const double Epsilon = 1e-12;

    /// <summary>
    /// Finds the nearest triangle the ray strikes. Returns null when there is
    /// no forward hit. The surface normal is the struck triangle's face normal
    /// (cross product), flipped to agree with the triangle's average vertex
    /// normal so it always points outward.
    /// </summary>
    public static MeshHit? Raycast(AimRay ray, CollisionMesh mesh)
    {
        if (!Finite(ray) || mesh.Vertices.Count == 0)
        {
            return null;
        }

        double bestT = double.PositiveInfinity;
        MeshHit? best = null;

        int triangleCount = mesh.TriangleCount;
        for (int i = 0; i < triangleCount; i++)
        {
            int baseIndex = i * 3;
            int ia = mesh.TriangleIndices[baseIndex];
            int ib = mesh.TriangleIndices[baseIndex + 1];
            int ic = mesh.TriangleIndices[baseIndex + 2];
            if (ia < 0 || ia >= mesh.Vertices.Count
                || ib < 0 || ib >= mesh.Vertices.Count
                || ic < 0 || ic >= mesh.Vertices.Count)
            {
                // A structurally corrupt triangle (index outside the vertex
                // array) is skipped like a degenerate one — never an exception.
                continue;
            }

            CollisionVertex a = mesh.Vertices[ia];
            CollisionVertex b = mesh.Vertices[ib];
            CollisionVertex c = mesh.Vertices[ic];
            if (!Finite(a) || !Finite(b) || !Finite(c))
            {
                continue;
            }

            if (!TryIntersect(ray, a, b, c, out double t, out double u, out double v) || t <= 0)
            {
                continue;
            }

            if (t >= bestT)
            {
                continue;
            }

            if (!TrySurfaceNormal(a, b, c, out double nx, out double ny, out double nz))
            {
                continue;
            }

            bestT = t;
            best = new MeshHit(
                t,
                ray.OriginX + ray.DirectionX * t,
                ray.OriginY + ray.DirectionY * t,
                ray.OriginZ + ray.DirectionZ * t,
                nx,
                ny,
                nz);
        }

        return best;
    }

    /// <summary>
    /// Möller–Trumbore ray-triangle intersection. On success <paramref name="t"/>
    /// is the distance along the ray and (<paramref name="u"/>, <paramref name="v"/>)
    /// are the barycentric coordinates of the hit inside the triangle.
    /// </summary>
    private static bool TryIntersect(
        AimRay ray,
        CollisionVertex a,
        CollisionVertex b,
        CollisionVertex c,
        out double t,
        out double u,
        out double v)
    {
        t = 0;
        u = 0;
        v = 0;

        double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
        double e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;

        double pvecx = (ray.DirectionY * e2z) - (ray.DirectionZ * e2y);
        double pvecy = (ray.DirectionZ * e2x) - (ray.DirectionX * e2z);
        double pvecz = (ray.DirectionX * e2y) - (ray.DirectionY * e2x);

        double det = (e1x * pvecx) + (e1y * pvecy) + (e1z * pvecz);
        if (Math.Abs(det) < Epsilon)
        {
            return false;
        }

        double invDet = 1.0 / det;
        double tvecx = ray.OriginX - a.X;
        double tvecy = ray.OriginY - a.Y;
        double tvecz = ray.OriginZ - a.Z;

        u = ((tvecx * pvecx) + (tvecy * pvecy) + (tvecz * pvecz)) * invDet;
        if (u < 0 || u > 1)
        {
            return false;
        }

        double qvecx = (tvecy * e1z) - (tvecz * e1y);
        double qvecy = (tvecz * e1x) - (tvecx * e1z);
        double qvecz = (tvecx * e1y) - (tvecy * e1x);

        v = ((ray.DirectionX * qvecx) + (ray.DirectionY * qvecy) + (ray.DirectionZ * qvecz)) * invDet;
        if (v < 0 || u + v > 1)
        {
            return false;
        }

        t = ((e2x * qvecx) + (e2y * qvecy) + (e2z * qvecz)) * invDet;
        return t > 0;
    }

    /// <summary>
    /// Computes the triangle's outward unit normal: the edge cross product,
    /// flipped to agree with the triangle's average vertex normal and then
    /// normalized. Returns false for a degenerate (zero-area) triangle.
    /// </summary>
    private static bool TrySurfaceNormal(
        CollisionVertex a,
        CollisionVertex b,
        CollisionVertex c,
        out double nx,
        out double ny,
        out double nz)
    {
        double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z;
        double e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;

        double cx = (e1y * e2z) - (e1z * e2y);
        double cy = (e1z * e2x) - (e1x * e2z);
        double cz = (e1x * e2y) - (e1y * e2x);
        double length = Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz));
        if (length <= Epsilon)
        {
            nx = 0;
            ny = 0;
            nz = 0;
            return false;
        }

        double avgNx = (a.NormalX + b.NormalX + c.NormalX) / 3.0;
        double avgNy = (a.NormalY + b.NormalY + c.NormalY) / 3.0;
        double avgNz = (a.NormalZ + b.NormalZ + c.NormalZ) / 3.0;

        // Orient outward: if the cross-product normal opposes the vertex
        // normals (inverted winding), flip it.
        double sign = ((cx * avgNx) + (cy * avgNy) + (cz * avgNz)) < 0 ? -1.0 : 1.0;
        nx = sign * cx / length;
        ny = sign * cy / length;
        nz = sign * cz / length;
        return true;
    }

    private static bool Finite(AimRay ray) =>
        double.IsFinite(ray.OriginX) && double.IsFinite(ray.OriginY)
        && double.IsFinite(ray.OriginZ)
        && double.IsFinite(ray.DirectionX) && double.IsFinite(ray.DirectionY)
        && double.IsFinite(ray.DirectionZ);

    private static bool Finite(CollisionVertex vertex) =>
        double.IsFinite(vertex.X) && double.IsFinite(vertex.Y) && double.IsFinite(vertex.Z)
        && double.IsFinite(vertex.NormalX) && double.IsFinite(vertex.NormalY)
        && double.IsFinite(vertex.NormalZ);
}
