using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Sorted by DeadlineTick, rebuilt from `Groups.Ownership` only when the document itself has changed since the
    // last rebuild (every mutation swaps `m_definition` for a new instance — this project never mutates a document
    // in place — so a reference compare is an exact "did anything change" test); a quiet tick with nothing to
    // reclaim just sweeps the table instead of walking every ownership row again.
    private readonly WorldDeadlineTable<OwnershipSubject> m_ownershipDeadlines = new();
    private WorldDefinition? m_ownershipDeadlineSource;

    // ESCROW RECOVERY — the "recovery is a LIFETIME RULE" shape: fires an ordinary SettleOwnership(Reclaim: true)
    // under WorldPrincipal.World — the SAME structural-exemption door a rule's own effects use
    // (Server.WorldServer.TryAdmitMutation admits it before the grant table is even consulted) — for every subject
    // whose escrow has reached its DeadlineTick with no accept. Recovery therefore needs no operator action: the
    // offerer gets the subject back the tick the deadline passes, exactly as if a world-authored rule had reclaimed
    // it. The table is built once per document revision, before any mutation in this pass swaps `m_definition`, so
    // a subject an earlier dequeue already reclaimed this tick simply is not read again.
    private void ReclaimExpiredEscrows(ulong tick) {
        if (!ReferenceEquals(objA: m_definition, objB: m_ownershipDeadlineSource)) {
            m_ownershipDeadlineSource = m_definition;
            m_ownershipDeadlines.Clear();

            foreach (var row in (m_definition.Groups ?? WorldGroupsSection.Empty).Ownership) {
                if (
                    (row.Owner.Kind == OwnershipOwnerKind.Escrow) &&
                    (row.Owner.Escrow is { } escrow)
                ) {
                    m_ownershipDeadlines.Add(dueTick: escrow.DeadlineTick, token: row.Subject);
                }
            }
        }

        var signedTick = unchecked((long)tick);

        while (m_ownershipDeadlines.TryDequeueDue(tick: signedTick, out var subject)) {
            _ = TryApplyMutation(
                mutation: new WorldMutation.SettleOwnership(
                    Principal: WorldPrincipal.World,
                    Subject: subject,
                    Reclaim: true
                ),
                tick: tick,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                preMetered: false
            );
        }
    }
}
