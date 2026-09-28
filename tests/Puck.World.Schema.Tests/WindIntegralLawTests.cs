using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A rate integrates each key segment and whole clock periods, never the current rate times elapsed time.</summary>
public sealed class WindIntegralLawTests {
    private static PresentedTick Tick(double seconds) => new((ulong)Math.Floor(seconds * EngineTicks.PerSecond), (seconds * EngineTicks.PerSecond) % 1d);
    private static WorldDefinition World(params WorldClock[] clocks) => new(TimelineRaw: new WorldTimelineSection(clocks));
    private static BindableScalar Rate(string clock, double at, WorldKeyEase ease) => new(new WorldKeys<BindableScalar>(clock, [new(0d, 0f, ease), new(at, 4f)]));
    private static WorldRateIntegral Compile(BindableScalar rate, WorldDefinition world) {
        Assert.True(WorldRateIntegral.TryCompile(rate, world, out var integral, out var reason), reason);
        return integral!;
    }

    [Theory]
    [InlineData(WorldKeyEase.Linear, 0.5d, 8d)]
    [InlineData(WorldKeyEase.Smooth, 0.21875d, 8d)]
    [InlineData(WorldKeyEase.Step, 0d, 0d)]
    public void A_keyed_wind_has_the_segment_integral_and_no_jump_at_a_key(WorldKeyEase ease, double firstSecond, double firstHalf) {
        var world = World(new WorldClock("wind", PeriodSeconds: 8d));
        var integral = Compile(Rate("wind", 4d, ease), world);
        Assert.Equal(firstSecond, integral.At(Tick(1d), 4096d), 10);
        Assert.Equal(firstHalf, integral.At(Tick(4d), 4096d), 10);
        Assert.Equal(firstHalf + 8d, integral.At(Tick(8d), 4096d), 10);
        var at = integral.At(Tick(4d), 4096d);
        var before = integral.At(new PresentedTick((4UL * EngineTicks.PerSecond) - 1UL, 0d), 4096d);
        var after = integral.At(new PresentedTick((4UL * EngineTicks.PerSecond) + 1UL, 0d), 4096d);
        Assert.InRange(Math.Abs(at - before), 0d, 4d / EngineTicks.PerSecond + 1e-10d);
        Assert.InRange(Math.Abs(after - at), 0d, 4d / EngineTicks.PerSecond + 1e-10d);
        var manyPeriods = new PresentedTick((8UL * EngineTicks.PerSecond * 65536UL) + EngineTicks.PerSecond, 0d);
        Assert.Equal(firstSecond, integral.At(manyPeriods, 4096d), 9);
    }

    [Fact]
    public void A_phase_keyed_clock_integrates_composed_smooth_polynomials_and_start_offsets() {
        var parent = new WorldClock("day", PeriodSeconds: 8d);
        var phase = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, 0f, WorldKeyEase.Smooth), new(4d, 1f, WorldKeyEase.Smooth)]));
        var child = new WorldClock("wind", Phase: phase);
        var world = World(parent, child);
        var integral = Compile(Rate("wind", 0.5d, WorldKeyEase.Linear), world);
        Assert.Equal(3d, integral.At(Tick(2d), 4096d), 9);
        Assert.Equal(12d, integral.At(Tick(8d), 4096d), 9);
        var shifted = Compile(Rate("wind", 0.5d, WorldKeyEase.Linear), World(parent with { StartSeconds = 2d }, child));
        Assert.Equal(0d, shifted.At(Tick(0d), 4096d), 10);
        Assert.Equal(3d, shifted.At(Tick(2d), 4096d), 9);
        Assert.Equal(12d, shifted.At(Tick(8d), 4096d), 9);
    }

    [Fact]
    public void Rates_refuse_state_history_directly_through_a_clock_or_inside_a_key() {
        var stateClock = new WorldClock("state-clock", State: "wind");
        var tickClock = new WorldClock("day", PeriodSeconds: 8d);
        var world = World(stateClock, tickClock);
        Assert.False(WorldRateIntegral.TryCompile(new BindableScalar("state.wind"), world, out _, out var reason));
        Assert.Contains("state", reason);
        Assert.False(WorldRateIntegral.TryCompile(Rate("state-clock", 0.5d, WorldKeyEase.Linear), world, out _, out reason));
        Assert.Contains("state-clock", reason);
        var stateKey = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, new("state.wind")), new(4d, 1f)]));
        Assert.False(WorldRateIntegral.TryCompile(stateKey, world, out _, out reason));
        Assert.Contains("rate key 0", reason);
        Assert.Contains("history", reason);
        Assert.True(WorldRateIntegral.TryCompile(2f, world, out var constant, out reason), reason);
        Assert.Equal(6d, constant!.At(Tick(3d), 4096d));
    }
}
