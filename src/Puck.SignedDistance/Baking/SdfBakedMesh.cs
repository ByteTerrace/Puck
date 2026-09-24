using System.Numerics;

namespace Puck.SignedDistance.Baking;

/// <summary>One vertex of a baked mesh, in the prototype's engine frame.</summary>
/// <param name="Position">The position, in world units at unit placement scale.</param>
/// <param name="Normal">The unit outward normal: the field's gradient at the vertex.</param>
/// <param name="Uv">The texture coordinate into the surface textures' atlas, in <c>[0, 1]</c>.</param>
public readonly record struct SdfBakedVertex(Vector3 Position, Vector3 Normal, Vector2 Uv) {
    /// <summary>The size, in bytes, of one vertex packed as three, three, then two 32-bit floats.</summary>
    public const int PackedBytes = 32;
}
/// <summary>
/// An indexed triangle mesh extracted from a signed-distance field: the neutral CPU form an indexed-geometry upload
/// reads. Every quad the extraction produced owns four vertices and one <see cref="SdfBakeTier.TileTexels"/>-square
/// tile of the surface textures, so a vertex shared by two quads appears once per quad; <see cref="Indices"/> lists
/// two triangles per quad, each wound counter-clockwise seen from outside the surface.
/// </summary>
/// <param name="Vertices">The vertices, four per quad in quad order.</param>
/// <param name="Indices">The 32-bit triangle indices, six per quad.</param>
/// <param name="CellSize">The extraction grid's cell size, in world units.</param>
/// <param name="TileColumns">The quad tiles per row of the texture atlas.</param>
public sealed record SdfBakedMesh(SdfBakedVertex[] Vertices, uint[] Indices, float CellSize, int TileColumns) {
    /// <summary>Gets the quads, each one tile of the texture atlas.</summary>
    public int Quads => (Vertices.Length / 4);
    /// <summary>Gets the triangles.</summary>
    public int Triangles => (Indices.Length / 3);
    /// <summary>Gets the texture atlas width, in texels.</summary>
    public int AtlasWidth => (TileColumns * SdfBakeTier.TileTexels);
    /// <summary>Gets the texture atlas height, in texels.</summary>
    public int AtlasHeight => (AtlasRows(quads: Quads, columns: TileColumns) * SdfBakeTier.TileTexels);

    /// <summary>Returns the rows of tiles an atlas of <paramref name="quads"/> tiles in <paramref name="columns"/> columns
    /// takes.</summary>
    /// <param name="quads">The tiles.</param>
    /// <param name="columns">The tiles per row; at least one.</param>
    /// <returns>The rows, at least one.</returns>
    public static int AtlasRows(int quads, int columns) =>
        Math.Max(
            val1: 1,
            val2: ((quads + (columns - 1)) / columns)
        );
    /// <summary>Returns the tiles per row that makes an atlas of <paramref name="quads"/> tiles as square as it can
    /// be.</summary>
    /// <param name="quads">The tiles.</param>
    /// <returns>The tiles per row, at least one.</returns>
    public static int AtlasColumns(int quads) {
        var columns = 1;

        while ((columns * columns) < quads) {
            columns++;
        }

        return columns;
    }
}
