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
/// The polar table is stored pre-divided as an <c>(axial, radial)</c> pair sharing one denominator, so
/// <c>axial² + radial² = 1</c> holds by construction rather than by a normalization step. Writing the cap's half-angle
/// as <c>a</c> and <c>k = tan(a)</c>, the direction at polar parameter <c>r</c> is the unit vector along
/// <c>axis + k·r·(radial direction)</c>: cosine-free area sampling of the cap's projected disc, which is what an area
/// light wants.
/// </para>
/// <para>
/// Both tables lean on the platform's transcendental library rather than on portable constants: the azimuth table
/// calls <see cref="Math.Cos(double)"/> and <see cref="Math.Sin(double)"/>, and every polar entry is scaled by
/// <see cref="Math.Tan(double)"/> of the cap's half-angle. Only the <see cref="Math.Sqrt(double)"/> calls are correctly
/// rounded by IEEE-754; the rest are a per-machine input. That is deliberate and bounded: the table is a build-time
/// upload, the invariant above holds on any machine, and the reproducibility claim a consumer may make on it is
/// same-machine replay, not cross-machine bit identity.
/// </para>
/// </remarks>
public static class SphericalCapSampleTable {
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
    /// Both tables are sampled at cell centres — <c>(i + ½) / count</c> — so no entry sits on a cell boundary and the
    /// quantization never produces a duplicated or degenerate direction. Nothing is allocated: the caller owns the
    /// buffer, and the table is rebuilt only when the half-angle changes.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not <see cref="WordCount"/> long.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capHalfAngleRadians"/> is negative, not a number, or at or above <c>π/2</c>.</exception>
    public static void Build(double capHalfAngleRadians, Span<uint> destination) {
        if (!(capHalfAngleRadians >= 0.0d) || (capHalfAngleRadians >= (0.5d * Math.PI))) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(capHalfAngleRadians),
                actualValue: capHalfAngleRadians,
                message: "A spherical cap's half-angle must lie in [0, pi/2)."
            );
        }

        if (WordCount != destination.Length) {
            throw new ArgumentException(message: $"The spherical-cap sample table occupies exactly {WordCount} words.", paramName: nameof(destination));
        }

        DigitalNetSampler.BuildPlaneDirectionNumbers(destination: destination.Slice(start: DirectionNumberOffset, length: DigitalNetSampler.PlaneDirectionNumberCount));

        for (var index = 0; (index < AzimuthEntryCount); ++index) {
            var angle = ((2.0d * Math.PI) * ((index + 0.5d) / AzimuthEntryCount));

            destination[AzimuthOffset + (2 * index)] = BitConverter.SingleToUInt32Bits(value: ((float)Math.Cos(d: angle)));
            destination[AzimuthOffset + (2 * index) + 1] = BitConverter.SingleToUInt32Bits(value: ((float)Math.Sin(a: angle)));
        }

        var slope = Math.Tan(a: capHalfAngleRadians);

        for (var index = 0; (index < RadiusEntryCount); ++index) {
            // The square root is the area-preserving map from a uniform parameter onto the disc's radius; it is
            // correctly rounded by IEEE-754, unlike the trigonometry above.
            var radius = Math.Sqrt(d: ((index + 0.5d) / RadiusEntryCount));
            var offset = (slope * radius);
            var denominator = Math.Sqrt(d: (1.0d + (offset * offset)));

            destination[RadiusOffset + (2 * index)] = BitConverter.SingleToUInt32Bits(value: ((float)(1.0d / denominator)));
            destination[RadiusOffset + (2 * index) + 1] = BitConverter.SingleToUInt32Bits(value: ((float)(offset / denominator)));
        }
    }
}
