using WotBTreader.Core.Overlay;

namespace WotBTreader.GameIntegration.Metadata;

internal enum TriangleHardJointKeyKind
{
    NoKey,
    StableKey,
    MixedKeys,
}

internal sealed record TriangleHardJointKeyAnalysis(
    int TriangleIndex,
    TriangleHardJointKeyKind Kind,
    int? StableKey);

internal sealed record CollisionPartHardJointAnalysis(
    long PartId,
    IReadOnlyList<int> KeyDomain,
    IReadOnlyList<TriangleHardJointKeyAnalysis> Triangles)
{
    public int KeyCardinality => KeyDomain.Count;
}

/// <summary>
/// Purely describes the integral hard-joint keys carried by a collision part.
/// It deliberately assigns no armor-group or thickness semantics to a key.
/// </summary>
internal static class CollisionHardJointAnalyzer
{
    public static CollisionPartHardJointAnalysis Analyze(CollisionMeshPart part)
    {
        int[] keyDomain = part.Mesh.Vertices
            .Select(vertex => IntegralKey(vertex.HardJointIndex))
            .Where(key => key.HasValue)
            .Select(key => key!.Value)
            .Distinct()
            .Order()
            .ToArray();

        List<TriangleHardJointKeyAnalysis> triangles =
            new(capacity: part.Mesh.TriangleCount);
        for (int triangleIndex = 0;
            triangleIndex < part.Mesh.TriangleIndices.Count / 3;
            triangleIndex++)
        {
            int offset = triangleIndex * 3;
            int firstIndex = part.Mesh.TriangleIndices[offset];
            int secondIndex = part.Mesh.TriangleIndices[offset + 1];
            int thirdIndex = part.Mesh.TriangleIndices[offset + 2];
            if (!ValidVertexIndex(firstIndex, part.Mesh.Vertices.Count)
                || !ValidVertexIndex(secondIndex, part.Mesh.Vertices.Count)
                || !ValidVertexIndex(thirdIndex, part.Mesh.Vertices.Count))
            {
                triangles.Add(new(
                    triangleIndex,
                    TriangleHardJointKeyKind.NoKey,
                    StableKey: null));
                continue;
            }

            int? first = IntegralKey(part.Mesh.Vertices[firstIndex].HardJointIndex);
            int? second = IntegralKey(part.Mesh.Vertices[secondIndex].HardJointIndex);
            int? third = IntegralKey(part.Mesh.Vertices[thirdIndex].HardJointIndex);
            if (!first.HasValue || !second.HasValue || !third.HasValue)
            {
                triangles.Add(new(
                    triangleIndex,
                    TriangleHardJointKeyKind.NoKey,
                    StableKey: null));
            }
            else if (first.Value == second.Value && first.Value == third.Value)
            {
                triangles.Add(new(
                    triangleIndex,
                    TriangleHardJointKeyKind.StableKey,
                    first.Value));
            }
            else
            {
                triangles.Add(new(
                    triangleIndex,
                    TriangleHardJointKeyKind.MixedKeys,
                    StableKey: null));
            }
        }

        return new CollisionPartHardJointAnalysis(part.PartId, keyDomain, triangles);
    }

    private static int? IntegralKey(double? value)
    {
        if (!value.HasValue
            || !double.IsFinite(value.Value)
            || value.Value < int.MinValue
            || value.Value > int.MaxValue
            || Math.Truncate(value.Value) != value.Value)
        {
            return null;
        }

        return (int)value.Value;
    }

    private static bool ValidVertexIndex(int index, int count) =>
        index >= 0 && index < count;
}
