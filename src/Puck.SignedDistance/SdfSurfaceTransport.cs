using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One render sample a view shades: its lit color before coverage and fog, its coverage of the pixel's ray (one
/// for a solid hit, the silhouette's share on an edge, zero for a miss), and the ray distance of the surface it
/// covers.</summary>
/// <param name="Color">The lit color, not premultiplied.</param>
/// <param name="Coverage">The coverage, in [0, 1].</param>
/// <param name="Distance">The surface's ray distance in world units, positive where the coverage is.</param>
public readonly record struct SdfRenderSample(Vector3 Color, float Coverage, float Distance);
/// <summary>What views and the resolve carry for a render sample or a resolved pixel, every channel premultiplied by
/// coverage, so a weighted sum of samples is the pixel the weights reconstruct: the lit color through the atmosphere's
/// transmittance, the coverage, each atmosphere kind's in-scatter weight (coverage times the kind's
/// <see cref="SdfAirSpan"/> weight) and the coverage over the ray distance, scaled by
/// <see cref="SdfSurfaceTransport.InverseDistanceScale"/>.</summary>
/// <param name="Lit">The lit color times coverage times the atmosphere's transmittance.</param>
/// <param name="Coverage">The coverage.</param>
/// <param name="Fog">The fog's in-scatter weight.</param>
/// <param name="InverseDistance">The scaled coverage over the ray distance.</param>
/// <param name="Haze">The haze's in-scatter weight.</param>
/// <param name="Medium">The medium's in-scatter weight.</param>
public readonly record struct SdfSurfaceSample(Vector3 Lit, float Coverage, float Fog, float InverseDistance, float Haze, float Medium) {
    /// <summary>Gets the sample scaled by a filter weight.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="weight">The weight.</param>
    /// <returns>Every channel times the weight.</returns>
    public static SdfSurfaceSample operator *(float weight, SdfSurfaceSample sample) =>
        new(Coverage: (weight * sample.Coverage), Fog: (weight * sample.Fog), Haze: (weight * sample.Haze), InverseDistance: (weight * sample.InverseDistance), Lit: (weight * sample.Lit), Medium: (weight * sample.Medium));
    /// <summary>Gets the channelwise sum of two samples.</summary>
    /// <param name="left">One sample.</param>
    /// <param name="right">The other.</param>
    /// <returns>Their sum.</returns>
    public static SdfSurfaceSample operator +(SdfSurfaceSample left, SdfSurfaceSample right) =>
        new(Coverage: (left.Coverage + right.Coverage), Fog: (left.Fog + right.Fog), Haze: (left.Haze + right.Haze), InverseDistance: (left.InverseDistance + right.InverseDistance), Lit: (left.Lit + right.Lit), Medium: (left.Medium + right.Medium));
}
/// <summary>A pixel's transport as the resolve writes it: two words (<c>shade/sdf-transport.hlsli</c>). With
/// <see cref="SdfSurfaceTransport.SampleBit"/> set in <see cref="Low"/>, one render sample copied whole, its ray
/// distance's float bits below the bit and <see cref="High"/> zero; with it clear, four half floats: the fog's weight
/// and the scaled inverse distance (its sign bit cleared) in <see cref="Low"/>, the haze's and the medium's weights in
/// <see cref="High"/>.</summary>
/// <param name="Low">The first word.</param>
/// <param name="High">The second word.</param>
public readonly record struct SdfTransportWord(uint Low, uint High);
/// <summary>A homogeneous bounded medium on a pixel's ray: the span of ray distances it occupies, its extinction per
/// world unit, and the radiance it emits per world unit.</summary>
/// <param name="Begin">The ray distance it begins at.</param>
/// <param name="End">The ray distance it ends at.</param>
/// <param name="Extinction">Its extinction per world unit, zero or more.</param>
/// <param name="Emission">Its emitted radiance per world unit.</param>
public readonly record struct SdfMediumSpan(float Begin, float End, float Extinction, Vector3 Emission) {
    /// <summary>Returns what the medium does to light crossing it between two ray distances: the radiance it adds and
    /// the fraction of the light behind it that it lets through, so the light it shows over a color c is
    /// radiance + transmission · c.</summary>
    /// <param name="near">The nearest ray distance the span is integrated from.</param>
    /// <param name="clip">The ray distance the light comes from, where the span is clipped.</param>
    /// <returns>The radiance and the transmission.</returns>
    public (Vector3 Radiance, float Transmission) Over(float near, float clip) {
        var length = MathF.Max(x: 0f, y: (MathF.Min(x: End, y: clip) - MathF.Max(x: Begin, y: near)));
        var transmission = MathF.Exp(x: (-Extinction * length));
        var integral = ((Extinction > 0f) ? ((1f - transmission) / Extinction) : length);

        return ((Emission * integral), transmission);
    }
}
/// <summary>
/// The CPU reference for how a view carries the air between its camera and each render sample's surface from views,
/// through the resolve and its history, to the composite. A sample's atmosphere is exponential in its own ray distance
/// and its media begin and end on its own ray, so neither is linear in a filtered distance; what is linear is each
/// sample's transport premultiplied by its coverage. Views multiplies a hit's lit color by the atmosphere's
/// transmittance (<see cref="SdfAir.Along"/>), and the resolve filters each kind's in-scatter weight and the scaled
/// inverse distance (<see cref="Sample"/>) with exactly the weights it filters color and coverage with, so a resolved
/// pixel's fog, haze and medium are the coverage-weighted ones of the samples it was filtered from, exactly, at any
/// footprint (<see cref="Filter"/>): each kind's in-scatter colour is a function of the pixel's direction alone, so the
/// composite applies it to the filtered weight. The composite (<see cref="Composite"/>) splits the pixel into its surface
/// share, of the resolved coverage, and its sky share, the rest, and clips the bounded media of each at its own end: the
/// sky share at the far distance and the surface share at <see cref="SurfaceDistance"/>, the harmonic mean of its
/// samples' distances, which is each sample's own where the footprint holds one surface, so a medium behind an edge
/// never paints over it. The sky share passes through the haze and the medium to the far distance, never the fog, whose
/// colour at infinity the sky is. Across a depth step inside one footprint the harmonic mean lies at or before the
/// arithmetic one, toward the nearer surface; a bounded medium lying between the two surfaces is the one case the clip
/// does not reproduce sample by sample. The kernels are <c>shade/sdf-atmosphere.hlsli</c>,
/// <c>shade/sdf-transport.hlsli</c>, <c>passes/sdf-resolve.comp.hlsl</c>, <c>passes/sdf-composite.comp.hlsl</c> and
/// <c>shade/shade-volumes.hlsli</c>.
/// </summary>
public static class SdfSurfaceTransport {
    /// <summary>The scale the coverage over the ray distance is held at, so a half float keeps it normal over every
    /// distance a march reaches. KEEP IN SYNC with <c>SdfTransportInverseDistanceScale</c> in
    /// <c>shade/sdf-transport.hlsli</c>.</summary>
    public const float InverseDistanceScale = 1024f;
    /// <summary>The top bit of a transport word's low word, set on a word that carries one render sample's ray distance
    /// whole and clear on a reconstruction's half floats. KEEP IN SYNC with <c>SdfTransportSampleBit</c> in
    /// <c>shade/sdf-transport.hlsli</c>.</summary>
    public const uint SampleBit = 0x8000_0000u;

    /// <summary>Returns what views and the resolve carry for one render sample.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="air">The atmosphere along the sample's ray.</param>
    /// <returns>Its premultiplied lit color, coverage and transport; a sample with no surface carries none.</returns>
    public static SdfSurfaceSample Sample(SdfRenderSample sample, in SdfAirRay air) {
        if ((sample.Coverage <= 0f) || (sample.Distance <= 0f)) {
            return default;
        }

        var span = air.Along(distance: sample.Distance, fog: true);

        return new SdfSurfaceSample(
            Coverage: sample.Coverage,
            Fog: (sample.Coverage * span.Fog),
            Haze: (sample.Coverage * span.Haze),
            InverseDistance: ((sample.Coverage * InverseDistanceScale) / sample.Distance),
            Lit: ((sample.Coverage * span.Transmittance) * sample.Color),
            Medium: (sample.Coverage * span.Medium)
        );
    }
    /// <summary>Returns the weighted sum of samples, as the resolve filters every channel with one set of weights.</summary>
    /// <param name="samples">The samples.</param>
    /// <param name="weights">Their weights, one each, summing to one.</param>
    /// <returns>The filtered pixel.</returns>
    /// <exception cref="ArgumentException"><paramref name="weights"/> does not hold one weight a sample.</exception>
    public static SdfSurfaceSample Filter(ReadOnlySpan<SdfSurfaceSample> samples, ReadOnlySpan<float> weights) {
        if (samples.Length != weights.Length) {
            throw new ArgumentException(message: "Every sample needs one weight.", paramName: nameof(weights));
        }

        var sum = default(SdfSurfaceSample);

        for (var index = 0; (index < samples.Length); index++) {
            sum += (weights[index] * samples[index]);
        }

        return sum;
    }
    /// <summary>Returns the weights the resolve's spatial path reconstructs one row of output pixels with at sharpness
    /// zero: the bilinear pair at the pixel's center on the render row, clamped to the row, as
    /// <c>puckReconstructionFootprintAt</c> places it.</summary>
    /// <param name="renderWidth">The render row's samples.</param>
    /// <param name="outputWidth">The output row's pixels.</param>
    /// <param name="pixel">The output pixel.</param>
    /// <returns>Each render sample's weight.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="renderWidth"/> or <paramref name="outputWidth"/> is
    /// less than one, or <paramref name="pixel"/> lies outside the output row.</exception>
    public static float[] BilinearWeights(int renderWidth, int outputWidth, int pixel) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: renderWidth, other: 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: outputWidth, other: 1);
        ArgumentOutOfRangeException.ThrowIfNegative(value: pixel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: pixel, other: outputWidth);

        var position = Math.Clamp(max: (renderWidth - 1f), min: 0f, value: ((((pixel + 0.5f) * renderWidth) / outputWidth) - 0.5f));
        var origin = ((int)position);
        var fraction = (position - origin);
        var weights = new float[renderWidth];

        weights[origin] += (1f - fraction);
        weights[Math.Min(val1: (origin + 1), val2: (renderWidth - 1))] += fraction;

        return weights;
    }
    /// <summary>Returns the words a reconstruction's transport is held in: the fog's weight and the scaled inverse
    /// distance (its sign bit cleared, so the low word never reads as a sample word) low, the haze's and the medium's
    /// weights high, each a half float.</summary>
    /// <param name="pixel">The reconstructed pixel.</param>
    /// <returns>The packed words.</returns>
    public static SdfTransportWord PackWord(SdfSurfaceSample pixel) => new(
        High: Half16(value: pixel.Haze) | (Half16(value: pixel.Medium) << 16),
        Low: Half16(value: pixel.Fog) | ((Half16(value: MathF.Min(x: pixel.InverseDistance, y: 65504f)) & 0x7FFFu) << 16)
    );
    /// <summary>Returns the words one render sample copied whole is held in: its ray distance's float bits under
    /// <see cref="SampleBit"/>, so the composite reads the distance back exactly.</summary>
    /// <param name="distance">The sample's ray distance, zero for a sample with no surface.</param>
    /// <returns>The sample words.</returns>
    public static SdfTransportWord SampleWord(float distance) =>
        new(High: 0u, Low: (BitConverter.SingleToUInt32Bits(value: distance) & ~SampleBit) | SampleBit);
    /// <summary>Returns the transport words the resolve's spatial path writes for an output pixel, which the first frame
    /// of a temporal epoch writes too: where the pixel is one render sample copied whole (the output at the render
    /// grid's extent with no jitter), that sample's words, and otherwise its reconstruction's.</summary>
    /// <param name="samples">The render samples the pixel is reconstructed from.</param>
    /// <param name="weights">Their weights, one each, summing to one.</param>
    /// <param name="exact">Whether the pixel is its one render sample, the only one in <paramref name="samples"/>.</param>
    /// <param name="air">The atmosphere along the output pixel's ray, which every sample of its footprint is carried
    /// through.</param>
    /// <returns>The words.</returns>
    /// <exception cref="ArgumentException"><paramref name="exact"/> is set over other than one sample.</exception>
    public static SdfTransportWord SpatialWord(ReadOnlySpan<SdfRenderSample> samples, ReadOnlySpan<float> weights, bool exact, in SdfAirRay air) {
        if (exact) {
            if (samples.Length != 1) {
                throw new ArgumentException(message: "A pixel copied whole is one render sample.", paramName: nameof(samples));
            }

            return SampleWord(distance: samples[0].Distance);
        }

        var transported = new SdfSurfaceSample[samples.Length];

        for (var index = 0; (index < samples.Length); index++) {
            transported[index] = Sample(air: in air, sample: samples[index]);
        }

        return PackWord(pixel: Filter(samples: transported, weights: weights));
    }
    /// <summary>Returns each atmosphere kind's in-scatter weight and the media clip distance the composite takes for a
    /// pixel of a given coverage. A pixel that is one render sample (a native view's, or a resolved sample word's)
    /// derives them from the sample's ray distance by one computation, so a sample copied whole reads exactly as its
    /// native view reads it; packed words are unpacked.</summary>
    /// <param name="word">The pixel's transport words, or <see langword="null"/> for a native view's pixel.</param>
    /// <param name="nativeDistance">A native pixel's ray distance from its record, zero for no surface.</param>
    /// <param name="coverage">The pixel's coverage, from its lit image.</param>
    /// <param name="air">The atmosphere along the pixel's ray.</param>
    /// <returns>The pixel's transport, each weight at most the coverage and its lit color zero, and the clip distance,
    /// zero for no surface.</returns>
    public static (SdfSurfaceSample Surface, float Distance) ReadSurface(SdfTransportWord? word, float nativeDistance, float coverage, in SdfAirRay air) {
        var sample = ((word is not { } packed) || ((packed.Low & SampleBit) != 0u));
        var distance = ((word is { } carried) ? BitConverter.UInt32BitsToSingle(value: carried.Low & ~SampleBit) : nativeDistance);
        SdfSurfaceSample surface;

        if (sample) {
            surface = Sample(air: in air, sample: new SdfRenderSample(Color: Vector3.Zero, Coverage: coverage, Distance: distance));
        } else {
            var words = word!.Value;

            surface = new SdfSurfaceSample(
                Coverage: coverage,
                Fog: FromHalf16(bits: words.Low),
                Haze: FromHalf16(bits: words.High),
                InverseDistance: FromHalf16(bits: (words.Low >> 16)),
                Lit: Vector3.Zero,
                Medium: FromHalf16(bits: (words.High >> 16))
            );
        }

        var clipped = surface with {
            Fog = Math.Clamp(value: surface.Fog, max: coverage, min: 0f),
            Haze = Math.Clamp(value: surface.Haze, max: coverage, min: 0f),
            Medium = Math.Clamp(value: surface.Medium, max: coverage, min: 0f),
        };

        return (clipped, (sample ? ((surface.InverseDistance > 0f) ? distance : 0f) : SurfaceDistance(pixel: surface)));
    }
    /// <summary>Returns the ray distance a resolved pixel's surface share clips its media at: the harmonic mean of its
    /// samples' distances, each weighted as its color is.</summary>
    /// <param name="pixel">The resolved pixel.</param>
    /// <returns>The distance, or zero for a pixel with no surface.</returns>
    public static float SurfaceDistance(SdfSurfaceSample pixel) =>
        (((pixel.Coverage > 0f) && (pixel.InverseDistance > 0f)) ? ((pixel.Coverage * InverseDistanceScale) / pixel.InverseDistance) : 0f);
    /// <summary>Returns the composite's color for a resolved pixel: its surface share, the lit color with each atmosphere
    /// kind's in-scatter by its weight, and its sky share, the sky through the haze and the medium to the far distance,
    /// each under the bounded media clipped at its own end and composited farthest entry first, from the camera's
    /// eye.</summary>
    /// <param name="pixel">The resolved pixel.</param>
    /// <param name="colors">Each atmosphere kind's in-scatter colour in the pixel's direction.</param>
    /// <param name="skyAir">The atmosphere along the pixel's ray to the far distance, without the fog
    /// (<see cref="SdfAirRay.Along"/>).</param>
    /// <param name="sky">The sky's composed runs in the pixel's direction.</param>
    /// <param name="farDistance">The ray distance every march ends at, where the sky share's media end.</param>
    /// <param name="media">The bounded media on the pixel's ray.</param>
    /// <returns>The pixel's color.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is <see langword="null"/>.</exception>
    public static Vector3 Composite(SdfSurfaceSample pixel, SdfAirColors colors, SdfAirSpan skyAir, Vector3 sky, float farDistance, IReadOnlyList<SdfMediumSpan> media) {
        ArgumentNullException.ThrowIfNull(argument: media);

        var coverage = pixel.Coverage;
        var surface = (
            ((pixel.Lit +
            (colors.Fog * Math.Clamp(value: pixel.Fog, max: coverage, min: 0f))) +
            (colors.Haze * Math.Clamp(value: pixel.Haze, max: coverage, min: 0f))) +
            (colors.Medium * Math.Clamp(value: pixel.Medium, max: coverage, min: 0f))
        );
        var skyShare = ((1f - coverage) * (((skyAir.Transmittance * sky) + (skyAir.Haze * colors.Haze)) + (skyAir.Medium * colors.Medium)));
        var distance = SurfaceDistance(pixel: pixel);
        var surfaceEnd = ((distance > 0f) ? distance : farDistance);

        foreach (var medium in media.OrderByDescending(keySelector: static medium => medium.Begin)) {
            if (coverage < 1f) {
                var (radiance, transmission) = medium.Over(clip: farDistance, near: 0f);

                skyShare = (((1f - coverage) * radiance) + (transmission * skyShare));
            }
            if (coverage > 0f) {
                var (radiance, transmission) = medium.Over(clip: surfaceEnd, near: 0f);

                surface = ((coverage * radiance) + (transmission * surface));
            }
        }

        return (surface + skyShare);
    }
    /// <summary>Returns what a pixel would show if each of its render samples were composited along its own ray, with its
    /// own atmosphere and its own media, and the results weighted: the pixel the resolve's weights describe, which
    /// <see cref="Composite"/> over <see cref="Filter"/> reproduces.</summary>
    /// <param name="samples">The render samples.</param>
    /// <param name="weights">Their weights, one each, summing to one.</param>
    /// <param name="air">The atmosphere along the pixel's ray.</param>
    /// <param name="colors">Each atmosphere kind's in-scatter colour in the pixel's direction.</param>
    /// <param name="sky">The sky's composed runs in the pixel's direction.</param>
    /// <param name="farDistance">The ray distance every march ends at.</param>
    /// <param name="media">The bounded media on the pixel's ray.</param>
    /// <returns>The weighted color.</returns>
    /// <exception cref="ArgumentException"><paramref name="weights"/> does not hold one weight a sample.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is <see langword="null"/>.</exception>
    public static Vector3 Expected(ReadOnlySpan<SdfRenderSample> samples, ReadOnlySpan<float> weights, in SdfAirRay air, SdfAirColors colors, Vector3 sky, float farDistance, IReadOnlyList<SdfMediumSpan> media) {
        if (samples.Length != weights.Length) {
            throw new ArgumentException(message: "Every sample needs one weight.", paramName: nameof(weights));
        }

        var sum = Vector3.Zero;
        var skyAir = air.Along(distance: farDistance, fog: false);

        for (var index = 0; (index < samples.Length); index++) {
            var alone = Sample(air: in air, sample: samples[index]);

            sum += (weights[index] * Composite(colors: colors, farDistance: farDistance, media: media, pixel: alone, sky: sky, skyAir: skyAir));
        }

        return sum;
    }

    private static uint Half16(float value) =>
        BitConverter.HalfToUInt16Bits(value: ((Half)value));
    private static float FromHalf16(uint bits) =>
        ((float)BitConverter.UInt16BitsToHalf(value: ((ushort)(bits & 0xFFFFu))));
}
