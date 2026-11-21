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
        writer.WriteArrayCount(count: count);
        for (var index = 0; (index < m_federatedIntents.Length); index++) {
            var state = m_federatedIntents[index];

            if (state.Active) { WorldWireLeaves.WriteFederatedInput(writer, new(index, state.Principal, state.Submission, state.Pending)); }
        }
        hash.Add(values: writer.WrittenSpan);
    }
    /// <summary>Installs the hosting boundary before admission. The predicate admits only authorities restored
    /// together with this world. It cannot be replaced during an activation.</summary>
    internal void ConstrainTransferAuthorities(Func<string, bool> allowed) {
        ArgumentNullException.ThrowIfNull(allowed);
        if (m_transferAuthorityAllowed is not null) { throw new InvalidOperationException(message: "transfer boundary is already installed"); }
        m_transferAuthorityAllowed = allowed;
    }

    // Admits every queued packet exactly once, in order, then republishes only the accepted held image. A row is
    // accepted only while the same peer principal still occupies its slot; an onward transfer leaves the old row
    // inert, and slot reuse can never inherit it. ApplyIntentSubmission remains the one Drive/grant/input-hold door.
    // Every event of the owner's current epoch is applied or the owner's input is restarted, which the authority
    // publishes: a packet is never skipped while later ones are applied over it, since each rests on the one before.
    private void ApplyFederatedIntents() {
        for (var index = 0; (index < m_federatedIntents.Length); index++) {
            ref var state = ref m_federatedIntents[index];

            if (!state.Active) { continue; }
            if (Host.Body(index: index) is not { } body) {
                NoteFederatedDrop(bodyIndex: index, reason: "its slot holds no body");
                continue;
            }
            if (!Host.Population.IsAdmittedPeer(bodyIndex: index)) {
                NoteFederatedDrop(bodyIndex: index, reason: "its body is not an admitted peer's");
                continue;
            }
            if (Host.Population.PeerPrincipal(index: index) != state.Principal) {
                NoteFederatedDrop(bodyIndex: index, reason: $"its slot belongs to {Host.Population.PeerPrincipal(index: index).Describe()}, not {state.Principal.Describe()}");
                continue;
            }

            var admitted = false;
            var restarted = false;

            foreach (var packet in state.Pending.Span) {
                WorldTickWork.Process.Add(WorldTickWork.FederatedBatches, 1);
                if (LeftEpoch(principal: state.Principal, batch: packet.Input, held: out var owner)) {
                    // The ordinary state of a stream for a moment after any epoch change: its sender has not yet taken
                    // the epoch this authority published. Restarting for it would restart again for every packet in
                    // flight.
                    WorldTickWork.Process.Add(WorldTickWork.FederatedStale, 1);
                    NoteFederatedDrop(bodyIndex: index, reason: $"its batches name input epoch {packet.Input.Epoch} of revision {packet.Input.Revision}; this authority holds epoch {owner!.Epoch} of revision {owner.Revision}");
                    continue;
                }

                var submission = packet with {
                    Tick = Host.Input.Interval.Tick,
                    EntityIndex = index,
                    Principal = state.Principal,
                    Input = packet.Input with { Tick = Host.Input.Interval.Tick },
                };
                var epoch = (owner?.Epoch ?? 0UL);
                var retimed = (owner?.RetimedEvents ?? 0L);
                var admission = ApplyIntentSubmission(body: body, remote: true, submission: submission);

                if (owner is not null) { WorldTickWork.Process.Add(WorldTickWork.FederatedRetimed, (owner.RetimedEvents - retimed)); }
                if (!admission.IsAccepted) {
                    var refusal = (admission.InputRefusal ?? admission.Permission.DescribeRefusal(
                        actor: state.Principal,
                        subject: $"body:{index}",
                        verb: "drive"
                    ));

                    if (!admission.Permission.IsAllowed || refusal.StartsWith(value: "input.permission", comparisonType: StringComparison.Ordinal) || refusal.StartsWith(value: "input.owner", comparisonType: StringComparison.Ordinal)) {
                        // Authority, not input: the same packet is taken again once the grant returns.
                        NoteFederatedDrop(bodyIndex: index, reason: refusal);
                        continue;
                    }
                    // A refused packet of the current epoch leaves every later one resting on input this authority
                    // never took, so the owner's input starts again; an admission that reset the owner itself (a lost
                    // capture, an overflow) has already moved the epoch.
                    if ((owner is not null) && (owner.Epoch == epoch)) { Host.Input.Reset(state.Principal); }
                    WorldTickWork.Process.Add(WorldTickWork.FederatedRestarts, 1);
                    NoteFederatedDrop(bodyIndex: index, reason: $"its input was restarted: {refusal}");
                    restarted = true;
                    break;
                }
                NoteFederatedDrop(bodyIndex: index, reason: null);
                admitted = true;
                var held = submission.Input.Image;
                var projected = submission.Intent;
                var mapping = Host.Population.InputCatalog.Mappings[submission.Input.Mapping];

                foreach (var logical in mapping.Live) {
                    if (Host.Population.InputCatalog.Channels[logical].Shape != ChannelShape.Relative) { continue; }
                    var address = mapping.Address(logicalId: logical);

                    held = held.WithRelative(ordinal: address, value: default);
                    projected = projected.WithRelative(ordinal: address, value: default);
                }
                state = state with {
                    Submission = submission with {
                        Intent = projected,
                        Input = submission.Input with { Image = held, Events = default, Contributions = WithoutRelative(contributions: submission.Input.Contributions) },
                    },
                };
            }
            state = state with { Pending = default };
            if (restarted) {
                state = state with { Submission = null };
                continue;
            }
            if (admitted) { continue; }
            if (state.Submission is not { } current) { continue; }
            if (LeftEpoch(principal: state.Principal, batch: current.Input, held: out _)) {
                // The held image belongs to an epoch this authority left; its sender republishes it in the new one.
                state = state with { Submission = null };
                continue;
            }
            var repeated = current with {
                Tick = Host.Input.Interval.Tick,
                Input = current.Input with { Tick = Host.Input.Interval.Tick },
            };

            _ = ApplyIntentSubmission(body: body, remote: true, submission: repeated);
        }
    }
    // Whether a batch names an input epoch its owner has left; an owner this authority has not admitted leaves none.
    private bool LeftEpoch(Principal principal, in WorldInputBatch batch, out WorldInputOwner? held) =>
        (Host.Input.TryGet(input: out held, principal: principal) && ((batch.Revision != held!.Revision) || (batch.Epoch != held.Epoch)));
    // The held image's sources with every relative source at rest: a relative source reports movement since the last
    // batch, which a republished image must not repeat. Copies only when one moved.
    private static ReadOnlyMemory<WorldInputContribution> WithoutRelative(ReadOnlyMemory<WorldInputContribution> contributions) {
        var moved = false;

        foreach (var contribution in contributions.Span) {
            if ((contribution.Kind == WorldInputContributionKind.Relative) && (contribution.RawValue != 0)) {
                moved = true;
                break;
            }
        }
        if (!moved) { return contributions; }

        var copy = contributions.ToArray();

        for (var source = 0; (source < copy.Length); source++) {
            if (copy[source].Kind == WorldInputContributionKind.Relative) { copy[source] = copy[source] with { RawValue = 0 }; }
        }

        return copy;
    }
    /// <summary>Names a federated input this authority did not apply, once when the reason starts or changes, so a
    /// remote seat whose body stands still says why. A <see langword="null"/> reason marks the body as taking input
    /// again.</summary>
    /// <param name="bodyIndex">The body the input names; an input that names none shares one key.</param>
    /// <param name="reason">Why the input was dropped, or <see langword="null"/>.</param>
    internal void NoteFederatedDrop(int bodyIndex, string? reason) {
        var key = ((((uint)bodyIndex) < ((uint)m_federatedIntents.Length)) ? bodyIndex : -1);

        if (reason is null) {
            _ = m_federatedDrops.Clear(key: key);

            return;
        }
        if (m_federatedDrops.Set(key: key, reason: reason) && Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.input.federated",
                text: $"[world.input.federated: body:{bodyIndex} input dropped: {reason}]"
            );
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
            for (var index = 0; (resetRemoved && (index < m_federatedIntents.Length)); index++) {
                var prior = m_federatedIntents[index];

                if (prior.Active && !held.Any(predicate: item => ((item.Index == index) && (item.Principal == prior.Principal)))) { Host.Input.Reset(prior.Principal); }
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
            if ((((uint)input.Index) >= ((uint)capacity)) || !indices.Add(item: input.Index) || (input.Principal.Kind != PrincipalKind.Peer) ||
                (input.Principal.Index != input.Index) || !input.Principal.IsStamped || (input.Pending.Length > 1024)) {
                throw new ArgumentException(message: "federated input checkpoint has an invalid owner, target or queue");
            }
            if (input.Submission is { } held) { Validate(submission: held); }
            foreach (var pending in input.Pending.Span) { Validate(submission: pending); }
            void Validate(IntentSubmission submission) {
                if ((submission.Principal != input.Principal) || (submission.EntityIndex != input.Index) || (submission.InputOwner is not null)) {
                    throw new ArgumentException(message: "federated input checkpoint has a foreign actor or local controller");
                }
            }
        }
    }
    internal IReadOnlyList<WorldFederatedIntentCheckpoint> CaptureFederatedIntents() {
        var held = new List<WorldFederatedIntentCheckpoint>();

        for (var index = 0; (index < m_federatedIntents.Length); index++) {
            ref readonly var state = ref m_federatedIntents[index];

            if (state.Active) { held.Add(item: new(index, state.Principal, state.Submission, state.Pending)); }
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
    /// <param name="confirmed">Home's answers to this destination's question for this commit, keyed by hop key and
    /// incarnation.</param>
    /// <returns><see cref="WorldTransferStatus.Committed"/> when the commit is authoritative here,
    /// <see cref="WorldTransferStatus.Uncertain"/> while only this authority's recovery can decide, and
    /// <see cref="WorldTransferStatus.Missing"/> for a refusal.</returns>
    internal WorldTransferStatus CommitTransfer(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason, IReadOnlyDictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), ulong>? confirmed = null) {
        if (
            (m_transferAuthorityAllowed is not null) &&
            !m_transferAuthorityAllowed(sourceAuthority)
        ) {
            reason = "closed rewind group refuses an external transfer"; return WorldTransferStatus.Missing;
        }
        var resolvedReason = string.Empty;
        // A commit waiting on a crossing record in flight is decided again when it settles: it is not uncertain.
        var (status, waiting) = Host.ExecuteAuthorityOperation(operation: () => (Host.TransferEscrow.Commit(
            confirmed: confirmed,
            members: members,
            reason: out resolvedReason,
            sourceAuthority: sourceAuthority,
            transferId: transferId
        ), Host.CrossingInFlight));

        reason = resolvedReason;
        if (
            (status == WorldTransferStatus.Uncertain) &&
            !waiting &&
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
            NoteFederatedDrop(bodyIndex: submission.EntityIndex, reason: $"lease {leaseId} or body index {submission.EntityIndex} names no held-input row");
            return;
        }

        var published = submission;

        Host.ExecuteAuthorityOperation(operation: () => {
            ref var state = ref m_federatedIntents[published.EntityIndex];

            if (!state.Active || (state.LeaseId != leaseId) || (state.Principal != published.Principal)) {
                if (state.Active) { Host.Input.Reset(state.Principal); }
                state = new(leaseId, published.Principal, default, true);
            }
            var maximum = Math.Min(val1: 1024, val2: ((Host.Definition.InputHistory?.MaximumEventsPerStep ?? 128) + 1));

            if (state.Pending.Length >= maximum) {
                // Overflow cancels the episode; it never silently drops an unobserved edge.
                published = published with {
                    Intent = default,
                    Input = published.Input with { Image = default, Contributions = default, Events = default, CaptureLost = true },
                };
                state = state with { Pending = new[] { published } };
            } else {
                var pending = new IntentSubmission[(state.Pending.Length + 1)];

                state.Pending.Span.CopyTo(destination: pending);
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
    /// <param name="confirmed">Home's answers to this destination's question for this reservation, keyed by hop key
    /// and incarnation.</param>
    /// <returns>The destination's verdict and assigned body indices.</returns>
    internal WorldTransferReservationReply ReserveTransfer(WorldTransferReservationRequest request, IReadOnlyDictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), ulong>? confirmed = null) =>
        Host.ExecuteAuthorityOperation(operation: () => (((m_transferAuthorityAllowed is not null) && !m_transferAuthorityAllowed(request.SourceAuthority))
            ? WorldTransferReservationReply.Refused(reason: "closed rewind group refuses an external transfer")
            : Host.TransferEscrow.Reserve(
                confirmed: confirmed,
                request: request
            )));
    /// <summary>Terminally retires a traveler incarnation after its accepted leave has propagated through this hop.</summary>
    internal void RetireTransferredMobility(in WorldMobilityIdentity mobility) {
        var credential = mobility;

        Host.ExecuteAuthorityOperation(operation: () => Host.TransferEscrow.RetireMobility(mobility: in credential));
    }
    /// <summary>Returns the destination's view of a source-scoped transfer. A <see cref="WorldTransferStatus.Missing"/>
    /// answer is final: the key is tombstoned before it is given (<see cref="WorldTransferEscrow.Status"/>).</summary>
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
