using System.Diagnostics.CodeAnalysis;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    private readonly record struct CarryRelationship(int CarrierIndex, int TargetIndex);

    // The broadphase scratch a carried target's neighbour queries share: an image of the awake bodies' poses, which is
    // Fresh only until a pose it holds changes, and the buffers its queries fill. Created with the first carry, so a
    // population that never carries pays nothing.
    private sealed class CarryNeighbourhood(int capacity) {
        public readonly FixedSweepGrid Grid = new(capacity: capacity);
        public readonly FixedSweepSphere[] Spheres = new FixedSweepSphere[capacity];
        public readonly int[] Found = new int[capacity];
        public readonly int[] Sleepers = new int[capacity];
        public bool Fresh;
    }

    /// <summary>Attempts to begin <paramref name="carrierIndex"/> carrying <paramref name="targetIndex"/> — see
    /// <see cref="WorldBody.TryBeginCarry"/> for the full refusal set (a carrier kit with no carry facet, a target
    /// with no rigid facet, either body already a party to another carry relationship, out of reach, over the
    /// carrier's mass ceiling).</summary>
    /// <param name="carrierIndex">The would-be carrier's population index.</param>
    /// <param name="targetIndex">The would-be target's population index.</param>
    /// <param name="reason">The refusal, by name, on failure; empty on success.</param>
    public bool TryBeginCarry(int carrierIndex, int targetIndex, out string reason) {
        if (
            (((uint)carrierIndex) >= ((uint)Capacity)) ||
            (m_entries[carrierIndex] is not { Active: true, Body: { } carrier })
        ) {
            reason = $"body:{carrierIndex} is not active";
            return false;
        }
        if (
            (((uint)targetIndex) >= ((uint)Capacity)) ||
            (m_entries[targetIndex] is not { Active: true, Body: { } target })
        ) {
            reason = $"body:{targetIndex} is not active";
            return false;
        }

        if (!carrier.TryBeginCarry(
            reason: out reason,
            selfIndex: carrierIndex,
            target: target,
            targetIndex: targetIndex
        )) {
            return false;
        }

        RegisterCarry(
            carrierIndex: carrierIndex,
            targetIndex: targetIndex
        );
        return true;
    }
    /// <summary>Ends <paramref name="carrierIndex"/>'s active carry, handing the target back to the rigid solver
    /// with the carrier's own current velocity — see <see cref="WorldBody.EndCarry"/>.</summary>
    /// <param name="carrierIndex">The carrier's population index.</param>
    /// <param name="reason">The refusal, by name, on failure (not active, or not currently carrying anything);
    /// empty on success.</param>
    public bool TryEndCarry(int carrierIndex, out string reason) {
        if (
            (((uint)carrierIndex) >= ((uint)Capacity)) ||
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

        if (IsCarriedTargetPenetrating(
            carrierIndex: carrierIndex,
            target: target,
            targetIndex: targetIndex
        )) {
            reason = $"body:{targetIndex}'s released pose penetrates geometry or another body";
            return false;
        }

        carrier.EndCarry(target: target);
        RemoveCarry(carrierIndex: carrierIndex);
        reason = "";
        return true;
    }

    /// <summary>Gets whether <paramref name="target"/>'s CURRENT pose (already the tangibly-blocked pose
    /// <see cref="WorldBody.FollowCarrier"/> and <see cref="ResolveCarriedBodyPush"/> left it at) overlaps static
    /// geometry or any other active solid body — the released-pose check <see cref="TryEndCarry"/> refuses a release
    /// by name against, so letting go never drops a body inside a wall or another body.</summary>
    private bool IsCarriedTargetPenetrating(int carrierIndex, WorldBody target, int targetIndex) {
        if (target.IsPenetratingStaticGeometry()) {
            return true;
        }

        if (target.Collider is null) {
            return false;
        }

        var targetVolumes = target.ScaledColliderVolumes();
        var neighbourhood = (m_carryNeighbourhood ??= new CarryNeighbourhood(capacity: Capacity));

        // A release runs between ticks, after bodies the last pass never saw may have moved.
        neighbourhood.Fresh = false;

        var neighbours = GatherCarriedNeighbours(
            carrierIndex: carrierIndex,
            neighbourhood: neighbourhood,
            target: target,
            targetIndex: targetIndex
        );

        for (var ordinal = 0; (ordinal < neighbours.Length); ordinal++) {
            var otherIndex = neighbours[ordinal];

            if (!IsCarryNeighbour(index: otherIndex, other: out var other)) {
                continue;
            }

            if (FixedDynamicBodyContacts.TryCorrection(
                leftPosition: target.FixedPosition,
                leftOrientation: target.FixedOrientation,
                leftVolumes: targetVolumes,
                rightPosition: other.FixedPosition,
                rightOrientation: other.FixedOrientation,
                rightVolumes: other.ScaledColliderVolumes(),
                tieBreaker: targetIndex ^ otherIndex,
                correction: out _
            )) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reconciles invalidated active relationships before body integration, so an orphaned target re-enters
    /// rigid advance and contact in the same tick its carrier disappears or either required facet is removed.</summary>
    public void PrepareCarriedBodies() => ReconcileCarriedBodies(follow: false);
    /// <summary>Updates valid attachments from their carriers' final pose after movement, dynamic contact, and tether
    /// correction, then performs the same bounded consistency check. Both carry passes walk the sorted,
    /// preallocated active-relationship table, so a NO-CARRY tick is O(1) and allocation-free rather than scanning
    /// population capacity. A tick with an active carry builds one <see cref="FixedSweepGrid"/> over the awake solid
    /// bodies' poses (rebuilt only after a push moved one) and asks it, with the retained sleeping index, for the
    /// bodies near each carried target inside <see cref="ResolveCarriedBodyPush"/> (and, on release,
    /// <see cref="IsCarriedTargetPenetrating"/>) — a carried target can be pushed by or block against ANY other active
    /// solid body, not only carriers, and only the ones whose bounds meet its own are tested.</summary>
    public void UpdateCarriedBodies() => ReconcileCarriedBodies(follow: true);

    private void ReconcileCarriedBodies(bool follow) {
        var relationshipIndex = 0;

        if (follow && (m_carryNeighbourhood is { } neighbourhood)) {
            // Bodies moved since the last pass (integration, contact, tethers), so its awake image is stale.
            neighbourhood.Fresh = false;
        }

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
                target.FollowCarrier(carrier: carrier, scratch: m_stepScratch);
                ResolveCarriedBodyPush(
                    carrier: carrier,
                    carrierIndex: relationship.CarrierIndex,
                    target: target,
                    targetIndex: relationship.TargetIndex
                );
            }
            relationshipIndex++;
        }
    }
    /// <summary>Pushes, and is blocked by, every other active solid body a carried target's own (already static-
    /// geometry-blocked — see <see cref="WorldBody.FollowCarrier"/>) position would overlap: the SAME positional
    /// split every plain dynamic pair applies, folded back onto the carrier too so it feels a body it is holding get
    /// stopped exactly as it already feels a wall. Physics owns the pairwise overlap geometry
    /// (<see cref="FixedDynamicBodyContacts.TryCorrection"/>); which bodies pair is this population's own call, on
    /// the same terms as <see cref="ResolveDynamicContacts"/>.</summary>
    private void ResolveCarriedBodyPush(WorldBody carrier, int carrierIndex, WorldBody target, int targetIndex) {
        if (target.Collider is null) {
            return;
        }

        var neighbourhood = (m_carryNeighbourhood ??= new CarryNeighbourhood(capacity: Capacity));
        var neighbours = GatherCarriedNeighbours(
            carrierIndex: carrierIndex,
            neighbourhood: neighbourhood,
            target: target,
            targetIndex: targetIndex
        );

        for (var ordinal = 0; (ordinal < neighbours.Length); ordinal++) {
            var otherIndex = neighbours[ordinal];

            if (!IsCarryNeighbour(index: otherIndex, other: out var other)) {
                continue;
            }

            if (!FixedDynamicBodyContacts.TryCorrection(
                leftPosition: target.FixedPosition,
                leftOrientation: target.FixedOrientation,
                leftVolumes: target.ScaledColliderVolumes(),
                rightPosition: other.FixedPosition,
                rightOrientation: other.FixedOrientation,
                rightVolumes: other.ScaledColliderVolumes(),
                tieBreaker: targetIndex ^ otherIndex,
                correction: out var correction
            )) {
                continue;
            }

            // Half each, unless a side's sweep was refused this tick: a refused body is immovable for the rest of its
            // tick, so the other side is resolved against it as static and takes the whole correction. A refused
            // target's share is zero, so a carrier is never corrected through it.
            var (targetShare, otherShare) = SplitPairCorrection(
                correction: correction,
                left: target,
                right: other
            );

            target.ApplyRigidPositionalCorrection(correction: targetShare);

            // A rigid other body takes the push through its rigid door, which wakes it — ApplyDynamicContact is the
            // locomotion body's own planar/vertical-velocity channels, which a rigid body does not read.
            ApplyPairShare(body: other, share: otherShare);
            ApplyPairShare(body: carrier, share: targetShare);
            // Two bodies moved, so the awake image the next carry queries no longer holds their poses.
            neighbourhood.Fresh = false;
        }
    }
    // Whether the slot holds an active solid body that a carried target pushes and is blocked by: the body must advance
    // on its own (so not itself carried) and have a collider.
    private bool IsCarryNeighbour(int index, [NotNullWhen(returnValue: true)] out WorldBody? other) {
        other = null;

        if (
            !m_entries[index].Active ||
            (BodyContact(index: index) != WorldBodyContactMode.Solid) ||
            (m_entries[index].Body is not { Collider: not null, OrdinaryAdvanceAdmitted: true, CarriedBy: null } body)
        ) {
            return false;
        }

        other = body;
        return true;
    }
    // The slots of the bodies whose bounds meet the carried target's, in ascending slot order (the order every carry
    // pass applied their corrections in), excluding the target and its carrier. Awake bodies come from an image of
    // their current poses, rebuilt only after a pose it holds changed; sleepers, which never move, come from the
    // retained sleeping index. Each slot still passes IsCarryNeighbour and the exact overlap test, so the broadphase
    // only narrows which slots are asked.
    private ReadOnlySpan<int> GatherCarriedNeighbours(int carrierIndex, CarryNeighbourhood neighbourhood, WorldBody target, int targetIndex) {
        if (!neighbourhood.Fresh) {
            var count = 0;

            foreach (var index in m_awakeBodies) {
                if (IsCarryNeighbour(index: index, other: out var awake)) {
                    neighbourhood.Spheres[count++] = new FixedSweepSphere(Index: index, Center: awake.FixedPosition, Radius: awake.BroadphaseRadius);
                }
            }
            neighbourhood.Grid.Rebuild(spheres: neighbourhood.Spheres.AsSpan(start: 0, length: count));
            neighbourhood.Fresh = true;
        }

        var radius = target.BroadphaseRadius;
        var found = neighbourhood.Grid.Query(
            box: FixedAabb.Around(center: target.FixedPosition, radius: radius),
            destination: neighbourhood.Found
        );
        var sleepers = NearbySleepingBodies(body: target, destination: neighbourhood.Sleepers, reach: radius);
        var kept = 0;

        for (var ordinal = 0; (ordinal < (found + sleepers)); ordinal++) {
            var index = ((ordinal < found) ? neighbourhood.Found[ordinal] : neighbourhood.Sleepers[(ordinal - found)]);

            if ((index != targetIndex) && (index != carrierIndex)) {
                neighbourhood.Found[kept++] = index;
            }
        }

        // Awake and sleeping slots never repeat; one sort of both yields slot order.
        var neighbours = neighbourhood.Found.AsSpan(start: 0, length: kept);

        neighbours.Sort();
        return neighbours;
    }
    // A carry share through the body's own door: the rigid positional correction for a rigid body, the locomotion one
    // otherwise.
    private static void ApplyPairShare(WorldBody body, FixedVector3 share) {
        if (body.IsRigid) {
            body.ApplyRigidPositionalCorrection(correction: share);
        } else {
            body.ApplyDynamicContact(correction: share);
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
                RegisterCarry(
                    carrierIndex: index,
                    targetIndex: targetIndex
                );
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
            m_activeCarries[index] = new CarryRelationship(
                CarrierIndex: carrierIndex,
                TargetIndex: targetIndex
            );
            return;
        }

        Array.Copy(
            destinationArray: m_activeCarries,
            destinationIndex: (index + 1),
            length: (m_activeCarryCount - index),
            sourceArray: m_activeCarries,
            sourceIndex: index
        );
        m_activeCarries[index] = new CarryRelationship(
            CarrierIndex: carrierIndex,
            TargetIndex: targetIndex
        );
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
            destinationArray: m_activeCarries,
            destinationIndex: index,
            length: (m_activeCarryCount - index),
            sourceArray: m_activeCarries,
            sourceIndex: (index + 1)
        );
        m_activeCarries[m_activeCarryCount] = default;
    }
}
