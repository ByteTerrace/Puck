using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A retained forwarding hop whose destination may be admitted after its source. Binding and invalidation
/// happen on the host thread; socket readers see one published arm or a named unavailable result.</summary>
internal sealed class WorldDeferredForwardedAuthority(WorldForwardingDestination destination, IWorldForwardedAuthority? initial = null)
    : IWorldForwardedAuthority, IDisposable {
    private readonly Lock m_gate = new();
    private IWorldForwardedAuthority? m_current = initial;
    private readonly WorldForwardingDestination m_destination = destination;
    private bool m_disposed;
    public bool IsBound => Volatile.Read(ref m_current) is not null;
    public bool TryBind(IWorldForwardedAuthority current) {
        lock (m_gate) {
            if (!m_disposed && m_current is null) { Volatile.Write(ref m_current, current); return true; }
        }
        (current as IDisposable)?.Dispose();
        return false;
    }
    public void Invalidate() {
        IWorldForwardedAuthority? retired;
        lock (m_gate) {
            retired = m_current;
            Volatile.Write(ref m_current, null);
        }
        (retired as IDisposable)?.Dispose();
    }
    public void Dispose() {
        IWorldForwardedAuthority? retired;
        lock (m_gate) {
            m_disposed = true;
            retired = m_current;
            Volatile.Write(ref m_current, null);
        }
        (retired as IDisposable)?.Dispose();
    }
    public WorldForwardingDestination DescribeForCheckpoint() => m_destination;
    public Task<string?> StreamProjectionAsync(Stream output, WorldDisclosureTier ceiling, byte remainingHops, CancellationToken ct) =>
        Volatile.Read(ref m_current) is { } current ? current.StreamProjectionAsync(output, ceiling, remainingHops, ct) : Task.FromResult<string?>(Unavailable);
    private string Unavailable => $"forwarding destination '{m_destination.DestinationAuthority}' is not yet available";
    public bool TryDescribeRoute(out WorldAuthorityRouteDescription route, out string reason) {
        if (Volatile.Read(ref m_current) is { } current) { return current.TryDescribeRoute(out route, out reason); }
        route = default; reason = Unavailable; return false;
    }
    public bool TryForwardIntent(in IntentSubmission submission, out string reason) {
        if (Volatile.Read(ref m_current) is { } current) { return current.TryForwardIntent(in submission, out reason); }
        reason = Unavailable; return false;
    }
    public bool TryForwardSubmission(WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        if (Volatile.Read(ref m_current) is { } current) { return current.TryForwardSubmission(payload, out result, out reason); }
        result = null; reason = Unavailable; return false;
    }
}
