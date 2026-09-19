using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>An explicitly composed, trusted provider's mutation boundary. Provider computation stays off the
/// simulation thread; contributions use the same authority, validation, metering, and replay path as other inputs.</summary>
/// <remarks>This object holds no credentials and executes no provider code under the authority lock. A mutation
/// submission returns a correlation id, not an application verdict. Use normal world read-back or mutation outcomes
/// to observe acceptance. Dispose retires this instance; its durable external operations remain in their journal.</remarks>
public sealed class WorldRecordedExtension : IWorldExtensionRuntime {
    private readonly long m_epoch;
    private readonly int m_maximumPendingContributions;
    private readonly WorldCapabilityRequest[] m_requests;
    private readonly WorldServer m_server;

    private bool m_disposed;
    private int m_pendingContributions;

    internal bool IsDisposed => m_disposed;

    /// <summary>Gets whether this runtime is still live. This is diagnostic only; the operation dispatcher holds
    /// an authority lifetime lease across actual external calls.</summary>
    public bool IsActive => m_server.ExecuteAuthorityOperation(operation: () => (!m_disposed && m_server.Extensions.IsActive(epoch: m_epoch)));
    /// <summary>Gets how many copied contributions are awaiting the simulation pump.</summary>
    public int PendingCount => m_server.ExecuteAuthorityOperation(operation: () => m_pendingContributions);
    /// <summary>Gets the provider's acting identity, fixed for this runtime instance.</summary>
    public WorldPrincipal Principal { get; }
    /// <inheritdoc/>
    public WorldExtensionReplayPolicy ReplayPolicy => WorldExtensionReplayPolicy.Recorded;

    internal IDisposable BeginDispatch() => m_server.ExecuteAuthorityOperation(operation: () => {
        CheckActive();
        return m_server.Extensions.BeginExternalOperation(epoch: m_epoch);
    });
    internal void ContributionDequeued() => m_pendingContributions--;

    private void CheckActive() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_server.Extensions.Check(epoch: m_epoch);
    }

    /// <inheritdoc/>
    public void Dispose() => m_server.ExecuteAuthorityOperation(operation: () => m_disposed = true);
    /// <summary>Reads through the ordinary Observe gate and visibility projection. The manifest must request
    /// the query's observation subject. No provider delegate runs while the authority is locked.</summary>
    /// <param name="query">The typed read request.</param>
    /// <returns>The authority's read-back, including an explicit refusal when permission is absent.</returns>
    public QueryAnswer Observe(WorldQuery query) {
        ArgumentNullException.ThrowIfNull(query);
        return m_server.ExecuteAuthorityOperation(operation: () => {
            CheckActive();
            if (!WorldCapabilityRequests.Contains(
                m_requests,
                WorldCapability.Observe,
                query.ObservationSubject()
            )) {
                throw new InvalidOperationException(message: "The observation exceeds the extension's requested subjects.");
            }
            return m_server.Extensions.Observe(
                m_epoch,
                query,
                Principal
            );
        });
    }
    /// <summary>Submits an immutable contribution. Returns its correlation id; the next edit drain decides acceptance.
    /// The trusted extension mints a distinct operation id when the contribution is admitted, and the id remains on
    /// the recorded envelope through replay.</summary>
    /// <param name="mutation">A mutation bearing this provider's identity.</param>
    /// <exception cref="InvalidOperationException">The identity, requested scope, or timeline does not admit submission.</exception>
    /// <exception cref="ObjectDisposedException">This runtime instance was retired.</exception>
    public long Submit(WorldMutation mutation) {
        ArgumentNullException.ThrowIfNull(mutation);
        if (
            !WorldSubmissionCodec.TryEncodeMutation(
            bytes: out var encoded,
            failure: out var failure,
            mutation: mutation
        ) ||
            !WorldSubmissionCodec.TryDecodeMutation(
            bytes: encoded,
            failure: out failure,
            mutation: out var frozen
        ) ||
            (frozen is null)
        ) {
            throw new InvalidOperationException(message: $"Invalid extension contribution: {failure}");
        }
        return m_server.ExecuteAuthorityOperation(operation: () => {
            CheckActive();
            if (!WorldExtensions.RequestsAllow(
                mutation: frozen,
                principal: Principal,
                requests: m_requests
            )) {
                throw new InvalidOperationException(message: "The contribution exceeds the extension's identity or requested sections.");
            }
            if (m_pendingContributions >= m_maximumPendingContributions) {
                throw new InvalidOperationException(message: "The extension's pending contribution budget is exhausted.");
            }
            var sequence = m_server.Extensions.Enqueue(
                epoch: m_epoch,
                mutation: frozen,
                owner: this
            );

            m_pendingContributions++;
            return sequence;
        });
    }

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
        m_epoch = server.Extensions.Admit();
    }
}
