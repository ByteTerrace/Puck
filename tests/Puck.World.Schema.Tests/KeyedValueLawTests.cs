using System.Numerics;
using System.Text.Json;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Keys resolve by field type and clock phase, with no renderer or accumulated frame state.</summary>
public sealed class KeyedValueLawTests {
    private static WorldDefinition World(params WorldClock[] clocks) => new(TimelineRaw: new WorldTimelineSection(Clocks: ((clocks.Length == 0)
        ? [new WorldClock("day", PeriodSeconds: 8d, SpanSeconds: 24d)] : clocks)));
    private static WorldValueResolver At(double seconds, WorldDefinition? world = null) => new((world ?? World()), new PresentedTick(Fraction: 0d, Whole: ((ulong)(seconds * EngineTicks.PerSecond))));
    private static WorldKeys<T> Keys<T>(T a, T b, WorldKeyEase ease = WorldKeyEase.Linear) => new(Clock: "day", Keys: [new WorldKey<T>(At: 0d, Ease: ease, Value: a), new WorldKey<T>(12d, b)]);

    [InlineData(WorldKeyEase.Linear, 0.25d)]
    [InlineData(WorldKeyEase.Smooth, 0.15625d)]
    [InlineData(WorldKeyEase.Step, 0d)]
    [Theory]
    public void A_key_shapes_time_before_the_scalar_color_and_vector_blends(WorldKeyEase ease, double weight) {
        var resolver = At(1d);

        Assert.Equal((2d + (8d * weight)), resolver.Scalar(new BindableScalar(keys: Keys<BindableScalar>(a: 2f, b: 10f, ease: ease)), -1d), 6);
        var color = resolver.Color(new BindableColor(keys: Keys(a: new BindableColor(Raw: "#00000000"), b: new BindableColor(Raw: "#FFFFFF80"), ease: ease)), Vector4.One);

        Assert.Equal(actual: color.X, expected: weight, precision: 6);
        Assert.Equal(actual: color.W, expected: (weight * (128d / 255d)), precision: 6);
        var vector = resolver.Vector(new BindableVector3(keys: Keys<BindableVector3>(a: new Vector3(x: 2f, y: 4f, z: 6f), b: new Vector3(x: 10f, y: 12f, z: 14f), ease: ease)), Vector3.Zero);

        Assert.Equal(actual: vector.Y, expected: (4d + (8d * weight)), precision: 6);
        // The last-to-first segment uses the same clock span, including ticks far beyond a 32-bit counter.
        var wrapped = new WorldValueResolver(World(), new PresentedTick(Fraction: 0d, Whole: (((8UL * EngineTicks.PerSecond) * 20000UL) + (7UL * EngineTicks.PerSecond))));

        Assert.Equal(4d, wrapped.Scalar(new BindableScalar(keys: Keys<BindableScalar>(2f, 10f)), -1d), 6);
    }
    [Fact]
    public void Angles_cross_zero_by_the_short_arc_and_directions_stay_on_the_great_circle() {
        var angle = new BindableAngle(Value: new BindableScalar(keys: Keys<BindableScalar>(((350f * MathF.PI) / 180f), ((10f * MathF.PI) / 180f))));

        Assert.InRange(Math.Abs(value: Math.IEEERemainder(x: At(2d).Angle(fallback: -1d, value: angle), y: Math.Tau)), 0d, 0.000001d);
        var direction = new BindableDirection(keys: Keys<BindableDirection>(Vector3.UnitX, Vector3.UnitY));
        var quarter = At(1d).Direction(direction, Vector3.Zero);

        Assert.Equal(Math.Cos(d: (Math.PI / 8d)), quarter.X, 6);
        Assert.Equal(Math.Sin(a: (Math.PI / 8d)), quarter.Y, 6);
        Assert.Equal(1d, quarter.Length(), 6);
        var antipode = At(2d).Direction(new BindableDirection(keys: Keys<BindableDirection>(Vector3.UnitX, -Vector3.UnitX)), Vector3.Zero);

        Assert.Equal(1d, antipode.Length(), 6);
        Assert.InRange(Math.Abs(value: Vector3.Dot(vector1: antipode, vector2: Vector3.UnitX)), 0f, 0.000001f);
    }
    [InlineData(float.Epsilon)]
    [InlineData(float.MaxValue)]
    [Theory]
    public void Any_finite_nonzero_direction_normalizes_without_squaring_in_float(float component) {
        BindableDirection direction = new Vector3(x: component, y: component, z: 0f);

        Assert.True(condition: direction.IsAuthorable(definition: World()));
        var actual = At(0d).Direction(direction, Vector3.Zero);

        Assert.Equal(Math.Sqrt(d: 0.5d), actual.X, 6);
        Assert.Equal(Math.Sqrt(d: 0.5d), actual.Y, 6);
    }
    [Fact]
    public void A_keyed_pair_round_trips_and_uses_the_same_component_blend() {
        var pair = new BindableVector2(keys: Keys<BindableVector2>(new Vector2(x: 2f, y: 4f), new Vector2(x: 10f, y: 20f)));
        var options = WorldJsonContext.Default.Options;
        var copy = JsonSerializer.Deserialize<BindableVector2>(json: JsonSerializer.Serialize(options: options, value: pair), options: options);

        Assert.Equal(new Vector2(x: 4f, y: 8f), At(1d).Vector(copy, Vector2.Zero));
        Assert.True(condition: copy.IsAuthorable(definition: World()));
    }
    [Fact]
    public void A_phase_curve_reads_its_parent_clock_and_clock_cycles_are_refused_by_name() {
        var parent = new WorldClock("day", PeriodSeconds: 8d, SpanSeconds: 24d);
        var phase = new BindableScalar(keys: Keys<BindableScalar>(a: 0.2f, b: 0.8f, ease: WorldKeyEase.Smooth));
        var child = new WorldClock("night", Phase: phase);
        var world = World(parent, child);

        Assert.True(condition: WorldValueValidation.TryValidateClock(clock: child, definition: world, reason: out var reason), userMessage: reason);
        Assert.Equal(0.29375d, At(seconds: 1d, world: world).Phase(clock: child), 6);
        var cycle = parent with { PeriodSeconds = null, Phase = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "night", Keys: [new(0d, 0f), new(0.5d, 1f)])) };

        Assert.False(condition: WorldValueValidation.TryValidateClock(clock: child, definition: World(cycle, child), reason: out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "clock 'night' forms a cycle");
    }
    [Fact]
    public void Bindable_wire_round_trips_keep_keys_and_refuse_unknown_structure() {
        var scalar = new BindableScalar(keys: Keys<BindableScalar>(a: 2f, b: 10f, ease: WorldKeyEase.Smooth));
        var options = WorldJsonContext.Default.Options;
        var json = JsonSerializer.Serialize(options: options, value: scalar);
        var copy = JsonSerializer.Deserialize<BindableScalar>(json: json, options: options);

        Assert.Equal(At(1d).Scalar(fallback: -1d, value: scalar), At(1d).Scalar(fallback: -2d, value: copy));
        var direction = new BindableDirection(keys: Keys<BindableDirection>(Vector3.UnitX, Vector3.UnitY));
        var directionCopy = JsonSerializer.Deserialize<BindableDirection>(json: JsonSerializer.Serialize(options: options, value: direction), options: options);

        Assert.Equal(At(1d).Direction(direction, Vector3.Zero), At(1d).Direction(directionCopy, Vector3.Zero));
    }
    [Fact]
    public void A_keyed_value_names_an_existing_clock() => Assert.False(condition: new BindableColor(keys: new WorldKeys<BindableColor>(Clock: "missing",
        Keys: [new(0d, new(Raw: "#000000")), new(0.5d, new(Raw: "#FFFFFF"))])).IsAuthorable(definition: World()));
    [Fact]
    public void A_keyed_scalar_cannot_smuggle_a_structural_seed() => Assert.Throws<JsonException>(testCode: () =>
        JsonSerializer.Deserialize<BindableScalar>(json: "{\"clock\":\"day\",\"seed\":2,\"keys\":[]}", options: WorldJsonContext.Default.Options));
}
