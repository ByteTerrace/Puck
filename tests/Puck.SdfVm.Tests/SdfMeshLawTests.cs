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
            triangleMaterials: new uint[] { 1 }
        );

        Assert.True(condition: (plain.Normals.IsEmpty && plain.TriangleMaterials.IsEmpty));
        Assert.Equal(expected: (3, 1), actual: (full.Normals.Length, full.TriangleMaterials.Length));
    }
    /// <summary>The mesh target writes a covered pixel's triangle and its draw plus one as floats, exact up to 2^24: the
    /// last triangle and the last draw plus one both round-trip, a mesh one triangle past <see cref="SdfMesh.MaxTriangles"/>
    /// is refused by name before its indices are read, and a region one draw past <see cref="SdfMeshRegion.MaxDraws"/> is
    /// refused by name too.</summary>
    [Fact]
    public void AMeshOrARegionPastTheExactFloatRangeIsRefusedByName() {
        Assert.Equal(actual: ((int)((float)(SdfMesh.MaxTriangles - 1))), expected: (SdfMesh.MaxTriangles - 1));
        Assert.Equal(actual: ((int)((float)SdfMeshRegion.MaxDraws)), expected: SdfMeshRegion.MaxDraws);

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
        Assert.Equal(expected: "triangleMaterials", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, triangleMaterials: new uint[] { 0, 1 })).ParamName);
        Assert.Equal(expected: "normals", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, normals: new Vector3[] { Vector3.UnitZ, new(x: float.NaN, y: 0f, z: 1f), Vector3.UnitZ }, positions: Positions)).ParamName);
        // A vertex names a member the mesh has, a member's pose is finite, and a mesh's vertices ride at most 255 members.
        var member = new SdfMeshMember(Orientation: Quaternion.Identity, Position: Vector3.Zero, SlotOffset: 1);

        Assert.Equal(expected: "vertexMembers", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, vertexMembers: new byte[] { 0, 0 })).ParamName);
        Assert.Equal(expected: "vertexMembers", actual: Assert.Throws<ArgumentException>(testCode: () => new SdfMesh(indices: Indices, members: new[] { member }, positions: Positions, vertexMembers: new byte[] { 0, 1, SdfMesh.NoMember })).ParamName);
        Assert.Equal(expected: "members", actual: Assert.Throws<ArgumentException>(testCode: () => new SdfMesh(indices: Indices, members: new[] { (member with { Position = new Vector3(x: float.NaN, y: 0f, z: 0f) }) }, positions: Positions)).ParamName);
        Assert.Equal(expected: "members", actual: Assert.Throws<ArgumentException>(testCode: () => new SdfMesh(indices: Indices, members: Enumerable.Repeat(element: member, count: (SdfMesh.MaxMembers + 1)).ToArray(), positions: Positions)).ParamName);
    }

    /// <summary>A mesh's levels are triangle lists over its own vertices, numbered after its own triangles in the region,
    /// with materials where the mesh has them and errors that only grow; the region lays each level's indices and
    /// materials after the finer ones', and a level past the coarsest draws the coarsest.</summary>
    [Fact]
    public void ALevelIsATriangleListOverTheMeshsVerticesNumberedAfterIt() {
        var mesh = new SdfMesh(indices: new uint[] { 0, 1, 2, 0, 2, 1 }, positions: Positions, triangleMaterials: new uint[] { 1, 2 }) {
            Levels = [
                new SdfMeshLevel(Error: 0.5f, Indices: new uint[] { 2, 1, 0 }, TriangleMaterials: new uint[] { 3 }),
                new SdfMeshLevel(Error: 0.5f, Indices: new uint[] { 1, 0, 2 }, TriangleMaterials: new uint[] { 4 }),
            ],
        };

        Assert.Equal(expected: (4, 12, 4), actual: (mesh.LevelledTriangleCount, SdfMeshRegion.IndexWordsOf(mesh: mesh), SdfMeshRegion.MaterialWordsOf(mesh: mesh)));
        Assert.Equal(expected: [(0, 2), (2, 1), (3, 1), (3, 1)], actual: [SdfMeshRegion.LevelOf(level: 0, mesh: mesh), SdfMeshRegion.LevelOf(level: 1, mesh: mesh), SdfMeshRegion.LevelOf(level: 2, mesh: mesh), SdfMeshRegion.LevelOf(level: 5, mesh: mesh)]);

        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var draws = new[] { new SdfMeshDraw(Identity: 0, Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity) };
        var layout = SdfMeshRegion.Plan(draws: draws, meshes: placements);
        var words = new uint[layout.Words];

        SdfMeshRegion.Write(destination: words, draws: draws, layout: layout, meshes: placements);
        Assert.Equal(expected: [0u, 1u, 2u, 0u, 2u, 1u, 2u, 1u, 0u, 1u, 0u, 2u], actual: words.AsSpan(length: 12, start: placements[mesh].IndexWord).ToArray());
        Assert.Equal(expected: [1u, 2u, 3u, 4u], actual: words.AsSpan(length: 4, start: placements[mesh].MaterialWord).ToArray());
        Assert.Equal(expected: layout.Words - SdfMeshRecord.DrawWords, actual: SdfMeshRegion.WordsOf(mesh: mesh));

        // A level without the mesh's materials, with materials the mesh lacks, empty, naming no vertex, or finer in error
        // than the level before it, is refused.
        var plain = new SdfMesh(indices: Indices, positions: Positions);

        foreach (var levels in (SdfMeshLevel[][])[
            [new SdfMeshLevel(Error: 0f, Indices: Indices, TriangleMaterials: default)],
            [new SdfMeshLevel(Error: 0f, Indices: Indices, TriangleMaterials: new uint[] { 1, 2 })],
            [new SdfMeshLevel(Error: 0f, Indices: new uint[] { 0, 1 }, TriangleMaterials: new uint[] { 1 })],
            [new SdfMeshLevel(Error: 0f, Indices: new uint[] { 0, 1, 3 }, TriangleMaterials: new uint[] { 1 })],
            [new SdfMeshLevel(Error: 1f, Indices: Indices, TriangleMaterials: new uint[] { 1 }), new SdfMeshLevel(Error: 0.5f, Indices: Indices, TriangleMaterials: new uint[] { 1 })],
            [new SdfMeshLevel(Error: float.NaN, Indices: Indices, TriangleMaterials: new uint[] { 1 })],
        ]) {
            Assert.Equal(expected: "Levels", actual: Assert.Throws<ArgumentException>(testCode: () => mesh with { Levels = levels }).ParamName);
        }

        Assert.Equal(expected: "Levels", actual: Assert.Throws<ArgumentException>(testCode: () => plain with { Levels = [new SdfMeshLevel(Error: 0f, Indices: Indices, TriangleMaterials: new uint[] { 1 })] }).ParamName);
        Assert.Throws<ArgumentNullException>(testCode: () => plain with { Levels = null! });
        Assert.Throws<ArgumentNullException>(testCode: () => plain with { Levels = [null!] });

        // The mesh target numbers every level's triangles together, so they hold at most MaxTriangles.
        var many = Assert.Throws<ArgumentException>(testCode: () => plain with { Levels = [new SdfMeshLevel(Error: 0f, Indices: new uint[(3 * SdfMesh.MaxTriangles)], TriangleMaterials: default)] });

        Assert.Contains(expectedSubstring: $"at most {SdfMesh.MaxTriangles} triangles", actualString: many.Message);
    }

    // A draw list that only counts: the region refuses it on its count before reading any draw.
    private sealed class CountedDraws(int count) : IReadOnlyList<SdfMeshDraw> {
        public int Count => count;

        public SdfMeshDraw this[int index] => throw new InvalidOperationException(message: "The count alone decides.");

        public IEnumerator<SdfMeshDraw> GetEnumerator() => throw new InvalidOperationException(message: "The count alone decides.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
