using Xunit;

namespace Puck.Networking.Tests;

/// <summary>Shared support for a socket-bearing law.</summary>
internal static class Laws {
    /// <summary>The wall-clock ceiling a socket-bearing law bounds its round trips with. It bounds a hang, not
    /// latency: each such law exchanges a handful of loopback frames costing milliseconds, and its verdict is a
    /// refusal name rather than a duration. A ceiling tight enough to expire while a saturated thread pool schedules
    /// the host's accept loop or a read continuation makes the law report on the machine instead of on the door.</summary>
    public static readonly TimeSpan SocketBudget = TimeSpan.FromSeconds(value: 60);

    /// <summary>Creates the cancellation source a socket-bearing law bounds its round trips with — linked to the
    /// runner's own test token, so the suite can still cancel a wedged law, and expiring after
    /// <see cref="SocketBudget"/>. Callers own disposal.</summary>
    /// <returns>The linked, self-expiring source.</returns>
    public static CancellationTokenSource SocketDeadline() {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: SocketBudget);

        return deadline;
    }
}
