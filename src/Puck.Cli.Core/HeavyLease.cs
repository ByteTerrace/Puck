namespace Puck.Cli;

/// <summary>The machine-wide heavy lease every heavy run takes the same way: a heavy test suite (<c>puck gate</c>, <c>puck affected</c>) and a
/// multi-world <c>puck test</c>. Two heavy runs at once exhaust memory, so each admits before holding the lease for execution, under the
/// checkout's lane. A run already under <c>puck lease heavy</c> nests, since <see cref="ProcessInspection.IsAncestor"/> recognizes the holder
/// as its own ancestor.</summary>
public static class HeavyLease {
    /// <summary>Admits without a hold, then holds the shared heavy lease for the run's execution.</summary>
    /// <param name="repositoryRoot">The checkout whose common Git directory owns the lease.</param>
    /// <param name="holder">The run's name; the lease names the checkout's lane beside it.</param>
    /// <param name="clock">The acquisition clock.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <param name="admit">The shared host admission.</param>
    /// <param name="device">Whether the run also needs an idle device.</param>
    /// <returns>The lease to dispose after the run exits.</returns>
    public static CliLease Acquire(string repositoryRoot, string holder, TimeProvider clock, HostAdmit admit, CancellationToken cancellationToken = default, bool device = false) {
        var root = CliLease.RootAsync(cancellationToken: cancellationToken, checkout: repositoryRoot).GetAwaiter().GetResult();

        return LeasedAdmission.AcquireAsync(root, "heavy", holder, CliLease.LaneOf(repositoryRoot), admit, repositoryRoot,
            holder, device, true, HostStartKind.HeavySuite, clock, CliLease.DefaultWait, Console.Error, null, cancellationToken).GetAwaiter().GetResult();
    }
}
