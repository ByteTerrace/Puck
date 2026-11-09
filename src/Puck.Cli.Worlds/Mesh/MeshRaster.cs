using System.Numerics;

using Puck.SdfVm;

namespace Puck.Cli.Mesh;

/// <summary>An orthographic view onto a mesh: a window centred on a point, <see cref="HalfWidth"/> metres either way
/// across, looking along <see cref="Forward"/> reversed.</summary>
/// <param name="Center">The point the window centres on.</param>
/// <param name="Right">The unit vector along the image's columns.</param>
/// <param name="Up">The unit vector up the image's rows.</param>
/// <param name="Forward">The unit vector from the point toward the camera.</param>
/// <param name="HalfWidth">Half the window's width, in metres.</param>
internal readonly record struct MeshView(Vector3 Center, Vector3 Right, Vector3 Up, Vector3 Forward, float HalfWidth) {
    /// <summary>Returns the view whose camera stands along a direction from a point.</summary>
    /// <param name="center">The point the window centres on.</param>
    /// <param name="toward">The direction from the point toward the camera; zero looks from +Z.</param>
    /// <param name="halfWidth">Half the window's width, in metres.</param>
    /// <param name="up">The direction that reads as up in the image, or <see langword="null"/> for +Y (+X where the view
    /// is within a few degrees of vertical).</param>
    /// <returns>The view.</returns>
    public static MeshView Looking(Vector3 center, Vector3 toward, float halfWidth, Vector3? up = null) {
        var forward = ((toward.LengthSquared() > 0f) ? Vector3.Normalize(value: toward) : Vector3.UnitZ);
        var helper = (up ?? ((MathF.Abs(x: forward.Y) < 0.9f) ? Vector3.UnitY : Vector3.UnitX));
        var right = Vector3.Normalize(value: Vector3.Cross(vector1: helper, vector2: forward));

        return new MeshView(Center: center, Forward: forward, HalfWidth: halfWidth, Right: right, Up: Vector3.Cross(vector1: forward, vector2: right));
    }
}
/// <summary>What a rasterised view shows at each pixel: the nearest triangle and the barycentric weights of its first two
/// corners (the third is the remainder), or no triangle.</summary>
internal sealed class MeshVisibility {
    /// <summary>The value of <see cref="Triangle"/> at a pixel no triangle covers.</summary>
    public const int None = -1;

    /// <summary>Initializes a new instance of the <see cref="MeshVisibility"/> class with every pixel uncovered.</summary>
    /// <param name="width">The image's width, in pixels.</param>
    /// <param name="height">The image's height, in pixels.</param>
    public MeshVisibility(int width, int height) {
        Width = width;
        Height = height;
        Triangle = new int[width * height];
        First = new float[width * height];
        Second = new float[width * height];
        Depth = new float[width * height];
        Array.Fill(array: Triangle, value: None);
        Array.Fill(array: Depth, value: float.NegativeInfinity);
    }

    /// <summary>Gets the image's width, in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets the image's height, in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets each pixel's nearest triangle, rows from the top.</summary>
    public int[] Triangle { get; }
    /// <summary>Gets each pixel's barycentric weight of its triangle's first corner.</summary>
    public float[] First { get; }
    /// <summary>Gets each pixel's barycentric weight of its triangle's second corner.</summary>
    public float[] Second { get; }
    /// <summary>Gets each pixel's distance toward the camera from the view's centre plane; the nearest wins.</summary>
    public float[] Depth { get; }
}
/// <summary>Draws a mesh's triangles into a visibility buffer on the CPU: a z-buffered orthographic scan conversion, one
/// implementation for every view the offline mesh tools draw (<see cref="MeshAuditImage"/>, <see cref="MeshPreviewImage"/>).</summary>
internal static class MeshRaster {
    /// <summary>Rasterises a mesh.</summary>
    /// <param name="mesh">The mesh; its own triangles, not its levels.</param>
    /// <param name="positions">The mesh's vertex positions in the view's frame, as many as <paramref name="mesh"/> holds.</param>
    /// <param name="view">The view; its window's width is spread over <paramref name="width"/> pixels, and the same
    /// scale is used up the image.</param>
    /// <param name="width">The image's width, in pixels.</param>
    /// <param name="height">The image's height, in pixels.</param>
    /// <param name="nearLimit">The depth past which a triangle with any corner is left out, so a view looks into a mesh
    /// from within.</param>
    /// <returns>The visibility buffer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <see langword="null"/>.</exception>
    public static MeshVisibility Draw(SdfMesh mesh, ReadOnlySpan<Vector3> positions, MeshView view, int width, int height, float nearLimit = float.PositiveInfinity) {
        ArgumentNullException.ThrowIfNull(argument: mesh);

        var buffer = new MeshVisibility(width: width, height: height);
        var indices = mesh.Indices.Span;
        var scale = ((width * 0.5f) / view.HalfWidth);
        var projected = new (float X, float Y, float Z)[positions.Length];

        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            var offset = (positions[vertex] - view.Center);

            projected[vertex] = (((width * 0.5f) + (Vector3.Dot(vector1: offset, vector2: view.Right) * scale)), ((height * 0.5f) - (Vector3.Dot(vector1: offset, vector2: view.Up) * scale)), Vector3.Dot(vector1: offset, vector2: view.Forward));
        }

        for (var triangle = 0; (triangle < mesh.TriangleCount); triangle++) {
            var a = projected[(int)indices[3 * triangle]];
            var b = projected[(int)indices[(3 * triangle) + 1]];
            var c = projected[(int)indices[(3 * triangle) + 2]];

            if ((a.Z > nearLimit) || (b.Z > nearLimit) || (c.Z > nearLimit)) { continue; }

            var area = (((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y)));

            if (MathF.Abs(x: area) < 1e-9f) { continue; }

            var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            var maxX = Math.Min((width - 1), (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            var maxY = Math.Min((height - 1), (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));

            for (var y = minY; (y <= maxY); y++) {
                for (var x = minX; (x <= maxX); x++) {
                    var (px, py) = ((x + 0.5f), (y + 0.5f));
                    var w0 = ((((b.X - px) * (c.Y - py)) - ((c.X - px) * (b.Y - py))) / area);
                    var w1 = ((((c.X - px) * (a.Y - py)) - ((a.X - px) * (c.Y - py))) / area);
                    var w2 = ((1f - w0) - w1);

                    if ((w0 < 0f) || (w1 < 0f) || (w2 < 0f)) { continue; }

                    var z = (((w0 * a.Z) + (w1 * b.Z)) + (w2 * c.Z));
                    var at = ((y * width) + x);

                    if (z <= buffer.Depth[at]) { continue; }
                    buffer.Depth[at] = z;
                    buffer.Triangle[at] = triangle;
                    buffer.First[at] = w0;
                    buffer.Second[at] = w1;
                }
            }
        }

        return buffer;
    }
}
