using Puck.Abstractions;

namespace Puck.Cli.Azure;

/// <summary>One provider-owned sixty-second deployment lease. Renewal and release must use its exact lease ID.
/// <see cref="RenewAsync"/> throws <see cref="WorldReleaseControllerLeaseLostException"/> when the provider answers that
/// the lease is no longer this controller's; any other failure is an attempt whose outcome is unknown, which the
/// controller retries while its safe lifetime lasts.</summary>
public interface IWorldReleaseControllerLeaseBackend {
    Task AcquireAsync(CancellationToken cancellationToken);
    Task RenewAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);
}
/// <summary>The release controller no longer holds its deployment lease, so it performs no further effects: a
/// cancellation whose message names why. The durable group phase stays where the controller left it.</summary>
public sealed class WorldReleaseControllerLeaseLostException : OperationCanceledException {
    /// <summary>Initializes a new instance of the <see cref="WorldReleaseControllerLeaseLostException"/> class for a
    /// provider's answer that the lease is gone.</summary>
    /// <param name="message">The provider's answer.</param>
    /// <param name="innerException">The provider's failure.</param>
    public WorldReleaseControllerLeaseLostException(string message, Exception innerException) : base(
        innerException: innerException,
        message: message
    ) { }
    /// <summary>Initializes a new instance of the <see cref="WorldReleaseControllerLeaseLostException"/> class for a
    /// controller whose <paramref name="token"/> its loss cancelled.</summary>
    /// <param name="message">Why the lease was lost.</param>
    /// <param name="innerException">The cancellation the loss caused.</param>
    /// <param name="token">The controller's cancelled token.</param>
    public WorldReleaseControllerLeaseLostException(string message, Exception innerException, CancellationToken token) : base(
        innerException: innerException,
        message: message,
        token: token
    ) { }
}
/// <summary>Exclusive controller lifetime with an early local deadline. Loss cancels in-flight work and prevents
/// further effects; disposal never releases a different controller's lease. Durable group phases remain recovery authority.
/// The local deadline, the renewal cadence, each renewal attempt's bound and the release bound all run on one clock.
/// <para>The invariant: an effect starts only while the last confirmed renewal began less than <see cref="Lifetime"/>
/// ago. The local expiry timer enforces it whatever renewal does, so retrying a renewal attempt whose outcome is
/// unknown never extends an effect past the provider's lease. Ownership ends when the provider says the lease is gone,
/// when no renewal is confirmed within <see cref="Lifetime"/>, or when the caller cancels. A loss records its reason
/// before it cancels <see cref="Token"/>, and <see cref="RunAsync{T}"/> and <see cref="EnsureHeldAsync"/> turn the
/// cancellation it causes into <see cref="WorldReleaseControllerLeaseLostException"/> carrying that reason.</para></summary>
public sealed class WorldReleaseControllerLease : IAsyncDisposable {
    /// <summary>Gets how long one confirmation keeps the controller's effects allowed, ten seconds inside the
    /// provider's sixty.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(seconds: 50);
    /// <summary>Gets how often the controller renews its lease.</summary>
    public static TimeSpan RenewalPeriod { get; } = TimeSpan.FromSeconds(seconds: 15);
    /// <summary>Gets how long one renewal attempt may go unanswered before the controller abandons it and tries again
    /// on a fresh request; a stalled connection would otherwise hold the renewal until the lease is gone.</summary>
    public static TimeSpan RenewalAttemptTimeout { get; } = TimeSpan.FromSeconds(seconds: 10);
    /// <summary>Gets the pause between a failed renewal attempt and the next.</summary>
    public static TimeSpan RenewalRetryDelay { get; } = TimeSpan.FromSeconds(seconds: 1);
    /// <summary>Gets the bound on releasing the lease at disposal.</summary>
    public static TimeSpan ReleaseTimeout { get; } = TimeSpan.FromSeconds(seconds: 10);

    private readonly IWorldReleaseControllerLeaseBackend m_backend;
    private readonly CancellationToken m_caller;
    private readonly TimeProvider m_clock;
    private readonly CancellationTokenSource m_expiry;
    private readonly CancellationTokenSource m_lifetime;
    private readonly Task m_loop;
    private readonly SemaphoreSlim m_renewal = new(
        initialCount: 1,
        maxCount: 1
    );

    private long m_confirmed;
    private int m_disposed;
    private volatile string? m_effect;
    private volatile Exception? m_lastRenewalFailure;
    private string? m_lossReason;

    public Exception? ReleaseFailure { get; private set; }
    /// <summary>Gets why the controller stopped holding its lease, or <see langword="null"/> while it holds it and after
    /// the caller's cancellation or disposal ended it.</summary>
    public string? LossReason => Volatile.Read(location: ref m_lossReason);
    public CancellationToken Token { get; }

    private WorldReleaseControllerLease(IWorldReleaseControllerLeaseBackend backend, TimeProvider clock, CancellationToken cancellationToken, long confirmed) {
        m_backend = backend;
        m_caller = cancellationToken;
        m_clock = clock;
        m_confirmed = confirmed;
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        Token = m_lifetime.Token;
        // The expiry is not linked into the lifetime: its callback records why before it cancels, so every token the
        // deadline cancels already has its reason.
        m_expiry = new CancellationTokenSource(
            delay: (Lifetime - clock.GetElapsedTime(startingTimestamp: confirmed)),
            timeProvider: clock
        );
        m_expiry.Token.UnsafeRegister(
            callback: static state => ((WorldReleaseControllerLease)state!).LoseToDeadline(),
            state: this
        );
        m_loop = RenewLoopAsync();
    }

    private void LoseToDeadline() => Lose(reason: ($"no lease renewal was confirmed within its {Lifetime.TotalSeconds:0}-second safe lifetime" +
        ((m_lastRenewalFailure is { } failure)
            ? $"; the last renewal attempt failed: {failure.Message}"
            : string.Empty)));
    // Records the first reason ownership ended, then closes the effect boundary. Only the first reason is kept: a later
    // failure is a consequence of the first.
    private void Lose(string reason) {
        _ = Interlocked.CompareExchange(
            comparand: null,
            location1: ref m_lossReason,
            value: reason
        );
        try { m_lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }
    private WorldReleaseControllerLeaseLostException Lost(string reason, Exception error) => new(
        innerException: error,
        message: ($"the release controller stopped because it lost its deployment lease: {reason}" +
            ((m_effect is { } effect)
                ? $"; the last external effect it started was '{effect}'"
                : string.Empty) +
            ". The durable operation keeps its phase, and the next deploy or 'puck world release resume' continues from it"),
        token: Token
    );
    private async Task RenewLoopAsync() {
        try {
            using var timer = new PeriodicTimer(
                period: RenewalPeriod,
                timeProvider: m_clock
            );

            while (await timer.WaitForNextTickAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false)) { await RenewAsync().ConfigureAwait(continueOnCapturedContext: false); }
        } catch (OperationCanceledException) when (Token.IsCancellationRequested) {
            // A loss, the caller's cancellation or disposal: each already closed the boundary, a loss with its reason.
        } catch (Exception error) {
            // The token is the effect boundary's failure signal; the next invocation must resume durable state.
            Lose(reason: $"lease renewal failed: {error.Message}");
        }
    }
    // Confirms ownership with the provider, retrying an attempt whose outcome is unknown until one is confirmed. The
    // attempt cap is unbounded because the local deadline bounds the retries: once it closes the boundary, the next
    // attempt's cancellation is not retryable. Only the provider's answer that the lease is gone ends ownership early.
    private async Task RenewAsync() {
        await m_renewal.WaitAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false);
        try {
            await Retry.RetryAsync(
                action: RenewOnceAsync,
                attempts: int.MaxValue,
                clock: m_clock,
                delay: RenewalRetryDelay,
                report: static (error, attempt) => $"Release controller lease renewal attempt {attempt} failed; retrying while the lease is held: {error.Message}",
                retryable: error => !Token.IsCancellationRequested
            ).ConfigureAwait(continueOnCapturedContext: false);
        } finally { m_renewal.Release(); }
    }
    private async Task RenewOnceAsync() {
        if (m_clock.GetElapsedTime(startingTimestamp: Interlocked.Read(location: ref m_confirmed)) >= Lifetime) { LoseToDeadline(); }
        Token.ThrowIfCancellationRequested();
        var start = m_clock.GetTimestamp();

        using (var deadline = new OperationDeadline(
            caller: Token,
            timeout: RenewalAttemptTimeout,
            timeProvider: m_clock
        )) {
            try {
                await m_backend.RenewAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
            } catch (WorldReleaseControllerLeaseLostException lost) {
                Lose(reason: $"the lease provider reports the lease is no longer this controller's: {lost.Message}");
                throw new OperationCanceledException(
                    innerException: lost,
                    message: LossReason,
                    token: Token
                );
            } catch (OperationCanceledException error) when (deadline.IsExpired && !Token.IsCancellationRequested) {
                var unanswered = new TimeoutException(
                    innerException: error,
                    message: $"the renewal went unanswered for {RenewalAttemptTimeout.TotalSeconds:0} seconds"
                );

                m_lastRenewalFailure = unanswered;
                throw unanswered;
            } catch (Exception error) when (!Token.IsCancellationRequested) {
                m_lastRenewalFailure = error;
                throw;
            }
        }
        Token.ThrowIfCancellationRequested();
        var elapsed = m_clock.GetElapsedTime(startingTimestamp: start);

        if (elapsed >= Lifetime) {
            Lose(reason: "a lease renewal took its whole safe lifetime to confirm");
            Token.ThrowIfCancellationRequested();
        }
        // A confirmation counts from when its request began, so the deadline never trusts the request's travel.
        Interlocked.Exchange(
            location1: ref m_confirmed,
            value: start
        );
        m_lastRenewalFailure = null;
        m_expiry.CancelAfter(delay: (Lifetime - elapsed));
    }

    /// <summary>Acquires the provider lease and starts renewing it on <paramref name="clock"/>.</summary>
    /// <param name="backend">The provider lease.</param>
    /// <param name="cancellationToken">The caller's cancellation, which also ends the controller's effects.</param>
    /// <param name="clock">The clock the local deadline, the renewal cadence, and the release bound run on;
    /// <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <returns>The held controller, which prints one standard-error line for each failed renewal attempt it
    /// retries.</returns>
    /// <exception cref="IOException">Acquisition took <see cref="Lifetime"/> or longer on <paramref name="clock"/>.</exception>
    public static async Task<WorldReleaseControllerLease> AcquireAsync(IWorldReleaseControllerLeaseBackend backend, CancellationToken cancellationToken, TimeProvider? clock = null) {
        clock ??= TimeProvider.System;
        var start = clock.GetTimestamp();

        // An acquisition that takes the whole lifetime may already have lost the lease, so the lifetime bounds it.
        using (var deadline = new OperationDeadline(
            caller: cancellationToken,
            timeout: Lifetime,
            timeProvider: clock
        )) {
            try {
                await backend.AcquireAsync(cancellationToken: deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException error) when (deadline.IsExpired && !cancellationToken.IsCancellationRequested) {
                throw new IOException(
                    innerException: error,
                    message: $"controller lease acquisition went unanswered for its {Lifetime.TotalSeconds:0}-second safe lifetime; wait for expiry before retrying"
                );
            }
        }
        if (clock.GetElapsedTime(startingTimestamp: start) >= Lifetime) {
            throw new IOException(message: "controller lease acquisition exceeded its safe lifetime; wait for expiry before retrying");
        }
        return new(
            backend: backend,
            cancellationToken: cancellationToken,
            clock: clock,
            confirmed: start
        );
    }
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        ) != 0) { return; }
        await m_lifetime.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
        await m_loop.ConfigureAwait(continueOnCapturedContext: false);
        await m_renewal.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
        try {
            using var timeout = new CancellationTokenSource(
                delay: ReleaseTimeout,
                timeProvider: m_clock
            );

            try { await m_backend.ReleaseAsync(cancellationToken: timeout.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (Exception error) { ReleaseFailure = error; }
        } finally {
            m_renewal.Release();
            m_expiry.Dispose();
            m_lifetime.Dispose();
        }
    }
    /// <summary>Rechecks provider ownership before an external effect, retrying an unanswered renewal while the lease
    /// is held.</summary>
    /// <param name="effect">The external effect about to start, which a later loss names; <see langword="null"/> names
    /// none.</param>
    /// <exception cref="WorldReleaseControllerLeaseLostException">The lease is lost; this controller stays
    /// cancelled.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled or the controller was disposed.</exception>
    public async Task EnsureHeldAsync(string? effect = null) {
        try {
            await RenewAsync().ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException error) when ((error is not WorldReleaseControllerLeaseLostException) && (LossReason is { } reason)) {
            throw Lost(
                error: error,
                reason: reason
            );
        }
        m_effect = effect;
    }
    /// <summary>Runs <paramref name="action"/> under <see cref="Token"/>.</summary>
    /// <typeparam name="T">The action's result.</typeparam>
    /// <param name="action">The work, given <see cref="Token"/>.</param>
    /// <returns>The action's result.</returns>
    /// <exception cref="WorldReleaseControllerLeaseLostException">The lease's loss cancelled the action; the message
    /// names why and the last external effect started.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action) {
        ArgumentNullException.ThrowIfNull(argument: action);
        try {
            return await action(arg: Token).ConfigureAwait(continueOnCapturedContext: false);
        } catch (OperationCanceledException error) when ((error is not WorldReleaseControllerLeaseLostException) && !m_caller.IsCancellationRequested && (LossReason is { } reason)) {
            throw Lost(
                error: error,
                reason: reason
            );
        }
    }
}
