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

        for (var texel = 0; (texel < Directions.Length); texel++) {
            var direction = Directions[texel];
            var hit = scene.Intersect(direction: direction, distance: out var distance,
                material: out _, maximum: reach, normal: out var normal, origin: origin);

            rays++;
            if (!hit) {
                surfaces[texel] = BitConverter.HalfToUInt16Bits(value: ((Half)(-reach)));
                continue;
            }
            if (Vector3.Dot(vector1: normal, vector2: direction) > 0f) { normal = -normal; }
            var uv = SdfOctahedral.Encode(direction: new Double3(X: normal.X, Y: normal.Y, Z: normal.Z));
            var scale = SdfMeshIrradianceProbe.DepthNormalScale;
            var x = ((int)Math.Round(a: (uv.X * scale)));
            var y = ((int)Math.Round(a: (uv.Y * scale)));
            var packed = (((uint)((byte)((sbyte)x))) << 16) | (((uint)((byte)((sbyte)y))) << 24);

            surfaces[texel] = packed | BitConverter.HalfToUInt16Bits(value: ((Half)distance));
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
            var gap = Vector3.Dot(vector1: Vector3.Normalize(value: normal), vector2: (toward - (point / total)));

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
