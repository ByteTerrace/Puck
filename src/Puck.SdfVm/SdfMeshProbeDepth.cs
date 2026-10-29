using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>Angular first-hit surface support and the receiver visibility rule shared with the views shader.</summary>
public static class SdfMeshProbeDepth {
    private const int Size = SdfMeshIrradianceProbe.DepthSize;

    private static readonly Vector3[] Directions = BuildDirections();

    /// <summary>Traces one bounded first hit per texel. Each word packs an FP16 ray distance and
    /// two signed octahedral normal bytes. A negative distance marks a miss and its measured reach.</summary>
    /// <param name="scene">The existing static mesh hierarchy.</param>
    /// <param name="origin">The relocated probe position.</param>
    /// <param name="reach">The receiver support radius, in metres.</param>
    /// <param name="surfaces">Receives the packed surface map.</param>
    /// <returns>The number of closest-hit rays issued.</returns>
    public static int Trace(SdfMeshIrradianceScene scene, Vector3 origin, float reach, Span<uint> surfaces) {
        var rays = 0;

        surfaces[(2 * Directions.Length)] = BitConverter.SingleToUInt32Bits(value: reach);

        for (var texel = 0; (texel < Directions.Length); texel++) {
            var direction = Directions[texel];
            var hit = scene.IntersectSurface(direction: direction, distance: out var distance,
                high: out var high, low: out var low, material: out _, maximum: reach, normal: out var normal, origin: origin);

            rays++;
            if (!hit) {
                surfaces[texel] = BitConverter.HalfToUInt16Bits(value: ((Half)(-reach)));
                surfaces[(Directions.Length + texel)] = 0;
                continue;
            }
            if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) { normal = -normal; }
            var uv = SdfOctahedral.Encode(direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z));
            var scale = SdfMeshIrradianceProbe.DepthNormalScale;
            var x = ((int)Math.Round(a: (uv.X * scale)));
            var y = ((int)Math.Round(a: (uv.Y * scale)));
            var packed = (((uint)((byte)((sbyte)x))) << 16) | (((uint)((byte)((sbyte)y))) << 24);

            surfaces[texel] = packed | BitConverter.HalfToUInt16Bits(value: ((Half)distance));
            // A finite hit must not extend its plane below a sign or past a wall's end. Store the triangle's
            // projected rectangle, padded by one angular footprint, rounded outward to signed bytes.
            var padding = (((2f * distance) / Size) + SdfMeshIrradianceProbe.DepthSurfaceFade);
            var minimum = (Project((low - origin), Normal(word: packed)) - new Vector2(value: padding));
            var maximum = (Project((high - origin), Normal(word: packed)) + new Vector2(value: padding));
            var scaleBounds = (127f / reach);

            static uint Code(float value) => ((byte)((sbyte)Math.Clamp(max: 127f, min: -127f, value: value)));
            surfaces[(Directions.Length + texel)] = Code(value: MathF.Floor(x: (minimum.X * scaleBounds))) |
                (Code(value: MathF.Ceiling(x: (maximum.X * scaleBounds))) << 8) |
                (Code(value: MathF.Floor(x: (minimum.Y * scaleBounds))) << 16) |
                (Code(value: MathF.Ceiling(x: (maximum.Y * scaleBounds))) << 24);
        }
        return rays;
    }

    private static Vector3 Normal(uint word) {
        var normal = SdfOctahedral.Decode(x: (((sbyte)((byte)(word >> 16))) / ((double)SdfMeshIrradianceProbe.DepthNormalScale)),
            y: (((sbyte)((byte)(word >> 24))) / ((double)SdfMeshIrradianceProbe.DepthNormalScale)));

        return new Vector3(x: ((float)normal.X), y: ((float)normal.Y), z: ((float)normal.Z));
    }
    // Drop the normal's dominant axis. CPU and shader choose from the same quantized normal.
    private static Vector2 Project(Vector3 point, Vector3 normal) {
        var absolute = Vector3.Abs(value: normal);

        return (((absolute.X >= absolute.Y) && (absolute.X >= absolute.Z)) ? new Vector2(x: point.Y, y: point.Z) :
            ((absolute.Y >= absolute.Z) ? new Vector2(x: point.X, y: point.Z) : new Vector2(x: point.X, y: point.Y)));
    }

    /// <summary>Tests signed surface gaps only in the four reflected angular taps. Coplanar receivers stay visible
    /// at grazing angles. A missed ray supplies no fictitious far blocker. KEEP IN SYNC with
    /// <c>sdfMeshProbeDepthVisibility</c>; there are no cross-probe or whole-sphere plane vetoes.</summary>
    /// <param name="surfaces">The packed surface map of one probe.</param>
    /// <param name="toward">Receiver minus probe position, after normal bias.</param>
    /// <returns>Visibility from zero to one; no support beyond a measured miss radius.</returns>
    public static float Visibility(ReadOnlySpan<uint> surfaces, Vector3 toward) {
        var distance = toward.Length();

        if (!(distance > 0f)) { return 1f; }
        var uv = SdfOctahedral.Encode(direction: new Double3(X: toward.X, Y: toward.Y, Z: toward.Z));
        var texel = ((((new Vector2(x: ((float)uv.X), y: ((float)uv.Y)) * 0.5f) + new Vector2(value: 0.5f)) * Size) - new Vector2(value: 0.5f));
        var x = ((int)MathF.Floor(x: texel.X));
        var y = ((int)MathF.Floor(x: texel.Y));
        var fraction = (texel - new Vector2(x: x, y: y));
        Span<Vector3> points = stackalloc Vector3[4];
        Span<Vector3> normals = stackalloc Vector3[4];
        Span<float> weights = stackalloc float[4];
        Span<Vector4> footprints = stackalloc Vector4[4];
        var footprintScale = (BitConverter.UInt32BitsToSingle(value: surfaces[(2 * Directions.Length)]) / 127f);

        weights.Clear();

        for (var corner = 0; (corner < 4); corner++) {
            var highX = corner & 1;
            var highY = (corner >> 1);
            var weight = (((highX != 0) ? fraction.X : (1f - fraction.X)) * ((highY != 0) ? fraction.Y : (1f - fraction.Y)));

            if (weight <= 0f) { continue; }
            var tap = SdfOctahedral.BorderSource(borderedX: ((x + highX) + 1), borderedY: ((y + highY) + 1), interiorTexels: Size);
            var index = (tap.X + (Size * tap.Y));
            var word = surfaces[index];
            var depth = ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)word)));

            if (depth < 0f) {
                if (distance > -depth) { return 0f; }
                continue;
            }
            points[corner] = (Directions[index] * depth);
            normals[corner] = Normal(word: word);
            weights[corner] = weight;
            var bounds = surfaces[(Directions.Length + index)];

            footprints[corner] = (new Vector4(w: ((sbyte)((byte)(bounds >> 24))), x: ((sbyte)((byte)bounds)),
                y: ((sbyte)((byte)(bounds >> 8))), z: ((sbyte)((byte)(bounds >> 16)))) * footprintScale);
        }
        var blocked = 0f;

        for (var corner = 0; (corner < 4); corner++) {
            if (weights[corner] <= 0f) { continue; }
            var point = Vector3.Zero;
            var normal = Vector3.Zero;
            var total = 0f;

            for (var neighbour = 0; (neighbour < 4); neighbour++) {
                if (weights[neighbour] <= 0f) { continue; }
                var sum = (normals[corner] + normals[neighbour]);
                // Same smooth patch: opposite faces and distinct parallel walls never interpolate together.
                if ((Vector3.Dot(vector1: normals[corner], vector2: normals[neighbour]) <= 0f) ||
                    (MathF.Abs(x: Vector3.Dot(vector1: sum, vector2: (points[neighbour] - points[corner]))) > SdfMeshIrradianceProbe.DepthSurfaceJoinGap)) { continue; }
                point += (weights[neighbour] * points[neighbour]);
                normal += (weights[neighbour] * normals[neighbour]);
                total += weights[neighbour];
            }
            point /= total;
            normal = Vector3.Normalize(value: normal);
            var gap = Vector3.Dot(vector1: normal, vector2: (toward - point));

            if (gap >= 0f) { continue; }
            var denominator = Vector3.Dot(vector1: normal, vector2: toward);

            if (MathF.Abs(x: denominator) < 1e-8f) { continue; }
            var along = (Vector3.Dot(vector1: normal, vector2: point) / denominator);

            if ((along < 0f) || (along > 1f)) { continue; }
            var projected = Project((toward * along), normals[corner]);
            var bounds = footprints[corner];

            if ((projected.X < bounds.X) || (projected.X > bounds.Y) || (projected.Y < bounds.Z) || (projected.Y > bounds.W)) { continue; }

            blocked += (weights[corner] * Math.Clamp(max: 1f, min: 0f, value: (-gap / SdfMeshIrradianceProbe.DepthSurfaceFade)));
        }
        var coverage = Math.Clamp(max: 1f, min: 0f, value: ((blocked - SdfMeshIrradianceProbe.DepthCoverageStart) * SdfMeshIrradianceProbe.DepthCoverageScale));

        coverage = ((coverage * coverage) * (3f - (2f * coverage)));
        return (1f - coverage);
    }

    private static Vector3[] BuildDirections() {
        var directions = new Vector3[(Size * Size)];

        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                var direction = SdfOctahedral.Decode(x: ((((x + 0.5) * 2.0) / Size) - 1.0), y: ((((y + 0.5) * 2.0) / Size) - 1.0));

                directions[(x + (Size * y))] = new Vector3(x: ((float)direction.X), y: ((float)direction.Y), z: ((float)direction.Z));
            }
        }
        return directions;
    }
}
