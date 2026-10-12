using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Presentation.Tests;

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
        "tests/Puck.Parity/parity.puck",
        "tests/Puck.Counters/sky-cycle.puck",
        "tests/Puck.World.Canaries/sky-cycle/fixture.puck",
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

        Assert.Equal(expected: 0f, actual: night.Sky.Atmosphere.FogDensity);
        Assert.Equal(expected: 0f, actual: night.Sky.First<SdfSkyDisc>().Intensity);
        Assert.Equal(expected: (0xB6 / 255f), actual: night.Sky.First<SdfSkyGradient>().Stop(index: 3).Color.X, precision: 6);

        var dayDefinition = WithRow(definition: courtyard, row: "skyMode", value: 0.5d);
        var day = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(
            definition: dayDefinition,
            mirror: ClientFixtures.StateMirror(definition: dayDefinition),
            revision: 0
        );

        Assert.Equal(expected: 0.004f, actual: day.Sky.Atmosphere.FogDensity, precision: 6);
        Assert.Equal(expected: 1.5f, actual: day.Sky.First<SdfSkyDisc>().Intensity, precision: 6);
        Assert.Equal(expected: (0x3C / 255f), actual: day.Sky.First<SdfSkyGradient>().Stop(index: 3).Color.X, precision: 6);

        // A quarter of the way round the night's density holds the line between the two keys.
        var dusk = WithRow(definition: courtyard, row: "skyMode", value: 0.25d);

        Assert.Equal(
            expected: 0.002f,
            actual: new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: dusk, mirror: ClientFixtures.StateMirror(definition: dusk), revision: 0).Sky.Atmosphere.FogDensity,
            precision: 6
        );
    }
}
