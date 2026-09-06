namespace Puck.Networking.Peers;

/// <summary>Owns a timed cancellation and its link to the caller's lifetime.</summary>
internal sealed class PeerDeadline : IDisposable {
    private readonly CancellationTokenSource m_timer;
    private readonly CancellationTokenSource m_linked;

    public PeerDeadline(CancellationToken caller, TimeSpan timeout, TimeProvider timeProvider) {
        m_timer = new CancellationTokenSource(timeout, timeProvider);
        m_linked = CancellationTokenSource.CreateLinkedTokenSource(caller, m_timer.Token);
    }

    public CancellationToken Token => m_linked.Token;

    public void Dispose() {
        m_linked.Dispose();
        m_timer.Dispose();
    }
}
