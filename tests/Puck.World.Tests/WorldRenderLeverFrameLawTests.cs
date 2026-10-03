using Microsoft.Extensions.DependencyInjection;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the live shading levers reach every frame the presenter dresses, read from
/// <see cref="WorldRenderSettings"/> each frame: <c>world.ao</c> is each view's ambient-occlusion lever, <c>world.shadows</c>'s
/// reach turns the soft shadows off at zero, scales their reach between, and leaves the engine's full-reach sentinel at
/// one, and <c>world.shadow-mask</c>, <c>world.shadow-march</c> and <c>world.ao-quality</c> force their fast paths on or
/// off, or leave them to the peer count, which a lone session keeps below the crowd tier. The kernels read these from the
/// pass block (<see cref="SdfFrameBlock"/>, SdfFrameBlockLawTests) and the ambient and shadow parts skip a frame whose
/// lever turns them off (the world-counters canary's pass lines).
/// </summary>
public sealed class WorldRenderLeverFrameLawTests : IDisposable {
    private const uint Display = 64;
    private const string World = "tests/Puck.Counters/counters.puck";

    private readonly TemporaryDirectory m_stateDirectory = new(prefix: "puck-render-levers-");

    private static (bool Ao, bool Shadows, float Scale, bool TileMask, bool FastMarch, bool FastAo) Levers(SdfFrame frame) {
        var quality = frame.Views[0].Quality;

        return (!quality.DisableAmbientOcclusion, !quality.DisableSoftShadows, quality.ShadowDistanceScale, quality.UseCameraTileShadowMask, quality.UseFastSoftShadowMarch, quality.UseFastAmbientOcclusion);
    }

    public void Dispose() => m_stateDirectory.Dispose();
    [Fact]
    public void EveryLiveLeverSettingReachesTheDressedFrame() {
        var host = m_stateDirectory.Own(owner: WorldBootHarness.Compose(
            presentation: WorldHostPresentation.Offscreen,
            stateDirectory: m_stateDirectory,
            world: World
        ).Build());
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
}
