using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private long m_extensionEpoch;
    private bool m_recordedExtensionsSuppressed;
    private bool m_extensionReplayActive;
    private int m_externalOperationsInFlight;
    private long m_extensionSequence;
    private readonly Queue<RecordedContribution> m_recordedContributions = new();

    private sealed record RecordedContribution(WorldRecordedExtension Owner, WorldMutation Mutation, long Sequence, Guid OperationId);

    /// <summary>Captures a versioned causal checkpoint for a trusted host to commit alongside an external operation.
    /// This is authority-private recovery data; never disclose it to a provider as an observation.</summary>
    /// <param name="hostRow">The host's cross-instance state for this authority.</param>
    /// <returns>A base64 checkpoint prefixed by its recovery format name.</returns>
    /// <exception cref="InvalidOperationException">The authority is replaying or cannot currently checkpoint.</exception>
    public string CaptureExternalOperationCause(WorldAuthorityHostRowCheckpoint hostRow) => ExecuteAuthorityOperation(() => {
        CheckRecordedExtension(m_extensionEpoch);
        if (!TryCaptureCheckpoint(hostRow, out var checkpoint, out var reason)) {
            throw new InvalidOperationException($"External operation capture refused: {reason}");
        }
        return "puck-checkpoint:" + Convert.ToBase64String(WorldAuthorityCheckpointCodec.Encode(checkpoint!));
    });

    internal QueryAnswer ObserveRecordedExtension(long epoch, WorldQuery query, WorldPrincipal principal) {
        CheckRecordedExtension(epoch);
        return AnswerSubmittedQuery(query, principal);
    }

    internal static bool ExtensionRequestsAllow(WorldMutation mutation, WorldPrincipal principal, IReadOnlyList<WorldCapabilityRequest> requests) =>
        mutation.Principal == principal && (mutation is WorldMutation.Batch batch
            ? batch.Mutations.All(member => ExtensionRequestsAllow(member, principal, requests))
            : WorldCapabilityRequests.Contains(requests, WorldCapability.Mutate, GrantSubject.Section(SectionOf(mutation))) ||
                (RowScopedMutateSubjectOf(mutation) is { } subject && WorldCapabilityRequests.Contains(requests, WorldCapability.Mutate, subject)));

    /// <summary>Revokes live external execution and contribution capabilities before a replay replaces this
    /// timeline. Suppression lasts until the host explicitly opens a new live integration epoch.</summary>
    public void SuppressRecordedExtensions() => ExecuteAuthorityOperation(() => {
        if (m_externalOperationsInFlight != 0) {
            throw new InvalidOperationException("An external operation is in flight; wait for dispatch to settle before replay.");
        }
        m_recordedExtensionsSuppressed = true;
        m_extensionEpoch = checked(m_extensionEpoch + 1);
        while (m_recordedContributions.TryDequeue(out var contribution)) {
            contribution.Owner.ContributionDequeued();
        }
    });

    /// <summary>Explicitly opens a new live integration epoch after recovery or a fork. Old runtime instances stay
    /// revoked. Only the trusted composition root may call this after choosing bindings and journal namespaces
    /// appropriate to the restored world; replay never calls it automatically.</summary>
    /// <exception cref="InvalidOperationException">An external call from the previous epoch is still in flight.</exception>
    public void StartRecordedExtensionEpoch() => ExecuteAuthorityOperation(() => {
        if (m_extensionReplayActive) {
            throw new InvalidOperationException("A live integration epoch cannot start while replay is active.");
        }
        SuppressRecordedExtensions();
        m_recordedExtensionsSuppressed = false;
    });

    internal void EnterExtensionReplay() => ExecuteAuthorityOperation(() => {
        SuppressRecordedExtensions();
        m_extensionReplayActive = true;
    });

    internal void CompleteExtensionReplay() => ExecuteAuthorityOperation(() => m_extensionReplayActive = false);

    internal bool IsRecordedExtensionActive(long epoch) => !m_recordedExtensionsSuppressed && epoch == m_extensionEpoch;

    internal IDisposable BeginExternalOperation(long epoch) => ExecuteAuthorityOperation<IDisposable>(() => {
        CheckRecordedExtension(epoch);
        m_externalOperationsInFlight = checked(m_externalOperationsInFlight + 1);
        return new ExternalOperationLease(this);
    });

    private sealed class ExternalOperationLease(WorldServer server) : IDisposable {
        private WorldServer? m_server = server;
        public void Dispose() {
            var owner = Interlocked.Exchange(ref m_server, null);
            owner?.ExecuteAuthorityOperation(() => owner.m_externalOperationsInFlight--);
        }
    }

    internal long AdmitRecordedExtension() => ExecuteAuthorityOperation(() => {
        if (m_recordedExtensionsSuppressed) {
            throw new InvalidOperationException("Recorded extensions cannot attach to a replay authority.");
        }
        return m_extensionEpoch;
    });

    internal void CheckRecordedExtension(long epoch) {
        if (!IsRecordedExtensionActive(epoch)) {
            throw new InvalidOperationException("The extension belongs to a retired or replay authority timeline.");
        }
    }

    internal long EnqueueRecordedExtension(WorldRecordedExtension owner, long epoch, WorldMutation mutation) => ExecuteAuthorityOperation(() => {
        CheckRecordedExtension(epoch);
        var sequence = checked(++m_extensionSequence);
        m_recordedContributions.Enqueue(new(owner, mutation, sequence, Guid.NewGuid()));
        return sequence;
    });

    private void DrainRecordedExtensions() {
        while (m_recordedContributions.TryDequeue(out var contribution)) {
            contribution.Owner.ContributionDequeued();
            if (m_recordedExtensionsSuppressed || contribution.Owner.IsDisposed) { continue; }
            var mutation = contribution.Mutation;
            var sequence = contribution.Sequence;
            // This is the ordinary admitted in-process door. MutationTap and MutationOutcomeTap record both the
            // proposal and its eventual admission result; grants, budgets, validation, and phase guards still apply.
            // Drain on the simulation pump, not on provider threads: the tape's tick bucket must not race NoteTick.
            Submit(new SubmissionEnvelope(
                ConnectionId: SubmissionEnvelope.RecordedExtensionConnectionId,
                SessionGeneration: 0,
                Sequence: sequence,
                CorrelationId: sequence,
                Principal: mutation.Principal,
                Payload: new WorldSubmissionPayload.Mutation(mutation),
                OperationId: contribution.OperationId));
        }
    }
}
