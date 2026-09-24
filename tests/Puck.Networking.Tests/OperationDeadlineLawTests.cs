using Puck.Testing;
using Xunit;

namespace Puck.Networking.Tests;

/// <summary>Laws for <see cref="OperationDeadline"/>: its token ends on whichever comes first of its clock's timeout and
/// the tokens it is linked to, and only the timeout reads as expiry.</summary>
public sealed class OperationDeadlineLawTests {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(value: 5);

    /// <summary>The timeout elapses on the supplied clock, at its due instant and not a tick before, and reads as
    /// expiry. Falsifier: a timer on system time never fires under an unadvanced test clock.</summary>
    [Fact]
    public void Deadline_ExpiresExactlyAtItsTimeoutOnTheSuppliedClock() {
        var clock = new VirtualClock();
        using var deadline = new OperationDeadline(
            timeout: Timeout,
            timeProvider: clock
        );

        clock.Advance(by: (Timeout - TimeSpan.FromTicks(value: 1)));

        Assert.False(condition: deadline.Token.IsCancellationRequested);
        Assert.False(condition: deadline.IsExpired);

        clock.Advance(by: TimeSpan.FromTicks(value: 1));

        Assert.True(condition: deadline.Token.IsCancellationRequested);
        Assert.True(condition: deadline.IsExpired);
    }
    /// <summary>The caller's cancellation, or the owner's lifetime, ends the token without reading as expiry, so a
    /// caller can tell its own cancellation from a timeout.</summary>
    /// <param name="lifetimeCancels">Whether the owner's lifetime, rather than the caller, cancels.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedCancellation_EndsTheTokenWithoutReadingAsExpiry(bool lifetimeCancels) {
        var clock = new VirtualClock();
        using var caller = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        using var deadline = new OperationDeadline(
            caller: caller.Token,
            lifetime: lifetime.Token,
            timeout: Timeout,
            timeProvider: clock
        );

        (lifetimeCancels
            ? lifetime
            : caller).Cancel();

        Assert.True(condition: deadline.Token.IsCancellationRequested);
        Assert.False(condition: deadline.IsExpired);
    }
}
