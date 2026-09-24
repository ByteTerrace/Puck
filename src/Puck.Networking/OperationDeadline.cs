namespace Puck.Networking;

/// <summary>One bounded operation's cancellation: its <see cref="Token"/> cancels when <c>timeout</c> elapses on the
/// supplied clock or when the caller's own token cancels, whichever comes first. The timer reads the
/// <see cref="TimeProvider"/> it is given rather than wall time, so a host's one clock — or a test clock — decides
/// when every operation it bounds expires.
/// <para>A caller that tells expiry from its own cancellation reads <see cref="IsExpired"/>. Disposing the deadline
/// when the operation ends releases the timer and the link.</para></summary>
public sealed class OperationDeadline : IDisposable {
    private readonly CancellationTokenSource m_expiry;
    private readonly CancellationTokenSource m_linked;

    /// <summary>Initializes a new instance of the <see cref="OperationDeadline"/> class and starts its timer.</summary>
    /// <param name="timeout">How long the operation may run, on <paramref name="timeProvider"/>;
    /// <see cref="Timeout.InfiniteTimeSpan"/> never expires.</param>
    /// <param name="timeProvider">The clock the timeout elapses on.</param>
    /// <param name="caller">The caller's own cancellation, which cancels <see cref="Token"/> too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative (other than
    /// <see cref="Timeout.InfiniteTimeSpan"/>) or too large for a timer.</exception>
    public OperationDeadline(TimeSpan timeout, TimeProvider timeProvider, CancellationToken caller = default) {
        ArgumentNullException.ThrowIfNull(argument: timeProvider);

        m_expiry = new CancellationTokenSource(
            delay: timeout,
            timeProvider: timeProvider
        );
        m_linked = CancellationTokenSource.CreateLinkedTokenSource(
            token1: caller,
            token2: m_expiry.Token
        );
    }
    /// <summary>Initializes a new instance of the <see cref="OperationDeadline"/> class for an operation that ends
    /// with its caller or with its owner's lifetime, and starts its timer.</summary>
    /// <param name="timeout">How long the operation may run, on <paramref name="timeProvider"/>;
    /// <see cref="Timeout.InfiniteTimeSpan"/> never expires.</param>
    /// <param name="timeProvider">The clock the timeout elapses on.</param>
    /// <param name="caller">The caller's own cancellation, which cancels <see cref="Token"/> too.</param>
    /// <param name="lifetime">The owning component's lifetime, which cancels <see cref="Token"/> too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative (other than
    /// <see cref="Timeout.InfiniteTimeSpan"/>) or too large for a timer.</exception>
    public OperationDeadline(TimeSpan timeout, TimeProvider timeProvider, CancellationToken caller, CancellationToken lifetime) {
        ArgumentNullException.ThrowIfNull(argument: timeProvider);

        m_expiry = new CancellationTokenSource(
            delay: timeout,
            timeProvider: timeProvider
        );
        m_linked = CancellationTokenSource.CreateLinkedTokenSource(
            caller,
            lifetime,
            m_expiry.Token
        );
    }

    /// <summary>Gets a value indicating whether the timeout has elapsed, whether or not the caller also
    /// cancelled.</summary>
    public bool IsExpired => m_expiry.IsCancellationRequested;
    /// <summary>Gets the token that cancels when the timeout elapses or the caller cancels.</summary>
    public CancellationToken Token => m_linked.Token;

    /// <summary>Releases the timer and the link to the caller's token.</summary>
    public void Dispose() {
        m_linked.Dispose();
        m_expiry.Dispose();
    }
}
