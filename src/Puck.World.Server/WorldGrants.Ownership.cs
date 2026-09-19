using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    // Sorted by DeadlineTick, rebuilt from `Groups.Ownership` only when the document itself has changed since the
    // last rebuild (every mutation swaps the live definition for a new instance — this project never mutates a
    // document in place — so a reference compare is an exact "did anything change" test); a quiet tick with nothing
    // to reclaim just sweeps the table instead of walking every ownership row again.
    private readonly WorldDeadlineTable<OwnershipSubject> m_ownershipDeadlines = new();

    private WorldDefinition? m_ownershipDeadlineSource;

    /// <summary>Reclaims every ownership escrow whose deadline has passed with no accept, firing one ordinary
    /// <c>SettleOwnership(Reclaim: true)</c> under <see cref="WorldPrincipal.World"/> per subject.</summary>
    /// <param name="tick">The simulation tick being swept.</param>
    /// <remarks>The table is rebuilt once per document revision, before any mutation in this pass swaps the live
    /// definition, so a subject an earlier dequeue already reclaimed this tick is not read again.</remarks>
    internal void ReclaimExpiredEscrows(ulong tick) {
        if (!ReferenceEquals(
            objA: Host.Definition,
            objB: m_ownershipDeadlineSource
        )) {
            m_ownershipDeadlineSource = Host.Definition;
            m_ownershipDeadlines.Clear();

            foreach (var row in (Host.Definition.Groups ?? WorldGroupsSection.Empty).Ownership) {
                if (
                    (row.Owner.Kind == OwnershipOwnerKind.Escrow) &&
                    (row.Owner.Escrow is { } escrow)
                ) {
                    m_ownershipDeadlines.Add(
                        dueTick: escrow.DeadlineTick,
                        token: row.Subject
                    );
                }
            }
        }

        var signedTick = unchecked((long)tick);

        while (m_ownershipDeadlines.TryDequeueDue(
            tick: signedTick,
            out var subject
        )) {
            _ = Host.TryApplyMutation(
                mutation: new WorldMutation.SettleOwnership(
                    Principal: WorldPrincipal.World,
                    Subject: subject,
                    Reclaim: true
                ),
                tick: tick,
                engineTick: Host.CompletedEngineTicks,
                connectionId: SubmissionEnvelope.LocalConnectionId,
                correlationId: 0,
                preMetered: false
            );
        }
    }
}
