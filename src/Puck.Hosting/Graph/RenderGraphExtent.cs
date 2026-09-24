namespace Puck.Hosting;

/// <summary>The extent a render-graph instance renders at, as a quantized fraction of the display on each axis.
/// <para>
/// A footprint fraction is rounded up to one of sixteen steps inside its power-of-two octave, so a fraction in
/// [2^e, 2^(e+1)) rounds up to a multiple of 2^(e-3): at most one sixteenth of the octave's top above the need, and
/// exact at every power of two, so a view occupying half of each axis renders at exactly half of each axis. Every step
/// is a power of two times an integer, so the arithmetic is exact in binary floating point. A shrink smaller than
/// <see cref="ShrinkThreshold"/> of the allocated fraction keeps the allocation, so a footprint wavering across a step
/// boundary does not reallocate its targets on every frame.
/// </para>
/// </summary>
public static class RenderGraphExtent {
    /// <summary>The fraction of its allocated extent a footprint must fall below before the allocation shrinks.</summary>
    public const double ShrinkThreshold = 0.875;
    /// <summary>The steps each power-of-two octave is divided into.</summary>
    public const int StepsPerOctave = 16;

    /// <summary>Quantizes a footprint fraction.</summary>
    /// <param name="fraction">The fraction of the display axis the footprint needs; values above one are clamped to
    /// one, the display's own extent.</param>
    /// <returns>The quantized fraction in (0, 1], or zero for a footprint of zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is negative or not finite.</exception>
    public static double Quantize(double fraction) {
        if (
            !double.IsFinite(d: fraction) ||
            (fraction < 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: fraction,
                message: "A footprint fraction must be finite and non-negative.",
                paramName: nameof(fraction)
            );
        }
        if (fraction == 0) {
            return 0;
        }
        if (fraction >= 1) {
            return 1;
        }

        var step = Math.ScaleB(
            n: ((Math.ILogB(x: fraction) + 1) - 4),
            x: 1.0
        );

        return Math.Min(
            val1: 1.0,
            val2: (Math.Ceiling(a: (fraction / step)) * step)
        );
    }
    /// <summary>Quantizes a footprint fraction against the fraction already allocated: grows to the quantized need at
    /// once, and shrinks only when the need falls below <see cref="ShrinkThreshold"/> of the allocation.</summary>
    /// <param name="fraction">The fraction of the display axis the footprint needs.</param>
    /// <param name="allocated">The quantized fraction currently allocated, or zero for none.</param>
    /// <returns>The fraction to render at.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fraction"/> is negative or not finite.</exception>
    public static double Quantize(double fraction, double allocated) {
        var needed = Quantize(fraction: fraction);

        return (
            ((needed < allocated) && (needed >= (allocated * ShrinkThreshold)))
                ? allocated
                : needed
        );
    }
    /// <summary>Converts a quantized fraction to pixels along a display axis.</summary>
    /// <param name="fraction">The quantized fraction.</param>
    /// <param name="display">The display axis, in pixels.</param>
    /// <returns>The pixels, rounded up and at least one.</returns>
    public static int Pixels(double fraction, int display) => Math.Max(
        val1: 1,
        val2: ((int)Math.Ceiling(a: (fraction * display)))
    );
}
