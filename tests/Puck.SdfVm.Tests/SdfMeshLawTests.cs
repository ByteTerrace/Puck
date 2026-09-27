using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a mesh's vertex and triangle attributes are each absent or one per vertex (one per triangle for the triangle
/// materials), and finite; any other length or a non-finite value is refused by the attribute's name, while a mesh with
/// no attribute, or with every one, is accepted.
/// </summary>
public sealed class SdfMeshLawTests {
    private static readonly Vector3[] Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
    private static readonly uint[] Indices = [0, 1, 2];

    [Fact]
    public void AMeshCarriesEachAttributeWhole() {
        var plain = new SdfMesh(
            indices: Indices,
            positions: Positions
        );
        var full = new SdfMesh(
            indices: Indices,
            normals: new Vector3[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
            positions: Positions,
            triangleMaterials: new uint[] { 1 },
            uvs: new Vector2[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY }
        );

        Assert.True(condition: (plain.Normals.IsEmpty && plain.Uvs.IsEmpty && plain.TriangleMaterials.IsEmpty));
        Assert.Equal(expected: (3, 3, 1), actual: (full.Normals.Length, full.Uvs.Length, full.TriangleMaterials.Length));
    }
    /// <summary>The mesh target writes a covered pixel's triangle and its draw plus one as floats, exact up to 2^24: the
    /// last triangle and the last draw plus one both round-trip, a mesh one triangle past <see cref="SdfMesh.MaxTriangles"/>
    /// is refused by name before its indices are read, and a region one draw past <see cref="SdfMeshRegion.MaxDraws"/> is
    /// refused by name too.</summary>
    [Fact]
    public void AMeshOrARegionPastTheExactFloatRangeIsRefusedByName() {
        Assert.Equal(expected: (SdfMesh.MaxTriangles - 1), actual: (int)(float)(SdfMesh.MaxTriangles - 1));
        Assert.Equal(expected: SdfMeshRegion.MaxDraws, actual: (int)(float)SdfMeshRegion.MaxDraws);

        var indices = GC.AllocateUninitializedArray<uint>(length: (3 * (SdfMesh.MaxTriangles + 1)));
        var triangles = Assert.Throws<ArgumentException>(testCode: () => new SdfMesh(indices: indices, positions: Positions));

        Assert.Equal(expected: "indices", actual: triangles.ParamName);
        Assert.Contains(expectedSubstring: $"at most {SdfMesh.MaxTriangles} triangles", actualString: triangles.Message);

        var draws = Assert.Throws<ArgumentException>(testCode: static () => SdfMeshRegion.Plan(
            draws: new CountedDraws(count: (SdfMeshRegion.MaxDraws + 1)),
            meshes: new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance)
        ));

        Assert.Equal(expected: "draws", actual: draws.ParamName);
    }
    [Fact]
    public void AnAttributeOfTheWrongLengthOrANonFiniteOneIsRefusedByName() {
        Assert.Equal(expected: "normals", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, normals: new Vector3[] { Vector3.UnitZ }, positions: Positions)).ParamName);
        Assert.Equal(expected: "uvs", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, uvs: new Vector2[] { Vector2.Zero, Vector2.Zero })).ParamName);
        Assert.Equal(expected: "triangleMaterials", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, triangleMaterials: new uint[] { 0, 1 })).ParamName);
        Assert.Equal(expected: "normals", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, normals: new Vector3[] { Vector3.UnitZ, new(x: float.NaN, y: 0f, z: 1f), Vector3.UnitZ }, positions: Positions)).ParamName);
        Assert.Equal(expected: "uvs", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, uvs: new Vector2[] { Vector2.Zero, new(x: float.PositiveInfinity, y: 0f), Vector2.Zero })).ParamName);
    }

    // A draw list that only counts: the region refuses it on its count before reading any draw.
    private sealed class CountedDraws(int count) : IReadOnlyList<SdfMeshDraw> {
        public int Count => count;
        public SdfMeshDraw this[int index] => throw new InvalidOperationException(message: "The count alone decides.");
        public IEnumerator<SdfMeshDraw> GetEnumerator() => throw new InvalidOperationException(message: "The count alone decides.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
