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

    private string Unavailable => $"forwarding destination '{m_destination.DestinationAuthority}' is not yet available";

    public bool IsBound => (Volatile.Read(location: ref m_current) is not null);

    public WorldForwardingDestination DescribeForCheckpoint() => m_destination;
    public void Dispose() {
        IWorldForwardedAuthority? retired;

        lock (m_gate) {
            m_disposed = true;
            retired = m_current;
            Volatile.Write(
                location: ref m_current,
                value: null
            );
        }
        (retired as IDisposable)?.Dispose();
    }
    public void Invalidate() {
        IWorldForwardedAuthority? retired;

        lock (m_gate) {
            retired = m_current;
            Volatile.Write(
                location: ref m_current,
                value: null
            );
        }
        (retired as IDisposable)?.Dispose();
    }
    public Task<string?> StreamProjectionAsync(Stream output, WorldDisclosureTier ceiling, byte remainingHops, CancellationToken ct) =>
        ((Volatile.Read(location: ref m_current) is { } current)
            ? current.StreamProjectionAsync(
                ceiling: ceiling,
                ct: ct,
                output: output,
                remainingHops: remainingHops
            )
            : Task.FromResult<string?>(result: Unavailable)
        );
    public bool TryBind(IWorldForwardedAuthority current) {
        lock (m_gate) {
            if (
                !m_disposed &&
                (m_current is null)
            ) {
                Volatile.Write(
                location: ref m_current,
                value: current
            ); return true;
            }
        }
        (current as IDisposable)?.Dispose();
        return false;
    }
    public bool TryDescribeRoute(out WorldAuthorityRouteDescription route, out string reason) {
        if (Volatile.Read(location: ref m_current) is { } current) {
            return current.TryDescribeRoute(
            reason: out reason,
            route: out route
        );
        }
        route = default; reason = Unavailable; return false;
    }
    public bool TryForwardIntent(in IntentSubmission submission, out string reason) {
        if (Volatile.Read(location: ref m_current) is { } current) {
            return current.TryForwardIntent(
            reason: out reason,
            submission: in submission
        );
        }
        reason = Unavailable; return false;
    }
    public bool TryForwardSubmission(WorldSubmissionPayload payload, out WorldSubmissionResult? result, out string reason) {
        if (Volatile.Read(location: ref m_current) is { } current) {
            return current.TryForwardSubmission(
            payload: payload,
            reason: out reason,
            result: out result
        );
        }
        result = null; reason = Unavailable; return false;
    }
    public bool TryForwardSubmission(WorldSubmissionPayload payload, Guid operationId, out WorldSubmissionResult? result, out string reason) {
        if (Volatile.Read(location: ref m_current) is { } current) {
            return current.TryForwardSubmission(
            operationId: operationId,
            payload: payload,
            reason: out reason,
            result: out result
        );
        }
        result = new WorldSubmissionResult.Refusal(
            Code: "world.forwarding.unavailable",
            Detail: Unavailable
        ); reason = Unavailable; return false;
    }
}
