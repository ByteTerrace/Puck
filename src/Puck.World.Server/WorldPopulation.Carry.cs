namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private readonly record struct CarryRelationship(int CarrierIndex, int TargetIndex);

    /// <summary>Attempts to begin <paramref name="carrierIndex"/> carrying <paramref name="targetIndex"/> — see
    /// <see cref="WorldBody.TryBeginCarry"/> for the full refusal set (a carrier kit with no carry facet, a target
    /// with no rigid facet, either body already a party to another carry relationship, out of reach, over the
    /// carrier's mass ceiling).</summary>
    /// <param name="carrierIndex">The would-be carrier's population index.</param>
    /// <param name="targetIndex">The would-be target's population index.</param>
    /// <param name="reason">The refusal, by name, on failure; empty on success.</param>
    public bool TryBeginCarry(int carrierIndex, int targetIndex, out string reason) {
        if (
            ((uint)carrierIndex >= (uint)Capacity) ||
            (m_entries[carrierIndex] is not { Active: true, Body: { } carrier })
        ) {
            reason = $"body:{carrierIndex} is not active";
            return false;
        }
        if (
            ((uint)targetIndex >= (uint)Capacity) ||
            (m_entries[targetIndex] is not { Active: true, Body: { } target })
        ) {
            reason = $"body:{targetIndex} is not active";
            return false;
        }

        if (!carrier.TryBeginCarry(
            target: target,
            targetIndex: targetIndex,
            selfIndex: carrierIndex,
            reason: out reason
        )) {
            return false;
        }

        RegisterCarry(carrierIndex: carrierIndex, targetIndex: targetIndex);
        return true;
    }

    /// <summary>Ends <paramref name="carrierIndex"/>'s active carry, handing the target back to the rigid solver
    /// with the carrier's own current velocity — see <see cref="WorldBody.EndCarry"/>.</summary>
    /// <param name="carrierIndex">The carrier's population index.</param>
    /// <param name="reason">The refusal, by name, on failure (not active, or not currently carrying anything);
    /// empty on success.</param>
    public bool TryEndCarry(int carrierIndex, out string reason) {
        if (
            ((uint)carrierIndex >= (uint)Capacity) ||
            (m_entries[carrierIndex] is not { Active: true, Body: { } carrier })
        ) {
            reason = $"body:{carrierIndex} is not active";
            return false;
        }
        if (carrier.Carrying is not { } targetIndex) {
            reason = $"body:{carrierIndex} is not carrying anything";
            return false;
        }
        if (m_entries[targetIndex] is not { Active: true, Body: { } target }) {
            // The carried body went inactive out from under the carrier (despawn, capacity reclaim) without going
            // through this path — drop the carrier's own half; there is nothing left to hand a release velocity to.
            carrier.ForceDropCarrying();
            RemoveCarry(carrierIndex: carrierIndex);
            reason = $"body:{targetIndex} is no longer active — carry dropped";
            return false;
        }

        carrier.EndCarry(target: target);
        RemoveCarry(carrierIndex: carrierIndex);
        reason = "";
        return true;
    }

    /// <summary>Reconciles invalidated active relationships before body integration, so an orphaned target re-enters
    /// rigid advance and contact in the same tick its carrier disappears or either required facet is removed.</summary>
    public void PrepareCarriedBodies() => ReconcileCarriedBodies(follow: false);
    /// <summary>Updates valid attachments from their carriers' final pose after movement, dynamic contact, and tether
    /// correction, then performs the same bounded consistency check. Both carry passes walk the sorted,
    /// preallocated active-relationship table, so a no-carry tick is O(1) and allocation-free rather than scanning
    /// population capacity.</summary>
    public void UpdateCarriedBodies() => ReconcileCarriedBodies(follow: true);
    private void ReconcileCarriedBodies(bool follow) {
        var relationshipIndex = 0;

        while (relationshipIndex < m_activeCarryCount) {
            var relationship = m_activeCarries[relationshipIndex];
            if (
                (m_entries[relationship.CarrierIndex] is not { Active: true, Body: { } carrier }) ||
                (m_entries[relationship.TargetIndex] is not { Active: true, Body: { } target }) ||
                !carrier.HasCarryFacet ||
                !target.IsRigid ||
                (carrier.Carrying != relationship.TargetIndex) ||
                (target.CarriedBy != relationship.CarrierIndex)
            ) {
                if (
                    (m_entries[relationship.CarrierIndex] is { Active: true, Body: { } orphanedCarrier }) &&
                    (orphanedCarrier.Carrying == relationship.TargetIndex)
                ) {
                    orphanedCarrier.ForceDropCarrying();
                }
                if (
                    (m_entries[relationship.TargetIndex] is { Active: true, Body: { } orphanedTarget }) &&
                    (orphanedTarget.CarriedBy == relationship.CarrierIndex)
                ) {
                    orphanedTarget.ForceRelease();
                }

                RemoveCarryAt(index: relationshipIndex);
                continue;
            }

            if (follow) {
                target.FollowCarrier(carrier: carrier);
            }
            relationshipIndex++;
        }
    }

    private void RebuildCarryRelationships() {
        m_activeCarryCount = 0;

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { Carrying: { } targetIndex } carrier }) {
                continue;
            }

            if (
                (m_entries[targetIndex] is { Active: true, Body: { } target }) &&
                carrier.HasCarryFacet &&
                target.IsRigid &&
                (target.CarriedBy == index)
            ) {
                RegisterCarry(carrierIndex: index, targetIndex: targetIndex);
            } else {
                carrier.ForceDropCarrying();
            }
        }

        // Restore can legitimately remove a captured remote carrier when reconnect grace is zero after checkpoint
        // validation has proved the image. This is a restore-time reconciliation scan, never part of the tick path.
        for (var index = 0; (index < Capacity); index++) {
            if (
                (m_entries[index] is { Active: true, Body: { CarriedBy: { } carrierIndex } target }) &&
                ((m_entries[carrierIndex] is not { Active: true, Body: { Carrying: { } targetIndex } }) ||
                    (targetIndex != index))
            ) {
                target.ForceRelease();
            }
        }
    }
    private void RegisterCarry(int carrierIndex, int targetIndex) {
        var index = 0;

        while (
            (index < m_activeCarryCount) &&
            (m_activeCarries[index].CarrierIndex < carrierIndex)
        ) {
            index++;
        }

        if (
            (index < m_activeCarryCount) &&
            (m_activeCarries[index].CarrierIndex == carrierIndex)
        ) {
            m_activeCarries[index] = new CarryRelationship(CarrierIndex: carrierIndex, TargetIndex: targetIndex);
            return;
        }

        Array.Copy(
            sourceArray: m_activeCarries,
            sourceIndex: index,
            destinationArray: m_activeCarries,
            destinationIndex: (index + 1),
            length: (m_activeCarryCount - index)
        );
        m_activeCarries[index] = new CarryRelationship(CarrierIndex: carrierIndex, TargetIndex: targetIndex);
        m_activeCarryCount++;
    }
    private void RemoveCarry(int carrierIndex) {
        for (var index = 0; (index < m_activeCarryCount); index++) {
            if (m_activeCarries[index].CarrierIndex == carrierIndex) {
                RemoveCarryAt(index: index);
                return;
            }
        }
    }
    private void RemoveCarryAt(int index) {
        m_activeCarryCount--;
        Array.Copy(
            sourceArray: m_activeCarries,
            sourceIndex: (index + 1),
            destinationArray: m_activeCarries,
            destinationIndex: index,
            length: (m_activeCarryCount - index)
        );
        m_activeCarries[m_activeCarryCount] = default;
    }
}
