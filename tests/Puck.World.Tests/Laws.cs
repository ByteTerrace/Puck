using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The ONE shape every refusal law in this suite is asserted through — the architectural enforcement README.md's
/// red-line #2 names ("every test can fail for a real reason ... a new law is proven once by breaking it"). Both
/// the denied case and its passing control are REQUIRED positional arguments; there is no overload that accepts
/// only one, so a control-less refusal test cannot be expressed through this API — the wrong shape is the awkward
/// one to write, not merely the discouraged one.
/// </summary>
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
    /// <summary>Asserts a denial/control pair: <paramref name="deniedOutcome"/> must report the action's ordinary
    /// POSITIVE outcome did NOT happen (refused), and <paramref name="controlOutcome"/> — the identical action with
    /// the ONE discriminating fact reversed (the missing grant restored, the reserved name replaced, the actor's own
    /// subject substituted for someone else's) — must report that it DID. Each probe performs its own action and
    /// observation; this method only asserts the pair.</summary>
    /// <param name="lawId">A short, stable id for the case, quoted on failure.</param>
    /// <param name="deniedOutcome">The refused case. Runs first. Must report <see langword="false"/> (no positive
    /// outcome — parsed clean, the document changed, the grant was recorded, etc., whichever the law is about).</param>
    /// <param name="controlOutcome">The SAME action under the passing control. Runs second. Must report
    /// <see langword="true"/>.</param>
    public static void RefusalWithControl(string lawId, Func<bool> deniedOutcome, Func<bool> controlOutcome) {
        ArgumentNullException.ThrowIfNull(argument: deniedOutcome);
        ArgumentNullException.ThrowIfNull(argument: controlOutcome);

        Assert.False(condition: deniedOutcome(), userMessage: $"{lawId}: the denied case was expected to refuse, but its ordinary positive outcome was observed");
        Assert.True(condition: controlOutcome(), userMessage: $"{lawId}: the control case was expected to succeed, but it refused");
    }
}
