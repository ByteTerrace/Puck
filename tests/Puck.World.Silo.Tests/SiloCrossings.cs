using Xunit;

namespace Puck.World.Silo.Tests;

/// <summary>Drives crossings between a silo's rows to rest in a test that does not step the rows. The silo's crossing
/// log answers off the tick thread, so a crossing takes several drains: each round waits for any record in flight, lets
/// each row settle it at its boundary (its administrative drain), and drains the host's transfers again.</summary>
internal static class SiloCrossings {
    // Generous: a colocated source hears its commit's answer through a task the thread pool completes.
    private const int Rounds = 400;

    /// <summary>Drains the host's transfers until <paramref name="done"/> holds with no crossing record in flight on any
    /// named row.</summary>
    /// <param name="host">The silo host.</param>
    /// <param name="rows">The rows the crossing involves.</param>
    /// <param name="done">The condition the crossing rests at.</param>
    public static async Task DriveAsync(WorldSiloHost host, IReadOnlyList<string> rows, Func<bool> done) {
        for (var round = 0; (round < Rounds); round++) {
            host.Instances.DrainPendingTransfers();

            var inFlight = false;

            foreach (var name in rows) {
                Assert.True(condition: host.Instances.TryGet(instance: out var row, name: name));
                var answered = row!.Server.CrossingAnswered;

                inFlight |= !answered.IsCompleted;
                await answered.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
                _ = row.Server.DrainAdministrative();
            }
            if (!inFlight && done()) {
                return;
            }
            await Task.Delay(millisecondsDelay: 1, cancellationToken: TestContext.Current.CancellationToken);
        }
        Assert.Fail(message: $"the crossing did not come to rest within {Rounds} rounds");
    }
}
