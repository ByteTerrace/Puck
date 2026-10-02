using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private static WorldInDoubtTransferCheckpoint CaptureInDoubt(InDoubtTransfer pending) => new(
        RollbackOnly: pending.RollbackOnly,
        CommitConfirmed: pending.CommitConfirmed,
        Continuation: CaptureTransferContinuation(
            pending.Transfer,
            pending.Landed
        ),
        TargetDefinitionJson: (((pending.TargetAuthority?.Remote?.Definition ?? pending.RecoveryDefinition) is { } remoteDefinition)
        ? WorldDefinitionSerialization.Serialize(definition: remoteDefinition)
        : null),
        CommitMembers: [.. pending.CommitMembers],
        Landed: [.. pending.Landed.Select(selector: static member => new WorldLandedMemberCheckpoint(
                AdmissionGrants: member.AdmissionGrants,
                BodyColor: member.BodyColor,
                Designations: member.Designations,
                DynamicState: member.DynamicState,
                Mobility: member.Mobility,
                FollowedSeatMask: member.FollowedSeatMask,
                Peer: member.Peer,
                Position: member.Position,
                SourceGrants: member.SourceGrants,
                SourceSlot: member.SourceSlot,
                TargetSlot: member.TargetSlot,
                Yaw: member.Yaw
            ))],
        MemberCount: pending.MemberCount,
        SourceDeadlineTick: pending.SourceDeadlineTick,
        SourceInstance: pending.Transfer.SourceInstance,
        Spawned: pending.Spawned,
        TargetAuthority: (pending.TargetAuthority?.Local?.Server.AuthorityIdentity
            ?? (pending.TargetAuthority?.Remote?.PeerAuthority ?? (pending.RecoveryAuthority ?? string.Empty))),
        TargetEndpoint: (pending.TargetAuthority?.Remote?.Endpoint ?? pending.RecoveryEndpoint),
        TargetName: pending.TargetName,
        TransferId: pending.Transfer.TransferId
    );
    private static WorldForwardedBodyCheckpoint CaptureForwarded(ForwardedBody body, WorldEntityAddress incarnation) {
        var destination = body.Authority.DescribeForCheckpoint();

        return new WorldForwardedBodyCheckpoint(
            SourceIncarnation: incarnation,
            DestinationAddress: new WorldEntityAddress(
                Authority: destination.DestinationAuthority,
                Index: body.BodyIndex,
                Generation: 0
            ),
            DestinationBodyIndex: body.BodyIndex,
            Mobility: destination.Mobility,
            SourceAuthority: destination.SourceAuthority,
            DestinationEndpoint: destination.Endpoint,
            DestinationDefinitionJson: ((destination.Definition is { } definition)
            ? WorldDefinitionSerialization.Serialize(definition: definition)
            : null)
        );
    }
    // Written ahead of the commit: once the destination may hold the cohort, a restart of this source must find the
    // cohort in doubt rather than at home.
    private bool TryRecordDeparture(InDoubtTransfer departure, out string reason) {
        if (!m_instances.TryGetValue(
            key: departure.Transfer.SourceInstance,
            value: out var source
        )) {
            reason = $"no instance named '{departure.Transfer.SourceInstance}'";
            return false;
        }

        return source.Server.TryRecordCrossing(
            reason: out reason,
            record: new WorldCrossingRecord.Departure(Transfer: CaptureInDoubt(pending: departure))
        );
    }
    // Written ahead of the acknowledgement for an arrival, and ahead of the restore for a cohort that stays: a restart
    // then redoes the outcome this source already acted on instead of asking the destination again. A settlement
    // that does not land keeps the transfer where it is, and the next drain tries again.
    private bool TrySettle(InDoubtTransfer pending, bool arrived) {
        if (pending.SettlementRecorded) {
            return true;
        }
        if (!m_instances.TryGetValue(
            key: pending.Transfer.SourceInstance,
            value: out var source
        )) {
            return false;
        }

        var forwarded = new List<WorldForwardedBodyCheckpoint>();

        if (arrived) {
            foreach (var member in pending.Landed) {
                if (m_forwardedBodies.TryGetValue(
                    key: (source.Server, member.Mobility.Incarnation),
                    value: out var body
                )) {
                    forwarded.Add(item: CaptureForwarded(
                        body: body,
                        incarnation: member.Mobility.Incarnation
                    ));
                }
            }
        }

        if (!source.Server.TryRecordCrossing(
            reason: out var reason,
            record: new WorldCrossingRecord.Settlement(
                Arrived: arrived,
                Forwarded: forwarded,
                TransferId: pending.Transfer.TransferId
            )
        )) {
            if (
                !pending.SettlementFailureReported &&
                m_narration.HasNarrationSink
            ) {
                m_narration.Narrate(
                    channel: "world.transfer",
                    text: $"[world.transfer: transfer={pending.Transfer.TransferId} SETTLEMENT-PENDING — the {(arrived
                        ? "arrival"
                        : "return")} could not be made durable ({reason}); recovery state retained]"
                );
            }
            pending.SettlementFailureReported = true;
            return false;
        }

        pending.SettlementRecorded = true;
        return true;
    }

    /// <summary>Redoes, in order, every crossing record <paramref name="row"/>'s restored checkpoint does not reflect:
    /// the records at or past the checkpoint's crossing sequence, read back from the row's durable crossing log. An
    /// arrival lands its cohort again through the row's own escrow; a departure detaches its cohort again and puts the
    /// transfer back in doubt; a settlement publishes the cohort's forwarding routes or returns it home. The ordinary
    /// drain then reconciles whatever is still in doubt with its destination, so the traveler ends arrived or never
    /// departed. Call after <see cref="Admit"/> and <see cref="RestoreRow"/>, before the row steps; a fresh row with
    /// no checkpoint redoes its whole log.</summary>
    /// <param name="row">The restored row.</param>
    /// <param name="entries">The row's crossing log, in any order; records the checkpoint reflects are skipped.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The log skips or repeats a sequence past the checkpoint, or a departure
    /// record is malformed. Records before the first malformed one stay redone.</exception>
    /// <exception cref="InvalidOperationException">A durable arrival cannot land again. Its sequence stays
    /// unapplied so recovery cannot discard the traveler.</exception>
    public void RecoverCrossings(WorldInstance row, IReadOnlyList<WorldCrossingEntry> entries) {
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: entries);

        var next = row.Server.CrossingSequence;

        foreach (var entry in entries.Where(predicate: entry => (entry.Sequence >= row.Server.CrossingSequence)).OrderBy(keySelector: static entry => entry.Sequence)) {
            if (entry.Sequence != next) {
                throw new ArgumentException(
                    message: $"crossing log for '{row.Name}' skips or repeats sequence {next} (found {entry.Sequence})",
                    paramName: nameof(entries)
                );
            }

            switch (entry.Record) {
                case WorldCrossingRecord.Arrival arrival:
                    RedoArrival(
                        arrival: arrival.Value,
                        row: row
                    );
                    break;
                case WorldCrossingRecord.Departure departure:
                    RedoDeparture(
                        record: departure.Transfer,
                        row: row
                    );
                    break;
                case WorldCrossingRecord.Settlement settlement:
                    RedoSettlement(
                        row: row,
                        settlement: settlement
                    );
                    break;
                default:
                    throw new ArgumentException(
                        message: $"crossing log for '{row.Name}' carries an undeclared record '{entry.Record.GetType().Name}'",
                        paramName: nameof(entries)
                    );
            }

            next = checked((next + 1UL));
            row.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.ResumeCrossingSequence(next: next));
        }
    }

    private void RedoArrival(WorldInstance row, WorldCrossingArrival arrival) {
        var reason = string.Empty;
        var landed = row.Server.ExecuteAuthorityOperation(operation: () => row.Server.TransferEscrow.TryReland(
            arrival: arrival,
            reason: out reason,
            recorded: null
        ));

        if (!landed) {
            throw new InvalidOperationException(message: $"crossing recovery refused: transfer={arrival.Request.TransferId} from '{arrival.Request.SourceAuthority}' could not land again in '{row.Name}' — {reason}");
        }
    }
    private void RedoDeparture(WorldInstance row, WorldInDoubtTransferCheckpoint record) {
        var restored = PrepareInDoubtTransfers(
            records: [record],
            row: row
        );

        if (m_inDoubtTransfers.Any(predicate: pending => (string.Equals(
            a: pending.Transfer.SourceInstance,
            b: row.Name,
            comparisonType: StringComparison.Ordinal
        ) && (pending.Transfer.TransferId == record.TransferId)))) {
            throw new ArgumentException(
                message: $"crossing log for '{row.Name}' departs transfer {record.TransferId} twice",
                paramName: nameof(record)
            );
        }

        // The checkpoint may predate the cohort's departure: its members are detached again by identity, wherever they
        // stand. A member the checkpoint never held has nothing to detach.
        row.Server.ExecuteAuthorityOperation(operation: () => {
            foreach (var member in record.Landed) {
                for (var slot = 0; (slot < row.Server.Population.Capacity); slot++) {
                    if (row.Server.Population.ResolveIncarnation(
                        authority: row.Server.AuthorityIdentity,
                        index: slot
                    ) != member.Mobility.Incarnation) {
                        continue;
                    }
                    // The rows the live departure dissolved, dissolved again — administration, as the live detach is.
                    foreach (var grant in member.SourceGrants) {
                        row.Server.Revoke(
                            actor: Principal.Console,
                            grant: grant
                        );
                    }
                    _ = row.Server.Population.TryDetachSeatForTransfer(
                        profile: out _,
                        slot: slot
                    );
                    break;
                }
            }
        });

        m_inDoubtTransfers.AddRange(collection: restored);
        row.NextTransferId = Math.Max(
            val1: row.NextTransferId,
            val2: checked((record.TransferId + 1UL))
        );
        m_appliedTransferHighWater[row.Name] = (m_appliedTransferHighWater.TryGetValue(
            key: row.Name,
            value: out var highWater
        )
            ? Math.Max(
                val1: highWater,
                val2: record.TransferId
            )
            : record.TransferId);
        _ = m_appliedTransferIds.Add(item: (row.Name, record.TransferId));
    }
    private void RedoSettlement(WorldInstance row, WorldCrossingRecord.Settlement settlement) {
        var index = m_inDoubtTransfers.FindIndex(match: pending => (string.Equals(
            a: pending.Transfer.SourceInstance,
            b: row.Name,
            comparisonType: StringComparison.Ordinal
        ) && (pending.Transfer.TransferId == settlement.TransferId)));

        if (settlement.Arrived) {
            if (index >= 0) {
                m_inDoubtTransfers.RemoveAt(index: index);
            }
            foreach (var (incarnation, body) in PrepareForwardedBodies(
                row,
                settlement.Forwarded
            )) {
                if (m_forwardedBodies.TryGetValue(
                    key: (row.Server, incarnation),
                    value: out var superseded
                )) {
                    (superseded.Authority as IDisposable)?.Dispose();
                }
                m_forwardedBodies[(row.Server, incarnation)] = body;
            }
            ResolveForwardedRecoveries();
            return;
        }
        if (index < 0) {
            return;
        }

        var pending = m_inDoubtTransfers[index] with { RollbackOnly = true };

        pending.SettlementRecorded = true;
        m_inDoubtTransfers[index] = pending;
        if (RestoreDetachedMembers(
            commits: pending.CommitMembers,
            members: pending.Landed,
            source: row,
            transferId: pending.Transfer.TransferId
        )) {
            m_inDoubtTransfers.RemoveAt(index: index);
        }
    }
}
