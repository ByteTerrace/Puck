using Puck.Hosting;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Guards the shipped world's idle-tick pipeline against added per-tick allocation. The tick-path readers
/// that resolve a state row (carrier rows, the drive-gate index, board-enforcement rows) go through a compiled
/// <see cref="StateHandle"/> rather than a per-tick linear name scan; the bound here covers the whole idle-tick
/// pipeline (population, physics, event collection included), not those readers alone. The world steps at its own
/// authored rate, and the measurement is the calling thread's, so a sibling test class running in parallel cannot
/// move it. A tick in which a rule writes a cell composes a document and is measured but not bounded here: the
/// bound holds the quiet tick, so the median across the window is the claim, and the widest tick is reported
/// beside it.</summary>
public sealed class HandleTickPathLawTests(ITestOutputHelper output) {
    [Fact]
    public void ShippedWorldIdleTicksStaySteadyStateAllocation() {
        var definition = AuthoredGameFixtures.Nexus;
        using var fixture = Fixtures.FreshServer(definition: definition);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));

        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step(stepTicks: width);
        }

        var samples = new long[120];

        for (var tick = 0; (tick < samples.Length); tick++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            fixture.Step(stepTicks: width);
            samples[tick] = (GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Array.Sort(array: samples);

        var median = samples[(samples.Length / 2)];
        var widest = samples[^1];

        output.WriteLine($"shipped world idle: median {median:N0} bytes/tick, widest {widest:N0} bytes/tick");

        Assert.True(median < (16L * 1024L), $"expected a quiet idle tick under 16 KiB, measured a median of {median:N0} bytes");
    }
}
