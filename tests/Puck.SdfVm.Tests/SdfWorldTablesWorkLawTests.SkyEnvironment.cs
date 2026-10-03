using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The sky's environment refreshes only on change: the upload renders the map and its coefficients when the sky's gradient
// moves while the fog reads it, and otherwise records nothing, its pass reading skipped. A still sky renders once; a body,
// the stars, the twinkle, the fog's density and the clouds leave the map as it is; a stop renders it again; an unfogged
// sky renders nothing until its fog reads the map; and an installed kernel reload renders it again.
public sealed partial class SdfWorldTablesWorkLawTests {
    [Fact]
    public void AStillSkyRendersItsEnvironmentOnceAndThenRecordsNothing() {
        using var rig = new Rig();

        for (var upload = 0; (upload < 4); upload++) {
            rig.Render();
        }

        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
        Assert.Contains(actualString: rig.Report(), expectedSubstring: "\nwork environment skipped\n");
    }
    [Fact]
    public void OnlyAMoveOfTheGradientTheFogReadsRendersTheEnvironmentAgain() {
        using var rig = new Rig();
        var sky = rig.Frame.Sky;

        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // Everything but the gradient: the twinkle's phase, the stars, a disc, the fog's density, the studio horizon and a
        // cloud layer, covering the sky and drifting.
        sky.Block.TwinklePhase = 0.25f;
        sky.Block.StarBrightness = 2f;
        sky.Block.DiscLight = 0;
        sky.Block.FogDensity = (2f * SdfSky.DefaultFogDensity);
        sky.Block.HorizonLow = Vector3.One;
        sky.Block.CloudCoverage = 0.5f;
        rig.Render();
        sky.Block.CloudDriftOffset = new Vector2(x: 3f, y: 1f);
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        // A stop moves the gradient.
        sky.SetStop(index: 1, stop: new SdfSkyStop(Color: new Vector3(x: 0.2f, y: 0.4f, z: 0.9f), Elevation: 1f));
        rig.Render();
        Assert.Equal(expected: 2L, actual: rig.Engine.SkyEnvironmentRenders);

        // A stop in use beyond the last moves it; one past the stops in use does not.
        sky.StopCount = 3;
        sky.SetStop(index: 2, stop: new SdfSkyStop(Color: Vector3.One, Elevation: 1f));
        rig.Render();
        sky.SetStop(index: 3, stop: new SdfSkyStop(Color: Vector3.Zero, Elevation: 1f));
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 3L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    [Fact]
    public void AnUnfoggedSkyRendersNoEnvironmentUntilItsFogReadsIt() {
        using var rig = new Rig();

        rig.Frame.Sky.Block.FogDensity = 0f;
        rig.Render();
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 0L, actual: rig.Engine.SkyEnvironmentRenders);
        Assert.Contains(actualString: rig.Report(), expectedSubstring: "\nwork environment skipped\n");

        rig.Frame.Sky.Block.FogDensity = SdfSky.DefaultFogDensity;
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    [Fact]
    public void AnInstalledKernelReloadRendersTheEnvironmentAgain() {
        using var rig = new Rig();

        rig.Render();
        Assert.Equal(expected: 0, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 1)));
        rig.Render();
        Assert.Equal(expected: 1L, actual: rig.Engine.SkyEnvironmentRenders);

        Assert.Equal(expected: 1, actual: rig.Reload(kernels: SdfTestPipelines.Kernels(beam: 2)));
        rig.Render();
        rig.Render();
        Assert.Equal(expected: 2L, actual: rig.Engine.SkyEnvironmentRenders);
    }
    [Fact]
    public void TheEnvironmentKeepsOneMapAndOneSetOfCoefficients() {
        // A 64 by 64 map of four half floats a texel and nine four-float coefficients: one pair a residency, however many
        // views read it.
        Assert.Equal(
            actual: (SdfSkyEnvironment.MapBytes, SdfSkyEnvironment.CoefficientBytes, SdfWorldTables.SkyEnvironmentBytes),
            expected: (32_768, 144, 32_912)
        );
        // Its kernel counters: a counter and a readback buffer a ring slot, a row an upload pass.
        Assert.Equal(expected: 96, actual: (SdfWorldTables.PassLabels.Length * GpuKernelCounters.RowBytes));
    }
}
