using Puck.Hosting;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Two independent boots of the shipped island, each advanced under no input and hashed after every tick:
/// the raw material of the laws that pin the island's state hashes and of the laws that prove those hashes reproduce
/// across boots. The boots share nothing but the document, run concurrently, and run once for every law that reads
/// them.</summary>
/// <remarks>Hashing reads the server and never advances it, so a run hashed at every tick reaches the same state a
/// run hashed at one tick does. The second boot stops at <see cref="SecondTicks"/>, the last tick a reproducibility
/// law compares.</remarks>
internal static class ShippedWorldIdleRuns {
    /// <summary>How many ticks the first boot advances: the last tick a pinned hash names.</summary>
    public const int FirstTicks = 151;
    /// <summary>How many ticks the second boot advances.</summary>
    public const int SecondTicks = 90;

    private static readonly Lazy<(ShippedWorldIdleRun First, ShippedWorldIdleRun Second)> Runs = new(valueFactory: () => {
        var first = Task.Run(function: () => Run(ticks: FirstTicks));
        var second = Task.Run(function: () => Run(ticks: SecondTicks));

        return (first.GetAwaiter().GetResult(), second.GetAwaiter().GetResult());
    });

    /// <summary>Gets the first boot's run.</summary>
    public static ShippedWorldIdleRun First => Runs.Value.First;
    /// <summary>Gets the second, independent boot's run.</summary>
    public static ShippedWorldIdleRun Second => Runs.Value.Second;

    private static ShippedWorldIdleRun Run(int ticks) {
        using var fixture = Fixtures.FreshServer(definition: AuthoredGameFixtures.Nexus);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)fixture.Server.Definition.SimulationRateHz));
        var authoritative = new ulong[(ticks + 1)];
        var capture = new ulong[(ticks + 1)];

        for (var tick = 0; (tick <= ticks); tick++) {
            if (tick > 0) {
                fixture.Step(stepTicks: width);
            }

            authoritative[tick] = WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: ((ulong)tick)
            );
            capture[tick] = WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Capture,
                server: fixture.Server,
                tick: ((ulong)tick)
            );
        }

        return new ShippedWorldIdleRun(
            Authoritative: authoritative,
            Capture: capture,
            GridCellSize: ((double?)fixture.Server.SolidField?.Grid?.CellSize)
        );
    }
}
/// <summary>One idle run of the shipped island.</summary>
/// <param name="Authoritative">The authoritative state hash after each tick, indexed by tick; index 0 is the boot.</param>
/// <param name="Capture">The capture-scope state hash after each tick, indexed by tick; index 0 is the boot.</param>
/// <param name="GridCellSize">The cell size of the solid field's distance grid the boot baked, or <see langword="null"/>
/// when it baked none.</param>
internal sealed record ShippedWorldIdleRun(ulong[] Authoritative, ulong[] Capture, double? GridCellSize);
