namespace Puck.Cli;

/// <summary>The machine-wide heavy lease every heavy run takes the same way: a heavy test suite (<c>puck gate</c>, <c>puck affected</c>) and a
/// multi-world <c>puck test</c>. Two heavy runs at once exhaust memory, so each holds the lease for its whole execution, under the
/// checkout's lane. A run already under <c>puck lease heavy</c> nests, since <see cref="ProcessInspection.IsAncestor"/> recognizes the holder
/// as its own ancestor.</summary>
public static class HeavyLease {
    /// <summary>Holds the shared heavy lease for a run's admission and entire execution.</summary>
    /// <param name="repositoryRoot">The checkout whose common Git directory owns the lease.</param>
    /// <param name="holder">The run's name; the lease names the checkout's lane beside it.</param>
    /// <param name="clock">The acquisition clock.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <returns>The lease to dispose after the run exits.</returns>
    public static CliLease Acquire(string repositoryRoot, string holder, TimeProvider clock, CancellationToken cancellationToken = default) {
        var root = CliLease.RootAsync(cancellationToken: cancellationToken, checkout: repositoryRoot).GetAwaiter().GetResult();

        return CliLease.AcquireAsync(ancestor: ProcessInspection.IsAncestor, cancellationToken: cancellationToken, checkout: repositoryRoot, clock: clock, holder: holder, lane: CliLease.LaneOf(checkout: repositoryRoot), name: "heavy", root: root, wait: CliLease.DefaultWait).GetAwaiter().GetResult();
    }
}
