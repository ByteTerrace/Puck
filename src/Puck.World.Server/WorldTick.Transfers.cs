using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldTick {
    private Func<string, bool>? m_transferAuthorityAllowed;
    // How many federated images the tape was last handed: the tape hears the held set every step while it is
    // non-empty, and once more as it empties.
    private int m_tapedFederatedIntents;
    private WireWriter? m_federatedHashWriter;

    internal void AppendFederatedInputHash(ref Fnv1aHash hash) {
        var writer = m_federatedHashWriter ??= new();
        writer.Reset();
        var count = 0;
        foreach (var state in m_federatedIntents) { if (state.Active) { count++; } }
        writer.WriteArrayCount(count);
        for (var index = 0; index < m_federatedIntents.Length; index++) {
            var state = m_federatedIntents[index];
            if (state.Active) { WorldWireLeaves.WriteFederatedInput(writer, new(index, state.Principal, state.Submission, state.Pending)); }
        }
        hash.Add(writer.WrittenSpan);
    }

    /// <summary>Installs the hosting boundary before admission. The predicate admits only authorities restored
    /// together with this world. It cannot be replaced during an activation.</summary>
    internal void ConstrainTransferAuthorities(Func<string, bool> allowed) {
        ArgumentNullException.ThrowIfNull(allowed);
        if (m_transferAuthorityAllowed is not null) { throw new InvalidOperationException(message: "transfer boundary is already installed"); }
        m_transferAuthorityAllowed = allowed;
    }

    // Admits every queued packet exactly once, then republishes only the accepted held image. A row is
    // accepted only while the same peer principal still occupies its slot; an onward transfer leaves the old row
    // inert, and slot reuse can never inherit it. ApplyIntentSubmission remains the one Drive/grant/input-hold door.
    private void ApplyFederatedIntents() {
        for (var index = 0; (index < m_federatedIntents.Length); index++) {
            ref var state = ref m_federatedIntents[index];

            if (
                !state.Active ||
                (Host.Body(index: index) is not { } body) ||
                !Host.Population.IsAdmittedPeer(bodyIndex: index) ||
                (Host.Population.PeerPrincipal(index: index) != state.Principal)
            ) {
                continue;
            }

            var admitted = false;
            foreach (var packet in state.Pending.Span) {
                var submission = packet with { Tick = Host.Input.Interval.Tick, EntityIndex = index, Principal = state.Principal,
                    Input = packet.Input with { Tick = Host.Input.Interval.Tick } };
                if (!ApplyIntentSubmission(body, submission).IsAccepted) { continue; }
                admitted = true;
                var held = submission.Input.Image;
                var projected = submission.Intent;
                var mapping = Host.Population.InputCatalog.Mappings[submission.Input.Mapping];
                foreach (var logical in mapping.Live) {
                    if (Host.Population.InputCatalog.Channels[logical].Shape != ChannelShape.Relative) { continue; }
                    var address = mapping.Address(logical);
                    held = held.WithRelative(address, default);
                    projected = projected.WithRelative(address, default);
                }
                var contributions = submission.Input.Contributions.ToArray();
                for (var source = 0; source < contributions.Length; source++) {
                    if (contributions[source].Kind == WorldInputContributionKind.Relative) {
                        contributions[source] = contributions[source] with { RawValue = 0 };
                    }
                }
                state = state with { Submission = submission with { Intent = projected,
                    Input = submission.Input with { Image = held, Events = default, Contributions = contributions } } };
            }
            state = state with { Pending = default };
            if (admitted) { continue; }
            if (state.Submission is not { } current) { continue; }
            var repeated = current with { Tick = Host.Input.Interval.Tick,
                Input = current.Input with { Tick = Host.Input.Interval.Tick } };
            _ = ApplyIntentSubmission(body, repeated);
        }
    }
    // Hands the tape the federated device images this step is about to apply — a forwarded or federated traveler's
    // input, which reaches this authority through no loopback — so a replay holds exactly the images this step held.
    private void TapFederatedIntents() {
        if (Host.FederatedIntentTap is not { } tap) {
            m_tapedFederatedIntents = 0;
            return;
        }

        var held = CaptureFederatedIntents();

        var count = held.Count;

        if (
            (count == 0) &&
            (m_tapedFederatedIntents == 0)
        ) {
            return;
        }
        tap(obj: held);
        m_tapedFederatedIntents = count;
    }

    /// <summary>Replaces every held federated device image with a taped set — the replay's door for the images a
    /// recorded step held. A replayed image belongs to no stream, so no stream can release it.</summary>
    internal void ReplaceFederatedIntents(IReadOnlyList<WorldFederatedIntentCheckpoint> held, bool resetRemoved = true) {
        lock (Host.AuthorityGate) {
            for (var index = 0; resetRemoved && index < m_federatedIntents.Length; index++) {
                var prior = m_federatedIntents[index];
                if (prior.Active && !held.Any(item => item.Index == index && item.Principal == prior.Principal)) { Host.Input.Reset(prior.Principal); }
            }
            Array.Clear(array: m_federatedIntents);
            foreach (var item in held) {
                var index = item.Index;
                if (((uint)index) < ((uint)m_federatedIntents.Length)) {
                    m_federatedIntents[index] = new WorldFederatedIntentState(
                        Active: true,
                        LeaseId: 0,
                        Principal: item.Principal,
                        Submission: item.Submission,
                        Pending: item.Pending
                    );
                }
            }
        }
    }
    internal static void ValidateFederatedInputs(IReadOnlyList<WorldFederatedIntentCheckpoint> inputs, int capacity) {
        var indices = new HashSet<int>();
        foreach (var input in inputs) {
            if ((uint)input.Index >= (uint)capacity || !indices.Add(input.Index) || input.Principal.Kind != PrincipalKind.Peer ||
                input.Principal.Index != input.Index || !input.Principal.IsStamped || input.Pending.Length > 1024) {
                throw new ArgumentException("federated input checkpoint has an invalid owner, target or queue");
            }
            if (input.Submission is { } held) { Validate(held); }
            foreach (var pending in input.Pending.Span) { Validate(pending); }
            void Validate(IntentSubmission submission) {
                if (submission.Principal != input.Principal || submission.EntityIndex != input.Index || submission.InputOwner is not null) {
                    throw new ArgumentException("federated input checkpoint has a foreign actor or local controller");
                }
            }
        }
    }
    internal IReadOnlyList<WorldFederatedIntentCheckpoint> CaptureFederatedIntents() {
        var held = new List<WorldFederatedIntentCheckpoint>();
        for (var index = 0; index < m_federatedIntents.Length; index++) {
            ref readonly var state = ref m_federatedIntents[index];
            if (state.Active) { held.Add(new(index, state.Principal, state.Submission, state.Pending)); }
        }
        return held;
    }
    /// <summary>Releases a reservation before commit. A destination that already committed ignores the abort.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    internal void AbortTransfer(string sourceAuthority, ulong transferId) =>
        Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.Abort(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));
    /// <summary>Retires the acknowledged transaction while preserving stable mobility replay protection.</summary>
    internal void AcknowledgeTransfer(string sourceAuthority, ulong transferId) =>
        Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.Acknowledge(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));
    /// <summary>Clears one exact authenticated arrival-border latch after reciprocal hysteresis is satisfied.
    /// Callers already execute under the authority operation gate.</summary>
    internal bool ClearTransferArrivalBorder(int bodyIndex, string expectedBorder) =>
        Host.TransferEscrow.ClearArrivalBorder(
            bodyIndex: bodyIndex,
            expectedBorder: expectedBorder
        );
    /// <summary>Commits detached bodies into a live reservation. A repeated committed id is idempotently accepted;
    /// an expired or absent reservation is refused; an arrival whose record the crossing log answered uncertain lands
    /// nothing and is narrated on <c>world.transfer</c>.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    /// <param name="members">The travelers in reservation order.</param>
    /// <param name="reason">The named refusal or uncertainty, or empty on success.</param>
    /// <returns><see cref="WorldTransferStatus.Committed"/> when the commit is authoritative here,
    /// <see cref="WorldTransferStatus.Uncertain"/> while only this authority's recovery can decide, and
    /// <see cref="WorldTransferStatus.Missing"/> for a refusal.</returns>
    internal WorldTransferStatus CommitTransfer(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        if (
            (m_transferAuthorityAllowed is not null) &&
            !m_transferAuthorityAllowed(sourceAuthority)
        ) {
            reason = "closed rewind group refuses an external transfer"; return WorldTransferStatus.Missing;
        }
        var resolvedReason = string.Empty;
        var status = Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.Commit(
            members: members,
            reason: out resolvedReason,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));

        reason = resolvedReason;
        if (
            (status == WorldTransferStatus.Uncertain) &&
            Host.Output.HasNarrationSink
        ) {
            Host.Output.Narrate(
                channel: "world.transfer",
                text: $"[world.transfer: transfer={transferId} from '{sourceAuthority}' UNCERTAIN in '{Host.AuthorityIdentity}' — {resolvedReason}]"
            );
        }
        return status;
    }
    /// <summary>Publishes one authenticated federation stream's latest device image. The image is held as replicated
    /// input state and reapplied once per destination tick; it is not consumed merely because this socket update was
    /// sparse relative to the destination clock.</summary>
    internal void PublishFederatedIntent(long leaseId, in IntentSubmission submission) {
        if (
            (leaseId <= 0) ||
            (((uint)submission.EntityIndex) >= ((uint)m_federatedIntents.Length))
        ) {
            return;
        }

        var published = submission;

        Host.ExecuteAuthorityOperation(operation: () => {
            ref var state = ref m_federatedIntents[published.EntityIndex];

            if (!state.Active || state.LeaseId != leaseId || state.Principal != published.Principal) {
                if (state.Active) { Host.Input.Reset(state.Principal); }
                state = new(leaseId, published.Principal, default, true);
            }
            var maximum = Math.Min(1024, (Host.Definition.InputHistory?.MaximumEventsPerStep ?? 128) + 1);
            if (state.Pending.Length >= maximum) {
                // Overflow cancels the episode; it never silently drops an unobserved edge.
                published = published with { Intent = default,
                    Input = published.Input with { Image = default, Contributions = default, Events = default, CaptureLost = true } };
                state = state with { Pending = new[] { published } };
            } else {
                var pending = new IntentSubmission[state.Pending.Length + 1];
                state.Pending.Span.CopyTo(pending);
                pending[^1] = published;
                state = state with { Pending = pending };
            }
        });
    }
    /// <summary>Releases every device image still owned by one closing federation stream. Lease comparison makes
    /// reconnect replacement atomic: a superseded stream cannot release the newer writer. A retired destination
    /// needs no stream cleanup and keeps its frozen checkpoint unchanged.</summary>
    internal void ReleaseFederatedIntents(long leaseId) {
        if (leaseId <= 0) {
            return;
        }

        lock (Host.AuthorityGate) {
            if (Host.AuthorityRetiring) { return; }
            for (var index = 0; (index < m_federatedIntents.Length); index++) {
                if (
                    m_federatedIntents[index].Active &&
                    (m_federatedIntents[index].LeaseId == leaseId)
                ) {
                    Host.Input.Reset(m_federatedIntents[index].Principal);
                    m_federatedIntents[index] = default;
                }
            }
        }
    }
    /// <summary>Reserves destination body indices under a binding transfer lease. The same method backs loopback
    /// colocation and the QUIC authority door; callers never reserve population capacity by inspecting it directly.</summary>
    /// <param name="request">The source-tick deadline, border policy, and prospective travelers.</param>
    /// <returns>The destination's verdict and assigned body indices.</returns>
    internal WorldTransferReservationReply ReserveTransfer(WorldTransferReservationRequest request) =>
        Host.ExecuteAuthorityOperation(operation: () => (((m_transferAuthorityAllowed is not null) && !m_transferAuthorityAllowed(request.SourceAuthority))
            ? WorldTransferReservationReply.Refused(reason: "closed rewind group refuses an external transfer")
            : Host.TransferEscrow.Reserve(request: request)));
    /// <summary>Terminally retires a traveler incarnation after its accepted leave has propagated through this hop.</summary>
    internal void RetireTransferredMobility(in WorldMobilityIdentity mobility) {
        var credential = mobility;

        Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.RetireMobility(mobility: in credential));
    }
    /// <summary>Returns the destination's idempotent view of a source-scoped transfer.</summary>
    internal WorldTransferStatus TransferStatus(string sourceAuthority, ulong transferId) =>
        Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.Status(
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));
    /// <summary>Reads the authenticated source-border identity for an active escrow-arrived body. Callers already
    /// under the authority operation gate use this to apply reciprocal adjacency hysteresis.</summary>
    internal bool TryTransferArrivalBorder(int bodyIndex, out string border) =>
        Host.TransferEscrow.TryArrivalBorder(
            bodyIndex: bodyIndex,
            border: out border
        );
    /// <summary>Resolves the ordinary peer principal a committed federated transfer assigned.</summary>
    internal bool TryTransferredPrincipal(string sourceAuthority, ulong transferId, int ordinal, out Principal principal) {
        var resolved = default(Principal);
        var found = Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.TryCommittedPrincipal(
            ordinal: ordinal,
            principal: out resolved,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ));

        principal = resolved;
        return found;
    }
    /// <summary>Resolves a stable incarnation/epoch credential without retaining its disposable transfer id.</summary>
    internal bool TryTransferredPrincipal(string sourceAuthority, in WorldMobilityIdentity mobility, out Principal principal) {
        var resolved = default(Principal);
        var credential = mobility;
        var found = Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.TryMobilityPrincipal(
            mobility: in credential,
            principal: out resolved,
            sourceAuthority: sourceAuthority
        ));

        principal = resolved;
        return found;
    }
}
