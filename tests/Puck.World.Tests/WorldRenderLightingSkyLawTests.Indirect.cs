using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldRenderLightingSkyLawTests {
    [Fact]
    public void IndirectLightGainUsesTheExistingRecordWithoutChangingDirectLight() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(Weight: .4f) { Bounce = 0f },
                new WorldRenderLight.Point(Weight: .7f) { Bounce = 2f },
                new WorldRenderLight.Directional(Weight: .6f),
            ]),
        });
        var copy = new SdfLights();

        copy.CopyFrom(source: resolved.Lights);
        Span<SdfLight> packed = stackalloc SdfLight[SdfLights.MaxLights];

        copy.Pack(records: packed);
        var words = MemoryMarshal.Cast<SdfLight, float>(span: packed);

        Assert.Equal(expected: 48, actual: Marshal.SizeOf<SdfLight>());
        Assert.Equal(expected: 0f, actual: words[11]);
        Assert.Equal(expected: 2f, actual: words[23]);
        Assert.Equal(expected: 1f, actual: words[35]);
        Assert.Equal(expected: .4f, actual: packed[0].Weight);
        Assert.Equal(expected: .7f, actual: packed[1].Weight);
        Assert.Equal(expected: .6f, actual: packed[2].Weight);
    }
    [Fact]
    public void SectionLightKeysResolveTheIndirectGainThroughTheSharedScalarPath() {
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 1d)]),
            RenderRaw = BaseDefaults() with {
                Lighting = new WorldRenderLighting(
                Lights: [new WorldRenderLight.Directional(Weight: .4f, Name: "sun") { Bounce = .25f }],
                Clock: "day",
                Keys: [new WorldRenderLightingKey(At: 0d, Lights: new Dictionary<string, WorldRenderLight> {
                    ["sun"] = new WorldRenderLight.Directional { Bounce = 2f },
                })]
            ),
            },
        };
        using var resolver = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var resolved = resolver.Resolve(definition: definition, mirror: ClientFixtures.StateMirror(definition), revision: 0);

        Assert.Equal(expected: 2f, actual: resolved.Lights[0].Bounce);
        Assert.Equal(expected: .4f, actual: resolved.Lights[0].Weight);
    }
    [InlineData(-.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void ADirectLightRecordRefusesAnInvalidIndirectGain(float gain) {
        var lights = new SdfLights();
        var light = new SdfLight(Kind: SdfLightKind.Directional, Direction: Vector3.UnitY,
            Color: Vector3.One, Weight: .4f, Param: .1f, Shadows: false, Bounce: gain);

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => lights.Set(index: 0, light: light));
        lights.Set(index: 0, light: light with { Bounce = 0f });
        lights.Set(index: 1, light: light with { Bounce = 2f });
        Assert.Equal(expected: 0f, actual: lights[0].Bounce);
        Assert.Equal(expected: 2f, actual: lights[1].Bounce);
    }
}
