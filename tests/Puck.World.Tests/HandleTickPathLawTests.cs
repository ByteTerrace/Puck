using System.Diagnostics;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Guards the shipped world's idle-tick pipeline against added per-tick allocation and against a
/// redundant per-tick-read recompute. The tick-path readers that resolve a state row (carrier rows, the drive-gate
/// index, board-enforcement rows) go through a compiled <see cref="StateHandle"/> rather than a per-tick linear
/// name scan; the bound here covers the whole idle-tick pipeline (population, physics, event collection included),
/// not those readers alone. The world steps at its own authored rate, and the measurement is the calling thread's,
/// so a sibling test class running in parallel cannot move it. A tick in which a rule writes a cell composes a
/// document and is measured but not bounded here: the bound holds the quiet tick, so the median across the window
/// is the claim, and the widest tick is reported beside it.</summary>
[Collection(AllocationCollection.Name)]
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

        Assert.True(median < (8L * 1024L), $"expected a quiet idle tick under 8 KiB, measured a median of {median:N0} bytes");
    }

    /// <summary>Bounds the shipped world's quiet-tick wall-clock median. Every rule-operand read resolves through
    /// <see cref="Puck.World.WorldDefinition.StateCatalog"/>, whose compiled product is keyed to the exact
    /// <c>StateRaw</c> reference it was proven against — a quiet tick never replaces that reference, so a fresh
    /// full-document shape walk on every one of those reads (rather than once per reference) is pure per-call
    /// waste that scales with the district count. The bound is loose against machine noise; it exists to catch a
    /// reintroduced per-read rescan, not to hold the engine to a specific throughput.</summary>
    [Fact]
    public void ShippedWorldIdleTicksStayUnderMedianTimeBound() {
        var definition = AuthoredGameFixtures.Nexus;
        using var fixture = Fixtures.FreshServer(definition: definition);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));

        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step(stepTicks: width);
        }

        var samples = new long[120];

        for (var tick = 0; (tick < samples.Length); tick++) {
            var before = Stopwatch.GetTimestamp();

            fixture.Step(stepTicks: width);
            samples[tick] = (Stopwatch.GetTimestamp() - before);
        }

        Array.Sort(array: samples);

        var medianMs = ((samples[(samples.Length / 2)] * 1000.0) / Stopwatch.Frequency);
        var widestMs = ((samples[^1] * 1000.0) / Stopwatch.Frequency);

        output.WriteLine($"shipped world idle: median {medianMs:F3} ms/tick, widest {widestMs:F3} ms/tick");

        Assert.True(medianMs < 25.0, $"expected a quiet idle tick under 25 ms, measured a median of {medianMs:F3} ms");
    }
}
