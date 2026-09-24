using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The recorded-extension facade of <see cref="WorldServer"/> — the live integration epoch, its suppression latch,
/// the external-operation dispatcher's in-flight count, and the contribution journal a mounted
/// <see cref="WorldRecordedExtension"/> enqueues its admitted mutations through. The addon and machine hosts hang
/// off the same seam: a guest's act reaches the ordinary submission door here, never a private write path.
/// </summary>
/// <remarks>Every mutating member runs under <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>, because a
/// provider thread may attach, dispatch, or retire while the simulation thread is mid-tick. The facade holds the
/// state; <see cref="WorldServer"/> keeps the three public verbs that name it (<c>CaptureExternalOperationCause</c>,
/// <c>SuppressRecordedExtensions</c>, <c>StartRecordedExtensionEpoch</c>) as forwarders, so a composition root and
/// the law suites address the server exactly as they always have.</remarks>
public sealed class WorldExtensions {
    // The contributions a mounted extension enqueued and the simulation pump has not drained yet. Drained on the
    // pump, never on a provider thread: the tape's tick bucket must not race NoteTick.
    private readonly Queue<RecordedContribution> m_recordedContributions = new();
    private readonly WorldServer m_server;

    private long m_epoch;
    private int m_externalOperationsInFlight;
    private bool m_replayActive;
    private long m_sequence;
    private bool m_suppressed;

    private sealed record RecordedContribution(WorldRecordedExtension Owner, WorldMutation Mutation, long Sequence, Guid OperationId);

    /// <summary>Gets the live integration epoch. Every attached extension carries the epoch it was admitted under;
    /// a suppression bumps this, which is what retires a whole generation of runtimes at once.</summary>
    private long Epoch => m_epoch;

    /// <summary>Gets the number of external operations currently dispatched and not yet settled — the count a
    /// retirement freeze and a replay entry both refuse on.</summary>
    internal int ExternalOperationsInFlight => m_externalOperationsInFlight;
    /// <summary>Gets the number of enqueued, undrained contributions — the count a checkpoint capture refuses on,
    /// since a contribution that has not reached the submission door is not yet anywhere a checkpoint can record.</summary>
    internal int PendingContributionCount => m_recordedContributions.Count;

    /// <summary>Whether a mutation's every leaf names <paramref name="principal"/> and is covered by that runtime's
    /// own requested capability set — the pre-check a recorded extension runs before submitting, so a guest can
    /// never launder a mutation through a principal it did not request.</summary>
    /// <param name="mutation">The proposed mutation (a batch is admitted only when every member is).</param>
    /// <param name="principal">The runtime's own principal.</param>
    /// <param name="requests">The capabilities the runtime requested at mount.</param>
    /// <returns>Whether the request set covers the mutation.</returns>
    internal static bool RequestsAllow(WorldMutation mutation, Principal principal, IReadOnlyList<WorldCapabilityRequest> requests) =>
        ((mutation.Principal == principal) && ((mutation is WorldMutation.Batch batch)
            ? batch.Mutations.All(predicate: member => RequestsAllow(
                mutation: member,
                principal: principal,
                requests: requests
            ))
            : (WorldCapabilityRequests.Contains(
                requests,
                WorldCapability.Mutate,
                GrantSubject.Section(section: WorldDocument.SectionOf(mutation: mutation))
            ) ||
                ((WorldDocument.RowScopedMutateSubjectOf(mutation: mutation) is { } subject) && WorldCapabilityRequests.Contains(
                capability: WorldCapability.Mutate,
                requests: requests,
                subject: subject
            )))));

    /// <summary>Initializes the facade over the server whose authority gate and submission door it runs through.</summary>
    /// <param name="server">The owning server.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    internal WorldExtensions(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        m_server = server;
    }

    /// <summary>Admits a new runtime, returning the epoch it is bound to.</summary>
    /// <returns>The current live epoch.</returns>
    /// <exception cref="InvalidOperationException">This authority is suppressed for replay.</exception>
    internal long Admit() => m_server.ExecuteAuthorityOperation(operation: () => {
        if (m_suppressed) {
            throw new InvalidOperationException(message: "Recorded extensions cannot attach to a replay authority.");
        }
        return m_epoch;
    });
    /// <summary>Opens one external operation, returning the lease that closes it.</summary>
    /// <param name="epoch">The caller's admitted epoch.</param>
    /// <returns>A lease whose disposal settles the operation.</returns>
    /// <exception cref="InvalidOperationException">The epoch is retired.</exception>
    internal IDisposable BeginExternalOperation(long epoch) => m_server.ExecuteAuthorityOperation<IDisposable>(operation: () => {
        Check(epoch: epoch);
        m_externalOperationsInFlight = checked((m_externalOperationsInFlight + 1));
        return new ExternalOperationLease(extensions: this);
    });
    /// <summary>Captures a versioned causal checkpoint for a trusted host to commit alongside an external operation.
    /// This is authority-private recovery data; never disclose it to a provider as an observation.</summary>
    /// <param name="hostRow">The host's cross-instance state for this authority.</param>
    /// <returns>A base64 checkpoint prefixed by its recovery format name.</returns>
    /// <exception cref="InvalidOperationException">The authority is replaying or cannot currently checkpoint.</exception>
    internal string CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint hostRow) => m_server.ExecuteAuthorityOperation(operation: () => {
        Check(epoch: m_epoch);
        if (!m_server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: hostRow,
            reason: out var reason
        )) {
            throw new InvalidOperationException(message: $"External operation capture refused: {reason}");
        }
        return ("puck-checkpoint:" + Convert.ToBase64String(inArray: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!)));
    });
    /// <summary>Throws unless <paramref name="epoch"/> is the live one.</summary>
    /// <param name="epoch">The caller's admitted epoch.</param>
    /// <exception cref="InvalidOperationException">The epoch is retired or suppressed.</exception>
    internal void Check(long epoch) {
        if (!IsActive(epoch: epoch)) {
            throw new InvalidOperationException(message: "The extension belongs to a retired or replay authority timeline.");
        }
    }
    /// <summary>Closes an extension replay, restoring ordinary live dispatch.</summary>
    internal void CompleteReplay() => m_server.ExecuteAuthorityOperation(operation: () => m_replayActive = false);
    /// <summary>Drains every enqueued contribution through the ordinary admitted submission door — grants, budgets,
    /// validation and phase guards all still apply, and both mutation taps observe proposal and outcome.</summary>
    /// <remarks>Runs on the simulation pump, never on a provider thread.</remarks>
    internal void Drain() {
        while (m_recordedContributions.TryDequeue(result: out var contribution)) {
            contribution.Owner.ContributionDequeued();
            if (
                m_suppressed ||
                contribution.Owner.IsDisposed
            ) { continue; }
            var mutation = contribution.Mutation;
            var sequence = contribution.Sequence;
            // This is the ordinary admitted in-process door. MutationTap and MutationOutcomeTap record both the
            // proposal and its eventual admission result; grants, budgets, validation, and phase guards still apply.
            // Drain on the simulation pump, not on provider threads: the tape's tick bucket must not race NoteTick.
            m_server.Submit(new SubmissionEnvelope(
                ConnectionId: SubmissionEnvelope.RecordedExtensionConnectionId,
                SessionGeneration: 0,
                Sequence: sequence,
                CorrelationId: sequence,
                Principal: mutation.Principal,
                Payload: new WorldSubmissionPayload.Mutation(Value: mutation),
                OperationId: contribution.OperationId
            ));
        }
    }
    /// <summary>Enqueues one contribution for the next pump drain, returning its sequence number.</summary>
    /// <param name="owner">The contributing runtime.</param>
    /// <param name="epoch">The caller's admitted epoch.</param>
    /// <param name="mutation">The proposed mutation.</param>
    /// <returns>The contribution's sequence number.</returns>
    /// <exception cref="InvalidOperationException">The epoch is retired.</exception>
    internal long Enqueue(WorldRecordedExtension owner, long epoch, WorldMutation mutation) => m_server.ExecuteAuthorityOperation(operation: () => {
        Check(epoch: epoch);
        var sequence = checked(++m_sequence);

        m_recordedContributions.Enqueue(item: new(
            owner,
            mutation,
            sequence,
            Guid.NewGuid()
        ));
        return sequence;
    });
    /// <summary>Enters an extension replay: live dispatch is suppressed until <see cref="CompleteReplay"/>.</summary>
    internal void EnterReplay() => m_server.ExecuteAuthorityOperation(operation: () => {
        Suppress();
        m_replayActive = true;
    });
    /// <summary>Whether <paramref name="epoch"/> is the live, unsuppressed epoch.</summary>
    /// <param name="epoch">The caller's admitted epoch.</param>
    /// <returns>Whether the caller's timeline is still live.</returns>
    internal bool IsActive(long epoch) => (!m_suppressed && (epoch == m_epoch));
    /// <summary>Answers a submitted query on behalf of an admitted runtime.</summary>
    /// <param name="epoch">The caller's admitted epoch.</param>
    /// <param name="query">The query.</param>
    /// <param name="principal">The runtime's principal.</param>
    /// <returns>The query answer.</returns>
    /// <exception cref="InvalidOperationException">The epoch is retired.</exception>
    internal QueryAnswer Observe(long epoch, WorldQuery query, Principal principal) {
        Check(epoch: epoch);
        return m_server.AnswerSubmittedQuery(
            principal: principal,
            query: query
        );
    }
    /// <summary>Explicitly opens a new live integration epoch after recovery or a fork. Old runtime instances stay
    /// revoked. Only the trusted composition root may call this after choosing bindings and journal namespaces
    /// appropriate to the restored world; replay never calls it automatically.</summary>
    /// <exception cref="InvalidOperationException">An external call from the previous epoch is still in flight.</exception>
    internal void StartEpoch() => m_server.ExecuteAuthorityOperation(operation: () => {
        if (m_replayActive) {
            throw new InvalidOperationException(message: "A live integration epoch cannot start while replay is active.");
        }
        Suppress();
        m_suppressed = false;
    });
    /// <summary>Revokes live external execution and contribution capabilities before a replay replaces this
    /// timeline. Suppression lasts until the host explicitly opens a new live integration epoch.</summary>
    internal void Suppress() => m_server.ExecuteAuthorityOperation(operation: () => {
        if (m_externalOperationsInFlight != 0) {
            throw new InvalidOperationException(message: "An external operation is in flight; wait for dispatch to settle before replay.");
        }
        m_suppressed = true;
        m_epoch = checked((m_epoch + 1));
        while (m_recordedContributions.TryDequeue(result: out var contribution)) {
            contribution.Owner.ContributionDequeued();
        }
    });

    private sealed class ExternalOperationLease(WorldExtensions extensions) : IDisposable {
        private WorldExtensions? m_extensions = extensions;

        public void Dispose() {
            var owner = Interlocked.Exchange(
                location1: ref m_extensions,
                value: null
            );

            owner?.m_server.ExecuteAuthorityOperation(operation: () => owner.m_externalOperationsInFlight--);
        }
    }
}
