namespace Puck.World;

public sealed partial class WorldInstanceHost {
    /// <summary>Arms a companion recording on every row of this host other than the one <paramref name="tape"/>
    /// records, so a recording taken on the boot row becomes a set of tapes that pairs every crossing between this
    /// process's authorities. A row admitted later while the recording runs is armed at its admission. A row that
    /// cannot be taped is named on stderr; crossings into it are then reported as not verified.</summary>
    /// <param name="tape">The recording that owns the set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tape"/> is <see langword="null"/>.</exception>
    public void RecordCompanions(WorldReplayTape tape) {
        ArgumentNullException.ThrowIfNull(argument: tape);

        foreach (var name in Names) {
            if (
                m_instances.TryGetValue(
                key: name,
                value: out var row
            ) &&
                !ReferenceEquals(
                objA: row.Tape,
                objB: tape
            )
            ) {
                RecordCompanion(
                    row: row,
                    tape: tape
                );
            }
        }
    }

    private void RecordCompanion(WorldInstance row, WorldReplayTape tape) {
        if (
            !tape.TryRecordCompanion(
            refusal: out var refusal,
            row: row
        ) &&
            m_narration.HasNarrationSink
        ) {
            m_narration.Narrate(
                channel: "replay.tape",
                text: $"[replay.tape: '{tape.Name}' records no companion tape for '{row.Name}' — {refusal}; a crossing into it is reported as not verified]"
            );
        }
    }
    // Arms a row admitted while the boot recording runs: it starts at its own boot image, so its tape is
    // boot-anchored from its first tick.
    private void RecordAdmittedCompanion(WorldInstance row) {
        if (
            (Boot?.Tape is { Mode: WorldReplayMode.Recording } tape) &&
            !ReferenceEquals(
            objA: Boot,
            objB: row
        )
        ) {
            RecordCompanion(
                row: row,
                tape: tape
            );
        }
    }
    // Records a settled transfer on the source row's own tape. The destination's tape carries the arrival itself,
    // from the escrow that landed it; a refusal or abort departs nothing.
    private void NoteTransferOutcome(in PendingTransfer transfer, string sourceName, string outcome, IReadOnlyList<int>? departedSlots = null, WorldPeerCall? target = null) {
        if (
            !m_instances.TryGetValue(
            key: sourceName,
            value: out var sourceRow
        ) ||
            (sourceRow.Tape is not { } tape)
        ) {
            return;
        }

        tape.NoteTransfer(
            departedSlots: (departedSlots ?? []),
            destinationName: (transfer.RecoveryDestinationName ?? (transfer.ResolvedDestinationRow?.Name.Value ?? string.Empty)),
            generationId: (transfer.FrozenGenerationId ?? 0UL),
            outcome: outcome,
            scopeKey: (transfer.FrozenScopeKey ?? string.Empty),
            target: (target?.Local?.Server.AuthorityIdentity ?? (target?.Remote?.PeerAuthority ?? string.Empty)),
            targetRemote: (target?.IsRemote == true),
            transferId: transfer.TransferId
        );
    }
}
