using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfSkyLightingLawTests {
    private static SdfSky Sky(Vector3 low, Vector3 high, SdfSkyVisibility visibility = SdfSkyVisibility.Both) {
        var sky = new SdfSky();

        sky.ClearLayers();
        var gradient = new SdfSkyGradient { Count = 2 };

        gradient.SetStop(color: low, elevation: -1f, index: 0);
        gradient.SetStop(color: high, elevation: 1f, index: 1);
        sky.Add(gradient, "light", visibility: visibility);
        return sky;
    }
    private static SdfSkyLayer[] Pack(SdfSky sky, out SdfSkyBlock block) {
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(SdfLights.Default(), 40f, new SdfSkyDetails(), out block, layers);
        return layers;
    }
    private static Vector3[] Project(SdfSky sky) {
        var layers = Pack(block: out var block, sky: sky);
        var map = new Vector3[SdfSkyEnvironment.Texels];
        var coefficients = new Vector3[9];

        SdfSkyEnvironment.Render(block: block, layers: layers, map: map);
        SdfSkyEnvironment.Project(coefficients: coefficients, map: map);
        return coefficients;
    }
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-6f) =>
        Assert.InRange(Vector3.Distance(value1: expected, value2: actual), 0f, tolerance);

    [Fact]
    public void AConstantSkyHasNoHigherBandsAndItsIrradianceIsPiTimesRadiance() {
        var color = new Vector3(x: .5f, y: .25f, z: 1f);
        var coefficients = Project(sky: Sky(color, color));

        Assert.All(coefficients.Skip(count: 1), c => Assert.Equal(Vector3.Zero, c));
        foreach (var normal in new[] { Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(value: Vector3.One) }) {
            Near((MathF.PI * color), SdfSkyEnvironment.Irradiance(coefficients: coefficients, normal: normal));
        }
    }
    [Fact]
    public void TwoColourSkyMatchesItsAnalyticCosineIntegralWithinOneThousandth() {
        var low = new Vector3(x: .125f, y: .25f, z: .5f);
        var high = new Vector3(x: 1f, y: .5f, z: .25f);
        var coefficients = Project(sky: Sky(low, high));

        for (var i = 0; (i <= 100); i++) {
            var y = ((i / 50f) - 1f);
            var normal = new Vector3(x: MathF.Sqrt(x: MathF.Max(x: 0f, y: (1f - (y * y)))), y: y, z: 0f);
            var analytic = (MathF.PI * (((low + high) * .5f) + ((high - low) * (y / 3f))));

            Near(analytic, SdfSkyEnvironment.Irradiance(coefficients: coefficients, normal: normal), .001f);
        }
    }
    [Fact]
    public void CameraOnlyAddsNoLightAndLightingOnlyAddsLightWithoutCameraRuns() {
        var camera = Sky(Vector3.One, Vector3.One, SdfSkyVisibility.Camera);

        Near(Vector3.Zero, SdfSkyEnvironment.Irradiance(coefficients: Project(sky: camera), normal: Vector3.UnitY));
        var lighting = Sky(Vector3.One, Vector3.One, SdfSkyVisibility.Lighting);

        Pack(block: out var block, sky: lighting);
        Assert.Equal(actual: block.BaseRun, expected: 0u);
        Assert.Equal(actual: block.UpperRuns, expected: 0u);
        Near(new Vector3(value: MathF.PI), SdfSkyEnvironment.Irradiance(coefficients: Project(sky: lighting), normal: Vector3.UnitY));
    }
    [Fact]
    public void SubCodeChangesAccumulateAgainstTheRenderedSkyAndCameraChangesDoNotProject() {
        var refresh = new SdfSkyEnvironmentRefresh();

        bool Owes(SdfSky sky) { var layers = Pack(block: out var block, sky: sky); return refresh.Owes(block: block, layers: layers); }
        var sky = Sky(new Vector3(value: .25f), new Vector3(value: .25f));

        Assert.True(condition: Owes(sky: sky));
        refresh.Rendered();
        Assert.False(condition: Owes(sky: sky));
        Assert.False(condition: refresh.Projected);
        var step = (SdfSkyEnvironment.DisplayCode / MathF.PI);

        Assert.False(condition: Owes(sky: Sky(new Vector3(value: (.25f + (.4f * step))), new Vector3(value: (.25f + (.4f * step))))));
        Assert.True(condition: refresh.Skipped);
        Assert.True(condition: Owes(sky: Sky(new Vector3(value: (.25f + (1.4f * step))), new Vector3(value: (.25f + (1.4f * step))))));
        refresh.Rendered();
        var changedCamera = Sky(new Vector3(value: (.25f + (1.4f * step))), new Vector3(value: (.25f + (1.4f * step))));

        changedCamera.Add(new SdfSkyStars { Brightness = 10f }, "camera", visibility: SdfSkyVisibility.Camera);
        Assert.False(condition: Owes(sky: changedCamera));
        Assert.False(condition: refresh.Projected);
    }
    [Fact]
    public void GainsAtZeroAndNoFogAvoidProjectionAndReenableCold() {
        var refresh = new SdfSkyEnvironmentRefresh();
        var sky = Sky(Vector3.One, Vector3.One);

        sky.Block.Ambient = sky.Block.Reflection = sky.Atmosphere.FogDensity = 0f;
        var layers = Pack(block: out var block, sky: sky);

        Assert.False(condition: refresh.Owes(block: block, layers: layers));
        Assert.False(condition: refresh.Projected);
        block.Ambient = 1f;
        Assert.True(condition: refresh.Owes(block: block, layers: layers));
        Assert.True(condition: refresh.Projected);
    }
    [Fact]
    public void PhysicalLightingProjectsWithArtisticGainsDisabled() {
        var refresh = new SdfSkyEnvironmentRefresh();
        var sky = Sky(Vector3.One, Vector3.One);
        sky.Block.Ambient = sky.Block.Reflection = sky.Atmosphere.FogDensity = 0f;
        var layers = Pack(block: out var block, sky: sky);

        Assert.False(condition: refresh.Owes(block: block, layers: layers));
        Assert.True(condition: refresh.Owes(block: block, layers: layers, physical: true));
        Assert.True(condition: refresh.Projected);
        refresh.Rendered();
        Assert.False(condition: refresh.Owes(block: block, layers: layers, physical: true));
        Assert.False(condition: refresh.Projected);
    }
    [Fact]
    public void PanoramaRefreshFollowsItsAcquiredImageWithoutInventingACpuProjection() {
        var refresh = new SdfSkyEnvironmentRefresh();
        var sky = Sky(Vector3.Zero, Vector3.Zero);
        var image = sky.Add(new SdfSkyPanorama { Screen = 0, Intensity = 1f }, "live", visibility: SdfSkyVisibility.Lighting);
        var layers = Pack(block: out var block, sky: sky);

        Assert.Throws<NotSupportedException>(() => SdfSkyEnvironment.Render(block: block, layers: layers,
            map: new Vector3[SdfSkyEnvironment.Texels]));
        Assert.True(condition: refresh.Owes(block: block, layers: layers));
        Assert.False(condition: refresh.Projected);
        refresh.Rendered();
        Assert.False(condition: refresh.Owes(block: block, layers: layers));
        Assert.True(condition: refresh.Owes(block: block, layers: layers, imageChanged: true));
        Assert.False(condition: refresh.Projected);
        refresh.Rendered();
        sky.Parameters<SdfSkyPanorama>(index: image).Intensity = 2f;
        layers = Pack(block: out block, sky: sky);
        Assert.True(condition: refresh.Owes(block: block, layers: layers));
        Assert.False(condition: refresh.Projected);
        refresh.Rendered();
        Assert.False(condition: refresh.Owes(block: block, layers: layers));
    }
    [Fact]
    public void APanelIsRectangularAndLightingVisibilityControlsItsIrradiance() {
        var panel = new SdfSkyPanel { Direction = Vector3.UnitZ, Size = new Vector2(x: .1f, y: .5f), Color = Vector3.One, Intensity = 2f };

        Assert.Equal(1f, SdfSkyEnvironment.Panel(panel, Vector3.UnitZ).W);
        Assert.Equal(0f, SdfSkyEnvironment.Panel(panel, Vector3.Normalize(value: new Vector3(x: .3f, y: 0f, z: 1f))).W);
        Assert.Equal(1f, SdfSkyEnvironment.Panel(panel, Vector3.Normalize(value: new Vector3(x: 0f, y: .3f, z: 1f))).W);
        var sky = Sky(Vector3.Zero, Vector3.Zero);
        var index = sky.Add(panel, "panel", blend: SdfSkyBlend.Add, visibility: SdfSkyVisibility.Lighting);

        Assert.True(condition: (SdfSkyEnvironment.Irradiance(coefficients: Project(sky: sky), normal: Vector3.UnitZ).X > .01f));
        sky.LayerAt(index: index).Visibility = SdfSkyVisibility.Camera;
        Near(Vector3.Zero, SdfSkyEnvironment.Irradiance(coefficients: Project(sky: sky), normal: Vector3.UnitZ));
    }
    [Fact]
    public void DifferenceFindsExtremaBetweenAxisNormals() {
        var coefficients = new Vector3[9];
        var zero = new Vector3[9];

        coefficients[1] = new Vector3(value: .1f);
        coefficients[3] = new Vector3(value: .1f);
        var maximum = SdfSkyEnvironment.IrradianceDifference(coefficients: coefficients, other: zero);
        var diagonal = SdfSkyEnvironment.Irradiance(coefficients: coefficients, normal: Vector3.Normalize(value: new Vector3(x: 1f, y: 1f, z: 0f))).X;

        Assert.InRange(Math.Abs(value: (maximum - diagonal)), 0d, 1e-7d);
        coefficients[4] = new Vector3(value: .2f);
        maximum = SdfSkyEnvironment.IrradianceDifference(coefficients: coefficients, other: zero);
        for (var i = 0; (i < 360); i++) {
            var angle = ((i * MathF.PI) / 180f);
            var actual = SdfSkyEnvironment.Irradiance(coefficients: coefficients, normal: new Vector3(x: MathF.Cos(x: angle), y: MathF.Sin(x: angle), z: 0f)).X;

            Assert.True(condition: (actual <= (maximum + 1e-7)));
        }
    }
}
