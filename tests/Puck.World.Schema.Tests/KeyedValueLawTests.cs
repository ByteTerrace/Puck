using System.Numerics;
using System.Text.Json;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Keys resolve by field type and clock phase, with no renderer or accumulated frame state.</summary>
public sealed class KeyedValueLawTests {
    private static WorldDefinition World(params WorldClock[] clocks) => new(TimelineRaw: new WorldTimelineSection(clocks.Length == 0
        ? [new WorldClock("day", PeriodSeconds: 8d, SpanSeconds: 24d)] : clocks));
    private static WorldValueResolver At(double seconds, WorldDefinition? world = null) => new(world ?? World(), new PresentedTick((ulong)(seconds * EngineTicks.PerSecond), 0d));
    private static WorldKeys<T> Keys<T>(T a, T b, WorldKeyEase ease = WorldKeyEase.Linear) => new("day", [new WorldKey<T>(0d, a, ease), new WorldKey<T>(12d, b)]);

    [Theory]
    [InlineData(WorldKeyEase.Linear, 0.25d)]
    [InlineData(WorldKeyEase.Smooth, 0.15625d)]
    [InlineData(WorldKeyEase.Step, 0d)]
    public void A_key_shapes_time_before_the_scalar_color_and_vector_blends(WorldKeyEase ease, double weight) {
        var resolver = At(1d);
        Assert.Equal(2d + (8d * weight), resolver.Scalar(new BindableScalar(Keys<BindableScalar>(2f, 10f, ease)), -1d), 6);
        var color = resolver.Color(new BindableColor(Keys(new BindableColor("#00000000"), new BindableColor("#FFFFFF80"), ease)), Vector4.One);
        Assert.Equal(weight, color.X, 6);
        Assert.Equal(weight * (128d / 255d), color.W, 6);
        var vector = resolver.Vector(new BindableVector3(Keys<BindableVector3>(new Vector3(2f, 4f, 6f), new Vector3(10f, 12f, 14f), ease)), Vector3.Zero);
        Assert.Equal(4d + (8d * weight), vector.Y, 6);
        // The last-to-first segment uses the same clock span, including ticks far beyond a 32-bit counter.
        var wrapped = new WorldValueResolver(World(), new PresentedTick((8UL * EngineTicks.PerSecond * 20000UL) + (7UL * EngineTicks.PerSecond), 0d));
        Assert.Equal(4d, wrapped.Scalar(new BindableScalar(Keys<BindableScalar>(2f, 10f)), -1d), 6);
    }

    [Fact]
    public void Angles_cross_zero_by_the_short_arc_and_directions_stay_on_the_great_circle() {
        var angle = new BindableAngle(new BindableScalar(Keys<BindableScalar>(350f * MathF.PI / 180f, 10f * MathF.PI / 180f)));
        Assert.InRange(Math.Abs(Math.IEEERemainder(At(2d).Angle(angle, -1d), Math.Tau)), 0d, 0.000001d);
        var direction = new BindableDirection(Keys<BindableDirection>(Vector3.UnitX, Vector3.UnitY));
        var quarter = At(1d).Direction(direction, Vector3.Zero);
        Assert.Equal(Math.Cos(Math.PI / 8d), quarter.X, 6);
        Assert.Equal(Math.Sin(Math.PI / 8d), quarter.Y, 6);
        Assert.Equal(1d, quarter.Length(), 6);
        var antipode = At(2d).Direction(new BindableDirection(Keys<BindableDirection>(Vector3.UnitX, -Vector3.UnitX)), Vector3.Zero);
        Assert.Equal(1d, antipode.Length(), 6);
        Assert.InRange(Math.Abs(Vector3.Dot(antipode, Vector3.UnitX)), 0f, 0.000001f);
    }

    [Theory]
    [InlineData(float.Epsilon)]
    [InlineData(float.MaxValue)]
    public void Any_finite_nonzero_direction_normalizes_without_squaring_in_float(float component) {
        BindableDirection direction = new Vector3(component, component, 0f);
        Assert.True(direction.IsAuthorable(World()));
        var actual = At(0d).Direction(direction, Vector3.Zero);
        Assert.Equal(Math.Sqrt(0.5d), actual.X, 6);
        Assert.Equal(Math.Sqrt(0.5d), actual.Y, 6);
    }

    [Fact]
    public void A_keyed_pair_round_trips_and_uses_the_same_component_blend() {
        var pair = new BindableVector2(Keys<BindableVector2>(new Vector2(2f, 4f), new Vector2(10f, 20f)));
        var options = WorldJsonContext.Default.Options;
        var copy = JsonSerializer.Deserialize<BindableVector2>(JsonSerializer.Serialize(pair, options), options);
        Assert.Equal(new Vector2(4f, 8f), At(1d).Vector(copy, Vector2.Zero));
        Assert.True(copy.IsAuthorable(World()));
    }

    [Fact]
    public void A_phase_curve_reads_its_parent_clock_and_clock_cycles_are_refused_by_name() {
        var parent = new WorldClock("day", PeriodSeconds: 8d, SpanSeconds: 24d);
        var phase = new BindableScalar(Keys<BindableScalar>(0.2f, 0.8f, WorldKeyEase.Smooth));
        var child = new WorldClock("night", Phase: phase);
        var world = World(parent, child);
        Assert.True(WorldValueValidation.TryValidateClock(child, world, out var reason), reason);
        Assert.Equal(0.29375d, At(1d, world).Phase(child), 6);
        var cycle = parent with { PeriodSeconds = null, Phase = new BindableScalar(new WorldKeys<BindableScalar>("night", [new(0d, 0f), new(0.5d, 1f)])) };
        Assert.False(WorldValueValidation.TryValidateClock(child, World(cycle, child), out reason));
        Assert.Contains("clock 'night' forms a cycle", reason);
    }

    [Fact]
    public void Bindable_wire_round_trips_keep_keys_and_refuse_unknown_structure() {
        var scalar = new BindableScalar(Keys<BindableScalar>(2f, 10f, WorldKeyEase.Smooth));
        var options = WorldJsonContext.Default.Options;
        var json = JsonSerializer.Serialize(scalar, options);
        var copy = JsonSerializer.Deserialize<BindableScalar>(json, options);
        Assert.Equal(At(1d).Scalar(scalar, -1d), At(1d).Scalar(copy, -2d));
        var direction = new BindableDirection(Keys<BindableDirection>(Vector3.UnitX, Vector3.UnitY));
        var directionCopy = JsonSerializer.Deserialize<BindableDirection>(JsonSerializer.Serialize(direction, options), options);
        Assert.Equal(At(1d).Direction(direction, Vector3.Zero), At(1d).Direction(directionCopy, Vector3.Zero));
    }

    [Fact]
    public void A_keyed_value_names_an_existing_clock() => Assert.False(new BindableColor(new WorldKeys<BindableColor>("missing",
        [new(0d, new("#000000")), new(0.5d, new("#FFFFFF"))])).IsAuthorable(World()));

    [Fact]
    public void A_keyed_scalar_cannot_smuggle_a_structural_seed() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize<BindableScalar>("{\"clock\":\"day\",\"seed\":2,\"keys\":[]}", WorldJsonContext.Default.Options));
}
