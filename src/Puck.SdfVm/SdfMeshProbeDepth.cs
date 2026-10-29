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
        Span<Vector3> points = stackalloc Vector3[(Size * Size)];
        Span<Vector3> normals = stackalloc Vector3[(Size * Size)];

        surfaces.Slice(start: (Size * Size)).Clear();

        for (var texel = 0; (texel < Directions.Length); texel++) {
            var direction = Directions[texel];
            var hit = scene.Intersect(direction: direction, distance: out var distance,
                material: out _, maximum: reach, normal: out var normal, origin: origin);

            rays++;
            if (!hit) {
                surfaces[texel] = BitConverter.HalfToUInt16Bits(value: ((Half)(-reach)));
                points[texel] = normals[texel] = Vector3.Zero;
                continue;
            }
            if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) { normal = -normal; }
            var uv = SdfOctahedral.Encode(direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z));
            var scale = SdfMeshIrradianceProbe.DepthNormalScale;
            var x = ((int)Math.Round(a: (uv.X * scale)));
            var y = ((int)Math.Round(a: (uv.Y * scale)));
            var packed = (((uint)((byte)((sbyte)x))) << 16) | (((uint)((byte)((sbyte)y))) << 24);

            surfaces[texel] = packed | BitConverter.HalfToUInt16Bits(value: ((Half)distance));
            points[texel] = (direction * ((float)((Half)distance)));
            normals[texel] = Normal(word: packed);
        }
        // Fit normal gradients once from the texel's local angular neighbourhood. Even a gather with only one
        // hit tap can then distinguish a plane from curvature; a lone hit in the whole neighbourhood is unsupported.
        for (var texel = 0; (texel < Directions.Length); texel++) {
            var variance = 0f;
            var covariance = 0f;

            if (normals[texel] != Vector3.Zero) {
                for (var dy = -1; (dy <= 1); dy++) {
                    for (var dx = -1; (dx <= 1); dx++) {
                        var source = SdfOctahedral.BorderSource(borderedX: (((texel % Size) + dx) + 1), borderedY: (((texel / Size) + dy) + 1), interiorTexels: Size);
                        var neighbour = (source.X + (Size * source.Y));

                        if ((neighbour == texel) || (normals[neighbour] == Vector3.Zero)) { continue; }
                        var delta = (points[neighbour] - points[texel]);

                        if ((Vector3.Dot(vector1: normals[texel], vector2: normals[neighbour]) <= 0f) ||
                            (MathF.Abs(x: Vector3.Dot(vector1: (normals[texel] + normals[neighbour]), vector2: delta)) > SdfMeshIrradianceProbe.DepthSurfaceJoinGap)) { continue; }
                        variance += delta.LengthSquared();
                        covariance += Vector3.Dot(vector1: delta, vector2: (normals[neighbour] - normals[texel]));
                    }
                }
            }
            var curvature = ((variance > 1e-8f) ? (covariance / variance) : float.NaN);
            var bits = ((uint)BitConverter.HalfToUInt16Bits(value: ((Half)curvature)));

            surfaces[(Directions.Length + (texel >> 1))] |= (bits << (16 * (texel & 1)));
        }
        return rays;
    }

    private static Vector3 Normal(uint word) {
        var normal = SdfOctahedral.Decode(x: (((sbyte)((byte)(word >> 16))) / ((double)SdfMeshIrradianceProbe.DepthNormalScale)),
            y: (((sbyte)((byte)(word >> 24))) / ((double)SdfMeshIrradianceProbe.DepthNormalScale)));

        return new Vector3(x: ((float)normal.X), y: ((float)normal.Y), z: ((float)normal.Z));
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
        Span<float> curvatures = stackalloc float[4];

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
            var packedCurvature = surfaces[(Directions.Length + (index >> 1))];
            var curvature = ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(packedCurvature >> (16 * (index & 1))))));

            if (!float.IsFinite(f: curvature)) { continue; }
            curvatures[corner] = curvature;
            weights[corner] = weight;
        }
        var blocked = 0f;

        for (var corner = 0; (corner < 4); corner++) {
            if (weights[corner] <= 0f) { continue; }
            var quadratic = 0f;
            var linear = Vector3.Zero;
            var constant = 0f;
            var total = 0f;

            for (var neighbour = 0; (neighbour < 4); neighbour++) {
                if (weights[neighbour] <= 0f) { continue; }
                var sum = (normals[corner] + normals[neighbour]);

                if ((Vector3.Dot(vector1: normals[corner], vector2: normals[neighbour]) <= 0f) ||
                    (MathF.Abs(x: Vector3.Dot(vector1: sum, vector2: (points[neighbour] - points[corner]))) > SdfMeshIrradianceProbe.DepthSurfaceJoinGap)) { continue; }
                var curvature = curvatures[neighbour];
                var point = points[neighbour];
                var normal = normals[neighbour];

                quadratic += ((weights[neighbour] * 0.5f) * curvature);
                linear += (weights[neighbour] * (normal - (curvature * point)));
                constant += (weights[neighbour] * (-Vector3.Dot(vector1: normal, vector2: point) + ((0.5f * curvature) * point.LengthSquared())));
                total += weights[neighbour];
            }
            quadratic /= total;
            linear /= total;
            constant /= total;
            var a = (quadratic * toward.LengthSquared());
            var b = Vector3.Dot(vector1: linear, vector2: toward);
            var c = constant;
            var discriminant = ((b * b) - ((4f * a) * c));

            if ((c <= 0f) || (discriminant <= 0f)) { continue; }
            var divisor = (MathF.Sqrt(x: discriminant) - b);

            if (divisor <= 1e-8f) { continue; }
            var along = ((2f * c) / divisor);

            if ((along <= 0f) || (along >= 1f)) { continue; }
            var hit = (toward * along);
            var gradient = (linear + ((2f * quadratic) * hit));
            var gap = Vector3.Dot(vector1: Vector3.Normalize(value: gradient), vector2: (toward - hit));

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
