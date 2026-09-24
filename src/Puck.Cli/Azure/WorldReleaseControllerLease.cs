namespace Puck.Cli.Azure;

/// <summary>One provider-owned sixty-second deployment lease. Renewal and release must use its exact lease ID.</summary>
internal interface IWorldReleaseControllerLeaseBackend {
    Task AcquireAsync(CancellationToken cancellationToken);
    Task RenewAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);
}
/// <summary>Exclusive controller lifetime with an early local deadline. Loss cancels in-flight work and prevents
/// further effects; disposal never releases a different controller's lease. Durable group phases remain recovery authority.
/// The local deadline, the renewal cadence, and the release bound all run on one clock.</summary>
internal sealed class WorldReleaseControllerLease : IAsyncDisposable {
    /// <summary>Gets how long one confirmation keeps the controller's effects allowed, ten seconds inside the
    /// provider's sixty.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(seconds: 50);
    /// <summary>Gets how often the controller renews its lease.</summary>
    public static TimeSpan RenewalPeriod { get; } = TimeSpan.FromSeconds(seconds: 15);
    /// <summary>Gets the bound on releasing the lease at disposal.</summary>
    public static TimeSpan ReleaseTimeout { get; } = TimeSpan.FromSeconds(seconds: 10);

    private readonly IWorldReleaseControllerLeaseBackend m_backend;
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

    public Exception? ReleaseFailure { get; private set; }
    public CancellationToken Token { get; }

    private WorldReleaseControllerLease(IWorldReleaseControllerLeaseBackend backend, TimeProvider clock, CancellationToken cancellationToken, long confirmed) {
        m_backend = backend;
        m_clock = clock;
        m_confirmed = confirmed;
        m_expiry = new CancellationTokenSource(
            delay: (Lifetime - clock.GetElapsedTime(startingTimestamp: confirmed)),
            timeProvider: clock
        );
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            token1: cancellationToken,
            token2: m_expiry.Token
        );
        Token = m_lifetime.Token;
        m_loop = RenewLoopAsync();
    }

    private async Task RenewLoopAsync() {
        try {
            using var timer = new PeriodicTimer(
                period: RenewalPeriod,
                timeProvider: m_clock
            );

            while (await timer.WaitForNextTickAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false)) { await EnsureHeldAsync().ConfigureAwait(continueOnCapturedContext: false); }
        } catch (Exception) {
            // The token is the effect boundary's failure signal; the next invocation must resume durable state.
            await m_lifetime.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    /// <summary>Acquires the provider lease and starts renewing it on <paramref name="clock"/>.</summary>
    /// <param name="backend">The provider lease.</param>
    /// <param name="cancellationToken">The caller's cancellation, which also ends the controller's effects.</param>
    /// <param name="clock">The clock the local deadline, the renewal cadence, and the release bound run on;
    /// <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <returns>The held controller.</returns>
    /// <exception cref="IOException">Acquisition took <see cref="Lifetime"/> or longer on <paramref name="clock"/>.</exception>
    public static async Task<WorldReleaseControllerLease> AcquireAsync(IWorldReleaseControllerLeaseBackend backend, CancellationToken cancellationToken, TimeProvider? clock = null) {
        clock ??= TimeProvider.System;
        var start = clock.GetTimestamp();

        await backend.AcquireAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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
            m_lifetime.Dispose();
            m_expiry.Dispose();
        }
    }
    /// <summary>Rechecks provider ownership before an external effect. A failed check permanently cancels this controller.</summary>
    public async Task EnsureHeldAsync() {
        Token.ThrowIfCancellationRequested();
        await m_renewal.WaitAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false);
        try {
            if (m_clock.GetElapsedTime(startingTimestamp: Interlocked.Read(location: ref m_confirmed)) >= Lifetime) {
                throw new IOException(message: "controller lease exceeded its local deadline");
            }
            var start = m_clock.GetTimestamp();

            await m_backend.RenewAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false);
            Token.ThrowIfCancellationRequested();
            var elapsed = m_clock.GetElapsedTime(startingTimestamp: start);

            if (elapsed >= Lifetime) { throw new IOException(message: "controller lease renewal exceeded its safe lifetime"); }
            Interlocked.Exchange(
                location1: ref m_confirmed,
                value: start
            );
            m_expiry.CancelAfter(delay: (Lifetime - elapsed));
        } catch {
            await m_lifetime.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            throw;
        } finally { m_renewal.Release(); }
    }
}
