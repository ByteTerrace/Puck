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
    [Fact]
    public void AnAttributeOfTheWrongLengthOrANonFiniteOneIsRefusedByName() {
        Assert.Equal(expected: "normals", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, normals: new Vector3[] { Vector3.UnitZ }, positions: Positions)).ParamName);
        Assert.Equal(expected: "uvs", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, uvs: new Vector2[] { Vector2.Zero, Vector2.Zero })).ParamName);
        Assert.Equal(expected: "triangleMaterials", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, triangleMaterials: new uint[] { 0, 1 })).ParamName);
        Assert.Equal(expected: "normals", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, normals: new Vector3[] { Vector3.UnitZ, new(x: float.NaN, y: 0f, z: 1f), Vector3.UnitZ }, positions: Positions)).ParamName);
        Assert.Equal(expected: "uvs", actual: Assert.Throws<ArgumentException>(testCode: static () => new SdfMesh(indices: Indices, positions: Positions, uvs: new Vector2[] { Vector2.Zero, new(x: float.PositiveInfinity, y: 0f), Vector2.Zero })).ParamName);
    }
}
