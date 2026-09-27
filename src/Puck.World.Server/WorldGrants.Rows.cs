using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    /// <summary>Snapshots the complete rows one peer principal currently holds, including every payload lane a peer
    /// may legally carry. Peer disconnect events carry this image so replay revokes the identical rows through the
    /// ordinary door.</summary>
    /// <param name="principal">The principal to snapshot.</param>
    /// <returns>The rows in stable capability/subject order.</returns>
    internal IReadOnlyList<WorldGrant> Rows(Principal principal) {
        var rows = new List<WorldGrant>();

        foreach (var (capability, subject) in Held(grantee: principal)) {
            var key = (principal, capability, subject);
            var exclusive = (m_exclusive.TryGetValue(
                key: new ExclusiveKey(
                    Capability: capability,
                    Subject: subject
                ),
                value: out var holder
            ) && (holder == principal));

            rows.Add(item: new WorldGrant(
                Grantee: principal,
                Capability: capability,
                Subject: subject,
                Exclusive: exclusive,
                Budget: (m_budgets.TryGetValue(
                    key: key,
                    value: out var budget
                )
                ? budget
                : null),
                EventBudget: (m_eventBudgets.TryGetValue(
                    key: key,
                    value: out var eventBudget
                )
                ? eventBudget
                : null),
                Reach: (m_channelReach.TryGetValue(
                    key: key,
                    value: out var reach
                )
                ? reach
                : null),
                KindMask: (m_kindMasks.TryGetValue(
                    key: key,
                    value: out var kinds
                )
                ? kinds
                : null),
                WriteMask: (m_writeMasks.TryGetValue(
                    key: key,
                    value: out var writes
                )
                ? writes
                : null),
                HoldCeiling: (m_holdCeilings.TryGetValue(
                    key: key,
                    value: out var holdCeiling
                )
                ? holdCeiling
                : null)
            ));
        }

        return rows;
    }
}
