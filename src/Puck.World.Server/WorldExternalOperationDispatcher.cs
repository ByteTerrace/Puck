using System.Collections.Frozen;

namespace Puck.World.Server;

/// <summary>Dispatches committed external operations through explicitly registered host bindings.</summary>
/// <remarks>Call from a host worker, never from a simulation tick. There is no automatic retry of side effects.
/// A restart reconciles Dispatching/Running/Unknown records; terminal records return without invoking the provider.
/// Results remain durable even when the originating extension is unloaded. Publishing a result into gameplay is a
/// separate, normally phase-guarded mutation through <see cref="WorldRecordedExtension.Submit"/>.</remarks>
public sealed class WorldExternalOperationDispatcher {
    private readonly WorldRecordedExtension m_extension;
    private readonly WorldExternalOperationJournal m_journal;
    private readonly FrozenDictionary<string, IWorldExternalOperationProvider> m_bindings;

    /// <summary>Creates an optional dispatcher. Bindings carry credentials and resource scope supplied by the host.</summary>
    /// <param name="extension">The live runtime capability.</param>
    /// <param name="journal">The authority lineage's durable history.</param>
    /// <param name="bindings">Explicitly registered service bindings, copied at construction.</param>
    /// <exception cref="ArgumentException">A binding name is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">An argument or provider is null.</exception>
    public WorldExternalOperationDispatcher(WorldRecordedExtension extension, WorldExternalOperationJournal journal,
        IReadOnlyDictionary<string, IWorldExternalOperationProvider> bindings) {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(bindings);
        m_extension = extension;
        m_journal = journal;
        m_bindings = bindings.ToFrozenDictionary(StringComparer.Ordinal);
        foreach (var (name, provider) in m_bindings) {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Identity);
        }
    }

    /// <summary>Durably commits an operation and its causal image. This does not call the external service.</summary>
    /// <param name="operation">The request, naming a registered binding.</param>
    /// <param name="cause">Authority-private, versioned recovery evidence.</param>
    /// <param name="cancellationToken">Cancels persistence; read back an uncertain write before retrying.</param>
    /// <returns>The existing or newly persisted entry.</returns>
    /// <exception cref="InvalidOperationException">The timeline, binding, id, or journal capacity refuses.</exception>
    /// <exception cref="ObjectDisposedException">The originating runtime has been retired.</exception>
    public async ValueTask<WorldExternalOperationEntry> CommitAsync(WorldExternalOperation operation, string cause,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = m_extension.BeginDispatch();
        _ = Binding(operation);
        return await m_journal.CommitAsync(operation, cause, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Claims a pending operation durably before executing it. A duplicate or recovered request is only
    /// read back; call <see cref="ReconcileAsync"/> to resolve a nonterminal request that has already been claimed.</summary>
    /// <param name="id">The committed operation id.</param>
    /// <param name="cancellationToken">Cancels work without implying the effect was undone.</param>
    /// <returns>The durable delivery status, which may still be uncertain or running.</returns>
    /// <exception cref="InvalidOperationException">The request or binding is absent, or the timeline is suppressed.</exception>
    /// <exception cref="ObjectDisposedException">The originating runtime has been retired.</exception>
    public ValueTask<WorldExternalOperationEntry> DispatchAsync(string id, CancellationToken cancellationToken = default) =>
        ProcessAsync(id, reconcile: false, cancellationToken);

    /// <summary>Observes an already-claimed operation without repeating its side effect. Unknown remains a valid
    /// outcome when the provider offers no reliable status query.</summary>
    /// <param name="id">The committed operation id.</param>
    /// <param name="cancellationToken">Cancels provider and storage work.</param>
    /// <returns>The durable observed status; pending requests remain unexecuted.</returns>
    /// <exception cref="InvalidOperationException">The request or binding is absent, or the timeline is suppressed.</exception>
    /// <exception cref="ObjectDisposedException">The originating runtime has been retired.</exception>
    public ValueTask<WorldExternalOperationEntry> ReconcileAsync(string id, CancellationToken cancellationToken = default) =>
        ProcessAsync(id, reconcile: true, cancellationToken);

    private async ValueTask<WorldExternalOperationEntry> ProcessAsync(string id, bool reconcile, CancellationToken cancellationToken) {
        using var lease = m_extension.BeginDispatch();
        var entry = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry.Status is WorldExternalOperationStatus.Succeeded or WorldExternalOperationStatus.Failed) { return entry; }
        var provider = Binding(entry.Operation);
        if (reconcile) {
            if (entry.Status == WorldExternalOperationStatus.Pending) { return entry; }
        } else {
            if (entry.Status != WorldExternalOperationStatus.Pending) { return entry; }
            var claimed = new WorldExternalOperationResult(WorldExternalOperationStatus.Dispatching, "");
            if (!await m_journal.TryTransitionAsync(entry, claimed, cancellationToken).ConfigureAwait(false)) {
                return await FindAsync(id, cancellationToken).ConfigureAwait(false);
            }
            entry = entry with { Status = claimed.Status, Result = claimed.Result };
        }

        WorldExternalOperationResult result;
        try {
            result = reconcile
                ? await provider.ReconcileAsync(entry.Operation, cancellationToken).ConfigureAwait(false)
                : await provider.ExecuteAsync(entry.Operation, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Result is null || result.Status is not (
                WorldExternalOperationStatus.Running or WorldExternalOperationStatus.Succeeded or
                WorldExternalOperationStatus.Failed or WorldExternalOperationStatus.Unknown)) {
                throw new InvalidDataException("The provider returned an invalid operation outcome.");
            }
        } catch (Exception exception) {
            // A timeout, cancellation, or provider fault does not prove the effect did not happen. Do not persist
            // arbitrary exception messages: SDK errors can contain URLs, request bodies, or credentials.
            result = new WorldExternalOperationResult(WorldExternalOperationStatus.Unknown, exception.GetType().Name);
        }
        // If shutdown cancels this write, Dispatching remains durable and recovery reconciles it. A store failure
        // is never confused with provider failure and must never cause ExecuteAsync to run again.
        _ = await m_journal.TryTransitionAsync(entry, result, cancellationToken).ConfigureAwait(false);
        return await FindAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private IWorldExternalOperationProvider Binding(WorldExternalOperation operation) {
        if (!m_bindings.TryGetValue(operation.Binding, out var provider)) {
            throw new InvalidOperationException($"No external operation binding named '{operation.Binding}' is registered.");
        }
        if (!string.Equals(operation.BindingIdentity, provider.Identity, StringComparison.Ordinal)) {
            throw new InvalidOperationException("The external operation binding identity changed; the request must not be redirected.");
        }
        return provider;
    }

    private async ValueTask<WorldExternalOperationEntry> FindAsync(string id, CancellationToken cancellationToken) =>
        (await m_journal.ReadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(entry => entry.Operation.Id == id)
        ?? throw new InvalidOperationException($"No committed external operation named '{id}' exists.");
}
