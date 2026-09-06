using Xunit;

namespace Puck.World.Tests;

/// <summary>Guards the shipped world's idle-tick pipeline against added per-tick allocation. The tick-path readers
/// that resolve a state row (carrier rows, the drive-gate index, board-enforcement rows) go through a compiled
/// <see cref="StateHandle"/> rather than a per-tick linear name scan; the bound here covers the whole idle-tick
/// pipeline (population, physics, event collection included), not those readers alone.</summary>
public sealed class HandleTickPathLawTests(ITestOutputHelper output) {
    [Fact]
    public void ShippedWorldIdleTicksStaySteadyStateAllocation() {
        using var fixture = Fixtures.FreshServer(definition: AuthoredGameFixtures.Nexus);

        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step();
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);

        for (var tick = 0; (tick < 120); tick++) {
            fixture.Step();
        }

        var after = GC.GetTotalAllocatedBytes(precise: true);
        var grown = (after - before);

        output.WriteLine($"shipped world idle: 120 ticks grew {grown:N0} bytes ({(grown / 120.0):F1} bytes/tick)");

        Assert.True(grown < (512L * 1024L), $"expected under 512 KiB of growth across 120 idle ticks, measured {grown:N0} bytes");
    }
}
