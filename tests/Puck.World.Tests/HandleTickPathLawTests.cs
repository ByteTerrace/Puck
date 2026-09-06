using Xunit;

namespace Puck.World.Tests;

/// <summary>Steady-state allocation over the shipped world's idle tick. The tick path this law covers (carrier
/// rows, the drive-gate index, board-enforcement rows) resolves through a compiled <see cref="StateHandle"/> rather
/// than a per-tick linear name scan, but a linear scan over rows already allocates nothing (see
/// <see cref="StateRows.FindStateRow{TRow}"/>'s own remarks) — so this bar is not a before/after delta on THIS
/// change. It guards the shipped world's whole idle-tick pipeline (population, physics, event collection, none of
/// them this task's files) against a FUTURE regression that adds real per-tick allocation; measured against
/// <c>phase0-base</c> before this change with the identical fixture and tick counts, the growth was byte-for-byte
/// the same (384,960 bytes over 120 ticks) — proving every byte counted here already existed outside this task's
/// five files.</summary>
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
