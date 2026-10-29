using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>Octahedral first-hit distance moments and the receiver visibility rule shared with the views shader.</summary>
public static class SdfMeshProbeDepth {
    private const int Size = SdfMeshIrradianceProbe.DepthSize;

    private static readonly Vector3[] Directions = BuildDirections();

    /// <summary>Traces four first hits per texel into packed FP16 mean and mean-squared metres, and measures this
    /// probe's sky cube from those same rays. Misses end at the reach.</summary>
    /// <param name="scene">The existing static mesh hierarchy.</param>
    /// <param name="origin">The relocated probe position.</param>
    /// <param name="reach">The lighting trace reach, in metres.</param>
    /// <param name="moments">Receives <c>DepthSize * DepthSize</c> packed moment pairs.</param>
    /// <param name="sky">Receives the six cosine-weighted sky visibility faces, weighted by octahedral solid angle.</param>
    public static void Trace(SdfMeshIrradianceScene scene, Vector3 origin, float reach, Span<uint> moments, Span<float> sky) {
        var maximum = MathF.Min(x: reach, y: SdfMeshIrradianceProbe.DepthMaximum);
        Span<float> total = stackalloc float[6];

        sky.Clear();

        for (var texel = 0; (texel < (Size * Size)); texel++) {
            var sum = 0f;
            var squared = 0f;
            var depthWeight = 0f;

            for (var sample = 0; (sample < 4); sample++) {
                var direction = Directions[((4 * texel) + sample)];
                var hit = scene.Intersect(direction: direction, distance: out var distance, material: out _, maximum: reach, normal: out _, origin: origin);
                var scale = ((MathF.Abs(x: direction.X) + MathF.Abs(x: direction.Y)) + MathF.Abs(x: direction.Z));
                // Jacobian of the normalized octahedron: |unnormalized direction|^-3. Constant texel area cancels.
                var solidAngle = ((scale * scale) * scale);

                for (var axis = 0; (axis < 3); axis++) {
                    var component = direction[axis];
                    var face = ((2 * axis) + ((component < 0f) ? 1 : 0));
                    var weight = (MathF.Abs(x: component) * solidAngle);

                    total[face] += weight;
                    if (!hit) { sky[face] += weight; }
                }
                distance = (hit ? MathF.Min(x: distance, y: maximum) : maximum);
                sum += (distance * solidAngle);
                squared += ((distance * distance) * solidAngle);
                depthWeight += solidAngle;
            }
            moments[texel] = Pack(mean: (sum / depthWeight), squared: (squared / depthWeight));
        }
        for (var face = 0; (face < 6); face++) { sky[face] /= total[face]; }
    }

    private static uint Pack(float mean, float squared) => ((uint)BitConverter.HalfToUInt16Bits(value: ((Half)mean))) | (((uint)BitConverter.HalfToUInt16Bits(value: ((Half)squared))) << 16);

    /// <summary>Tests the receiver against bilinearly interpolated depth moments with the one-sided Chebyshev bound.
    /// KEEP IN SYNC with <c>sdfMeshProbeDepthVisibility</c>, including octahedral seam reflection and weight crushing.</summary>
    /// <param name="moments">The packed depth map of one probe.</param>
    /// <param name="toward">Receiver minus probe position, in metres, after normal bias.</param>
    /// <returns>Visibility from zero to one. A receiver before the mean is fully visible.</returns>
    public static float Visibility(ReadOnlySpan<uint> moments, Vector3 toward) {
        var distance = toward.Length();

        if (!(distance > 0f)) { return 1f; }
        var uv = SdfOctahedral.Encode(direction: new Double3(X: toward.X, Y: toward.Y, Z: toward.Z));
        var texel = ((((new Vector2(x: ((float)uv.X), y: ((float)uv.Y)) * 0.5f) + new Vector2(value: 0.5f)) * Size) - new Vector2(value: 0.5f));
        var x = ((int)MathF.Floor(x: texel.X));
        var y = ((int)MathF.Floor(x: texel.Y));
        var fraction = (texel - new Vector2(x: x, y: y));
        var mean = 0f;
        var squared = 0f;

        for (var corner = 0; (corner < 4); corner++) {
            var highX = corner & 1;
            var highY = (corner >> 1);
            var weight = (((highX != 0) ? fraction.X : (1f - fraction.X)) * ((highY != 0) ? fraction.Y : (1f - fraction.Y)));
            var tap = SdfOctahedral.BorderSource(borderedX: ((x + highX) + 1), borderedY: ((y + highY) + 1), interiorTexels: Size);
            var word = moments[(tap.X + (Size * tap.Y))];

            mean += (weight * ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(word & 0xffffu)))));
            squared += (weight * ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(word >> 16)))));
        }
        if (distance <= mean) { return 1f; }
        var variance = MathF.Max(x: 0f, y: (squared - (mean * mean)));
        var delta = (distance - mean);
        var probability = (variance / MathF.Max(x: 1e-8f, y: (variance + (delta * delta))));
        var visibility = ((probability * probability) * probability);

        return ((visibility < 0.2f) ? (((visibility * visibility) * visibility) / 0.04f) : visibility);
    }

    private static Vector3[] BuildDirections() {
        var directions = new Vector3[((Size * Size) * 4)];

        for (var y = 0; (y < Size); y++) {
            for (var x = 0; (x < Size); x++) {
                for (var sample = 0; (sample < 4); sample++) {
                    var uv = new Vector2(x: (((x + 0.25f) + (0.5f * (sample & 1))) / Size), y: (((y + 0.25f) + (0.5f * (sample >> 1))) / Size));

                    var direction = SdfOctahedral.Decode(x: ((uv.X * 2.0) - 1.0), y: ((uv.Y * 2.0) - 1.0));

                    directions[((4 * (x + (Size * y))) + sample)] = new Vector3(x: ((float)direction.X), y: ((float)direction.Y), z: ((float)direction.Z));
                }
            }
        }
        return directions;
    }
}
