using System.Numerics;

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
        var helper = ((MathF.Abs(x: forward.Y) < 0.9f) ? Vector3.UnitY : Vector3.UnitX);
        var right = Vector3.Normalize(value: Vector3.Cross(vector1: helper, vector2: forward));
        var up = Vector3.Cross(vector1: forward, vector2: right);
        var depth = new float[Side * Side];
        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var indices = mesh.Indices.Span;

        Array.Fill(array: depth, value: float.NegativeInfinity);

        (float X, float Y, float Z) Project(Vector3 point) {
            var offset = (point - center);

            return ((((Vector3.Dot(vector1: offset, vector2: right) / halfWidth) * 0.5f) + 0.5f) * Side, ((0.5f - ((Vector3.Dot(vector1: offset, vector2: up) / halfWidth) * 0.5f)) * Side), Vector3.Dot(vector1: offset, vector2: forward));
        }

        for (var triangle = 0; (triangle < mesh.TriangleCount); triangle++) {
            var i0 = (int)indices[3 * triangle];
            var i1 = (int)indices[(3 * triangle) + 1];
            var i2 = (int)indices[(3 * triangle) + 2];
            var a = Project(point: positions[i0]);
            var b = Project(point: positions[i1]);
            var c = Project(point: positions[i2]);

            if ((a.Z > halfWidth) || (b.Z > halfWidth) || (c.Z > halfWidth)) { continue; }

            var area = (((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y)));

            if (MathF.Abs(x: area) < 1e-9f) { continue; }

            var face = Vector3.Cross(vector1: (positions[i1] - positions[i0]), vector2: (positions[i2] - positions[i0]));
            var flat = ((face.LengthSquared() > 0f) ? Vector3.Normalize(value: face) : Vector3.Zero);
            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            var maxX = Math.Min((Side - 1), (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            var maxY = Math.Min((Side - 1), (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));

            for (var y = minY; (y <= maxY); y++) {
                for (var x = minX; (x <= maxX); x++) {
                    var (px, py) = ((x + 0.5f), (y + 0.5f));
                    var w0 = ((((b.X - px) * (c.Y - py)) - ((c.X - px) * (b.Y - py))) / area);
                    var w1 = ((((c.X - px) * (a.Y - py)) - ((a.X - px) * (c.Y - py))) / area);
                    var w2 = ((1f - w0) - w1);

                    if ((w0 < 0f) || (w1 < 0f) || (w2 < 0f)) { continue; }

                    var z = (((w0 * a.Z) + (w1 * b.Z)) + (w2 * c.Z));
                    var at = ((y * Side) + x);

                    if (z <= depth[at]) { continue; }
                    depth[at] = z;

                    var shading = (normals.IsEmpty ? flat : (((normals[i0] * w0) + (normals[i1] * w1)) + (normals[i2] * w2)));

                    Paint(normal: ((shading.LengthSquared() > 0f) ? Vector3.Normalize(value: shading) : Vector3.Zero), rgba: rgba, x: x, y: ((row * Side) + y));
                    Paint(normal: flat, rgba: rgba, x: (x + Side), y: ((row * Side) + y));
                }
            }
        }
    }
    private static void Paint(Vector3 normal, byte[] rgba, int x, int y) {
        var at = (((y * (2 * Side)) + x) * 4);

        rgba[at] = (byte)Math.Clamp(value: (int)(((normal.X * 0.5f) + 0.5f) * 255f), min: 0, max: 255);
        rgba[at + 1] = (byte)Math.Clamp(value: (int)(((normal.Y * 0.5f) + 0.5f) * 255f), min: 0, max: 255);
        rgba[at + 2] = (byte)Math.Clamp(value: (int)(((normal.Z * 0.5f) + 0.5f) * 255f), min: 0, max: 255);
    }
}
