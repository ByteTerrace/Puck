using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfSkyLightingLawTests {
    private static SdfSky Sky(Vector3 low, Vector3 high, SdfSkyVisibility visibility = SdfSkyVisibility.Both) {
        var sky = new SdfSky();
        sky.ClearLayers();
        var gradient = new SdfSkyGradient { Count = 2 };
        gradient.SetStop(0, low, -1f);
        gradient.SetStop(1, high, 1f);
        sky.Add(gradient, "light", visibility: visibility);
        return sky;
    }
    private static SdfSkyLayer[] Pack(SdfSky sky, out SdfSkyBlock block) {
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];
        sky.Pack(SdfLights.Default(), new SdfSkyDetails(), out block, layers);
        return layers;
    }
    private static Vector3[] Project(SdfSky sky) {
        var layers = Pack(sky, out var block);
        var map = new Vector3[SdfSkyEnvironment.Texels];
        var coefficients = new Vector3[9];
        SdfSkyEnvironment.Render(block, layers, map);
        SdfSkyEnvironment.Project(map, coefficients);
        return coefficients;
    }
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-6f) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0f, tolerance);

    [Fact]
    public void AConstantSkyHasNoHigherBandsAndItsIrradianceIsPiTimesRadiance() {
        var color = new Vector3(.5f, .25f, 1f);
        var coefficients = Project(Sky(color, color));
        Assert.All(coefficients.Skip(1), c => Assert.Equal(Vector3.Zero, c));
        foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(Vector3.One) }) {
            Near(MathF.PI * color, SdfSkyEnvironment.Irradiance(coefficients, normal));
        }
    }

    [Fact]
    public void TwoColourSkyMatchesItsAnalyticCosineIntegralWithinOneThousandth() {
        var low = new Vector3(.125f, .25f, .5f);
        var high = new Vector3(1f, .5f, .25f);
        var coefficients = Project(Sky(low, high));
        for (var i = 0; i <= 100; i++) {
            var y = i / 50f - 1f;
            var normal = new Vector3(MathF.Sqrt(MathF.Max(0f, 1f - y * y)), y, 0f);
            var analytic = MathF.PI * ((low + high) * .5f + (high - low) * (y / 3f));
            Near(analytic, SdfSkyEnvironment.Irradiance(coefficients, normal), .001f);
        }
    }

    [Fact]
    public void CameraOnlyAddsNoLightAndLightingOnlyAddsLightWithoutCameraRuns() {
        var camera = Sky(Vector3.One, Vector3.One, SdfSkyVisibility.Camera);
        Near(Vector3.Zero, SdfSkyEnvironment.Irradiance(Project(camera), Vector3.UnitY));
        var lighting = Sky(Vector3.One, Vector3.One, SdfSkyVisibility.Lighting);
        Pack(lighting, out var block);
        Assert.Equal(0u, block.BaseRun);
        Assert.Equal(0u, block.UpperRuns);
        Near(new Vector3(MathF.PI), SdfSkyEnvironment.Irradiance(Project(lighting), Vector3.UnitY));
    }

    [Fact]
    public void SubCodeChangesAccumulateAgainstTheRenderedSkyAndCameraChangesDoNotProject() {
        var refresh = new SdfSkyEnvironmentRefresh();
        bool Owes(SdfSky sky) { var layers = Pack(sky, out var block); return refresh.Owes(block, layers); }
        var sky = Sky(new Vector3(.25f), new Vector3(.25f));
        Assert.True(Owes(sky));
        refresh.Rendered();
        Assert.False(Owes(sky));
        Assert.False(refresh.Projected);
        var step = SdfSkyEnvironment.DisplayCode / MathF.PI;
        Assert.False(Owes(Sky(new Vector3(.25f + .4f * step), new Vector3(.25f + .4f * step))));
        Assert.True(refresh.Skipped);
        Assert.True(Owes(Sky(new Vector3(.25f + 1.4f * step), new Vector3(.25f + 1.4f * step))));
        refresh.Rendered();
        var changedCamera = Sky(new Vector3(.25f + 1.4f * step), new Vector3(.25f + 1.4f * step));
        changedCamera.Add(new SdfSkyStars { Brightness = 10f }, "camera", visibility: SdfSkyVisibility.Camera);
        Assert.False(Owes(changedCamera));
        Assert.False(refresh.Projected);
    }

    [Fact]
    public void GainsAtZeroAndNoFogAvoidProjectionAndReenableCold() {
        var refresh = new SdfSkyEnvironmentRefresh();
        var sky = Sky(Vector3.One, Vector3.One);
        sky.Block.Ambient = sky.Block.Reflection = sky.Block.FogDensity = 0f;
        var layers = Pack(sky, out var block);
        Assert.False(refresh.Owes(block, layers));
        Assert.False(refresh.Projected);
        block.Ambient = 1f;
        Assert.True(refresh.Owes(block, layers));
        Assert.True(refresh.Projected);
    }

    [Fact]
    public void APanelIsRectangularAndLightingVisibilityControlsItsIrradiance() {
        var panel = new SdfSkyPanel { Direction = Vector3.UnitZ, Size = new Vector2(.1f, .5f), Color = Vector3.One, Intensity = 2f };
        Assert.Equal(1f, SdfSkyEnvironment.Panel(panel, Vector3.UnitZ).W);
        Assert.Equal(0f, SdfSkyEnvironment.Panel(panel, Vector3.Normalize(new Vector3(.3f, 0f, 1f))).W);
        Assert.Equal(1f, SdfSkyEnvironment.Panel(panel, Vector3.Normalize(new Vector3(0f, .3f, 1f))).W);
        var sky = Sky(Vector3.Zero, Vector3.Zero);
        var index = sky.Add(panel, "panel", blend: SdfSkyBlend.Add, visibility: SdfSkyVisibility.Lighting);
        Assert.True(SdfSkyEnvironment.Irradiance(Project(sky), Vector3.UnitZ).X > .01f);
        sky.LayerAt(index).Visibility = SdfSkyVisibility.Camera;
        Near(Vector3.Zero, SdfSkyEnvironment.Irradiance(Project(sky), Vector3.UnitZ));
    }

    [Fact]
    public void DifferenceFindsExtremaBetweenAxisNormals() {
        var coefficients = new Vector3[9];
        var zero = new Vector3[9];
        coefficients[1] = new Vector3(.1f);
        coefficients[3] = new Vector3(.1f);
        var maximum = SdfSkyEnvironment.IrradianceDifference(coefficients, zero);
        var diagonal = SdfSkyEnvironment.Irradiance(coefficients, Vector3.Normalize(new Vector3(1f, 1f, 0f))).X;
        Assert.InRange(Math.Abs(maximum - diagonal), 0d, 1e-7d);
        coefficients[4] = new Vector3(.2f);
        maximum = SdfSkyEnvironment.IrradianceDifference(coefficients, zero);
        for (var i = 0; i < 360; i++) {
            var angle = i * MathF.PI / 180f;
            var actual = SdfSkyEnvironment.Irradiance(coefficients, new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f)).X;
            Assert.True(actual <= maximum + 1e-7);
        }
    }
}
