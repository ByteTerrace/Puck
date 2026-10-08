using Puck.Abstractions.Sources;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>A cached look follows the presented values of its own dependencies, including interpolation between
/// deliveries. Repeating the same presentation performs no keyed resolution.</summary>
public sealed class KeyedDependencyGateLawTests {
    [InlineData(1L)]
    [InlineData(-1L)]
    [Theory]
    public void A_state_clock_moves_the_environment_and_theme_between_deliveries(long rate) {
        var baseline = Fixtures.BuildDocument();
        var definition = (baseline with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", State: "phase")]),
            RenderRaw = new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(
                Density: new BindableScalar(keys: new WorldKeyTrack<float>(clock: "day", keys: [
                    new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f),
                    new WorldKey<float>(At: 0.5d, Ease: WorldEase.Linear, Value: 0.1f),
                ]))
            ))),
            ThemeRaw = baseline.Theme with {
                Color = baseline.Theme.Color with {
                    SurfaceBase = new BindableColor(keys: new WorldKeyTrack<BindableColor>(clock: "day", keys: [
                        new WorldKey<BindableColor>(At: 0d, Value: new BindableColor(Raw: "#000000"), Ease: WorldEase.Linear),
                        new WorldKey<BindableColor>(At: 0.5d, Value: new BindableColor(Raw: "#FFFFFF"), Ease: WorldEase.Linear),
                    ])),
                },
            },
        }).WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: "phase"),
            Kind: CellKind.Fixed,
            Advance: new StateAdvance(PerSecondDenominator: 1L, PerSecondNumerator: rate),
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: 0L))]
        )]);
        var mirror = ClientFixtures.StateMirror(definition: definition);
        var environment = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var theme = new WorldThemeResolve(domains: new WorldValueDomainGuard());

        mirror.Advance(engineTick: (EngineTicks.PerSecond / 4UL), tick: 1UL);

        var resolutions = 0;

        foreach (var fraction in new[] { 0f, 0.25f, 0.75f, 1f, 0.25f }) {
            mirror.Apply(fraction: fraction);
            Assert.Equal(expected: (0.05f * fraction), actual: environment.Resolve(definition: definition, mirror: mirror, revision: 0).Sky.Atmosphere.FogDensity, precision: 6);
            Assert.Equal(
                expected: ((float)ImageSourceConversion.LinearToSrgb(value: (fraction / 2d))),
                actual: theme.Resolve(definition: definition, mirror: mirror, revision: 0).Color.SurfaceBase.R,
                precision: 6
            );
            resolutions++;
            Assert.Equal(expected: resolutions, actual: environment.Resolutions);
            Assert.Equal(expected: resolutions, actual: theme.Resolutions);

            var keyed = mirror.KeyedResolutionCount;

            mirror.Apply(fraction: fraction);
            _ = environment.Resolve(definition: definition, mirror: mirror, revision: 0);
            _ = theme.Resolve(definition: definition, mirror: mirror, revision: 0);
            Assert.Equal(expected: resolutions, actual: environment.Resolutions);
            Assert.Equal(expected: resolutions, actual: theme.Resolutions);
            Assert.Equal(expected: keyed, actual: mirror.KeyedResolutionCount);
        }
    }

}
