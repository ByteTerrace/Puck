using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A rate integrates each key segment and whole clock periods, never the current rate times elapsed time.</summary>
public sealed class WindIntegralLawTests {
    private static PresentedTick Tick(double seconds) => new(((ulong)Math.Floor(d: (seconds * EngineTicks.PerSecond))), ((seconds * EngineTicks.PerSecond) % 1d));
    private static WorldDefinition World(params WorldClock[] clocks) => new(TimelineRaw: new WorldTimelineSection(Clocks: clocks));
    private static BindableScalar Rate(string clock, double at, WorldKeyEase ease) => new(keys: new WorldKeys<BindableScalar>(Clock: clock, Keys: [new(At: 0d, Ease: ease, Value: 0f), new(at, 4f)]));
    private static WorldRateIntegral Compile(BindableScalar rate, WorldDefinition world) {
        Assert.True(condition: WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason), userMessage: reason);
        return integral!;
    }

    [InlineData(WorldKeyEase.Linear, 0.5d, 8d)]
    [InlineData(WorldKeyEase.Smooth, 0.21875d, 8d)]
    [InlineData(WorldKeyEase.Step, 0d, 0d)]
    [Theory]
    public void A_keyed_wind_has_the_segment_integral_and_no_jump_at_a_key(WorldKeyEase ease, double firstSecond, double firstHalf) {
        var world = World(new WorldClock("wind", PeriodSeconds: 8d));
        var integral = Compile(rate: Rate(at: 4d, clock: "wind", ease: ease), world: world);

        Assert.Equal(firstSecond, integral.At(Tick(seconds: 1d), 4096d), 10);
        Assert.Equal(firstHalf, integral.At(Tick(seconds: 4d), 4096d), 10);
        Assert.Equal((firstHalf + 8d), integral.At(Tick(seconds: 8d), 4096d), 10);
        var at = integral.At(Tick(seconds: 4d), 4096d);
        var before = integral.At(new PresentedTick(Fraction: 0d, Whole: ((4UL * EngineTicks.PerSecond) - 1UL)), 4096d);
        var after = integral.At(new PresentedTick(Fraction: 0d, Whole: ((4UL * EngineTicks.PerSecond) + 1UL)), 4096d);

        Assert.InRange(Math.Abs(value: (at - before)), 0d, ((4d / EngineTicks.PerSecond) + 1e-10d));
        Assert.InRange(Math.Abs(value: (after - at)), 0d, ((4d / EngineTicks.PerSecond) + 1e-10d));
        var manyPeriods = new PresentedTick(Fraction: 0d, Whole: (((8UL * EngineTicks.PerSecond) * 65536UL) + EngineTicks.PerSecond));

        Assert.Equal(firstSecond, integral.At(modulus: 4096d, tick: manyPeriods), 9);
    }
    [Fact]
    public void A_phase_keyed_clock_integrates_composed_smooth_polynomials_and_start_offsets() {
        var parent = new WorldClock("day", PeriodSeconds: 8d);
        var phase = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(At: 0d, Ease: WorldKeyEase.Smooth, Value: 0f), new(At: 4d, Ease: WorldKeyEase.Smooth, Value: 1f)]));
        var child = new WorldClock("wind", Phase: phase);
        var world = World(parent, child);
        var integral = Compile(rate: Rate(at: 0.5d, clock: "wind", ease: WorldKeyEase.Linear), world: world);

        Assert.Equal(3d, integral.At(Tick(seconds: 2d), 4096d), 9);
        Assert.Equal(12d, integral.At(Tick(seconds: 8d), 4096d), 9);
        var shifted = Compile(rate: Rate(at: 0.5d, clock: "wind", ease: WorldKeyEase.Linear), world: World(parent with { StartSeconds = 2d }, child));

        Assert.Equal(0d, shifted.At(Tick(seconds: 0d), 4096d), 10);
        Assert.Equal(3d, shifted.At(Tick(seconds: 2d), 4096d), 9);
        Assert.Equal(12d, shifted.At(Tick(seconds: 8d), 4096d), 9);
    }
    [Fact]
    public void Rates_refuse_state_history_directly_through_a_clock_or_inside_a_key() {
        var stateClock = new WorldClock("state-clock", State: "wind");
        var tickClock = new WorldClock("day", PeriodSeconds: 8d);
        var world = World(stateClock, tickClock);

        Assert.False(condition: WorldRateIntegral.TryCompile(new BindableScalar(binding: "state.wind"), world, out _, out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "state");
        Assert.False(condition: WorldRateIntegral.TryCompile(Rate(at: 0.5d, clock: "state-clock", ease: WorldKeyEase.Linear), world, out _, out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "state-clock");
        var stateKey = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, new(binding: "state.wind")), new(4d, 1f)]));

        Assert.False(condition: WorldRateIntegral.TryCompile(stateKey, world, out _, out reason));
        Assert.Contains(actualString: reason, expectedSubstring: "rate key 0");
        Assert.Contains(actualString: reason, expectedSubstring: "history");
        Assert.True(condition: WorldRateIntegral.TryCompile(2f, world, out var constant, out reason), userMessage: reason);
        Assert.Equal(6d, constant!.At(Tick(seconds: 3d), 4096d));
    }
}
