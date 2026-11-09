using System.Globalization;
using System.Numerics;

using Puck.Assets;
using Puck.Maths;
using Puck.SdfVm;

namespace Puck.Cli.Mesh;

/// <summary>One view of a creation in its author frame: the camera stands along an azimuth and elevation from the
/// creation's front, optionally on a close window about a point.</summary>
/// <param name="Name">The name the view's file and label carry.</param>
/// <param name="Azimuth">The camera's turn about up from the front (+Z), in degrees: +90 stands on the subject's own left
/// (+X), 180 behind.</param>
/// <param name="Elevation">The camera's height above the horizon, in degrees: 90 looks straight down.</param>
/// <param name="Focus">The author-frame point a close window centres on, or <see langword="null"/> to frame the whole
/// creation.</param>
/// <param name="HalfWidth">Half the close window's width, in metres; read only with <paramref name="Focus"/>.</param>
internal sealed record MeshPreviewView(string Name, float Azimuth, float Elevation, Vector3? Focus = null, float HalfWidth = 0f) {
    /// <summary>Gets the standard set: the four sides, the four three-quarter views from slightly above, the top and the
    /// view from below.</summary>
    public static IReadOnlyList<MeshPreviewView> Standard { get; } = [
        new("front", 0f, 0f), new("back", 180f, 0f), new("left", 90f, 0f), new("right", -90f, 0f),
        new("front-left", 45f, 18f), new("front-right", -45f, 18f), new("back-left", 135f, 18f), new("back-right", -135f, 18f),
        new("top", 0f, 90f), new("below", 0f, -90f),
    ];

    /// <summary>Gets the direction from the creation toward the camera.</summary>
    public Vector3 Toward {
        get {
            var azimuth = (Azimuth * (MathF.PI / 180f));
            var elevation = (Elevation * (MathF.PI / 180f));

            return new Vector3(x: (MathF.Sin(x: azimuth) * MathF.Cos(x: elevation)), y: MathF.Sin(x: elevation), z: (MathF.Cos(x: azimuth) * MathF.Cos(x: elevation)));
        }
    }
    /// <summary>Gets the direction that reads as up in the image: +Y, or the creation's front (+Z) from straight above or
    /// below, so the front is at the top of the image.</summary>
    public Vector3 Up => ((MathF.Abs(x: Elevation) >= 89f) ? Vector3.UnitZ : Vector3.UnitY);

    /// <summary>Reads the views a request names: the standard names, <c>all</c> for the standard set, or
    /// <c>name=azimuth,elevation</c> optionally followed by <c>,x,y,z,halfWidth</c> for a close window.</summary>
    /// <param name="tokens">The request's tokens; a token without <c>=</c> holds standard names separated by commas.</param>
    /// <param name="views">The views, in the order named, when this returns <see langword="true"/>.</param>
    /// <param name="error">Why a token was refused, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when every token was read.</returns>
    public static bool TryParse(IEnumerable<string> tokens, out List<MeshPreviewView> views, out string error) {
        views = [];
        error = string.Empty;

        foreach (var token in tokens) {
            var equals = token.IndexOf(value: '=');

            if (equals < 0) {
                foreach (var name in token.Split(separator: ',', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))) {
                    if (string.Equals(a: name, b: "all", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                        views.AddRange(collection: Standard);
                    } else if (Standard.FirstOrDefault(predicate: view => string.Equals(a: view.Name, b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) is { } standard) {
                        views.Add(item: standard);
                    } else {
                        error = $"'{name}' names no standard view ({string.Join(separator: ", ", values: Standard.Select(selector: static view => view.Name))}) and has no '=azimuth,elevation'";
                        return false;
                    }
                }
                continue;
            }

            var numbers = token[(equals + 1)..].Split(separator: ',', options: StringSplitOptions.TrimEntries);
            var values = new float[numbers.Length];

            if (((numbers.Length != 2) && (numbers.Length != 6)) || (equals == 0)) {
                error = $"'{token}' is not name=azimuth,elevation or name=azimuth,elevation,x,y,z,halfWidth";
                return false;
            }
            for (var at = 0; (at < numbers.Length); at++) {
                if (!float.TryParse(provider: CultureInfo.InvariantCulture, result: out values[at], s: numbers[at], style: NumberStyles.Float) || !float.IsFinite(f: values[at])) {
                    error = $"'{numbers[at]}' in '{token}' is not a number";
                    return false;
                }
            }
            if ((numbers.Length == 6) && !(values[5] > 0f)) {
                error = $"the half width in '{token}' is positive";
                return false;
            }
            views.Add(item: new MeshPreviewView(Name: token[..equals], Azimuth: values[0], Elevation: values[1], Focus: ((numbers.Length == 6) ? new Vector3(x: values[2], y: values[3], z: values[4]) : null), HalfWidth: ((numbers.Length == 6) ? values[5] : 0f)));
        }

        if (views.Count == 0) {
            views.AddRange(collection: Standard);
        }

        return true;
    }
}
/// <summary>A creation's rest mesh in its author frame, ready to draw: one mesh, each triangle's colour and whether it holds
/// an open edge.</summary>
/// <param name="Mesh">The joined mesh of every group of the creation.</param>
/// <param name="Albedo">One display colour, each channel 0 to 1, per triangle.</param>
/// <param name="OpenTriangles">One flag per triangle: it holds an edge no other triangle shares.</param>
internal sealed record MeshPreviewSubject(SdfMesh Mesh, Vector3[] Albedo, bool[] OpenTriangles);
/// <summary>
/// Draws a creation's baked mesh from a view as a lit image, on the CPU through <see cref="MeshRaster"/>: orthographic,
/// lit by one light fixed in the camera's frame (above and to the left of the lens, so every view is lit alike) with a
/// little sky term, each triangle in its material's palette colour, drawn at twice the resolution and averaged. A triangle
/// seen from behind (the mesh's inside showing, which a closed surface never does) is magenta, a triangle holding an open
/// edge is tinted red, and a depth step darkens the nearer side, so overlapping parts of one colour still part. Paints,
/// insets, wear and every other layer of a material are omitted: the colour is its albedo.
/// </summary>
internal static class MeshPreviewImage {
    /// <summary>The height, in pixels, of the caption strip under a view's image.</summary>
    public const int StripHeight = 34;

    private const int Supersample = 2;
    // A depth step wider than this share of the window's width is an outline.
    private const float OutlineShare = 0.02f;
    private static readonly Vector3 Background = new(x: (206f / 255f), y: (208f / 255f), z: (212f / 255f));
    private static readonly byte[] StripColor = [24, 26, 30, 255];
    private static readonly byte[] CaptionColor = [235, 235, 235, 255];
    private static readonly (int X, int Y)[] Neighbours = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    /// <summary>Frames a view: the whole mesh centred with a margin, or the close window the view names.</summary>
    /// <param name="mesh">The mesh, in the author frame.</param>
    /// <param name="view">The view.</param>
    /// <param name="aspect">The image's width over its height.</param>
    /// <returns>The orthographic window.</returns>
    public static MeshView Frame(SdfMesh mesh, MeshPreviewView view, float aspect) {
        if (view.Focus is { } focus) {
            return MeshView.Looking(center: focus, halfWidth: view.HalfWidth, toward: view.Toward, up: view.Up);
        }

        var axes = MeshView.Looking(center: Vector3.Zero, halfWidth: 1f, toward: view.Toward, up: view.Up);
        var (low, high) = (new Vector2(value: float.PositiveInfinity), new Vector2(value: float.NegativeInfinity));

        foreach (var position in mesh.Positions.Span) {
            var projected = new Vector2(x: Vector3.Dot(vector1: position, vector2: axes.Right), y: Vector3.Dot(vector1: position, vector2: axes.Up));

            low = Vector2.Min(value1: low, value2: projected);
            high = Vector2.Max(value1: high, value2: projected);
        }

        var middle = ((low + high) * 0.5f);
        var half = MathF.Max(((high.X - low.X) * 0.5f), (((high.Y - low.Y) * 0.5f) * aspect));

        return axes with { Center = ((axes.Right * middle.X) + (axes.Up * middle.Y)), HalfWidth = MathF.Max(0.001f, (half * 1.08f)) };
    }
    /// <summary>Draws a subject from a view with a caption strip under it.</summary>
    /// <param name="subject">The subject.</param>
    /// <param name="view">The window to draw.</param>
    /// <param name="width">The image's width, in pixels.</param>
    /// <param name="height">The view's height, in pixels, above the caption strip.</param>
    /// <param name="flat">Whether each triangle is lit by its own face normal rather than by normals interpolated across it.</param>
    /// <param name="caption">The caption's lines, drawn small in the strip.</param>
    /// <returns>The image, RGBA8, <paramref name="width"/> by <paramref name="height"/> plus <see cref="StripHeight"/>.</returns>
    public static byte[] Render(MeshPreviewSubject subject, MeshView view, int width, int height, bool flat, IReadOnlyList<string> caption) {
        var mesh = subject.Mesh;
        var positions = mesh.Positions.Span;
        var normals = mesh.Normals.Span;
        var indices = mesh.Indices.Span;
        var (wide, tall) = ((width * Supersample), (height * Supersample));
        var seen = MeshRaster.Draw(height: tall, mesh: mesh, positions: positions, view: view, width: wide);
        var colors = new Vector3[wide * tall];
        var light = Vector3.Normalize(value: (view.Forward + (0.6f * view.Up) - (0.4f * view.Right)));
        var outline = (OutlineShare * 2f * view.HalfWidth);

        for (var y = 0; (y < tall); y++) {
            for (var x = 0; (x < wide); x++) {
                var at = ((y * wide) + x);
                var triangle = seen.Triangle[at];

                if (triangle == MeshVisibility.None) {
                    colors[at] = Background;
                    continue;
                }

                var i0 = (int)indices[3 * triangle];
                var i1 = (int)indices[(3 * triangle) + 1];
                var i2 = (int)indices[(3 * triangle) + 2];
                var (w0, w1) = (seen.First[at], seen.Second[at]);
                var smooth = (normals.IsEmpty ? Vector3.Zero : (((normals[i0] * w0) + (normals[i1] * w1)) + (normals[i2] * ((1f - w0) - w1))));
                var face = Vector3.Cross(vector1: (positions[i1] - positions[i0]), vector2: (positions[i2] - positions[i0]));

                // A face normal is turned to the side the vertex normals face, so a mesh wound either way lights alike.
                if ((smooth != Vector3.Zero) && (Vector3.Dot(vector1: face, vector2: smooth) < 0f)) { face = -face; }

                var lit = (flat ? face : ((smooth != Vector3.Zero) ? smooth : face));
                var normal = ((lit.LengthSquared() > 0f) ? Vector3.Normalize(value: lit) : view.Forward);
                var color = Vector3.Zero;

                if (Vector3.Dot(vector1: normal, vector2: view.Forward) < -0.15f) {
                    color = new Vector3(x: 0.85f, y: 0.1f, z: 0.75f);
                } else {
                    var shade = ((0.30f + (0.62f * MathF.Max(0f, Vector3.Dot(vector1: normal, vector2: light)))) + (0.12f * ((0.5f * normal.Y) + 0.5f)));

                    color = Vector3.Min(value1: Vector3.One, value2: (subject.Albedo[triangle] * shade));
                }
                if (subject.OpenTriangles[triangle]) { color = Vector3.Lerp(value1: color, value2: new Vector3(x: 1f, y: 0.1f, z: 0.1f), amount: 0.65f); }

                var depth = seen.Depth[at];

                if (Steps(depth: depth, seen: seen, x: x, y: y, outline: outline)) { color *= 0.55f; }
                colors[at] = color;
            }
        }

        var stride = width;
        var rgba = new byte[(width * (height + StripHeight)) * 4];

        for (var y = 0; (y < height); y++) {
            for (var x = 0; (x < width); x++) {
                var sum = Vector3.Zero;

                for (var sy = 0; (sy < Supersample); sy++) {
                    for (var sx = 0; (sx < Supersample); sx++) { sum += colors[(((y * Supersample) + sy) * wide) + ((x * Supersample) + sx)]; }
                }

                sum /= (Supersample * Supersample);
                var at = (((y * stride) + x) * 4);

                rgba[at] = VectorFunctions.ToUnorm8(value: sum.X);
                rgba[at + 1] = VectorFunctions.ToUnorm8(value: sum.Y);
                rgba[at + 2] = VectorFunctions.ToUnorm8(value: sum.Z);
                rgba[at + 3] = 255;
            }
        }
        for (var pixel = (width * height); (pixel < (rgba.Length / 4)); pixel++) {
            StripColor.CopyTo(array: rgba, index: (pixel * 4));
        }
        for (var line = 0; (line < caption.Count); line++) {
            BitmapFont.Draw(color: CaptionColor, height: (height + StripHeight), left: 4, rgba: rgba, scale: 1, text: caption[line], top: (height + 3 + (line * (BitmapFont.GlyphSize + 2))), width: width);
        }

        return rgba;
    }
    // Whether a covered pixel is nearer than a neighbour by more than an outline's depth: the near side of a step.
    private static bool Steps(float depth, MeshVisibility seen, int x, int y, float outline) {
        foreach (var (dx, dy) in Neighbours) {
            var (nx, ny) = ((x + dx), (y + dy));

            if ((nx < 0) || (ny < 0) || (nx >= seen.Width) || (ny >= seen.Height)) { continue; }
            if ((depth - seen.Depth[((ny * seen.Width) + nx)]) > outline) { return true; }
        }

        return false;
    }
    /// <summary>Joins rendered images into a grid, left to right and top to bottom.</summary>
    /// <param name="tiles">The images, each <paramref name="tileWidth"/> by <paramref name="tileHeight"/>.</param>
    /// <param name="tileWidth">A tile's width, in pixels.</param>
    /// <param name="tileHeight">A tile's height, in pixels.</param>
    /// <param name="columns">The tiles in a row.</param>
    /// <returns>The sheet's width and height, in pixels, and its RGBA8 bytes.</returns>
    public static (int Width, int Height, byte[] Rgba) Sheet(IReadOnlyList<byte[]> tiles, int tileWidth, int tileHeight, int columns) {
        const int Gap = 4;
        var rows = ((tiles.Count + columns - 1) / columns);
        var (width, height) = (((columns * tileWidth) + ((columns - 1) * Gap)), ((rows * tileHeight) + ((rows - 1) * Gap)));
        var rgba = new byte[(width * height) * 4];

        for (var pixel = 0; (pixel < (width * height)); pixel++) {
            StripColor.CopyTo(array: rgba, index: (pixel * 4));
        }
        for (var tile = 0; (tile < tiles.Count); tile++) {
            var (left, top) = (((tile % columns) * (tileWidth + Gap)), ((tile / columns) * (tileHeight + Gap)));

            for (var row = 0; (row < tileHeight); row++) {
                tiles[tile].AsSpan(start: (row * tileWidth * 4), length: (tileWidth * 4)).CopyTo(destination: rgba.AsSpan(start: ((((top + row) * width) + left) * 4)));
            }
        }

        return (width, height, rgba);
    }
}
