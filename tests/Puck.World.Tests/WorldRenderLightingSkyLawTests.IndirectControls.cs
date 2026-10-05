using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRenderLightingSkyLawTests {
    [Fact]
    public void IndirectControlsResolveBindingsKeysAndExplicitZeroThroughTheExistingEnvironment() {
        var indirect = new WorldRenderIndirect(Sources: new WorldRenderIndirectSources(
            Lights: .125f, Emission: .25f, Screens: 0f, Sky: .5f,
            Feedback: new BindableScalar(new WorldKeyTrack<float>(clock: "day", keys: [new(At: 0, Ease: WorldEase.Linear, Value: .75f)]))),
            Bounces: 1, Apply: new WorldRenderIndirectApply(Intensity: .5f, Tint: new BindableColor("state.colors.sun"), Contact: .25f));
        var definition = Fixtures.BuildDocument().WithWorldState([ColorsRow("#FF0000")]) with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 1)]),
            RenderRaw = BaseDefaults() with { Indirect = indirect },
        };
        var restored = WorldDefinitionSerialization.Deserialize(WorldDefinitionSerialization.Serialize(definition));
        using var resolver = new WorldEnvironmentResolve(new WorldValueDomainGuard());
        var actual = resolver.Resolve(restored, 0, ClientFixtures.StateMirror(restored)).Indirect;
        Assert.Equal(new SdfIndirectGains(.125f, .25f, 0f, .5f, .75f), actual.Gains);
        Assert.Equal(SdfIndirectSources.All & ~SdfIndirectSources.Screens, actual.Gains.Sources);
        Assert.Equal(1, actual.Bounces);
        Assert.Equal(new SdfIndirectApplication(.5f, Vector3.UnitX, .25f), actual.Apply);
        var defaults = Resolve(BaseDefaults()).Indirect;
        Assert.Equal(SdfIndirectGains.One, defaults.Gains);
        Assert.Equal(SdfIndirectSources.All, defaults.Gains.Sources);
        Assert.Null(defaults.Bounces);
        Assert.Equal(SdfIndirectApplication.Default, defaults.Apply);
        Assert.Contains("screens=0", WorldLightingText.Describe(restored));
    }

    [Theory]
    [InlineData(-.125f)]
    [InlineData(1.125f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void IndirectSourceAndApplyGainsRefuseOutsideTheFiniteUnitDomain(float value) {
        WorldRenderIndirect[] controls = [
            new(Sources: new(Lights: value)), new(Sources: new(Emission: value)),
            new(Sources: new(Screens: value)), new(Sources: new(Sky: value)), new(Sources: new(Feedback: value)),
            new(Apply: new(Intensity: value)), new(Apply: new(Contact: value)),
        ];
        foreach (var control in controls) {
            var definition = Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Indirect = control } };
            Assert.False(WorldDefinitionValidator.TryValidate(definition, neighbours: null, out var reason));
            Assert.Contains("render.indirect", reason);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void IndirectDepthRefusesBeyondTheExistingTierCapacity(int depth) {
        var definition = Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Indirect = new(Bounces: depth) } };
        Assert.False(WorldDefinitionValidator.TryValidate(definition, neighbours: null, out var reason));
        Assert.Contains("render.indirect.bounces", reason);
    }
}
