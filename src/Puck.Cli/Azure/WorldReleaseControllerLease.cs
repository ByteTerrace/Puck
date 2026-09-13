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
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(50);
    private readonly IWorldReleaseControllerLeaseBackend m_backend;
    private readonly CancellationTokenSource m_lifetime;
    private readonly SemaphoreSlim m_renewal = new(1, 1);
    private readonly Task m_loop;
    private long m_confirmed;
    private int m_disposed;
    public CancellationToken Token { get; }
    public Exception? ReleaseFailure { get; private set; }

    private WorldReleaseControllerLease(IWorldReleaseControllerLeaseBackend backend, CancellationToken cancellationToken, long confirmed) {
        m_backend = backend;
        m_confirmed = confirmed;
        m_lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Token = m_lifetime.Token;
        m_lifetime.CancelAfter(Lifetime - Stopwatch.GetElapsedTime(confirmed));
        m_loop = RenewLoopAsync();
    }

    public static async Task<WorldReleaseControllerLease> AcquireAsync(IWorldReleaseControllerLeaseBackend backend, CancellationToken cancellationToken) {
        var start = Stopwatch.GetTimestamp();
        await backend.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (Stopwatch.GetElapsedTime(start) >= Lifetime) {
            throw new IOException("controller lease acquisition exceeded its safe lifetime; wait for expiry before retrying");
        }
        return new(backend, cancellationToken, start);
    }

    /// <summary>Rechecks provider ownership before an external effect. A failed check permanently cancels this controller.</summary>
    public async Task EnsureHeldAsync() {
        Token.ThrowIfCancellationRequested();
        await m_renewal.WaitAsync(Token).ConfigureAwait(false);
        try {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref m_confirmed)) >= Lifetime) {
                throw new IOException("controller lease exceeded its local deadline");
            }
            var start = Stopwatch.GetTimestamp();
            await m_backend.RenewAsync(Token).ConfigureAwait(false);
            Token.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed >= Lifetime) { throw new IOException("controller lease renewal exceeded its safe lifetime"); }
            Interlocked.Exchange(ref m_confirmed, start);
            m_lifetime.CancelAfter(Lifetime - elapsed);
        } catch {
            await m_lifetime.CancelAsync().ConfigureAwait(false);
            throw;
        } finally { m_renewal.Release(); }
    }

    private async Task RenewLoopAsync() {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(Token).ConfigureAwait(false)) { await EnsureHeldAsync().ConfigureAwait(false); }
        } catch (Exception) {
            // The token is the effect boundary's failure signal; the next invocation must resume durable state.
            await m_lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0) { return; }
        await m_lifetime.CancelAsync().ConfigureAwait(false);
        await m_loop.ConfigureAwait(false);
        await m_renewal.WaitAsync().ConfigureAwait(false);
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await m_backend.ReleaseAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception error) { ReleaseFailure = error; }
        } finally {
            m_renewal.Release();
            m_lifetime.Dispose();
        }
    }
}
