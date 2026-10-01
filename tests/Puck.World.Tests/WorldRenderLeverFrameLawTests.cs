using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the live shading levers reach every frame the presenter dresses, read from
/// <see cref="WorldRenderSettings"/> each frame: <c>world.ao</c> is each view's ambient-occlusion lever, <c>world.shadows</c>'s
/// reach turns the soft shadows off at zero, scales their reach between, and leaves the engine's full-reach sentinel at
/// one, and <c>world.shadow-mask</c>, <c>world.shadow-march</c> and <c>world.ao-quality</c> force their fast paths on or
/// off, or leave them to the peer count, which a lone session keeps below the crowd tier. The kernels read these from the
/// pass block (<see cref="SdfFrameBlock"/>, SdfFrameBlockLawTests) and the ambient and shadow parts skip a frame whose
/// lever turns them off (the world-counters canary's pass lines). <c>world.march-seed</c> reaches each view the same way,
/// off until asked, and the counters workload's panning leg turns its camera every tick.
/// </summary>
public sealed class WorldRenderLeverFrameLawTests : IDisposable {
    private const uint Display = 64;
    private const string PanningWorld = "tests/Puck.Counters/counters-pan.world.json";
    private const string World = "tests/Puck.Counters/counters.world.json";

    private readonly TemporaryDirectory m_stateDirectory = new(prefix: "puck-render-levers-");

    private static (bool Ao, bool Shadows, float Scale, bool TileMask, bool FastMarch, bool FastAo) Levers(SdfFrame frame) {
        var quality = frame.Views[0].Quality;

        return (!quality.DisableAmbientOcclusion, !quality.DisableSoftShadows, quality.ShadowDistanceScale, quality.UseCameraTileShadowMask, quality.UseFastSoftShadowMarch, quality.UseFastAmbientOcclusion);
    }

    public void Dispose() => m_stateDirectory.Dispose();
    [Fact]
    public void EveryLiveLeverSettingReachesTheDressedFrame() {
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();

        SdfFrame Dress() => presenter.CaptureFrame(
            deltaSeconds: 0f,
            height: Display,
            interpolationAlpha: 1f,
            width: Display
        );

        settings.AmbientOcclusion = true;
        settings.ShadowReach = 1f;
        settings.ShadowMask = ShadowMaskMode.Auto;
        settings.ShadowMarch = ShadowMarchMode.Auto;
        settings.AmbientOcclusionQuality = AmbientOcclusionMode.Auto;
        Assert.Equal(actual: Levers(frame: Dress()), expected: (true, true, 0f, false, false, false));

        settings.ShadowReach = 0.5f;
        Assert.Equal(actual: Levers(frame: Dress()), expected: (true, true, 0.5f, false, false, false));

        settings.AmbientOcclusion = false;
        settings.ShadowReach = 0f;
        Assert.Equal(actual: Levers(frame: Dress()), expected: (false, false, 0f, false, false, false));

        settings.ShadowMask = ShadowMaskMode.CameraTile;
        settings.ShadowMarch = ShadowMarchMode.Fast;
        settings.AmbientOcclusionQuality = AmbientOcclusionMode.Fast;
        Assert.Equal(actual: Levers(frame: Dress()), expected: (false, false, 0f, true, true, true));

        settings.ShadowMask = ShadowMaskMode.ExactGather;
        settings.ShadowMarch = ShadowMarchMode.Exact;
        settings.AmbientOcclusionQuality = AmbientOcclusionMode.Exact;
        Assert.Equal(actual: Levers(frame: Dress()), expected: (false, false, 0f, false, false, false));
    }
    // March seeding is a view lever like the shading levers: off until asked, it reaches each dressed view whether or not
    // the view resolves temporally, and only a temporal view, whose resolve keeps the history surface, seeds from it.
    [Fact]
    public void MarchSeedingReachesTheDressedFrameOnlyWhenAsked() {
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var settings = host.Services.GetRequiredService<WorldRenderSettings>();

        SdfViewQuality Dress() => presenter.CaptureFrame(
            deltaSeconds: 0f,
            height: Display,
            interpolationAlpha: 1f,
            width: Display
        ).Views[0].Quality;

        Assert.False(condition: settings.MarchSeed);
        Assert.False(condition: Dress().MarchSeed);

        settings.Temporal = true;
        settings.MarchSeed = true;
        Assert.Equal(actual: (Dress().Temporal, Dress().MarchSeed), expected: (true, true));

        settings.MarchSeed = false;
        Assert.Equal(actual: (Dress().Temporal, Dress().MarchSeed), expected: (true, false));
    }
    // The counters workload's panning leg orbits its camera about the blocks by a state the workload advances every tick, so
    // every frame the collector reads moves the camera the history reprojects through; the still leg's camera never moves.
    [InlineData(PanningWorld, true)]
    [InlineData(World, false)]
    [Theory]
    public void ThePanningWorkloadTurnsItsCameraEveryTick(string world, bool pans) {
        using var host = WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: world
        ).Build();
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var server = host.Services.GetRequiredService<WorldServer>();

        Vector3 Forward() => presenter.CaptureFrame(
            deltaSeconds: 0f,
            height: Display,
            interpolationAlpha: 1f,
            width: Display
        ).Views[0].Camera.Forward;

        var steps = Fixtures.StepTicksAt(rateHz: server.Definition.SimulationRateHz);

        server.Advance(stepTicks: steps);
        var before = Forward();

        server.Advance(stepTicks: steps);
        var after = Forward();

        Assert.Equal(actual: (Vector3.Distance(value1: before, value2: after) > 1e-3f), expected: pans);
    }
}
