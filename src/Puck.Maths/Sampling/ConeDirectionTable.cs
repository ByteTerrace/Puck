namespace Puck.Maths;

/// <summary>
/// Bakes the whole cost of turning a two-dimensional net point into a direction inside a spherical cap into one flat
/// table of 32-bit words: the net's direction numbers, then a quantized azimuth table, then a quantized polar table.
/// A consumer indexes both tables with the high bits of a net coordinate and combines four looked-up scalars with
/// multiplies and adds — no square root, no reciprocal square root, no normalization, and no trigonometry at the point
/// of use.
/// </summary>
/// <remarks>
/// <para>
/// The table exists because those are exactly the operations a shading language does not round identically everywhere:
/// Vulkan permits three units in the last place on <c>Sqrt</c> and two and a half on division, so a sampler built from
/// them has no enumerable float surface. Every value here is computed once in <see cref="double"/> and rounded exactly
/// once into a <see cref="float"/> bit pattern, so the surface a consumer sees is a fixed list of constants.
/// </para>
/// <para>
/// The polar table is stored pre-divided as an <c>(axial, radial)</c> pair sharing one denominator, so the pair is
/// unit length in <see cref="double"/> before anything is stored, and no normalization step is needed at the point of
/// use. Writing the cap's half-angle as <c>a</c> and <c>k = tan(a)</c>, the direction at polar parameter <c>r</c> is
/// the unit vector along <c>axis + k·r·(radial direction)</c>: cosine-free area sampling of the cap's projected disc,
/// which is what an area light wants.
/// </para>
/// <para>
/// That shared denominator does not survive storage as an exact identity, and the contract states the surviving one
/// rather than the discarded ideal. Each component is rounded once and independently into a <see cref="float"/>, so
/// what a consumer reads back satisfies <c>|axial² + radial² − 1| ≤ 2⁻²³ + 2⁻⁴⁰</c> — two roundings, one per
/// component, each at a relative error of at most <c>2⁻²⁴</c>, with the second term absorbing their product and the
/// <see cref="double"/> construction's own rounding. Exact unit length is not generally representable by two
/// independently stored binary32 values at all, and buying it back — reconstructing one component or normalizing at
/// consumption — would reinstate the very square root and division this type exists to delete, so the envelope is the
/// contract and the representation stands.
/// </para>
/// <para>
/// Both tables lean on the platform's transcendental library rather than on portable constants: the azimuth table
/// calls <see cref="Math.Cos(double)"/> and <see cref="Math.Sin(double)"/>, and every polar entry is scaled by
/// <see cref="Math.Tan(double)"/> of the cap's half-angle. Only the <see cref="Math.Sqrt(double)"/> calls are correctly
/// rounded by IEEE-754; the rest are a per-machine input. That is deliberate and bounded: the table is a build-time
/// upload, the envelope above holds on any machine — it bounds the storage rounding, whatever doubles the platform's
/// library produced — and the reproducibility claim a consumer may make on it is same-machine replay, not
/// cross-machine bit identity.
/// </para>
/// </remarks>
public static class ConeDirectionTable {
    /// <summary>The number of quantized azimuths, which is the resolution the first net coordinate is read at.</summary>
    public const int AzimuthEntryCount = (1 << TableIndexBitCount);
    /// <summary>The word index at which the azimuth table begins.</summary>
    public const int AzimuthOffset = DigitalNetSampler.PlaneDirectionNumberCount;
    /// <summary>The word index at which the direction numbers begin.</summary>
    public const int DirectionNumberOffset = 0;
    /// <summary>The number of quantized polar parameters, which is the resolution the second net coordinate is read at.</summary>
    public const int RadiusEntryCount = (1 << TableIndexBitCount);
    /// <summary>The word index at which the polar table begins.</summary>
    public const int RadiusOffset = (AzimuthOffset + (2 * AzimuthEntryCount));
    /// <summary>The number of high bits of a net coordinate a table index consumes.</summary>
    public const int TableIndexBitCount = 12;
    /// <summary>The whole table's length in 32-bit words.</summary>
    public const int WordCount = (RadiusOffset + (2 * RadiusEntryCount));

    /// <summary>Builds the table for a cap of a given half-angle.</summary>
    /// <param name="capHalfAngleRadians">The cap's half-angle in radians, in <c>[0, π/2)</c>; zero degenerates to the cap's axis.</param>
    /// <param name="destination">Receives exactly <see cref="WordCount"/> words.</param>
    /// <remarks>
    /// <para>
    /// Both tables are sampled at cell centres — <c>(i + ½) / count</c> — so no entry sits on a cell boundary and no
    /// two parameter cells coincide. The azimuth table's cosine/sine pairs are pairwise distinct, and so are the polar
    /// table's <c>(axial, radial)</c> pairs at every half-angle a cap of real size is built for.
    /// </para>
    /// <para>
    /// Distinctness of the stored pairs is a property of the half-angle, not a promise made for every one of them, and
    /// this is where the type says so. As the half-angle shrinks, every polar entry approaches the cap's axis, and
    /// below the resolution of a binary32 the entries coincide there — continuously, exactly as the geometry does. A
    /// half-angle of exactly zero is the limit and is contract rather than accident: a cap of zero angle is one
    /// direction, so all <see cref="RadiusEntryCount"/> polar entries are the identical axis pair <c>(1, 0)</c>. A
    /// negative-zero half-angle is admitted on the same terms and is not canonicalized — it writes the radial
    /// components as <c>-0.0</c> rather than <c>+0.0</c>, a difference no consumer can observe, because a zero radial
    /// scales the whole azimuth contribution away either way.
    /// </para>
    /// <para>
    /// Nothing is allocated: the caller owns the buffer, and the table is rebuilt only when the half-angle changes.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not <see cref="WordCount"/> long.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capHalfAngleRadians"/> is negative, not a number, or at or above <c>π/2</c>.</exception>
    public static void Build(double capHalfAngleRadians, Span<uint> destination) {
        if (
            !(capHalfAngleRadians >= 0.0d) ||
            (capHalfAngleRadians >= (0.5d * Math.PI))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(capHalfAngleRadians),
                actualValue: capHalfAngleRadians,
                message: "A spherical cap's half-angle must lie in [0, pi/2)."
            );
        }

        if (WordCount != destination.Length) {
            throw new ArgumentException(
                message: $"The spherical-cap sample table occupies exactly {WordCount} words.",
                paramName: nameof(destination)
            );
        }

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: destination.Slice(
            length: DigitalNetSampler.PlaneDirectionNumberCount,
            start: DirectionNumberOffset
        ));

        for (var index = 0; (index < AzimuthEntryCount); ++index) {
            var angle = ((2.0d * Math.PI) * ((index + 0.5d) / AzimuthEntryCount));

            destination[(AzimuthOffset + (2 * index))] = BitConverter.SingleToUInt32Bits(value: ((float)Math.Cos(d: angle)));
            destination[((AzimuthOffset + (2 * index)) + 1)] = BitConverter.SingleToUInt32Bits(value: ((float)Math.Sin(a: angle)));
        }

        var slope = Math.Tan(a: capHalfAngleRadians);

        for (var index = 0; (index < RadiusEntryCount); ++index) {
            // The square root is the area-preserving map from a uniform parameter onto the disc's radius; it is
            // correctly rounded by IEEE-754, unlike the trigonometry above.
            var radius = Math.Sqrt(d: ((index + 0.5d) / RadiusEntryCount));
            var offset = (slope * radius);
            var denominator = Math.Sqrt(d: (1.0d + (offset * offset)));

            destination[(RadiusOffset + (2 * index))] = BitConverter.SingleToUInt32Bits(value: ((float)(1.0d / denominator)));
            destination[((RadiusOffset + (2 * index)) + 1)] = BitConverter.SingleToUInt32Bits(value: ((float)(offset / denominator)));
        }
    }
}
