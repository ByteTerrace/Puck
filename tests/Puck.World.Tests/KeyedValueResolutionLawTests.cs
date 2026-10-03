using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a keyed value resolves through one resolver wherever it is read. The validator resolves a key
/// at its own time with no live source; the client's state mirror resolves it at its clock's presented phase; the two
/// agree on every key every shipped world authors. The environment and the theme read keys through the mirror, a key
/// on an undeclared clock is refused by name, a rate integrates to the presented tick, and the environment resolves
/// again only when a clock a key reads moves.
/// </summary>
public sealed class KeyedValueResolutionLawTests {
    public static TheoryData<string> ShippedKeyedWorlds => [
        "src/Puck.World/Assets/worlds/moth-courtyard.puck",
        "tests/Puck.Parity/parity.world.json",
        "tests/Puck.Counters/sky-cycle.world.json",
        "tests/Puck.World.Canaries/sky-cycle/fixture.world.json",
    ];

    private static WorldDefinition WithRow(WorldDefinition definition, string row, double value) => definition.WithWorldState(rows: [
        .. definition.State.Where(predicate: candidate => !string.Equals(a: candidate.Name.Value, b: row, comparisonType: StringComparison.Ordinal)),
        new WorldStateRow(
            Name: CellName.Parse(candidate: row),
            Kind: CellKind.Fixed,
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: value).Value))]
        ),
    ]);
    // The document as every presentation resolves it: each keyed section expanded into the value keys it lands in.
    private static WorldDefinition Expanded(WorldDefinition definition) => (definition with {
        RenderRaw = definition.Render with {
            Lighting = WorldRenderKeys.Expand(lighting: definition.Render.Lighting),
            Sky = WorldRenderKeys.Expand(sky: definition.Render.Sky),
        },
    });
    // The engine tick at which a tick clock stands at a phase.
    private static ulong TickAt(WorldClock clock, double phase) {
        var period = WorldClocks.PeriodTicks(clock: clock);

        return ((((ulong)Math.Round(a: (phase * period))) + period) - WorldClocks.StartTicks(clock: clock));
    }
    private static WorldStateMirror MirrorAt(WorldDefinition definition, WorldClock clock, double phase) => (clock.IsStateClock
        ? ClientFixtures.StateMirror(definition: WithRow(definition: definition, row: clock.State!, value: phase))
        : ClientFixtures.StateMirror(definition: definition, engineTick: TickAt(clock: clock, phase: phase)));
    private static WorldDefinition TickClockFog(BindableScalar density) => Fixtures.BuildDocument() with {
        RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: density)])),
        TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 1d)]),
    };

    [MemberData(nameof(ShippedKeyedWorlds))]
    [Theory]
    public void Every_shipped_key_resolves_on_the_client_as_the_validator_resolves_it(string path) {
        var definition = Expanded(definition: AuthoredGameFixtures.Load(relativePath: path));
        var keyed = WorldKeyedValues.Of(definition: definition);

        Assert.NotEmpty(collection: keyed);

        foreach (var value in keyed) {
            Assert.True(condition: WorldKeyResolver.TryClock(clock: out var clock, name: value.Track.Clock, timeline: definition.Timeline));

            for (var index = 0; (index < value.Track.Count); index++) {
                var phase = (value.Track.AtOf(index: index) / clock.Span);
                var mirror = MirrorAt(clock: clock, definition: definition, phase: phase);

                // The validator's resolution at the key's own time, with no live source, and the client's through its
                // mirror at the clock's presented phase.
                switch (value.Value) {
                    case BindableScalar scalar:
                        Assert.Equal(
                            expected: WorldKeyResolver.Scalar(phase: phase, span: clock.Span, track: scalar.Keys!),
                            actual: mirror.Scalar(fallback: float.NaN, scalar: in scalar),
                            precision: 5
                        );
                        Assert.Equal(expected: scalar.Keys!.Keys[index].Value, actual: mirror.Scalar(fallback: float.NaN, scalar: in scalar), precision: 5);

                        break;
                    case BindableAngle angle:
                        Assert.Equal(
                            expected: WorldKeyResolver.Angle(phase: phase, span: clock.Span, track: angle.Value.Keys!),
                            actual: mirror.Angle(angle: in angle, fallback: float.NaN),
                            precision: 5
                        );

                        break;
                    case BindableColor color:
                        var expected = WorldKeyResolver.Color(fallback: Vector4.Zero, phase: phase, span: clock.Span, track: color.Keys!);
                        var actual = mirror.Color(color: in color, fallback: new Vector4(value: float.NaN));

                        Assert.Equal(actual: actual.X, expected: expected.X, precision: 5);
                        Assert.Equal(actual: actual.Y, expected: expected.Y, precision: 5);
                        Assert.Equal(actual: actual.Z, expected: expected.Z, precision: 5);
                        Assert.Equal(expected: color.Keys!.Keys[index].Value.Literal!.Value.X, actual: actual.X, precision: 5);

                        break;
                    case BindableDirection direction:
                        Assert.Equal(expected: WorldKeyResolver.Direction(phase: phase, span: clock.Span, track: direction.Keys!), actual: mirror.Direction(direction: in direction, fallback: Vector3.Zero));

                        break;
                    case BindableVector2 vector:
                        Assert.Equal(expected: WorldKeyResolver.Vector(phase: phase, span: clock.Span, track: vector.Keys!), actual: mirror.Vector(fallback: Vector2.Zero, vector: in vector));

                        break;
                    case BindableVector3 vector:
                        Assert.Equal(expected: WorldKeyResolver.Vector(phase: phase, span: clock.Span, track: vector.Keys!), actual: mirror.Vector(fallback: Vector3.Zero, vector: in vector));

                        break;
                    default:
                        Assert.Fail(message: $"{value.Path} is no bindable the mirror resolves.");

                        break;
                }
            }
        }
    }
    [Fact]
    public void The_courtyard_environment_toggles_between_its_night_and_day_keys() {
        var courtyard = AuthoredGameFixtures.Load(relativePath: "src/Puck.World/Assets/worlds/moth-courtyard.puck");
        var night = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(
            definition: WithRow(definition: courtyard, row: "skyMode", value: 0d),
            mirror: ClientFixtures.StateMirror(definition: WithRow(definition: courtyard, row: "skyMode", value: 0d)),
            revision: 0
        );

        Assert.Equal(expected: 0f, actual: night.Sky.Block.FogDensity);
        Assert.Equal(expected: 0f, actual: night.Sky.First<SdfSkyDisc>().Intensity);
        Assert.Equal(expected: (0xB6 / 255f), actual: night.Sky.First<SdfSkyGradient>().Stop(index: 3).Color.X, precision: 6);

        var dayDefinition = WithRow(definition: courtyard, row: "skyMode", value: 0.5d);
        var day = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(
            definition: dayDefinition,
            mirror: ClientFixtures.StateMirror(definition: dayDefinition),
            revision: 0
        );

        Assert.Equal(expected: 0.004f, actual: day.Sky.Block.FogDensity, precision: 6);
        Assert.Equal(expected: 1.5f, actual: day.Sky.First<SdfSkyDisc>().Intensity, precision: 6);
        Assert.Equal(expected: (0x3C / 255f), actual: day.Sky.First<SdfSkyGradient>().Stop(index: 3).Color.X, precision: 6);

        // A quarter of the way round the night's density holds the line between the two keys.
        var dusk = WithRow(definition: courtyard, row: "skyMode", value: 0.25d);

        Assert.Equal(
            expected: 0.002f,
            actual: new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: dusk, mirror: ClientFixtures.StateMirror(definition: dusk), revision: 0).Sky.Block.FogDensity,
            precision: 6
        );
    }
    [Fact]
    public void A_theme_colour_keyed_on_a_clock_resolves_through_the_one_path() {
        var keys = new WorldKeyTrack<BindableColor>(clock: "day", keys: [
            new WorldKey<BindableColor>(At: 0d, Ease: WorldEase.Linear, Value: new BindableColor(Raw: "#000000")),
            new WorldKey<BindableColor>(At: 0.5d, Ease: WorldEase.Linear, Value: new BindableColor(Raw: "#FFFFFF")),
        ]);
        var baseline = Fixtures.BuildDocument();
        var definition = baseline with {
            ThemeRaw = baseline.Theme with {
                Color = baseline.Theme.Color with { SurfaceBase = new BindableColor(keys: keys) },
            },
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 1d)]),
        };

        // The fixture's zeroed theme is refused for its own sizes; the keyed colour on a declared clock is not.
        _ = WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason);

        Assert.DoesNotContain(actualString: (reason ?? string.Empty), expectedSubstring: "theme.color.surfaceBase");

        var theme = new WorldThemeResolve(domains: new WorldValueDomainGuard());
        var dark = theme.Resolve(definition: definition, mirror: ClientFixtures.StateMirror(definition: definition, engineTick: EngineTicks.PerSecond), revision: 0);
        var light = theme.Resolve(definition: definition, mirror: ClientFixtures.StateMirror(definition: definition, engineTick: (EngineTicks.PerSecond / 2UL)), revision: 0);

        Assert.Equal(expected: 0f, actual: dark.Color.SurfaceBase.R);
        Assert.Equal(expected: 1f, actual: light.Color.SurfaceBase.R);

        // Red leg: the same keys on a clock the timeline does not declare are refused by name.
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: (definition with { TimelineRaw = WorldTimelineSection.Absent }),
            reason: out var refusal
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "theme.color.surfaceBase keys on clock 'day', which timeline.clocks does not declare");
    }
    [Fact]
    public void The_environment_resolves_again_only_when_a_clock_a_key_reads_moves() {
        var keyed = TickClockFog(density: new BindableScalar(keys: new WorldKeyTrack<float>(clock: "day", keys: [
            new WorldKey<float>(At: 0d, Ease: WorldEase.Linear, Value: 0f),
            new WorldKey<float>(At: 0.5d, Ease: WorldEase.Linear, Value: 0.1f),
        ])));
        var mirror = ClientFixtures.StateMirror(definition: keyed);
        var environment = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        for (var frame = 0; (frame < 4); frame++) {
            _ = environment.Resolve(definition: keyed, mirror: mirror, revision: 0);
        }

        Assert.Equal(expected: 1, actual: environment.Resolutions);

        mirror.Advance(engineTick: (EngineTicks.PerSecond / 4UL), tick: 1UL);
        mirror.Apply(fraction: 1f);

        Assert.Equal(expected: 0.05f, actual: environment.Resolve(definition: keyed, mirror: mirror, revision: 0).Sky.Block.FogDensity, precision: 6);
        Assert.Equal(expected: 2, actual: environment.Resolutions);

        // Red leg: a still literal sky never resolves again while the tick moves.
        var still = TickClockFog(density: new BindableScalar(literal: 0.1f));
        var stillMirror = ClientFixtures.StateMirror(definition: still);
        var stillEnvironment = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());

        for (var tick = 1UL; (tick < 5UL); tick++) {
            stillMirror.Advance(engineTick: (tick * 1680UL), tick: tick);
            stillMirror.Apply(fraction: 1f);
            _ = stillEnvironment.Resolve(definition: still, mirror: stillMirror, revision: 0);
        }

        Assert.Equal(expected: 1, actual: stillEnvironment.Resolutions);
    }
    [Fact]
    public void The_environment_integrates_every_cloud_rate_and_the_twinkle_to_the_presented_tick() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [
                new WorldRenderSkyLayer.Stars(Brightness: 1f, Density: 48f, Twinkle: new WorldRenderSkyTwinkle(Depth: 0.5f, Rate: 2f, Share: 0.5f)),
                new WorldRenderSkyLayer.Clouds(Coverage: 0.5f, Drift: new Vector2(x: 0.02f, y: -0.01f), Shear: new Vector2(x: 0.005f, y: 0.003f), Spin: 0.1f),
            ])),
        };
        const ulong Wrap = (1UL << 32);
        var before = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: definition, mirror: ClientFixtures.StateMirror(definition: definition, engineTick: (Wrap - 1UL)), revision: 0);
        var after = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: definition, mirror: ClientFixtures.StateMirror(definition: definition, engineTick: Wrap), revision: 0);
        (float Before, float After, double Rate, double Modulus)[] lanes = [
            (before.Sky.First<SdfSkyClouds>().DriftOffset.X, after.Sky.First<SdfSkyClouds>().DriftOffset.X, 0.02d, SdfVolume.NoisePeriodCells),
            (before.Sky.First<SdfSkyClouds>().DriftOffset.Y, after.Sky.First<SdfSkyClouds>().DriftOffset.Y, -0.01d, SdfVolume.NoisePeriodCells),
            (before.Sky.First<SdfSkyClouds>().ShearOffset.X, after.Sky.First<SdfSkyClouds>().ShearOffset.X, 0.005d, SdfVolume.NoisePeriodCells),
            (before.Sky.First<SdfSkyClouds>().ShearOffset.Y, after.Sky.First<SdfSkyClouds>().ShearOffset.Y, 0.003d, SdfVolume.NoisePeriodCells),
            (before.Sky.First<SdfSkyClouds>().SpinAngle, after.Sky.First<SdfSkyClouds>().SpinAngle, 0.1d, Math.Tau),
            (before.Sky.First<SdfSkyStars>().TwinklePhase, after.Sky.First<SdfSkyStars>().TwinklePhase, 2d, 1d),
        ];

        // Across 2^32 engine ticks each offset moves by one tick's worth of its rate.
        foreach (var (first, second, rate, modulus) in lanes) {
            var step = Math.IEEERemainder(x: (second - ((double)first)), y: modulus);

            Assert.InRange(actual: Math.Abs(value: (step - (((double)((float)rate)) / EngineTicks.PerSecond))), high: 1e-3d, low: 0d);
        }

        // Red leg: with no star brightness nothing twinkles, and the phase holds zero so a still sky can stand.
        var dark = definition with {
            RenderRaw = new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [
                new WorldRenderSkyLayer.Stars(Brightness: 0f, Twinkle: new WorldRenderSkyTwinkle(Depth: 0.5f, Rate: 2f, Share: 0.5f)),
            ])),
        };

        Assert.Equal(expected: 0f, actual: new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: dark, mirror: ClientFixtures.StateMirror(definition: dark, engineTick: (Wrap + 1234UL)), revision: 0).Sky.First<SdfSkyStars>().TwinklePhase);
    }
}
