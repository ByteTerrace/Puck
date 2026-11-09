using System.Numerics;

using Puck.Maths;
using Puck.SdfVm;

namespace Puck.Cli.Mesh;

/// <summary>
/// Draws the part of a baked mesh around a point as a PNG, on the CPU: three rows, each a view onto the point, and in each
/// row its shading normals, interpolated across each triangle as the normals debug view (<c>world.debug-view normals</c>)
/// draws them, beside its faces' own normals, flat, each as <c>(n + 1) / 2</c> in RGB. The first row looks down a
/// direction; the other two look from that direction turned sixty degrees either way about an axis, the line a defect
/// runs along, so an edge's two faces and its rim are each seen. Each view is orthographic over a square window and draws
/// only the mesh within that window's depth in front of the point, so a defect the audit found is seen without a GPU.
/// </summary>
internal static class MeshAuditImage {
    // Each panel's side, in pixels.
    private const int Side = 384;
    // The turn, in radians, of the second and third rows' views about the axis.
    private const float Turn = (MathF.PI / 3f);

    /// <summary>Writes the panels of a mesh around a point.</summary>
    /// <param name="mesh">The mesh, in the frame the point and directions are in.</param>
    /// <param name="center">The point the views centre on.</param>
    /// <param name="toward">The direction the first view looks from: the camera stands along it from the point.</param>
    /// <param name="axis">The axis the other views turn about.</param>
    /// <param name="halfWidth">Half the window's side, in metres.</param>
    /// <param name="path">The PNG to write.</param>
    public static void Write(SdfMesh mesh, Vector3 center, Vector3 toward, Vector3 axis, float halfWidth, string path) {
        var forward = ((toward.LengthSquared() > 0f) ? Vector3.Normalize(value: toward) : Vector3.UnitZ);
        var about = ((axis.LengthSquared() > 0f) ? Vector3.Normalize(value: axis) : Vector3.UnitY);
        var rgba = new byte[((2 * Side) * (3 * Side)) * 4];

        for (var pixel = 0; (pixel < (rgba.Length / 4)); pixel++) {
            rgba[(4 * pixel) + 3] = 255;
        }

        Draw(center: center, forward: forward, halfWidth: halfWidth, mesh: mesh, rgba: rgba, row: 0);
        Draw(center: center, forward: Vector3.Transform(value: forward, rotation: Quaternion.CreateFromAxisAngle(angle: Turn, axis: about)), halfWidth: halfWidth, mesh: mesh, rgba: rgba, row: 1);
        Draw(center: center, forward: Vector3.Transform(value: forward, rotation: Quaternion.CreateFromAxisAngle(angle: -Turn, axis: about)), halfWidth: halfWidth, mesh: mesh, rgba: rgba, row: 2);
        Puck.Assets.PngEncoder.Write(height: (3 * Side), path: path, rgba: rgba, width: (2 * Side));
    }

    // One row: the mesh seen from forward, its shading normals on the left and its face normals on the right.
    private static void Draw(SdfMesh mesh, Vector3 center, Vector3 forward, float halfWidth, byte[] rgba, int row) {
        var view = MeshView.Looking(center: center, halfWidth: halfWidth, toward: forward);
        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var indices = mesh.Indices.Span;
        var seen = MeshRaster.Draw(height: Side, mesh: mesh, nearLimit: halfWidth, positions: positions, view: view, width: Side);

        for (var y = 0; (y < Side); y++) {
            for (var x = 0; (x < Side); x++) {
                var at = ((y * Side) + x);
                var triangle = seen.Triangle[at];

                if (triangle == MeshVisibility.None) { continue; }

                var i0 = (int)indices[3 * triangle];
                var i1 = (int)indices[(3 * triangle) + 1];
                var i2 = (int)indices[(3 * triangle) + 2];
                var (w0, w1) = (seen.First[at], seen.Second[at]);
                var face = Vector3.Cross(vector1: (positions[i1] - positions[i0]), vector2: (positions[i2] - positions[i0]));
                var flat = ((face.LengthSquared() > 0f) ? Vector3.Normalize(value: face) : Vector3.Zero);
                var shading = (normals.IsEmpty ? flat : (((normals[i0] * w0) + (normals[i1] * w1)) + (normals[i2] * ((1f - w0) - w1))));

                Paint(normal: ((shading.LengthSquared() > 0f) ? Vector3.Normalize(value: shading) : Vector3.Zero), rgba: rgba, x: x, y: ((row * Side) + y));
                Paint(normal: flat, rgba: rgba, x: (x + Side), y: ((row * Side) + y));
            }
        }
    }
    private static void Paint(Vector3 normal, byte[] rgba, int x, int y) {
        var at = (((y * (2 * Side)) + x) * 4);

        rgba[at] = VectorFunctions.ToUnorm8(value: ((normal.X * 0.5f) + 0.5f));
        rgba[at + 1] = VectorFunctions.ToUnorm8(value: ((normal.Y * 0.5f) + 0.5f));
        rgba[at + 2] = VectorFunctions.ToUnorm8(value: ((normal.Z * 0.5f) + 0.5f));
    }
}
