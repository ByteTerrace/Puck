using System.Numerics;
using System.Text.RegularExpressions;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the sky's environment map stands for the layers its lighting sees (a gradient here), which the
/// composite's fog in-scatters,
/// within a stated bound, and its coefficients are the map's spherical harmonics, on the CPU reference
/// (<see cref="SdfSkyEnvironment"/>) the kernels follow. The texels' solid angles total the sphere within 1e-6 of 4π. A
/// constant sky projects to its colour times √(4π) in the first coefficient within 1e-6 and to under 2e-3 of its colour in
/// every other; the default look's linear gradient projects to its analytic first and second coefficients within 1e-3 and
/// to under 1e-3 of the first in the rest. A lookup reads the default look within half a display code (1/510) in every
/// direction; two lookups a hair either side of a seam of the octahedral fold read within 1e-3 of each other over a map of
/// the direction itself, since a tap past an edge reads the texel the fold puts there; and fog toward a bright body reads the gradient alone within half a display
/// code, since the map holds no body. The map's size and coefficient count in <c>shade/sdf-sky-environment.hlsli</c>
/// are the reference's.
/// </summary>
public sealed partial class SdfSkyEnvironmentLawTests {
    // Half an 8-bit display code of a unit channel.
    private const float HalfDisplayCode = (0.5f / 255f);
    // The most two lookups a hair either side of a seam may differ by over a map of the direction itself: 2.0e-5 with the
    // taps the fold puts there, against 0.045 with taps clamped to the edge, which read a texel step apart.
    private const float FoldBound = 1e-3f;

    private static readonly double SphereRoot = Math.Sqrt(d: (4.0 * Math.PI));

    [Fact]
    public void TheTexelsSolidAnglesTotalTheSphere() {
        var total = 0.0;

        for (var y = 0; (y < SdfSkyEnvironment.Size); y++) {
            for (var x = 0; (x < SdfSkyEnvironment.Size); x++) {
                total += SdfSkyEnvironment.SolidAngle(x: x, y: y);
            }
        }

        Assert.InRange(actual: (Math.Abs(value: (total - (4.0 * Math.PI))) / (4.0 * Math.PI)), high: 1e-6, low: 0.0);
    }
    [Fact]
    public void AConstantSkyProjectsToItsColourTimesTheRootOfTheSphere() {
        var color = new Vector3(x: 0.5f, y: 0.25f, z: 1f);
        var coefficients = Project(sky: Gradient(stops: [(color, -1f), (color, 1f)]));

        AssertNear(actual: coefficients[0], expected: (color * ((float)SphereRoot)), relative: 1e-6f);
        for (var index = 1; (index < SdfSkyEnvironment.CoefficientCount); index++) {
            AssertSmall(actual: coefficients[index], bound: (2e-3f * color));
        }
    }
    [Fact]
    public void ALinearGradientProjectsToItsAnalyticHarmonics() {
        // The default look, ground at -1 and zenith at +1: f(y) = mean + half · y, whose projection is the mean times
        // Y00 · 4π and half times Y1-1's normalization times the integral of y², 4π/3, with nothing in any other harmonic.
        var sky = new SdfSky();
        var ground = SdfSky.DefaultGroundColor;
        var zenith = SdfSky.DefaultZenithColor;
        var coefficients = Project(sky: sky);
        var mean = (0.5f * (ground + zenith));
        var half = (0.5f * (zenith - ground));

        AssertNear(actual: coefficients[0], expected: (mean * ((float)((0.28209479177387814 * 4.0) * Math.PI))), relative: 1e-3f);
        AssertNear(actual: coefficients[1], expected: (half * ((float)(((0.48860251190291992 * 4.0) * Math.PI) / 3.0))), relative: 1e-3f);
        for (var index = 2; (index < SdfSkyEnvironment.CoefficientCount); index++) {
            AssertSmall(actual: coefficients[index], bound: (1e-3f * coefficients[0]));
        }
    }
    [Fact]
    public void ALookupReadsASmoothSkyWithinHalfADisplayCodeInEveryDirection() {
        var sky = new SdfSky();
        var map = Render(block: out _, sky: sky);
        var worst = 0f;

        foreach (var direction in Directions()) {
            worst = MathF.Max(x: worst, y: Error(actual: SdfSkyEnvironment.Sample(direction: direction, map: map), expected: SdfSkyEnvironment.Gradient(direction: direction, gradient: SdfSky.DefaultGradient)));
        }

        Assert.InRange(actual: worst, high: HalfDisplayCode, low: 0f);
    }
    [Fact]
    public void ATapPastAnEdgeReadsTheDirectionTheFoldPutsThere() {
        // The map's edges are the lower hemisphere's seams, the half-planes z = 0 (u = 0 and 1) and x = 0 (v = 0 and 1),
        // each glued to itself mirrored about its midpoint. Two directions a hair either side of a seam are one direction
        // to within the hair, so a lookup reads them alike when the taps past the edge read the texels the fold puts there:
        // the four taps either side of the seam are then the same four texels with the same weights. Read from the texel
        // at the edge instead, each side reads only its own half a texel inward, so in a map steep across the seam the two
        // sides differ by its whole texel step. The map is the direction itself, each channel steep across one seam.
        var map = new Vector3[SdfSkyEnvironment.Texels];

        for (var y = 0; (y < SdfSkyEnvironment.Size); y++) {
            for (var x = 0; (x < SdfSkyEnvironment.Size); x++) {
                map[((y * SdfSkyEnvironment.Size) + x)] = SdfSkyEnvironment.Direction(x: x, y: y);
            }
        }

        const int Steps = 1000;
        const float Hair = 1e-5f;
        var worst = 0f;

        // Every seam point below the horizon, away from the pole the four corners share, at each side of each seam.
        for (var step = 0; (step < Steps); step++) {
            var angle = ((float)(((step + 0.5) / Steps) * (2.0 * Math.PI)));

            var (sin, cos) = MathF.SinCos(x: angle);

            if ((sin > -0.05f) || (sin < -0.95f)) {
                continue;
            }

            foreach (var seam in ((ReadOnlySpan<Vector3>)[new Vector3(x: cos, y: sin, z: 0f), new Vector3(x: 0f, y: sin, z: cos)])) {
                var across = ((seam.Z == 0f) ? Vector3.UnitZ : Vector3.UnitX);
                var one = SdfSkyEnvironment.Sample(direction: Vector3.Normalize(value: (seam + (Hair * across))), map: map);
                var other = SdfSkyEnvironment.Sample(direction: Vector3.Normalize(value: (seam - (Hair * across))), map: map);

                worst = MathF.Max(x: worst, y: Error(actual: one, expected: other));
            }
        }
        Assert.InRange(actual: worst, high: FoldBound, low: 0f);
    }
    [Fact]
    public void FogTowardABrightBodyReadsTheGradientAlone() {
        // A disc fifty times the sky's brightest channel on the default sun: the fog about it, out to ten degrees, is the
        // gradient's within the lookup's bound, so no texel holds the disc.
        var sky = new SdfSky();

        _ = sky.Add(blend: SdfSkyBlend.Add, label: "disc", parameters: new SdfSkyDisc { Intensity = 50f, Light = 0 }, visibility: SdfSkyVisibility.Both);

        var layers = new SdfSkyLayer[SdfSky.MaxLayers];
        var map = Render(block: out _, layers: layers, sky: sky);
        var worst = 0f;
        var disc = SdfSky.PayloadOf<SdfSkyDisc>(layer: ref layers[1]).Direction;

        Assert.NotEqual(expected: Vector3.Zero, actual: disc);
        foreach (var direction in Directions()) {
            if (Vector3.Dot(vector1: direction, vector2: disc) < MathF.Cos(x: (10f * (MathF.PI / 180f)))) {
                continue;
            }

            worst = MathF.Max(x: worst, y: Error(actual: SdfSkyEnvironment.Sample(direction: direction, map: map), expected: SdfSkyEnvironment.Gradient(direction: direction, gradient: SdfSky.DefaultGradient)));
        }

        Assert.InRange(actual: worst, high: HalfDisplayCode, low: 0f);
    }
    [Fact]
    public void TheKernelsMapIsTheReferencesSize() {
        var header = File.ReadAllText(path: Path.Combine(
            path1: RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory),
            path2: "shade/sdf-sky-environment.hlsli"
        ));

        Assert.Equal(expected: SdfSkyEnvironment.Size.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), actual: SizePattern().Match(input: header).Groups[1].Value);
        Assert.Equal(expected: SdfSkyEnvironment.CoefficientCount.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), actual: CountPattern().Match(input: header).Groups[1].Value);
    }

    // A sky of the default look but its one gradient's stops.
    private static SdfSky Gradient((Vector3 Color, float Elevation)[] stops) {
        var sky = new SdfSky();
        var gradient = new SdfSkyGradient { Count = ((uint)stops.Length) };

        for (var index = 0; (index < stops.Length); index++) {
            gradient.SetStop(color: stops[index].Color, elevation: stops[index].Elevation, index: index);
        }

        sky.ClearLayers();
        _ = sky.Add(label: "gradient", parameters: gradient, visibility: SdfSkyVisibility.Both);

        return sky;
    }
    private static Vector3[] Render(SdfSky sky, out SdfSkyBlock block, SdfSkyLayer[]? layers = null) {
        var map = new Vector3[SdfSkyEnvironment.Texels];

        layers ??= new SdfSkyLayer[SdfSky.MaxLayers];
        sky.Pack(block: out block, details: new SdfSkyDetails(), farDistance: 40f, layers: layers, lights: SdfLights.Default(), softboxes: new SdfSoftbox[SdfSky.MaxSoftboxes]);
        SdfSkyEnvironment.Render(block: in block, layers: layers, map: map);

        return map;
    }
    private static Vector3[] Project(SdfSky sky) {
        var coefficients = new Vector3[SdfSkyEnvironment.CoefficientCount];

        SdfSkyEnvironment.Project(coefficients: coefficients, map: Render(block: out _, sky: sky));

        return coefficients;
    }
    // Every direction of a 400 by 800 sweep of elevation and azimuth, offset so none lies on a texel's edge.
    private static IEnumerable<Vector3> Directions() {
        const int Rows = 400;

        for (var row = 0; (row < Rows); row++) {
            var elevation = (((Math.PI * (row + 0.5)) / Rows) - (0.5 * Math.PI));

            for (var column = 0; (column < (2 * Rows)); column++) {
                var azimuth = (((2.0 * Math.PI) * (column + 0.37)) / (2 * Rows));

                yield return new Vector3(
                    x: ((float)(Math.Cos(d: elevation) * Math.Cos(d: azimuth))),
                    y: ((float)Math.Sin(a: elevation)),
                    z: ((float)(Math.Cos(d: elevation) * Math.Sin(a: azimuth)))
                );
            }
        }
    }
    private static float Error(Vector3 actual, Vector3 expected) {
        var difference = Vector3.Abs(value: (actual - expected));

        return MathF.Max(x: difference.X, y: MathF.Max(x: difference.Y, y: difference.Z));
    }
    private static void AssertNear(Vector3 actual, Vector3 expected, float relative) =>
        Assert.True(
            condition: (Error(actual: actual, expected: expected) <= (relative * MathF.Max(x: expected.X, y: MathF.Max(x: expected.Y, y: expected.Z)))),
            userMessage: $"{actual} is not within {relative} of {expected}."
        );
    private static void AssertSmall(Vector3 actual, Vector3 bound) =>
        Assert.True(
            condition: ((MathF.Abs(x: actual.X) <= bound.X) && (MathF.Abs(x: actual.Y) <= bound.Y) && (MathF.Abs(x: actual.Z) <= bound.Z)),
            userMessage: $"{actual} exceeds {bound}."
        );
    [GeneratedRegex(pattern: @"static const int SdfSkyEnvironmentSize = (\d+);")]
    private static partial Regex SizePattern();
    [GeneratedRegex(pattern: @"static const uint SdfSkyEnvironmentCoefficients = (\d+)u;")]
    private static partial Regex CountPattern();
}
