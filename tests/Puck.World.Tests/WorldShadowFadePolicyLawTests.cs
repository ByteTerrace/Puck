using Microsoft.Extensions.DependencyInjection;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A free-form session lever reaches the frame's live fade capacity. One shadow kernel and one kernel per views
/// variant serve every capacity, so no authored policy row declares pipelines of its own.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldShadowFadePolicyLawTests {
    [Fact]
    public void AFreeFormLeverReachesTheLiveFadeCapacity() {
        using var state = new TemporaryDirectory(prefix: "shadow-fade-lever-");
        var host = state.Own(owner: WorldBootHarness.Compose(presentation: WorldHostPresentation.Offscreen, stateDirectory: state,
            world: "tests/Puck.World.Canaries/shadow-slots/fixture.world.json").Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var sink = host.Services.GetRequiredService<WorldSessionLeverSink>();

        Assert.True(condition: sink.TryApply(lever: WorldSessionLevers.ShadowPolicy(preset: Preset(capacity: 2))));
        var frame = presenter.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64);

        Assert.Equal(expected: 2, actual: frame.Lights.ShadowSlots.FadeCapacity);
    }

    private static WorldQualityPreset Preset(int capacity) => new(Shadows: ShadowTier.High, AmbientOcclusion: false,
        RenderScale: 1f, ShadowLights: 2, ShadowFadeSlots: capacity, ShadowFadeTicks: ((capacity == 0) ? 0u : 30u));
}
