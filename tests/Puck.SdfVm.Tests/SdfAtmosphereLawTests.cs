using System.Numerics;
using System.Text.RegularExpressions;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the atmosphere's closed forms (<see cref="SdfAir"/>, mirrored by <c>shade/sdf-atmosphere.hlsli</c>).
/// A height medium's optical depth along any ray is its density's integral, held to a numeric quadrature within a relative
/// 1e-4, a ray parallel to its base and rays crossing it steeply among them; a ray crossing the medium's level surface
/// passes through the air and then the water, or the reverse, with the exact product of their transmittances; the haze
/// takes its amount over the far distance and is brighter toward a low sun than away from it; and an atmosphere authoring
/// no kind does nothing to any ray.
/// </summary>
public sealed partial class SdfAtmosphereLawTests {
    private const float FarDistance = 40f;

    private static string KernelRoot => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);

    [Fact]
    public void HeightFogsIntegralAlongARayIsItsClosedForm() {
        // Rays from below, at and above the base, level (parallel to the base), grazing, climbing and plunging, over
        // segments that start at the eye and away from it.
        float[] heights = [-12f, -1f, 0f, 0.5f, 3f, 25f];
        float[] slopes = [0f, 1e-7f, -1e-7f, 1e-4f, -1e-4f, 0.05f, -0.05f, 0.4f, -0.4f, 0.9f, -0.9f, 1f, -1f];
        (float Near, float Far)[] segments = [(0f, 1f), (0f, 40f), (3f, 17f), (0f, 400f)];
        const float Extinction = 0.03f;
        const float BaseHeight = 0.5f;
        const float Falloff = 6f;

        foreach (var height in heights) {
            foreach (var slope in slopes) {
                foreach (var (near, far) in segments) {
                    var closed = SdfAir.Depth(baseHeight: BaseHeight, directionY: slope, extinction: Extinction, falloff: Falloff, far: far, near: near, originY: height);
                    var lowest = Math.Min(val1: (height + (near * slope)), val2: (height + (far * slope)));

                    // Past the bounded exponent the density is clamped, where a depth lets no light through anyway.
                    if ((-(lowest - BaseHeight) / Falloff) > SdfAir.ExponentLimit) {
                        Assert.True(condition: (float.IsFinite(f: closed) && (MathF.Exp(x: -closed) == 0f)), userMessage: $"height {height}, slope {slope}: {closed}");

                        continue;
                    }

                    var numeric = Quadrature(baseHeight: BaseHeight, directionY: slope, extinction: Extinction, falloff: Falloff, far: far, near: near, originY: height);

                    Assert.True(
                        condition: (float.IsFinite(f: closed) && (Math.Abs(value: (closed - numeric)) <= (1e-4d * Math.Max(val1: numeric, val2: 1e-6d)))),
                        userMessage: $"height {height}, slope {slope}, [{near}, {far}]: closed form {closed}, integral {numeric}"
                    );
                }
            }
        }

        // A level fog's depth is its density times the length, whatever the ray's slope.
        Assert.Equal(expected: (Extinction * 30f), actual: SdfAir.Depth(baseHeight: 0f, directionY: -0.7f, extinction: Extinction, falloff: 0f, far: 32f, near: 2f, originY: 5f));
        // Far below the base the density's exponent is bounded, so a depth stays finite and lets no light through.
        Assert.True(condition: float.IsFinite(f: SdfAir.Depth(baseHeight: 0f, directionY: -1f, extinction: Extinction, falloff: 0.01f, far: 8192f, near: 0f, originY: -500f)));
    }
    [Fact]
    public void TheKernelsHeightMediumIsTheReferences() {
        var atmosphere = CodeOf(path: "shade/sdf-atmosphere.hlsli");

        // The series a ray parallel to the base takes, and the bounds, carry the reference's constants.
        Assert.Contains(actualString: atmosphere, expectedSubstring: "((abs(x) < SdfAirSeriesLimit) ? (1.0 - (x * (0.5 - (x / 6.0)))) : ((1.0 - exp(-x)) / x))");
        Assert.Matches(actualString: atmosphere, expectedRegexPattern: $@"static const float SdfAirSeriesLimit = {SdfAir.SeriesLimit:0.0e+0};");
        Assert.Matches(actualString: atmosphere, expectedRegexPattern: $@"static const float SdfAirExponentLimit = {SdfAir.ExponentLimit:0.0};");
        Assert.Matches(actualString: atmosphere, expectedRegexPattern: $@"static const float SdfAirSteepLimit = {SdfAir.SteepLimit:0.0};");
        Assert.Matches(actualString: atmosphere, expectedRegexPattern: $@"static const uint SdfAirMaxLights = {SdfAir.MaxLights}u;");
        Assert.Matches(actualString: atmosphere, expectedRegexPattern: $@"static const uint SdfAirFogColorAuthored = {SdfAir.FogColorAuthored}u;");
        Assert.Matches(expectedRegexPattern: $@"static const float SdfVolumeScatterAnisotropy = {SdfVolume.ScatterAnisotropy:0.0};", actualString: CodeOf(path: "shade/shade-volumes.hlsli"));
    }
    [Fact]
    public void ARayCrossingTheMediumsSurfacePassesThroughEachSideInOrder() {
        var block = Pack(atmosphere: SdfAtmosphere.None with {
            FogDensity = 0.05f,
            MediumColor = new Vector3(x: 0f, y: 0.2f, z: 0.3f),
            MediumExtinction = 0.4f,
            MediumSurface = 0f,
        });
        var down = Vector3.Normalize(value: new Vector3(x: 0f, y: -1f, z: -1f));
        // From two units above the surface, falling at 45 degrees: the surface lies 2√2 units along the ray.
        var crossing = (2f * MathF.Sqrt(x: 2f));
        var from = new Vector3(x: 0f, y: 2f, z: 0f);
        var span = SdfAir.Along(block: in block, direction: down, distance: 10f, fog: true, origin: from);
        var air = MathF.Exp(x: -(0.05f * crossing));
        var water = MathF.Exp(x: -(0.4f * (10f - crossing)));

        Assert.Equal(expected: (air * water), actual: span.Transmittance, tolerance: 1e-6f);
        Assert.Equal(expected: (1f - air), actual: span.Fog, tolerance: 1e-6f);
        Assert.Equal(expected: (air * (1f - water)), actual: span.Medium, tolerance: 1e-6f);
        Assert.Equal(expected: 0f, actual: span.Haze);

        // From below, looking up at the sky: the water first, then the air above it, which holds no fog on the sky's ray.
        var up = Vector3.Normalize(value: new Vector3(x: 0f, y: 1f, z: -1f));
        var sky = SdfAir.Along(block: in block, direction: up, distance: FarDistance, fog: false, origin: -from);
        var under = MathF.Exp(x: -(0.4f * crossing));

        Assert.Equal(expected: under, actual: sky.Transmittance, tolerance: 1e-6f);
        Assert.Equal(expected: (1f - under), actual: sky.Medium, tolerance: 1e-6f);
        Assert.Equal(expected: 0f, actual: sky.Fog);

        // A level ray in the air never meets the water.
        Assert.Equal(expected: 0f, actual: SdfAir.Along(block: in block, direction: -Vector3.UnitZ, distance: 30f, fog: true, origin: from).Medium);
    }
    [Fact]
    public void HazeTakesItsAmountOverTheFarDistanceAndGlowsTowardALowSun() {
        var lights = new SdfLights { Count = 1 };
        var toward = Vector3.Normalize(value: new Vector3(x: 0f, y: 0.08f, z: -1f));

        lights.Set(index: 0, light: new SdfLight(Color: new Vector3(x: 1f, y: 0.85f, z: 0.6f), Direction: toward, Kind: SdfLightKind.Directional, Param: 0f, Shadows: true, Weight: 2f));
        var block = Pack(atmosphere: SdfAtmosphere.None with { HazeAmount = 0.3f }, lights: lights);
        var level = SdfAir.Along(block: in block, direction: -Vector3.UnitZ, distance: FarDistance, fog: true, origin: Vector3.Zero);

        Assert.Equal(expected: 0.7f, actual: level.Transmittance, tolerance: 1e-6f);
        Assert.Equal(expected: 0.3f, actual: level.Haze, tolerance: 1e-6f);
        Assert.Equal(actual: block.AirLightCount, expected: 1u);

        var sky = new Vector3(x: 0.3f, y: 0.4f, z: 0.6f);
        var sunward = SdfAir.Colors(block: in block, direction: toward, sky: sky).Haze;
        var away = SdfAir.Colors(block: in block, direction: new Vector3(x: -toward.X, y: toward.Y, z: -toward.Z), sky: sky).Haze;

        Assert.True(condition: (Luminance(color: sunward) > (4f * Luminance(color: away))), userMessage: $"toward {sunward}, away {away}");
        Assert.True(condition: (Luminance(color: away) > Luminance(color: sky)), userMessage: "The haze adds the sun's light away from it too, by the phase's back lobe.");
    }
    [Fact]
    public void AnAtmosphereOfNoKindDoesNothingToAnyRay() {
        var block = Pack(atmosphere: SdfAtmosphere.None);
        var random = new Random(Seed: 1810);

        for (var trial = 0; (trial < 200); trial++) {
            var direction = Vector3.Normalize(value: new Vector3(x: ((random.NextSingle() * 2f) - 1f), y: ((random.NextSingle() * 2f) - 1f), z: -1f));
            var origin = new Vector3(x: 0f, y: ((random.NextSingle() * 40f) - 20f), z: 0f);

            Assert.Equal(expected: SdfAirSpan.Clear, actual: SdfAir.Along(block: in block, direction: direction, distance: (random.NextSingle() * 8192f), fog: true, origin: origin));
        }
        Assert.Equal(actual: block.AirFlags | block.AirLightCount, expected: 0u);
    }

    // The integral of the height medium's density along the ray by composite Simpson in double, fine enough that its
    // error lies far inside the law's tolerance.
    private static double Quadrature(float extinction, float baseHeight, float falloff, float originY, float directionY, float near, float far) {
        const int Intervals = 20000;
        var step = ((far - ((double)near)) / Intervals);
        var sum = 0d;

        for (var index = 0; (index <= Intervals); index++) {
            var t = (near + (index * step));
            var density = (extinction * Math.Exp(d: (-((originY + (t * directionY)) - baseHeight) / falloff)));
            var weight = (((index == 0) || (index == Intervals)) ? 1d : (((index % 2) == 0) ? 2d : 4d));

            sum += (weight * density);
        }

        return ((sum * step) / 3d);
    }
    private static SdfSkyBlock Pack(SdfAtmosphere atmosphere, SdfLights? lights = null) {
        var block = default(SdfSkyBlock);

        SdfSky.PackAtmosphere(atmosphere: in atmosphere, block: ref block, farDistance: FarDistance, lights: (lights ?? new SdfLights()));

        return block;
    }
    private static float Luminance(Vector3 color) =>
        (((0.2126f * color.X) + (0.7152f * color.Y)) + (0.0722f * color.Z));
    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: KernelRoot, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
}
