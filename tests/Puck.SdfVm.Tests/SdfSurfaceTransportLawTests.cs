using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: what the composite fogs a pixel with and clips its media at is reconstructed from the render
/// samples with the weights its color and coverage are (<see cref="SdfSurfaceTransport"/>), so a resolved pixel shows
/// what its samples, each composited along its own ray, show weighted, within a float tolerance of 1e-5 a channel: the
/// fog of a partly covered pixel is its covered share's, and a medium behind an edge never paints over the surface. The
/// shipped kernels hold the same structure: views fogs each hit, the resolve reads every transport tap beside its color
/// tap over one footprint and accumulates it with the color's weights, and the composite clips each share at its own
/// end.
/// </summary>
public sealed partial class SdfSurfaceTransportLawTests {
    private const float Tolerance = 1e-5f;
    private const float FogDensity = SdfSky.DefaultFogDensity;
    private const float FarDistance = 1000f;

    private static readonly Vector3 Gradient = new(x: 0.6f, y: 0.7f, z: 0.9f);
    private static readonly Vector3 Sky = new(x: 0.2f, y: 0.4f, z: 0.8f);
    // Two render samples, white geometry at a ray distance of 100 and a miss, upscaled to a row of four pixels.
    private static readonly SdfRenderSample[] Row = [
        new(Color: Vector3.One, Coverage: 1f, Distance: 100f),
        new(Color: Vector3.Zero, Coverage: 0f, Distance: 0f),
    ];

    private const int OutputWidth = 4;

    private static string KernelRoot => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);

    [Fact]
    public void APartlyCoveredPixelFogsAsItsCoveredShare() {
        // The third pixel's center lies three quarters of the way from the geometry sample to the miss.
        var weights = SdfSurfaceTransport.BilinearWeights(outputWidth: OutputWidth, pixel: 2, renderWidth: Row.Length);

        Assert.Equal(actual: weights, expected: [0.25f, 0.75f]);
        var pixel = Resolve(pixel: 2);
        var transmittance = MathF.Exp(x: (-FogDensity * 100f));
        // A quarter of the pixel is the geometry seen through 100 units of fog; the rest is the sky.
        var expected = ((0.25f * ((transmittance * Vector3.One) + ((1f - transmittance) * Gradient))) + (0.75f * Sky));

        Assert.Equal(expected: 0.25f, actual: pixel.Coverage, tolerance: Tolerance);
        Assert.Equal(expected: (0.25f * (1f - transmittance)), actual: pixel.Fog, tolerance: Tolerance);
        Assert.Equal(expected: 100f, actual: SdfSurfaceTransport.SurfaceDistance(pixel: pixel), tolerance: 1e-3f);
        Near(actual: SdfSurfaceTransport.Composite(farDistance: FarDistance, gradient: Gradient, media: [], pixel: pixel, sky: Sky), expected: expected);
        for (var output = 0; (output < OutputWidth); output++) {
            Near(
                actual: SdfSurfaceTransport.Composite(farDistance: FarDistance, gradient: Gradient, media: [], pixel: Resolve(pixel: output), sky: Sky),
                expected: Expected(media: [], pixel: output)
            );
        }
    }
    [Fact]
    public void AMediumBehindAnEdgeNeverPaintsOverItsSurface() {
        SdfMediumSpan behind = new(Begin: 150f, End: 200f, Emission: new Vector3(x: 0.02f, y: 0.01f, z: 0f), Extinction: 0.03f);
        SdfMediumSpan before = new(Begin: 20f, End: 60f, Emission: new Vector3(x: 0f, y: 0.01f, z: 0.02f), Extinction: 0.01f);

        for (var output = 0; (output < OutputWidth); output++) {
            var pixel = Resolve(pixel: output);

            Near(actual: Composite(media: [behind], pixel: pixel), expected: Expected(media: [behind], pixel: output));
            Near(actual: Composite(media: [behind, before], pixel: pixel), expected: Expected(media: [behind, before], pixel: output));
        }

        // At the quarter-covered pixel the medium behind the surface reaches only the sky share: the surface share is the
        // fogged geometry, untouched.
        var edge = Resolve(pixel: 2);

        var (radiance, transmission) = behind.Over(clip: FarDistance, near: 0f);
        var change = (Composite(media: [behind], pixel: edge) - Composite(media: [], pixel: edge));

        Near(actual: change, expected: (0.75f * (radiance + ((transmission - 1f) * Sky))));
    }
    [Fact]
    public void FogIsTheWeightedFogOfAnyFootprintsSamples() {
        var random = new Random(Seed: 1805);

        for (var trial = 0; (trial < 500); trial++) {
            var count = random.Next(maxValue: 17, minValue: 1);
            var samples = new SdfRenderSample[count];
            var weights = new float[count];
            var total = 0f;

            for (var index = 0; (index < count); index++) {
                var covered = (random.Next(maxValue: 3) != 0);

                samples[index] = (covered
                    ? new SdfRenderSample(
                        Color: new Vector3(x: random.NextSingle(), y: random.NextSingle(), z: random.NextSingle()),
                        Coverage: MathF.Max(x: random.NextSingle(), y: 0.001f),
                        Distance: (0.5f + (random.NextSingle() * 400f)))
                    : default);
                weights[index] = random.NextSingle();
                total += weights[index];
            }
            for (var index = 0; (index < count); index++) {
                weights[index] /= MathF.Max(x: total, y: 1e-6f);
            }

            var pixel = SdfSurfaceTransport.Filter(samples: [.. samples.Select(selector: static sample => SdfSurfaceTransport.Sample(fogDensity: FogDensity, sample: sample))], weights: weights);

            Near(
                actual: SdfSurfaceTransport.Composite(farDistance: FarDistance, gradient: Gradient, media: [], pixel: pixel, sky: Sky),
                expected: SdfSurfaceTransport.Expected(farDistance: FarDistance, fogDensity: FogDensity, gradient: Gradient, media: [], samples: samples, sky: Sky, weights: weights),
                tolerance: 1e-4f
            );
        }
    }
    [Fact]
    public void TheSurfaceShareClipsAtItsOwnSurfaceAndBeforeTheMeanAcrossADepthStep() {
        var one = SdfSurfaceTransport.Filter(
            samples: [Sample(coverage: 0.4f, distance: 37f), Sample(coverage: 0.9f, distance: 37f), default],
            weights: [0.3f, 0.5f, 0.2f]
        );

        Assert.Equal(expected: 37f, actual: SdfSurfaceTransport.SurfaceDistance(pixel: one), tolerance: 1e-3f);
        // Across a step from 50 to 200 the clip lies toward the nearer surface, before the coverage-weighted mean of 125.
        var step = SdfSurfaceTransport.Filter(samples: [Sample(coverage: 1f, distance: 50f), Sample(coverage: 1f, distance: 200f)], weights: [0.5f, 0.5f]);
        var distance = SdfSurfaceTransport.SurfaceDistance(pixel: step);

        Assert.InRange(actual: distance, high: 125f, low: 50f);
        Assert.Equal(actual: distance, expected: 80f, tolerance: 1e-3f);
        // No surface clips nothing.
        Assert.Equal(expected: 0f, actual: SdfSurfaceTransport.SurfaceDistance(pixel: default));
    }
    [Fact]
    public void TheFirstFrameOfAnEpochReadsEachSampleAsItsNativeViewDoes() {
        var random = new Random(Seed: 1865);
        float[] distances = [0f, SdfWorldPackage.MinimumNear, 0.1f, 1f, 37.25f, 100f, 1000f, 8192f];
        var packedDiffers = false;

        for (var trial = 0; (trial < 4000); trial++) {
            var coverage = ((trial % 4) switch { 0 => 0f, 1 => 1f, _ => random.NextSingle() });
            var distance = (((trial % 3) == 0) ? distances[random.Next(maxValue: distances.Length)] : (random.NextSingle() * 8192f));
            var sample = new SdfRenderSample(Color: Vector3.One, Coverage: coverage, Distance: distance);
            var word = SdfSurfaceTransport.SpatialWord(exact: true, fogDensity: FogDensity, samples: [sample], weights: [1f]);
            var native = SdfSurfaceTransport.ReadSurface(coverage: coverage, fogDensity: FogDensity, nativeDistance: distance, word: null);
            var copied = SdfSurfaceTransport.ReadSurface(coverage: coverage, fogDensity: FogDensity, nativeDistance: 0f, word: word);

            // The copy carries its sample's distance whole, and the composite derives the rest at the one site a native
            // pixel reaches, so the two agree to the bit, not within a tolerance.
            Assert.NotEqual(actual: word & SdfSurfaceTransport.SampleBit, expected: 0u);
            Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: native.Fog), actual: BitConverter.SingleToUInt32Bits(value: copied.Fog));
            Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: native.Distance), actual: BitConverter.SingleToUInt32Bits(value: copied.Distance));

            // A reconstruction's packed word never reads as a sample word, and it rounds what a sample word keeps.
            var packed = SdfSurfaceTransport.PackWord(pixel: SdfSurfaceTransport.Sample(fogDensity: FogDensity, sample: sample));
            var unpacked = SdfSurfaceTransport.ReadSurface(coverage: coverage, fogDensity: FogDensity, nativeDistance: 0f, word: packed);

            Assert.Equal(actual: packed & SdfSurfaceTransport.SampleBit, expected: 0u);
            packedDiffers |= (unpacked != native);
        }

        Assert.True(condition: packedDiffers, userMessage: "Half-float packing must be what the sample word avoids.");
    }
    [Fact]
    public void TheFirstFrameOfAnEpochRunsTheSpatialPathsOneSite() {
        var resolve = CodeOf(path: "passes/sdf-resolve.comp.hlsl");
        var sky = CodeOf(path: "passes/sdf-sky-pass.hlsli");
        var composite = CodeOf(path: "passes/sdf-composite.comp.hlsl");
        var main = resolve[resolve.IndexOf(comparisonType: StringComparison.Ordinal, value: "void CSMain(")..];

        // The resolve computes the spatial color and word once, before it knows whether the view is temporal, and an
        // unaccepted temporal pixel, every pixel of an epoch's first frame, writes exactly those values.
        Assert.Single(collection: Regex.Matches(input: main, pattern: @"\bsdfResolveSpatial\("));
        Assert.Contains(actualString: main, expectedSubstring: "transportRW[((id.y * extent.x) + id.x)] = spatialWord;");
        Assert.Contains(actualString: main, expectedSubstring: "float4 resolved = (accepted ? lerp(spatial, accumulated, saturate(total)) : spatial);");
        Assert.Contains(actualString: main, expectedSubstring: "(accepted ? sdfPackTransport(lerp(spatialTransport, accumulatedTransport, saturate(total))) : spatialWord)");
        // A pixel copied whole carries its sample's distance, read where a native view's composite reads it.
        Assert.Matches(actualString: resolve, expectedRegexPattern: @"if \(SDF_VISIBILITY_CURRENT\(pixel, cullBounds\)\) \{[^}]*t = \(sdfVisibilityHit\(visibility\) \? visibility\.t : 0\.0\);\s*\}\s*transport = sdfSampleTransport\(color\.a, t\);\s*word = sdfTransportSampleWord\(t\);");
        // The composite derives a sample's transport at one site, which a native pixel and a sample word both reach.
        Assert.Single(collection: Regex.Matches(input: (sky + composite), pattern: @"\bsdfSampleTransport\("));
        Assert.Contains(actualString: sky, expectedSubstring: "float2 surface = (sample ? sdfSampleTransport(coverage, t) : sdfUnpackTransport(word));");
        // That site's arithmetic is precise, so no compiler contracts or reorders it differently in another kernel.
        var transport = CodeOf(path: "shade/sdf-transport.hlsli");

        Assert.Contains(actualString: transport, expectedSubstring: "precise float transmittance = ");
        Assert.Contains(actualString: transport, expectedSubstring: "precise float fog = ");
        Assert.Contains(actualString: transport, expectedSubstring: "precise float inverseDistance = ");
        Assert.Matches(actualString: transport, expectedRegexPattern: $@"static const uint SdfTransportSampleBit = 0x{SdfSurfaceTransport.SampleBit:X8}u;");
    }
    [Fact]
    public void TheKernelsCarryTheTransportWithTheColorsWeights() {
        var views = CodeOf(path: "passes/sdf-hit-stages.hlsli");
        var resolve = CodeOf(path: "passes/sdf-resolve.comp.hlsl");
        var composite = CodeOf(path: "passes/sdf-composite.comp.hlsl");
        var sky = CodeOf(path: "passes/sdf-sky-pass.hlsli");
        var transport = CodeOf(path: "shade/sdf-transport.hlsli");

        // Views leaves each hit's color through its own fog transmittance.
        Assert.Contains(actualString: views, expectedSubstring: "sdfFogTransmittance(s.t)");
        // The spatial path reads each transport tap beside its color tap and combines both over one footprint.
        Assert.Matches(actualString: resolve, expectedRegexPattern: @"colors\[tap\] = puckReconstructionTapWithin\(currentColor, at,[^;]*;\s*transports\[tap\]\.xy = sdfResolveTransportAt\(at, render, colors\[tap\]\.a\);");
        Assert.Contains(actualString: resolve, expectedSubstring: "color = puckReconstructionCombine(footprint, colors);");
        Assert.Contains(actualString: resolve, expectedSubstring: "transport = puckReconstructionCombine(footprint, transports).xy;");
        // The temporal path accumulates it with the Gaussian weights, and its history with the history color's.
        Assert.Contains(actualString: resolve, expectedSubstring: "transportSum += (weight * transport);");
        Assert.Contains(actualString: resolve, expectedSubstring: "rowTransport += (wx[x] * sdfUnpackTransport(");
        Assert.Contains(actualString: resolve, expectedSubstring: "transport += (wy[y] * rowTransport);");
        Assert.Contains(actualString: resolve, expectedSubstring: "lerp(spatialTransport, accumulatedTransport, saturate(total))");
        // No nearest-sample distance survives anywhere the composite reads from.
        Assert.DoesNotContain(actualString: ((resolve + composite) + sky), expectedSubstring: "surfaceDistance");
        // The composite clips each share at its own end.
        Assert.Contains(actualString: composite, expectedSubstring: "sdfSkyPassSurface(id.xy, coverage, fog, t);");
        Assert.Matches(actualString: composite, expectedRegexPattern: @"shadeVolumes\(surface, coverage, \(\(t > 0\.0\) \? t : far\), sky,");
        // The CPU reference and the kernels hold the inverse distance at one scale.
        Assert.Matches(
            actualString: transport,
            expectedRegexPattern: $@"static const float SdfTransportInverseDistanceScale = {SdfSurfaceTransport.InverseDistanceScale:0}\.0;"
        );
    }

    private static SdfSurfaceSample Sample(float coverage, float distance) =>
        SdfSurfaceTransport.Sample(fogDensity: FogDensity, sample: new SdfRenderSample(Color: Vector3.One, Coverage: coverage, Distance: distance));
    private static SdfSurfaceSample Resolve(int pixel) => SdfSurfaceTransport.Filter(
        samples: [.. Row.Select(selector: static sample => SdfSurfaceTransport.Sample(fogDensity: FogDensity, sample: sample))],
        weights: SdfSurfaceTransport.BilinearWeights(outputWidth: OutputWidth, pixel: pixel, renderWidth: Row.Length)
    );
    private static Vector3 Composite(SdfSurfaceSample pixel, SdfMediumSpan[] media) =>
        SdfSurfaceTransport.Composite(farDistance: FarDistance, gradient: Gradient, media: media, pixel: pixel, sky: Sky);
    private static Vector3 Expected(int pixel, SdfMediumSpan[] media) => SdfSurfaceTransport.Expected(
        farDistance: FarDistance,
        fogDensity: FogDensity,
        gradient: Gradient,
        media: media,
        samples: Row,
        sky: Sky,
        weights: SdfSurfaceTransport.BilinearWeights(outputWidth: OutputWidth, pixel: pixel, renderWidth: Row.Length)
    );
    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: KernelRoot, path2: path)), replacement: string.Empty);
    private static void Near(Vector3 actual, Vector3 expected, float tolerance = Tolerance) {
        Assert.Equal(actual: actual.X, expected: expected.X, tolerance: tolerance);
        Assert.Equal(actual: actual.Y, expected: expected.Y, tolerance: tolerance);
        Assert.Equal(actual: actual.Z, expected: expected.Z, tolerance: tolerance);
    }
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
}
