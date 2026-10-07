using Microsoft.Extensions.DependencyInjection;
using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The host and followed worlds declare only the fade capacities of their current authored policy rows.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldShadowFadePolicyLawTests {
    [InlineData(0, 0, 0, 0, SdfShadowFadeVariants.None)]
    [InlineData(1, 0, 0, 0, SdfShadowFadeVariants.One)]
    [InlineData(0, 1, 0, 0, SdfShadowFadeVariants.One)]
    [InlineData(0, 0, 1, 0, SdfShadowFadeVariants.One)]
    [InlineData(0, 0, 0, 1, SdfShadowFadeVariants.One)]
    [InlineData(2, 1, 0, 2, SdfShadowFadeVariants.One | SdfShadowFadeVariants.Two)]
    [Theory]
    public void BootAndEveryPresetReachHostAndSessionFrames(int boot, int low, int medium, int high, SdfShadowFadeVariants expected) {
        var render = new WorldRenderDefaults(ShadowFadeSlots: boot, ShadowFadeTicks: ((boot == 0) ? 0u : 30u),
            LowRaw: Preset(capacity: low), MediumRaw: Preset(capacity: medium), HighRaw: Preset(capacity: high));
        using var state = new TemporaryDirectory(prefix: "s60b-fix3-shadow-policy-");
        var host = state.Own(owner: WorldBootHarness.Compose(presentation: WorldHostPresentation.Offscreen, stateDirectory: state,
            world: "tests/Puck.World.Canaries/shadow-slots/fixture.world.json", edit: definition => definition with { RenderRaw = render }).Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();

        Assert.Equal(expected: expected, actual: presenter.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64).ShadowFadeVariants);

        var definition = host.Services.GetRequiredService<WorldClient>().Definition;
        using var session = new WorldSessionSceneEmitter(domains: new WorldValueDomainGuard(), effectiveCameraName: null, mirror: new WorldSessionMirror(placeholder: definition));
        var source = new SdfCompositionFrameSource(dresser: session, emitters: [session]);

        Assert.Equal(expected: expected, actual: source.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64).ShadowFadeVariants);
    }
    [Fact]
    public void DefinitionDeliveryChangesReachableRowsAndAFreeFormLeverChangesOnlyTheLiveDemand() {
        using var state = new TemporaryDirectory(prefix: "s60b-fix3-shadow-delivery-");
        var host = state.Own(owner: WorldBootHarness.Compose(presentation: WorldHostPresentation.Offscreen, stateDirectory: state,
            world: "tests/Puck.World.Canaries/shadow-slots/fixture.world.json").Build());
        var presenter = host.Services.GetRequiredService<WorldFramePresenter>();
        var client = host.Services.GetRequiredService<WorldClient>();
        var sink = host.Services.GetRequiredService<WorldSessionLeverSink>();
        var mirror = new WorldSessionMirror(placeholder: client.Definition);
        using var session = new WorldSessionSceneEmitter(domains: new WorldValueDomainGuard(), effectiveCameraName: null, mirror: mirror);
        var source = new SdfCompositionFrameSource(dresser: session, emitters: [session]);

        foreach (var capacity in new[] { 0, 1, 2, 0 }) {
            var definition = client.Definition with { RenderRaw = client.Definition.Render with { HighRaw = Preset(capacity: capacity) } };

            client.DeliverDefinition(definition: definition, version: default);
            mirror.DeliverDefinition(definition: definition, version: default);
            var expected = capacity switch { 1 => SdfShadowFadeVariants.One, 2 => SdfShadowFadeVariants.Two, _ => SdfShadowFadeVariants.None };

            Assert.Equal(expected: expected, actual: presenter.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64).ShadowFadeVariants);
            Assert.Equal(expected: expected, actual: source.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64).ShadowFadeVariants);
        }
        Assert.True(condition: sink.TryApply(lever: WorldSessionLevers.ShadowPolicy(preset: Preset(capacity: 2))));
        var frame = presenter.CaptureFrame(deltaSeconds: 0f, height: 64, interpolationAlpha: 0f, width: 64);

        Assert.Equal(expected: SdfShadowFadeVariants.None, actual: frame.ShadowFadeVariants);
        Assert.Equal(expected: 2, actual: frame.Lights.ShadowSlots.FadeCapacity);
    }

    private static WorldQualityPreset Preset(int capacity) => new(Shadows: ShadowTier.High, AmbientOcclusion: false,
        RenderScale: 1f, ShadowLights: 2, ShadowFadeSlots: capacity, ShadowFadeTicks: ((capacity == 0) ? 0u : 30u));
}
