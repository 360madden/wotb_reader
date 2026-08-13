namespace WotBTreader.Core.Overlay;

/// <summary>
/// One collision-mesh vertex: its position in the tank's LOCAL collision
/// space plus its unit surface normal (the DAVA <c>EVF_VERTEX</c> +
/// <c>EVF_NORMAL</c> attributes). The normal is what the pen model needs to
/// compute the true incidence angle — the four-face hull box's facing-derived
/// normals were shown too coarse (PN-4's honest negative).
/// </summary>
public readonly record struct CollisionVertex(
    double X,
    double Y,
    double Z,
    double NormalX,
    double NormalY,
    double NormalZ);

/// <summary>
/// A tank collision surface: vertices with surface normals plus a triangle
/// index list (three consecutive indices per triangle, into
/// <see cref="Vertices"/>). Parsed read-only from the install's
/// <c>CollisionMeshes/{nation}-{tank}.scg.dvpl</c> (SCPG → KeyedArchive). The
/// coordinates are the tank's local Z-UP collision space (+X right, +Y
/// forward, +Z up), so the consumer must rotate the Y-up world aim ray into
/// tank-local space and then Y↔Z-swap it into this Z-up space before
/// raycasting (see <c>PenetrationAim.EvaluateAgainstMesh</c>).
/// </summary>
public sealed record CollisionMesh(
    IReadOnlyList<CollisionVertex> Vertices,
    IReadOnlyList<int> TriangleIndices)
{
    /// <summary>The number of triangles (<see cref="TriangleIndices"/>.Count / 3).</summary>
    public int TriangleCount => TriangleIndices.Count / 3;
}
