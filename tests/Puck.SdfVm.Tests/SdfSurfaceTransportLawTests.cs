using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: what the composite's atmosphere adds to a pixel and where it clips its media is reconstructed from
/// the render samples with the weights its color and coverage are (<see cref="SdfSurfaceTransport"/>), each atmosphere
/// kind's in-scatter weight carried apart, so a resolved pixel shows what its samples, each composited along its own ray,
/// show weighted, within a float tolerance of 1e-5 a channel: the fog of a partly covered pixel is its covered share's,
/// the fog, the haze and the medium of any footprint are its samples' weighted ones, and a medium behind an edge never
/// paints over the surface. The shipped kernels hold the same structure: views carries each hit through the atmosphere's
/// transmittance, the resolve reads every transport tap beside its color tap over one footprint and accumulates it with
/// the color's weights, and the composite clips each share at its own end.
/// </summary>
public sealed partial class SdfSurfaceTransportLawTests {
    private const float Tolerance = 1e-5f;
    private const float FarDistance = 1000f;

    // The default look's level fog along a level ray from the origin, which every fog-only law here reads.
    private static readonly SdfAirRay Air = AirOf(atmosphere: SdfAtmosphere.Default, direction: -Vector3.UnitZ, origin: Vector3.Zero);
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
        var transmittance = MathF.Exp(x: (-SdfSky.DefaultFogDensity * 100f));
        // A quarter of the pixel is the geometry seen through 100 units of fog; the rest is the sky.
        var expected = ((0.25f * ((transmittance * Vector3.One) + ((1f - transmittance) * Gradient))) + (0.75f * Sky));

        Assert.Equal(expected: 0.25f, actual: pixel.Coverage, tolerance: Tolerance);
        Assert.Equal(expected: (0.25f * (1f - transmittance)), actual: pixel.Fog, tolerance: Tolerance);
        Assert.Equal(expected: 100f, actual: SdfSurfaceTransport.SurfaceDistance(pixel: pixel), tolerance: 1e-3f);
        Near(actual: Composite(media: [], pixel: pixel), expected: expected);
        for (var output = 0; (output < OutputWidth); output++) {
            Near(actual: Composite(media: [], pixel: Resolve(pixel: output)), expected: Expected(media: [], pixel: output));
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

            var pixel = SdfSurfaceTransport.Filter(samples: [.. samples.Select(selector: static sample => SdfSurfaceTransport.Sample(air: Air, sample: sample))], weights: weights);

            Near(
                actual: Composite(media: [], pixel: pixel),
                expected: SdfSurfaceTransport.Expected(air: Air, colors: Colors(air: Air), farDistance: FarDistance, media: [], samples: samples, sky: Sky, weights: weights),
                tolerance: 1e-4f
            );
        }
    }
    [Fact]
    public void EachKindsInScatterIsTheWeightedInScatterOfAnyFootprintsSamples() {
        // A height fog, a falling haze toward a low sun and a medium below the eye, along a descending ray that crosses the
        // medium's surface: three kinds, each with its own colour, carried apart.
        var lights = new SdfLights();

        lights.Set(index: 0, light: new SdfLight(Kind: SdfLightKind.Directional, Direction: new Vector3(x: 0f, y: 0.1f, z: -1f), Color: new Vector3(x: 1f, y: 0.8f, z: 0.5f), Weight: 2f, Param: 0f, Shadows: false));
        lights.Count = 1;
        var atmosphere = SdfAtmosphere.None with {
            FogBase = 0f,
            FogDensity = 0.02f,
            FogFalloff = 6f,
            HazeAmount = 0.4f,
            HazeFalloff = 20f,
            MediumColor = new Vector3(x: 0.01f, y: 0.2f, z: 0.25f),
            MediumExtinction = 0.3f,
            MediumSurface = -3f,
        };
        var air = AirOf(atmosphere: atmosphere, direction: Vector3.Normalize(value: new Vector3(x: 0f, y: -0.2f, z: -1f)), lights: lights, origin: new Vector3(x: 0f, y: 2f, z: 0f));
        var colors = Colors(air: air);
        var random = new Random(Seed: 1810);

        Assert.True(condition: ((colors.Fog != colors.Haze) && (colors.Haze != colors.Medium)), userMessage: "Each kind needs its own colour for the law to tell them apart.");
        for (var trial = 0; (trial < 500); trial++) {
            var count = random.Next(maxValue: 17, minValue: 1);
            var samples = new SdfRenderSample[count];
            var weights = new float[count];
            var total = 0f;

            for (var index = 0; (index < count); index++) {
                samples[index] = ((random.Next(maxValue: 3) != 0)
                    ? new SdfRenderSample(
                        Color: new Vector3(x: random.NextSingle(), y: random.NextSingle(), z: random.NextSingle()),
                        Coverage: MathF.Max(x: random.NextSingle(), y: 0.001f),
                        Distance: (0.5f + (random.NextSingle() * 60f)))
                    : default);
                weights[index] = random.NextSingle();
                total += weights[index];
            }
            for (var index = 0; (index < count); index++) {
                weights[index] /= MathF.Max(x: total, y: 1e-6f);
            }

            var pixel = SdfSurfaceTransport.Filter(samples: [.. samples.Select(selector: sample => SdfSurfaceTransport.Sample(air: air, sample: sample))], weights: weights);

            Near(
                actual: SdfSurfaceTransport.Composite(colors: colors, farDistance: FarDistance, media: [], pixel: pixel, sky: Sky, skyAir: air.Along(distance: FarDistance, fog: false)),
                expected: SdfSurfaceTransport.Expected(air: air, colors: colors, farDistance: FarDistance, media: [], samples: samples, sky: Sky, weights: weights),
                tolerance: 1e-4f
            );
        }

        // A ray through all three carries a weight of each, each lit by its own kind's colour, and the eye sees each
        // sample's light through the product.
        var through = SdfSurfaceTransport.Sample(air: air, sample: new SdfRenderSample(Color: Vector3.One, Coverage: 1f, Distance: 40f));
        var span = air.Along(distance: 40f, fog: true);

        Near(
            actual: SdfSurfaceTransport.Composite(colors: colors, farDistance: FarDistance, media: [], pixel: through, sky: Sky, skyAir: air.Along(distance: FarDistance, fog: false)),
            expected: ((((span.Transmittance * Vector3.One) + (span.Fog * colors.Fog)) + (span.Haze * colors.Haze)) + (span.Medium * colors.Medium))
        );

        Assert.True(condition: ((through.Fog > 0f) && (through.Haze > 0f) && (through.Medium > 0f)), userMessage: $"fog {through.Fog}, haze {through.Haze}, medium {through.Medium}");
        Assert.Equal(expected: 1f, actual: ((through.Lit.X + through.Fog) + (through.Haze + through.Medium)), tolerance: 1e-5f);
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
            var word = SdfSurfaceTransport.SpatialWord(air: Air, exact: true, samples: [sample], weights: [1f]);
            var native = SdfSurfaceTransport.ReadSurface(air: Air, coverage: coverage, nativeDistance: distance, word: null);
            var copied = SdfSurfaceTransport.ReadSurface(air: Air, coverage: coverage, nativeDistance: 0f, word: word);

            // The copy carries its sample's distance whole, and the composite derives the rest at the one site a native
            // pixel reaches, so the two agree to the bit, not within a tolerance.
            Assert.NotEqual(actual: word.Low & SdfSurfaceTransport.SampleBit, expected: 0u);
            Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: native.Surface.Fog), actual: BitConverter.SingleToUInt32Bits(value: copied.Surface.Fog));
            Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: native.Distance), actual: BitConverter.SingleToUInt32Bits(value: copied.Distance));

            // A reconstruction's packed word never reads as a sample word, and it rounds what a sample word keeps.
            var packed = SdfSurfaceTransport.PackWord(pixel: SdfSurfaceTransport.Sample(air: Air, sample: sample));
            var unpacked = SdfSurfaceTransport.ReadSurface(air: Air, coverage: coverage, nativeDistance: 0f, word: packed);

            Assert.Equal(actual: packed.Low & SdfSurfaceTransport.SampleBit, expected: 0u);
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
        Assert.Matches(actualString: resolve, expectedRegexPattern: @"if \(SDF_VISIBILITY_CURRENT\(pixel, cullBounds\)\) \{[^}]*t = \(sdfVisibilityHit\(visibility\) \? visibility\.t : 0\.0\);\s*\}\s*transport = sdfSampleTransport\(color\.a, t, origin, direction\);\s*word = sdfTransportSampleWord\(t\);");
        // The composite derives a sample's transport at one site, which a native pixel and a sample word both reach.
        Assert.Single(collection: Regex.Matches(input: (sky + composite), pattern: @"\bsdfSampleTransport\("));
        Assert.Contains(actualString: sky, expectedSubstring: "float4 surface = (sample ? sdfSampleTransport(coverage, t, origin, direction) : sdfUnpackTransport(word));");
        // That site's arithmetic is precise, so no compiler contracts or reorders it differently in another kernel.
        var transport = CodeOf(path: "shade/sdf-transport.hlsli");
        var atmosphere = CodeOf(path: "shade/sdf-atmosphere.hlsli");

        Assert.Contains(actualString: atmosphere, expectedSubstring: "precise float through = ");
        Assert.Contains(actualString: atmosphere, expectedSubstring: "precise float scattered = ");
        Assert.Contains(actualString: transport, expectedSubstring: "precise float fog = ");
        Assert.Contains(actualString: transport, expectedSubstring: "precise float haze = ");
        Assert.Contains(actualString: transport, expectedSubstring: "precise float medium = ");
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

        // Views leaves each hit's color through the atmosphere's transmittance along its own ray.
        Assert.Contains(actualString: views, expectedSubstring: "sdfAirTransmittance(p.rayOrigin, p.rayDirection, s.t)");
        // The spatial path reads each transport tap beside its color tap and combines both over one footprint.
        Assert.Matches(actualString: resolve, expectedRegexPattern: @"colors\[tap\] = puckReconstructionTapWithin\(currentColor, at,[^;]*;\s*transports\[tap\] = sdfResolveTransportAt\(at, render, colors\[tap\]\.a, origin, direction\);");
        Assert.Contains(actualString: resolve, expectedSubstring: "color = puckReconstructionCombine(footprint, colors);");
        Assert.Contains(actualString: resolve, expectedSubstring: "transport = puckReconstructionCombine(footprint, transports);");
        // The temporal path accumulates it with the Gaussian weights, and its history with the history color's.
        Assert.Contains(actualString: resolve, expectedSubstring: "transportSum += (weight * transport);");
        Assert.Contains(actualString: resolve, expectedSubstring: "rowTransport += (wx[x] * sdfUnpackTransport(");
        Assert.Contains(actualString: resolve, expectedSubstring: "transport += (wy[y] * rowTransport);");
        Assert.Contains(actualString: resolve, expectedSubstring: "lerp(spatialTransport, accumulatedTransport, saturate(total))");
        // No nearest-sample distance survives anywhere the composite reads from.
        Assert.DoesNotContain(actualString: ((resolve + composite) + sky), expectedSubstring: "surfaceDistance");
        // The composite clips each share at its own end.
        Assert.Contains(actualString: composite, expectedSubstring: "sdfSkyPassSurface(id.xy, coverage, origin, direction, weights, t);");
        Assert.Matches(actualString: composite, expectedRegexPattern: @"shadeVolumes\(surface, coverage, \(\(t > 0\.0\) \? t : far\), sky,");
        // The CPU reference and the kernels hold the inverse distance at one scale.
        Assert.Matches(
            actualString: transport,
            expectedRegexPattern: $@"static const float SdfTransportInverseDistanceScale = {SdfSurfaceTransport.InverseDistanceScale:0}\.0;"
        );
    }

    // A ray through an atmosphere packed as the tables pack it, the far distance the haze is measured over.
    private static SdfAirRay AirOf(SdfAtmosphere atmosphere, Vector3 origin, Vector3 direction, SdfLights? lights = null) {
        var block = default(SdfSkyBlock);

        SdfSky.PackAtmosphere(atmosphere: in atmosphere, block: ref block, farDistance: FarDistance, lights: (lights ?? new SdfLights()));

        return new SdfAirRay(Block: block, Direction: direction, Origin: origin);
    }
    // Each kind's colour along a ray, the sky the fog and the haze in-scatter being the gradient.
    private static SdfAirColors Colors(SdfAirRay air) =>
        SdfAir.Colors(block: air.Block, direction: air.Direction, sky: Gradient);
    private static SdfSurfaceSample Sample(float coverage, float distance) =>
        SdfSurfaceTransport.Sample(air: Air, sample: new SdfRenderSample(Color: Vector3.One, Coverage: coverage, Distance: distance));
    private static SdfSurfaceSample Resolve(int pixel) => SdfSurfaceTransport.Filter(
        samples: [.. Row.Select(selector: static sample => SdfSurfaceTransport.Sample(air: Air, sample: sample))],
        weights: SdfSurfaceTransport.BilinearWeights(outputWidth: OutputWidth, pixel: pixel, renderWidth: Row.Length)
    );
    private static Vector3 Composite(SdfSurfaceSample pixel, SdfMediumSpan[] media) =>
        SdfSurfaceTransport.Composite(colors: Colors(air: Air), farDistance: FarDistance, media: media, pixel: pixel, sky: Sky, skyAir: Air.Along(distance: FarDistance, fog: false));
    private static Vector3 Expected(int pixel, SdfMediumSpan[] media) => SdfSurfaceTransport.Expected(
        air: Air,
        colors: Colors(air: Air),
        farDistance: FarDistance,
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
