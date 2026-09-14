using System.Diagnostics;

namespace Puck.Cli.Azure;

/// <summary>One provider-owned sixty-second deployment lease. Renewal and release must use its exact lease ID.</summary>
internal interface IWorldReleaseControllerLeaseBackend {
    Task AcquireAsync(CancellationToken cancellationToken);
    Task RenewAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);
}
/// <summary>Exclusive controller lifetime with an early local deadline. Loss cancels in-flight work and prevents
/// further effects; disposal never releases a different controller's lease. Durable group phases remain recovery authority.</summary>
internal sealed class WorldReleaseControllerLease : IAsyncDisposable {
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(seconds: 50);

    private readonly IWorldReleaseControllerLeaseBackend m_backend;
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

    private WorldReleaseControllerLease(IWorldReleaseControllerLeaseBackend backend, CancellationToken cancellationToken, long confirmed) {
        m_backend = backend;
        m_confirmed = confirmed;
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(token: cancellationToken);
        Token = m_lifetime.Token;
        m_lifetime.CancelAfter(delay: (Lifetime - Stopwatch.GetElapsedTime(startingTimestamp: confirmed)));
        m_loop = RenewLoopAsync();
    }

    private async Task RenewLoopAsync() {
        try {
            using var timer = new PeriodicTimer(period: TimeSpan.FromSeconds(seconds: 15));

            while (await timer.WaitForNextTickAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false)) { await EnsureHeldAsync().ConfigureAwait(continueOnCapturedContext: false); }
        } catch (Exception) {
            // The token is the effect boundary's failure signal; the next invocation must resume durable state.
            await m_lifetime.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    public static async Task<WorldReleaseControllerLease> AcquireAsync(IWorldReleaseControllerLeaseBackend backend, CancellationToken cancellationToken) {
        var start = Stopwatch.GetTimestamp();

        await backend.AcquireAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (Stopwatch.GetElapsedTime(startingTimestamp: start) >= Lifetime) {
            throw new IOException(message: "controller lease acquisition exceeded its safe lifetime; wait for expiry before retrying");
        }
        return new(
            backend: backend,
            cancellationToken: cancellationToken,
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
            using var timeout = new CancellationTokenSource(delay: TimeSpan.FromSeconds(seconds: 10));

            try { await m_backend.ReleaseAsync(cancellationToken: timeout.Token).ConfigureAwait(continueOnCapturedContext: false); } catch (Exception error) { ReleaseFailure = error; }
        } finally {
            m_renewal.Release();
            m_lifetime.Dispose();
        }
    }
    /// <summary>Rechecks provider ownership before an external effect. A failed check permanently cancels this controller.</summary>
    public async Task EnsureHeldAsync() {
        Token.ThrowIfCancellationRequested();
        await m_renewal.WaitAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false);
        try {
            if (Stopwatch.GetElapsedTime(startingTimestamp: Interlocked.Read(location: ref m_confirmed)) >= Lifetime) {
                throw new IOException(message: "controller lease exceeded its local deadline");
            }
            var start = Stopwatch.GetTimestamp();

            await m_backend.RenewAsync(cancellationToken: Token).ConfigureAwait(continueOnCapturedContext: false);
            Token.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.GetElapsedTime(startingTimestamp: start);

            if (elapsed >= Lifetime) { throw new IOException(message: "controller lease renewal exceeded its safe lifetime"); }
            Interlocked.Exchange(
                location1: ref m_confirmed,
                value: start
            );
            m_lifetime.CancelAfter(delay: (Lifetime - elapsed));
        } catch {
            await m_lifetime.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);
            throw;
        } finally { m_renewal.Release(); }
    }
}
