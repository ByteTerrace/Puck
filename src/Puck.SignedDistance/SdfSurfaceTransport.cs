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
/// coverage, so a weighted sum of samples is the pixel the weights reconstruct: the lit color through the fog's
/// transmittance, the coverage, the fog's in-scatter weight (coverage times one minus the transmittance) and the coverage
/// over the ray distance, scaled by <see cref="SdfSurfaceTransport.InverseDistanceScale"/>.</summary>
/// <param name="Lit">The lit color times coverage times the fog's transmittance.</param>
/// <param name="Coverage">The coverage.</param>
/// <param name="Fog">The fog's in-scatter weight.</param>
/// <param name="InverseDistance">The scaled coverage over the ray distance.</param>
public readonly record struct SdfSurfaceSample(Vector3 Lit, float Coverage, float Fog, float InverseDistance) {
    /// <summary>Gets the sample scaled by a filter weight.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="weight">The weight.</param>
    /// <returns>Every channel times the weight.</returns>
    public static SdfSurfaceSample operator *(float weight, SdfSurfaceSample sample) =>
        new(Coverage: (weight * sample.Coverage), Fog: (weight * sample.Fog), InverseDistance: (weight * sample.InverseDistance), Lit: (weight * sample.Lit));
    /// <summary>Gets the channelwise sum of two samples.</summary>
    /// <param name="left">One sample.</param>
    /// <param name="right">The other.</param>
    /// <returns>Their sum.</returns>
    public static SdfSurfaceSample operator +(SdfSurfaceSample left, SdfSurfaceSample right) =>
        new(Coverage: (left.Coverage + right.Coverage), Fog: (left.Fog + right.Fog), InverseDistance: (left.InverseDistance + right.InverseDistance), Lit: (left.Lit + right.Lit));
}
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
/// through the resolve and its history, to the composite. A sample's fog is exponential in its
/// own ray distance and its media begin and end on its own ray, so neither is linear in a filtered distance; what is
/// linear is each sample's transport premultiplied by its coverage. Views multiplies a hit's lit color by its fog
/// transmittance (<see cref="Transmittance"/>), and the resolve filters the in-scatter weight and the scaled inverse
/// distance (<see cref="Sample"/>) with exactly the weights it filters color and coverage with, so a resolved pixel's
/// fog is the coverage-weighted fog of the samples it was filtered from, exactly, at any footprint
/// (<see cref="Filter"/>). The composite (<see cref="Composite"/>) splits the pixel into its surface share, of the
/// resolved coverage, and its sky share, the rest, and clips the media of each at its own end: the sky share at the far
/// distance and the surface share at <see cref="SurfaceDistance"/>, the harmonic mean of its samples' distances, which
/// is each sample's own where the footprint holds one surface, so a medium behind an edge never paints over it. Across a
/// depth step inside one footprint the harmonic mean lies at or before the arithmetic one, toward the nearer surface;
/// a medium lying between the two surfaces is the one case the clip does not reproduce sample by sample. The kernels are
/// <c>shade/sdf-transport.hlsli</c>, <c>passes/sdf-resolve.comp.hlsl</c>, <c>passes/sdf-composite.comp.hlsl</c> and
/// <c>shade/shade-volumes.hlsli</c>.
/// </summary>
public static class SdfSurfaceTransport {
    /// <summary>The scale the coverage over the ray distance is held at, so a half float keeps it normal over every
    /// distance a march reaches. KEEP IN SYNC with <c>SdfTransportInverseDistanceScale</c> in
    /// <c>shade/sdf-transport.hlsli</c>.</summary>
    public const float InverseDistanceScale = 1024f;

    /// <summary>Returns the fraction of a surface's light the fog lets through over a ray distance.</summary>
    /// <param name="distance">The ray distance in world units.</param>
    /// <param name="fogDensity">The fog's density per world unit; zero or less is no fog.</param>
    /// <returns><c>exp(-density · distance)</c>, or one without fog.</returns>
    public static float Transmittance(float distance, float fogDensity) =>
        ((fogDensity > 0f) ? MathF.Exp(x: (-fogDensity * distance)) : 1f);
    /// <summary>Returns what views and the resolve carry for one render sample.</summary>
    /// <param name="sample">The sample.</param>
    /// <param name="fogDensity">The fog's density per world unit.</param>
    /// <returns>Its premultiplied lit color, coverage and transport; a sample with no surface carries none.</returns>
    public static SdfSurfaceSample Sample(SdfRenderSample sample, float fogDensity) {
        if ((sample.Coverage <= 0f) || (sample.Distance <= 0f)) {
            return default;
        }

        var transmittance = Transmittance(distance: sample.Distance, fogDensity: fogDensity);

        return new SdfSurfaceSample(
            Coverage: sample.Coverage,
            Fog: (sample.Coverage * (1f - transmittance)),
            InverseDistance: ((sample.Coverage * InverseDistanceScale) / sample.Distance),
            Lit: ((sample.Coverage * transmittance) * sample.Color)
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
    /// <summary>Returns the ray distance a resolved pixel's surface share clips its media at: the harmonic mean of its
    /// samples' distances, each weighted as its color is.</summary>
    /// <param name="pixel">The resolved pixel.</param>
    /// <returns>The distance, or zero for a pixel with no surface.</returns>
    public static float SurfaceDistance(SdfSurfaceSample pixel) =>
        (((pixel.Coverage > 0f) && (pixel.InverseDistance > 0f)) ? ((pixel.Coverage * InverseDistanceScale) / pixel.InverseDistance) : 0f);
    /// <summary>Returns the composite's color for a resolved pixel: its surface share, the lit color with the fog's
    /// in-scatter of the gradient, and its sky share, each under the media clipped at its own end and composited farthest
    /// entry first, from the camera's eye.</summary>
    /// <param name="pixel">The resolved pixel.</param>
    /// <param name="gradient">The sky's gradient in the pixel's direction, the fog's in-scatter color.</param>
    /// <param name="sky">The sky's composed runs in the pixel's direction.</param>
    /// <param name="farDistance">The ray distance every march ends at, where the sky share's media end.</param>
    /// <param name="media">The bounded media on the pixel's ray.</param>
    /// <returns>The pixel's color.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is <see langword="null"/>.</exception>
    public static Vector3 Composite(SdfSurfaceSample pixel, Vector3 gradient, Vector3 sky, float farDistance, IReadOnlyList<SdfMediumSpan> media) {
        ArgumentNullException.ThrowIfNull(argument: media);

        var coverage = pixel.Coverage;
        var surface = (pixel.Lit + (gradient * Math.Clamp(value: pixel.Fog, max: coverage, min: 0f)));
        var skyShare = ((1f - coverage) * sky);
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
    /// own fog and its own media, and the results weighted: the pixel the resolve's weights describe, which
    /// <see cref="Composite"/> over <see cref="Filter"/> reproduces.</summary>
    /// <param name="samples">The render samples.</param>
    /// <param name="weights">Their weights, one each, summing to one.</param>
    /// <param name="fogDensity">The fog's density per world unit.</param>
    /// <param name="gradient">The sky's gradient in the pixel's direction.</param>
    /// <param name="sky">The sky's composed runs in the pixel's direction.</param>
    /// <param name="farDistance">The ray distance every march ends at.</param>
    /// <param name="media">The bounded media on the pixel's ray.</param>
    /// <returns>The weighted color.</returns>
    /// <exception cref="ArgumentException"><paramref name="weights"/> does not hold one weight a sample.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="media"/> is <see langword="null"/>.</exception>
    public static Vector3 Expected(ReadOnlySpan<SdfRenderSample> samples, ReadOnlySpan<float> weights, float fogDensity, Vector3 gradient, Vector3 sky, float farDistance, IReadOnlyList<SdfMediumSpan> media) {
        if (samples.Length != weights.Length) {
            throw new ArgumentException(message: "Every sample needs one weight.", paramName: nameof(weights));
        }

        var sum = Vector3.Zero;

        for (var index = 0; (index < samples.Length); index++) {
            var alone = Sample(fogDensity: fogDensity, sample: samples[index]);

            sum += (weights[index] * Composite(farDistance: farDistance, gradient: gradient, media: media, pixel: alone, sky: sky));
        }

        return sum;
    }
}
