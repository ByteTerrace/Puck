using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>A primary march's certified approach in the visibility record's one spare word: a half-float distance
/// back along its camera ray and a downward-rounded half-float clearance at that reconstructed point.</summary>
public static class SdfIndirectApproach {
    /// <summary>Packs an approach only when its clear ball joins the surface sample's clear ball and the reconstructed
    /// point remains within half a lattice spacing. Quantization consumes clearance instead of inventing free space.</summary>
    /// <param name="surface">The accepted camera-ray sample.</param>
    /// <param name="direction">The unit camera-ray direction.</param>
    /// <param name="sample">An earlier complete-field sample on that ray, or the accepted sample itself.</param>
    /// <param name="clearance">The sample's certified ball radius.</param>
    /// <param name="surfaceClearance">The accepted sample's certified ball radius.</param>
    /// <param name="spacing">The receiver's lattice spacing.</param>
    /// <returns>The packed approach, or zero when either certificate or the finite representation is unavailable.</returns>
    public static uint Pack(Vector3 surface, Vector3 direction, Vector3 sample, float clearance, float surfaceClearance, float spacing) {
        var distance = Vector3.Dot(surface - sample, direction);
        if (!float.IsFinite(distance) || !float.IsFinite(clearance) || !float.IsFinite(surfaceClearance) ||
            !float.IsFinite(spacing) || !(spacing > 0) || !(clearance > 0) || surfaceClearance < 0 || distance < 0 ||
            distance > spacing * 0.5f || distance > 65504f || Vector3.Distance(surface, sample) > clearance + surfaceClearance) { return 0; }
        var offset = BitConverter.HalfToUInt16Bits((Half)distance);
        var reconstructed = Point(surface, direction, offset);
        var radius = clearance - Vector3.Distance(sample, reconstructed);
        if (!(radius > 0) || Vector3.Distance(surface, reconstructed) > spacing * 0.5f) { return 0; }
        var rounded = BitConverter.HalfToUInt16Bits((Half)Math.Min(radius, 65504f));
        if ((float)BitConverter.UInt16BitsToHalf(rounded) > radius) { rounded--; }
        var stored = (float)BitConverter.UInt16BitsToHalf(rounded);
        if (!(stored > 0) || Vector3.Distance(surface, reconstructed) > stored + surfaceClearance) { return 0; }
        return offset | ((uint)rounded << 16);
    }

    /// <summary>Reconstructs the point named by the low half of an approach word.</summary>
    /// <param name="surface">The same accepted camera-ray sample used when packing.</param>
    /// <param name="direction">The same unit camera-ray direction.</param>
    /// <param name="packed">The approach word.</param>
    /// <returns>The point whose certified clearance <see cref="Clearance"/> returns.</returns>
    public static Vector3 Point(Vector3 surface, Vector3 direction, uint packed) =>
        surface - direction * (float)BitConverter.UInt16BitsToHalf((ushort)packed);

    /// <summary>Reads the certified clearance; zero means the primary march published no approach.</summary>
    /// <param name="packed">The approach word.</param>
    /// <returns>A nonnegative radius, zero for the absent word.</returns>
    public static float Clearance(uint packed) => (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16));
}
