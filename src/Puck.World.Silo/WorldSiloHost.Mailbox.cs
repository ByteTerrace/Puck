using System.Threading.Channels;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    private readonly Channel<Action> m_mailbox = Channel.CreateUnbounded<Action>();

    /// <summary>Drives <paramref name="operation"/> from a caller that is not the tick thread: drains every host's
    /// activation mailbox, then drains again each time work is posted to any of them, until the operation completes.
    /// The wait is for posted work or the operation's own completion, never for elapsed time.</summary>
    /// <param name="hosts">The hosts whose mailboxes the operation's steps queue onto.</param>
    /// <param name="operation">The operation to drive.</param>
    /// <param name="cancellationToken">The token that abandons the wait; the operation itself is not cancelled.</param>
    /// <returns>The operation's own completion, faulted or cancelled as the operation was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hosts"/> or <paramref name="operation"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the
    /// operation completed.</exception>
    public static async Task PumpActivationMailboxesAsync(IReadOnlyList<WorldSiloHost> hosts, Task operation, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(argument: hosts);
        ArgumentNullException.ThrowIfNull(argument: operation);

        var waits = new Task[(hosts.Count + 1)];

        waits[0] = operation;
        while (true) {
            foreach (var host in hosts) {
                host.DrainActivationMailbox();
            }

            if (operation.IsCompleted) {
                break;
            }

            using var posted = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);

            for (var index = 0; (index < hosts.Count); index++) {
                waits[(index + 1)] = hosts[index].m_mailbox.Reader.WaitToReadAsync(cancellationToken: posted.Token).AsTask();
            }

            _ = await Task.WhenAny(tasks: waits).ConfigureAwait(continueOnCapturedContext: false);
            await posted.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await operation.ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Drains queued activation/deactivation/checkpoint work built off the tick thread, then sweeps every
    /// held row for adjacency priming and recomputes the master cadence — the one thing every
    /// <see cref="Puck.Hosting.IFixedStepSimulation.Step"/> call must do before stepping.</summary>
    public void DrainActivationMailbox() {
        while (m_mailbox.Reader.TryRead(item: out var action)) {
            action();
        }

        if (IsDraining) { return; }
        SweepAwaitingMirrors();
        RecomputeMasterRateHz();
    }

    private void Post(Action action) => _ = m_mailbox.Writer.TryWrite(item: action);
}
