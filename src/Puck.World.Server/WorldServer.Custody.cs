using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer : IWorldCustodyPeer {
    /// <summary>Gets or sets how this authority reaches a companion's title authority by its identity: the door a
    /// destination confirms a hop through and a custodian asks for an onward hop through. Installed by the host that
    /// owns the authority; <see langword="null"/>, or a resolver that answers <see langword="null"/> for an identity,
    /// leaves every companion of that home unverified.</summary>
    public Func<string, IWorldCustodyPeer?>? CustodyPeers { get; set; }

    /// <inheritdoc/>
    public IReadOnlyList<ulong?>? ConfirmHops(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, string destination) {
        ArgumentNullException.ThrowIfNull(argument: incarnations);
        ArgumentNullException.ThrowIfNull(argument: destination);

        return ExecuteAuthorityOperation(operation: () => {
            var answers = new ulong?[incarnations.Count];

            for (var index = 0; (index < incarnations.Count); index++) {
                // A latched authority confirms nothing: the record it is uncertain about may have ended the title.
                if (
                    (m_uncertainCrossing is null) &&
                    m_transferEscrow.Custody.IsGrantCurrent(
                        destination: destination,
                        incarnation: incarnations[index],
                        key: key,
                        next: out var next
                    )
                ) {
                    answers[index] = next;
                }
            }
            return (IReadOnlyList<ulong?>)answers;
        });
    }
    /// <inheritdoc/>
    public IReadOnlyList<WorldHopAnswer> RequestHop(WorldTransferKey key, string source, string destination, IReadOnlyList<WorldHopRequestMember> members) {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: destination);
        ArgumentNullException.ThrowIfNull(argument: members);

        return ExecuteAuthorityOperation(operation: () => DecideHop(
            destination: destination,
            key: key,
            members: members,
            source: source
        ));
    }
    /// <inheritdoc/>
    public WorldCustodyAnswer QueryCustody(WorldTransferKey key, WorldEntityAddress incarnation, string asker) {
        ArgumentNullException.ThrowIfNull(argument: asker);

        return ExecuteAuthorityOperation(operation: () => (
            !string.Equals(a: incarnation.Authority, b: asker, comparisonType: StringComparison.Ordinal)
                ? WorldCustodyAnswer.Unauthorized
                : ((m_uncertainCrossing is not null)
                    ? WorldCustodyAnswer.Uncertain
                    : m_transferEscrow.CustodyAnswer(
                        incarnation: incarnation,
                        key: key
                    ))
        ));
    }
    /// <inheritdoc/>
    public bool SettleHop(WorldTransferKey key, WorldEntityAddress incarnation, string source) {
        ArgumentNullException.ThrowIfNull(argument: source);

        // Decided under the gate; the destination is asked off it, and the settlement recorded under it again.
        var (settled, destination) = ExecuteAuthorityOperation(operation: () => {
            var custody = m_transferEscrow.Custody;

            if (custody.IsHopDone(incarnation: incarnation, key: key)) {
                return (true, (string?)null);
            }
            if (custody.HopOf(incarnation: incarnation, key: key) is not { } hop) {
                // A notice that reaches home before its hop cancels the hop: no destination can hold a lease on a hop
                // home never granted.
                return ((custody.TitleOf(incarnation: incarnation) is not null) && (RecordCustodyHeld(
                    reason: out _,
                    record: new WorldCrossingRecord.HopDone(Arrived: false, Incarnation: incarnation, Key: key)
                ) == WorldCrossingDurability.Durable), (string?)null);
            }
            // Only the hop's own source gives it up, and home's own hops settle from home's own records.
            return (false, ((string.Equals(a: hop.From, b: source, comparisonType: StringComparison.Ordinal) && !string.Equals(a: hop.From, b: AuthorityIdentity, comparisonType: StringComparison.Ordinal))
                ? hop.To
                : null));
        });

        if (settled || (destination is null) || (CustodyPeers?.Invoke(arg: destination) is not { } door)) {
            return settled;
        }

        var answer = door.QueryCustody(
            asker: AuthorityIdentity,
            incarnation: incarnation,
            key: key
        );

        if (answer is not (WorldCustodyAnswer.Landed or WorldCustodyAnswer.NotLanded or WorldCustodyAnswer.Dead)) {
            return false;
        }
        return ExecuteAuthorityOperation(operation: () => m_transferEscrow.Custody.IsHopDone(incarnation: incarnation, key: key) || (RecordCustodyHeld(
            reason: out _,
            record: new WorldCrossingRecord.HopDone(Arrived: (answer == WorldCustodyAnswer.Landed), Incarnation: incarnation, Key: key)
        ) == WorldCrossingDurability.Durable));
    }
    /// <summary>Authorizes this authority's own hop: one record naming every listed companion whose title is home,
    /// leaving at its current epoch toward <paramref name="destination"/> under <paramref name="key"/>. A companion whose
    /// title is not home is answered as the onward door would answer it.</summary>
    /// <param name="key">The crossing's key, minted here.</param>
    /// <param name="destination">Where the crossing goes.</param>
    /// <param name="incarnations">The companions.</param>
    /// <returns>One answer per companion, in order.</returns>
    public IReadOnlyList<WorldHopAnswer> AuthorizeOwnHop(WorldTransferKey key, string destination, IReadOnlyList<WorldEntityAddress> incarnations) {
        ArgumentNullException.ThrowIfNull(argument: destination);
        ArgumentNullException.ThrowIfNull(argument: incarnations);

        return ExecuteAuthorityOperation(operation: () => {
            var members = new List<WorldHopRequestMember>(capacity: incarnations.Count);

            foreach (var incarnation in incarnations) {
                var title = m_transferEscrow.Custody.TitleOf(incarnation: incarnation);

                members.Add(item: new WorldHopRequestMember(
                    Epoch: (title?.Epoch ?? 0UL),
                    Incarnation: incarnation
                ));
            }
            return DecideHop(
                destination: destination,
                key: key,
                members: members,
                source: AuthorityIdentity
            );
        });
    }

    // Home decides each companion on its own title and records one hop naming the ones it grants; each entry is its own
    // title's fact. Callers hold the authority gate.
    private IReadOnlyList<WorldHopAnswer> DecideHop(WorldTransferKey key, string source, string destination, IReadOnlyList<WorldHopRequestMember> members) {
        var custody = m_transferEscrow.Custody;
        var answers = new WorldHopAnswer[members.Count];
        var grantable = new List<(int Index, WorldCustodyHopMember Member)>();

        for (var index = 0; (index < members.Count); index++) {
            var member = members[index];
            var incarnation = member.Incarnation;

            if (custody.IsHopDone(incarnation: incarnation, key: key)) {
                answers[index] = new WorldHopAnswer(Incarnation: incarnation, Next: 0UL, Verdict: WorldHopVerdict.Dead);
                continue;
            }
            if (custody.HopOf(incarnation: incarnation, key: key) is { } existing) {
                answers[index] = ((m_uncertainCrossing is not null)
                    ? new WorldHopAnswer(Incarnation: incarnation, Next: 0UL, Verdict: WorldHopVerdict.Unavailable)
                    : ((custody.TitleOf(incarnation: incarnation) is { Installed: true })
                        ? new WorldHopAnswer(Incarnation: incarnation, Next: 0UL, Verdict: WorldHopVerdict.Released)
                        : new WorldHopAnswer(Incarnation: incarnation, Next: existing.Next, Verdict: WorldHopVerdict.Granted)));
                continue;
            }
            if (custody.TitleOf(incarnation: incarnation) is not { } title) {
                answers[index] = new WorldHopAnswer(Incarnation: incarnation, Next: 0UL, Verdict: WorldHopVerdict.NotMyTitle);
                continue;
            }

            var ownHop = string.Equals(a: source, b: AuthorityIdentity, comparisonType: StringComparison.Ordinal);
            WorldHopVerdict? refusal = title.Kind switch {
                WorldCustodyKind.Released => WorldHopVerdict.Released,
                WorldCustodyKind.Pending => ((custody.HopOf(incarnation: incarnation, key: title.PendingKey!.Value) is { } pending) && (string.Equals(a: pending.From, b: source, comparisonType: StringComparison.Ordinal) || string.Equals(a: pending.To, b: source, comparisonType: StringComparison.Ordinal)))
                    ? WorldHopVerdict.Pending
                    : WorldHopVerdict.Stale,
                WorldCustodyKind.Home => ((ownHop && (title.Epoch == member.Epoch))
                    ? null
                    : WorldHopVerdict.Stale),
                _ => ((!ownHop && string.Equals(a: title.Custodian, b: source, comparisonType: StringComparison.Ordinal) && (title.Epoch == member.Epoch))
                    ? null
                    : WorldHopVerdict.Stale),
            };

            if (refusal is { } refused) {
                answers[index] = new WorldHopAnswer(Incarnation: incarnation, Next: 0UL, Verdict: refused);
                continue;
            }
            grantable.Add(item: (index, new WorldCustodyHopMember(
                Epoch: member.Epoch,
                Incarnation: incarnation,
                Next: checked((member.Epoch + 1UL))
            )));
        }
        if (grantable.Count > 0) {
            var durable = (RecordCustodyHeld(
                reason: out _,
                record: new WorldCrossingRecord.Hop(
                    From: source,
                    Key: key,
                    Members: [.. grantable.Select(selector: static granted => granted.Member)],
                    To: destination
                )
            ) == WorldCrossingDurability.Durable);

            foreach (var (index, granted) in grantable) {
                answers[index] = (durable
                    ? new WorldHopAnswer(Incarnation: granted.Incarnation, Next: granted.Next, Verdict: WorldHopVerdict.Granted)
                    : new WorldHopAnswer(Incarnation: granted.Incarnation, Next: 0UL, Verdict: WorldHopVerdict.Unavailable));
            }
        }
        return answers;
    }
    // Asks each companion's title authority, off this authority's gate, whether its hop under `key` to this destination
    // is currently granted: one question per home, for this call alone, so an answer never outlives the decision it was
    // asked for.
    private IReadOnlyDictionary<(WorldTransferKey Key, WorldEntityAddress Incarnation), ulong>? ConfirmCompanionHops(WorldTransferKey key, IEnumerable<WorldCustodyTicket> tickets) {
        Dictionary<(WorldTransferKey, WorldEntityAddress), ulong>? confirmed = null;

        foreach (var home in tickets.Where(predicate: ticket => (ticket.Hop == key)).GroupBy(keySelector: static ticket => ticket.Home, comparer: StringComparer.Ordinal)) {
            if (CustodyPeers?.Invoke(arg: home.Key) is not { } door) {
                continue;
            }

            var incarnations = home.Select(selector: static ticket => ticket.Incarnation).ToArray();
            var answers = door.ConfirmHops(
                destination: AuthorityIdentity,
                incarnations: incarnations,
                key: key
            );

            if ((answers is null) || (answers.Count != incarnations.Length)) {
                continue;
            }
            for (var index = 0; (index < incarnations.Length); index++) {
                if (answers[index] is { } next) {
                    (confirmed ??= new())[(key, incarnations[index])] = next;
                }
            }
        }
        return confirmed;
    }
    // Whether this authority is latched on a crossing record it cannot yet call durable: home then verifies no return
    // and confirms no grant, since the record it is uncertain about may end a title. Callers hold the authority gate.
    internal bool CustodyLatched => (m_uncertainCrossing is not null);
    private bool IsUncertainAboutHeld(WorldTransferKey key) => (m_uncertainCrossing is not null) && (m_uncertainRecord switch {
        WorldCrossingRecord.Hop hop => (hop.Key == key),
        WorldCrossingRecord.HopDone done => (done.Key == key),
        WorldCrossingRecord.HopAcked acked => (acked.Key == key),
        WorldCrossingRecord.Arrival arrival => (arrival.Value.Key == key),
        WorldCrossingRecord.Lease lease => (lease.Key == key),
        WorldCrossingRecord.Tombstone tombstone => (tombstone.Key == key),
        WorldCrossingRecord.Departure departure => (string.Equals(a: AuthorityIdentity, b: key.SourceAuthority, comparisonType: StringComparison.Ordinal) && (departure.Transfer.TransferId == key.TransferId)),
        WorldCrossingRecord.Settlement settlement => (string.Equals(a: AuthorityIdentity, b: key.SourceAuthority, comparisonType: StringComparison.Ordinal) && (settlement.TransferId == key.TransferId)),
        _ => false,
    });
    /// <summary>Folds one durable custody record and carries out what it states about the bodies here: a retired
    /// companion's copy is destroyed. Live recording, crossing-log recovery and a replay's re-drive all fold through
    /// this door, so each derives the same custody and the same embodiment. The caller holds the authority gate.</summary>
    /// <param name="record">The durable record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public void ApplyCustodyRecord(WorldCrossingRecord record) {
        ArgumentNullException.ThrowIfNull(argument: record);

        m_transferEscrow.Custody.Apply(record: record);
        if (record is WorldCrossingRecord.Retire retire) {
            for (var slot = 0; (slot < Population.Capacity); slot++) {
                if (Population.IsActive(index: slot) && (Population.ResolveIncarnation(authority: AuthorityIdentity, index: slot) == retire.Incarnation)) {
                    GrantTable.RollbackTransferredEntity(slot: slot);
                    break;
                }
            }
        }
    }
    /// <summary>Returns the request-local ids a committed transfer landed here, or <see langword="null"/> when this
    /// authority holds no commit for the key.</summary>
    /// <param name="sourceAuthority">The source namespace.</param>
    /// <param name="transferId">The transfer id.</param>
    /// <returns>The landed ids, or <see langword="null"/>.</returns>
    public IReadOnlyList<int>? TransferLanded(string sourceAuthority, ulong transferId) => ExecuteAuthorityOperation(operation: () => m_transferEscrow.Landed(
        sourceAuthority: sourceAuthority,
        transferId: transferId
    ));
}
