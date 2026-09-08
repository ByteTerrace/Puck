using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>An explicitly composed, trusted provider's mutation boundary. Provider computation stays off the
/// simulation thread; contributions use the same authority, validation, metering, and replay path as other inputs.</summary>
/// <remarks>This object holds no credentials and executes no provider code under the authority lock. A mutation
/// submission returns a correlation id, not an application verdict. Use normal world read-back or mutation outcomes
/// to observe acceptance. Dispose retires this instance; its durable external operations remain in their journal.</remarks>
public sealed class WorldRecordedExtension : IWorldExtensionRuntime {
    private readonly WorldServer m_server;
    private readonly long m_epoch;
    private readonly WorldCapabilityRequest[] m_requests;
    private readonly int m_maximumPendingContributions;
    private int m_pendingContributions;
    private bool m_disposed;
    internal bool IsDisposed => m_disposed;
    internal void ContributionDequeued() => m_pendingContributions--;

    /// <summary>Creates an optional provider boundary. Requests restrict its reach; they never grant permission.</summary>
    /// <param name="server">The live authority.</param>
    /// <param name="principal">The provider identity resolved by the trusted composition root.</param>
    /// <param name="requests">The same capability manifest shape used by WASM addon rows.</param>
    /// <param name="maximumPendingContributions">Bounds queued work before the ordinary apply-time grant budgets run.</param>
    public WorldRecordedExtension(WorldServer server, WorldPrincipal principal, IEnumerable<WorldCapabilityRequest> requests, int maximumPendingContributions) {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingContributions);
        m_server = server;
        Principal = principal;
        m_requests = [.. requests];
        m_maximumPendingContributions = maximumPendingContributions;
        m_epoch = server.AdmitRecordedExtension();
    }

    /// <summary>Gets the provider's acting identity, fixed for this runtime instance.</summary>
    public WorldPrincipal Principal { get; }
    /// <summary>Gets how many copied contributions are awaiting the simulation pump.</summary>
    public int PendingCount => m_server.ExecuteAuthorityOperation(() => m_pendingContributions);
    /// <inheritdoc/>
    public WorldExtensionReplayPolicy ReplayPolicy => WorldExtensionReplayPolicy.Recorded;

    /// <summary>Submits an immutable contribution. Returns its correlation id; the next edit drain decides acceptance.</summary>
    /// <param name="mutation">A mutation bearing this provider's identity.</param>
    /// <exception cref="InvalidOperationException">The identity, requested scope, or timeline does not admit submission.</exception>
    /// <exception cref="ObjectDisposedException">This runtime instance was retired.</exception>
    public long Submit(WorldMutation mutation) {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!WorldSubmissionCodec.TryEncodeMutation(mutation, out var encoded, out var failure) ||
            !WorldSubmissionCodec.TryDecodeMutation(encoded, out var frozen, out failure) || frozen is null) {
            throw new InvalidOperationException($"Invalid extension contribution: {failure}");
        }
        return m_server.ExecuteAuthorityOperation(() => {
            CheckActive();
            if (!WorldServer.ExtensionRequestsAllow(frozen, Principal, m_requests)) {
                throw new InvalidOperationException("The contribution exceeds the extension's identity or requested sections.");
            }
            if (m_pendingContributions >= m_maximumPendingContributions) {
                throw new InvalidOperationException("The extension's pending contribution budget is exhausted.");
            }
            var sequence = m_server.EnqueueRecordedExtension(this, m_epoch, frozen);
            m_pendingContributions++;
            return sequence;
        });
    }

    /// <summary>Gets whether this runtime is still live. This is diagnostic only; the operation dispatcher holds
    /// an authority lifetime lease across actual external calls.</summary>
    public bool IsActive => m_server.ExecuteAuthorityOperation(() => !m_disposed && m_server.IsRecordedExtensionActive(m_epoch));

    /// <summary>Reads through the ordinary Observe gate and visibility projection. The manifest must request
    /// the query's observation subject. No provider delegate runs while the authority is locked.</summary>
    /// <param name="query">The typed read request.</param>
    /// <returns>The authority's read-back, including an explicit refusal when permission is absent.</returns>
    public QueryAnswer Observe(WorldQuery query) {
        ArgumentNullException.ThrowIfNull(query);
        return m_server.ExecuteAuthorityOperation(() => {
            CheckActive();
            if (!WorldCapabilityRequests.Contains(m_requests, WorldCapability.Observe, query.ObservationSubject())) {
                throw new InvalidOperationException("The observation exceeds the extension's requested subjects.");
            }
            return m_server.ObserveRecordedExtension(m_epoch, query, Principal);
        });
    }

    internal IDisposable BeginDispatch() => m_server.ExecuteAuthorityOperation(() => {
        CheckActive();
        return m_server.BeginExternalOperation(m_epoch);
    });

    private void CheckActive() {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        m_server.CheckRecordedExtension(m_epoch);
    }

    /// <inheritdoc/>
    public void Dispose() => m_server.ExecuteAuthorityOperation(() => m_disposed = true);
}
