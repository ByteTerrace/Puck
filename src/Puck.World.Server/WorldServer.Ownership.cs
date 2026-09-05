using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // ESCROW RECOVERY — the "recovery is a LIFETIME RULE" shape: fires an ordinary SettleOwnership(Reclaim: true)
    // under WorldPrincipal.World — the SAME structural-exemption door a rule's own effects use
    // (Server.WorldServer.TryAdmitMutation admits it before the grant table is even consulted) — for every subject
    // whose escrow has reached its DeadlineTick with no accept. Recovery therefore needs no operator action: the
    // offerer gets the subject back the tick the deadline passes, exactly as if a world-authored rule had reclaimed
    // it. `ownership` is read once, before any mutation in this pass swaps `m_definition` — an IReadOnlyList this
    // project never mutates in place (every write rebuilds a new list via Upsert), so iterating the pre-sweep
    // snapshot while TryApplyMutation installs later candidates is safe; a subject an earlier iteration already
    // reclaimed this tick simply is not read again.
    private void ReclaimExpiredEscrows(ulong tick) {
        var ownership = (m_definition.Groups ?? WorldGroupsSection.Empty).Ownership;

        foreach (var row in ownership) {
            if (
                (row.Owner.Kind == OwnershipOwnerKind.Escrow) &&
                (row.Owner.Escrow is { } escrow) &&
                (unchecked((long)tick) >= escrow.DeadlineTick)
            ) {
                _ = TryApplyMutation(
                    mutation: new WorldMutation.SettleOwnership(
                        Principal: WorldPrincipal.World,
                        Subject: row.Subject,
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
}
