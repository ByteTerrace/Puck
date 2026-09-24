using System.Numerics;

namespace Puck.SdfVm;

/// <summary>An indexed triangle list in object space: opaque geometry a frame draws beside its SDF program.</summary>
/// <remarks>Triangles wind counter-clockwise seen from their front, in the right-handed convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>. Nothing produces or draws a mesh yet.</remarks>
public sealed record SdfMesh {
    /// <summary>Creates a mesh, refusing a malformed index list or a non-finite position.</summary>
    /// <param name="positions">The object-space vertex positions.</param>
    /// <param name="indices">Three vertex indices per triangle.</param>
    /// <exception cref="ArgumentException">The index count is zero or not a multiple of three, an index names no
    /// vertex, or a position is not finite.</exception>
    public SdfMesh(ReadOnlyMemory<Vector3> positions, ReadOnlyMemory<uint> indices) {
        if (
            indices.IsEmpty ||
            ((indices.Length % 3) != 0)
        ) {
            throw new ArgumentException(
                message: "A mesh needs at least one triangle and three indices per triangle.",
                paramName: nameof(indices)
            );
        }

        foreach (var position in positions.Span) {
            if (
                !float.IsFinite(f: position.X) ||
                !float.IsFinite(f: position.Y) ||
                !float.IsFinite(f: position.Z)
            ) {
                throw new ArgumentException(
                    message: "Every mesh position must be finite.",
                    paramName: nameof(positions)
                );
            }
        }
        foreach (var index in indices.Span) {
            if (index >= ((uint)positions.Length)) {
                throw new ArgumentException(
                    message: $"Index {index} names no vertex of the {positions.Length} the mesh has.",
                    paramName: nameof(indices)
                );
            }
        }

        Positions = positions;
        Indices = indices;
    }

    /// <summary>Gets three vertex indices per triangle.</summary>
    public ReadOnlyMemory<uint> Indices { get; }
    /// <summary>Gets the object-space vertex positions.</summary>
    public ReadOnlyMemory<Vector3> Positions { get; }
    /// <summary>Gets the triangle count.</summary>
    public int TriangleCount => (Indices.Length / 3);
}
/// <summary>One placement of an <see cref="SdfMesh"/> in a frame.</summary>
/// <param name="Mesh">The triangles to draw.</param>
/// <param name="ObjectToWorld">The object-to-world transform, in the row-vector convention of
/// <see cref="Puck.Abstractions.Cameras.ViewProjection"/>.</param>
/// <param name="Material">The material-table index the triangles shade with, as an SDF hit's material does.</param>
public readonly record struct SdfMeshDraw(SdfMesh Mesh, Matrix4x4 ObjectToWorld, int Material);
