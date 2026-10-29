using System.Numerics;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>Octahedral first-hit distances and the receiver visibility rule shared with the views shader.</summary>
public static class SdfMeshProbeDepth {
    private const int Size = SdfMeshIrradianceProbe.DepthSize;

    private static readonly Vector3[] Directions = BuildDirections();

    /// <summary>Traces one bounded first hit per texel, packing two FP16 distances per word.
    /// A negative distance marks a miss and its measured reach; it never enters blocker moments.</summary>
    /// <param name="scene">The existing static mesh hierarchy.</param>
    /// <param name="origin">The relocated probe position.</param>
    /// <param name="reach">The receiver support radius, in metres.</param>
    /// <param name="moments">Receives the packed distance map.</param>
    /// <returns>The number of closest-hit rays issued.</returns>
    public static int Trace(SdfMeshIrradianceScene scene, Vector3 origin, float reach, Span<uint> moments) {
        var rays = 0;

        for (var texel = 0; (texel < Directions.Length); texel += 2) {
            var word = 0U;

            for (var half = 0; (half < 2); half++) {
                var hit = scene.Intersect(direction: Directions[(texel + half)], distance: out var distance,
                    material: out _, maximum: reach, normal: out _, origin: origin);

                rays++;
                word |= (((uint)BitConverter.HalfToUInt16Bits(value: ((Half)(hit ? distance : -reach)))) << (16 * half));
            }
            moments[(texel / 2)] = word;
        }
        return rays;
    }
    /// <summary>Tests the receiver against moments of the footprint's hits. Misses establish measured support,
    /// never a fictitious far blocker. KEEP IN SYNC with <c>sdfMeshProbeDepthVisibility</c>.</summary>
    /// <param name="moments">The packed distance map of one probe.</param>
    /// <param name="toward">Receiver minus probe position, after normal bias.</param>
    /// <returns>Visibility from zero to one; no support beyond the measured miss radius.</returns>
    public static float Visibility(ReadOnlySpan<uint> moments, Vector3 toward) {
        var distance = toward.Length();

        if (!(distance > 0f)) { return 1f; }
        var uv = SdfOctahedral.Encode(direction: new Double3(X: toward.X, Y: toward.Y, Z: toward.Z));
        var texel = ((((new Vector2(x: ((float)uv.X), y: ((float)uv.Y)) * 0.5f) + new Vector2(value: 0.5f)) * Size) - new Vector2(value: 0.5f));
        var x = ((int)MathF.Floor(x: texel.X));
        var y = ((int)MathF.Floor(x: texel.Y));
        var fraction = (texel - new Vector2(x: x, y: y));
        Span<float> depths = stackalloc float[4];
        Span<float> weights = stackalloc float[4];

        depths.Clear();
        weights.Clear();
        var mean = 0f;
        var total = 0f;

        for (var corner = 0; (corner < 4); corner++) {
            var highX = corner & 1;
            var highY = (corner >> 1);
            var weight = (((highX != 0) ? fraction.X : (1f - fraction.X)) * ((highY != 0) ? fraction.Y : (1f - fraction.Y)));
            var tap = SdfOctahedral.BorderSource(borderedX: ((x + highX) + 1), borderedY: ((y + highY) + 1), interiorTexels: Size);
            var index = (tap.X + (Size * tap.Y));
            var word = moments[(index >> 1)];
            var depth = ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(word >> (16 * (index & 1))))));

            if (weight <= 0f) { continue; }
            if (depth < 0f) {
                if (distance > -depth) { return 0f; }
                continue;
            }
            depths[corner] = depth;
            weights[corner] = weight;
            mean += (weight * depth);
            total += weight;
        }
        if (total <= 0f) { return 1f; }
        mean /= total;
        if (distance <= mean) { return 1f; }
        // Central variance avoids cancellation between independently rounded FP16 mean and mean-squared values.
        var variance = 0f;

        for (var corner = 0; (corner < 4); corner++) {
            var deviation = (depths[corner] - mean);

            variance += ((weights[corner] * deviation) * deviation);
        }
        variance /= total;
        var delta = (distance - mean);
        var probability = (variance / MathF.Max(x: 1e-8f, y: (variance + (delta * delta))));
        var visibility = ((probability * probability) * probability);

        visibility = ((visibility < 0.2f) ? (((visibility * visibility) * visibility) / 0.04f) : visibility);
        // A minority hit beside an opening must not extend a finite block over the whole footprint.
        // A majority footprint closes continuously by three-quarter coverage, before miss taps can leak a silhouette.
        var coverage = Math.Clamp(max: 1f, min: 0f, value: ((total - SdfMeshIrradianceProbe.DepthCoverageStart) * SdfMeshIrradianceProbe.DepthCoverageScale));

        coverage = ((coverage * coverage) * (3f - (2f * coverage)));
        return (1f + (coverage * (visibility - 1f)));
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
