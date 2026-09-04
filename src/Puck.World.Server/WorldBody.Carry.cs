using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // -1 (no relationship) on the same "raw index, negative means none" terms m_affectingSubject already carries —
    // never compared as a boolean fact, always as a population index or its absence.
    private int m_carryingIndex = -1;
    private int m_carriedByIndex = -1;

    /// <summary>Gets the population index of the rigid body this body is currently carrying, or
    /// <see langword="null"/> when it is carrying nothing.</summary>
    public int? Carrying => ((m_carryingIndex >= 0) ? m_carryingIndex : null);
    /// <summary>Gets whether the currently compiled kit still owns the carry facet an active relationship needs.</summary>
    internal bool HasCarryFacet => (m_carry is not null);
    /// <summary>Gets the population index of the body currently carrying this one, or <see langword="null"/> when
    /// this body is not carried. A carried body's own <see cref="Advance"/> call is a no-op — its pose and rigid
    /// velocity are DERIVED from the carrier every tick by <see cref="FollowCarrier"/> instead, called from
    /// <see cref="WorldPopulation"/> after movement and correction passes complete.</summary>
    public int? CarriedBy => ((m_carriedByIndex >= 0) ? m_carriedByIndex : null);

    /// <summary>Attempts to begin carrying <paramref name="target"/>: this body's kit must author a carry facet,
    /// <paramref name="target"/> must carry a rigid kit facet neither body may already be a party to another carry
    /// relationship, <paramref name="target"/> must sit within the carrier's own live-scaled reach, and
    /// <paramref name="target"/>'s own live-scaled <see cref="RigidMass"/> must not exceed the carrier's own
    /// live-scaled carry ceiling (<see cref="FixedWorldCarry.MaxCarryMass"/> × this body's Scale³, the same mass law
    /// <see cref="ScaleRigid"/> derives against). On success <paramref name="target"/>'s rigid velocity is zeroed
    /// (its own integration is suspended — <see cref="FollowCarrier"/> takes over every tick) and it wakes from
    /// rest.</summary>
    /// <param name="target">The candidate body to pick up.</param>
    /// <param name="targetIndex">The candidate's population index.</param>
    /// <param name="selfIndex">This body's own population index — refuses carrying itself.</param>
    /// <param name="reason">The refusal, by name, on failure; empty on success.</param>
    internal bool TryBeginCarry(WorldBody target, int targetIndex, int selfIndex, out string reason) {
        if (m_carry is not { } carry) {
            reason = "its kit carries no carry facet";
            return false;
        }
        if (!target.IsRigid) {
            reason = $"body:{targetIndex} carries no rigid kit facet";
            return false;
        }
        if (targetIndex == selfIndex) {
            reason = "cannot carry itself";
            return false;
        }
        if (m_carryingIndex >= 0) {
            reason = $"already carrying body:{m_carryingIndex}";
            return false;
        }
        if (m_carriedByIndex >= 0) {
            reason = $"body:{selfIndex} is already carried by body:{m_carriedByIndex}";
            return false;
        }
        if (target.m_carriedByIndex >= 0) {
            reason = $"already carried by body:{target.m_carriedByIndex}";
            return false;
        }
        if (target.m_carryingIndex >= 0) {
            reason = $"body:{targetIndex} is already carrying body:{target.m_carryingIndex}";
            return false;
        }

        var scaleSquared = SaturatingNonnegativeProduct(left: m_scale, right: m_scale);
        var scaleCubed = SaturatingNonnegativeProduct(left: scaleSquared, right: m_scale);
        var reach = SaturatingNonnegativeProduct(left: carry.MaxReach, right: m_scale);
        var distance = (target.m_position - m_position).Length;

        if (distance > reach) {
            reason = $"body:{targetIndex} is out of reach ({(double)distance:0.###} > {(double)reach:0.###})";
            return false;
        }

        var ceiling = SaturatingNonnegativeProduct(left: carry.MaxCarryMass, right: scaleCubed);
        var targetMass = target.RigidMass;

        if (targetMass > ceiling) {
            reason = $"body:{targetIndex}'s mass {(double)targetMass:0.###} exceeds this body's carry ceiling {(double)ceiling:0.###}";
            return false;
        }

        m_carryingIndex = targetIndex;
        target.m_carriedByIndex = selfIndex;
        target.m_rigidVelocity = FixedVector3.Zero;
        target.m_angularVelocity = FixedVector3.Zero;
        target.m_resting = false;
        target.m_restingHoldTicks = 0UL;
        reason = "";
        return true;
    }

    /// <summary>Gets whether this body's own collider, at its CURRENT pose, overlaps the static contact field — a
    /// zero-displacement probe sweep (previous position equal to current) that still reports a correction exactly
    /// when the pose is already embedded. Used to refuse a carry release whose left-behind pose would penetrate a
    /// wall — see <c>WorldPopulation.TryEndCarry</c>. <see langword="false"/> for a body with no collider or contact
    /// field, since there is nothing there to embed in.</summary>
    internal bool IsPenetratingStaticGeometry() {
        if (
            (m_contactField is not { } field) ||
            (m_collider is not { } collider)
        ) {
            return false;
        }

        Span<FixedBodyColliderVolume> scratch = stackalloc FixedBodyColliderVolume[WorldCollider.MaxVolumes];
        var volumes = ScaledColliderVolumes(
            scratch: scratch,
            volumes: collider.Volumes
        );
        var probePosition = m_position;
        var probeVelocity = FixedVector3.Zero;

        field.ResolveSweep(
            orientation: in m_orientation,
            position: ref probePosition,
            previousPosition: in m_position,
            up: in UnitY,
            velocity: ref probeVelocity,
            volumes: volumes
        );

        return (probePosition != m_position);
    }
    /// <summary>Ends this body's active carry, handing <paramref name="target"/> back to the rigid solver with this
    /// body's own current world velocity (<see cref="ApproximateWorldVelocity"/>) — a released body leaves with the
    /// carrier's motion rather than snapping to rest. A no-op (never a refusal) when this body carries nothing;
    /// there is nothing a retry could change about "already released".</summary>
    /// <param name="target">The carried body — the caller resolves it from <see cref="Carrying"/>.</param>
    internal void EndCarry(WorldBody target) {
        if (m_carryingIndex < 0) {
            return;
        }

        target.m_carriedByIndex = -1;
        target.m_rigidVelocity = ApproximateWorldVelocity();
        target.m_angularVelocity = FixedVector3.Zero;
        m_carryingIndex = -1;
    }

    /// <summary>Clears this body's own <see cref="Carrying"/> without touching any other body — the carrier-side
    /// half of an orphaned relationship's cleanup (its target went inactive, lost its rigid facet, or the mirror
    /// otherwise broke), applied by <see cref="WorldPopulation"/>'s per-tick sweep rather than
    /// <see cref="EndCarry"/>, which additionally hands the target a release velocity that presumes it is still a
    /// live body to hand one to.</summary>
    internal void ForceDropCarrying() {
        m_carryingIndex = -1;
    }
    /// <summary>Clears this body's own <see cref="CarriedBy"/> and re-arms it for the rigid solver — the
    /// target-side half of an orphaned relationship's cleanup (its carrier went inactive or lost its carry facet).
    /// Velocity is left at whatever <see cref="FollowCarrier"/> last wrote (typically the carrier's own, zero if it
    /// never ran this tick) rather than forced to zero, so a body orphaned mid-swing keeps falling from where it
    /// was instead of snapping to rest.</summary>
    internal void ForceRelease() {
        m_carriedByIndex = -1;
    }

    /// <summary>Derives this carried body's pose and rigid velocity from <paramref name="carrier"/>'s own frame for
    /// the tick that just completed — called once per tick after movement, dynamic contact, and tether correction,
    /// so it reads the carrier's final authoritative pose for that tick. The body-local carry point scales with the
    /// carrier on the same geometric terms as its collider and reach. TANGIBLE: the carrier's frame names a desired
    /// pose, never an unconditional one — this body's own collider is swept from its previous position to that
    /// desired one against static geometry, so it is BLOCKED at a wall rather than passing through it as the carrier
    /// keeps walking; whatever correction the sweep applied is handed straight back to the carrier
    /// (<see cref="ApplyDynamicContact"/>), so the carrier is stopped too, not just the object it is holding. A
    /// no-op when <paramref name="carrier"/>'s kit carries no carry facet (its own <see cref="RecompileKit"/> retuned
    /// away from one while this body stayed attached — the caller is still responsible for tearing down the
    /// relationship in that case). Pushing and being blocked by another BODY (rather than static geometry) is the
    /// caller's own separate pass — see <c>WorldPopulation.ResolveCarriedBodyPush</c> — since only the population
    /// can see every other body.</summary>
    internal void FollowCarrier(WorldBody carrier) {
        if (carrier.m_carry is not { } carry) {
            return;
        }

        var previousPosition = m_position;
        var desiredPosition = (carrier.m_position + carrier.m_orientation.Rotate(
            vector: (carry.Offset * carrier.m_scale)
        ));

        m_orientation = carrier.m_orientation;

        if (
            (m_contactField is { } field) &&
            (m_collider is { } collider)
        ) {
            Span<FixedBodyColliderVolume> scratch = stackalloc FixedBodyColliderVolume[WorldCollider.MaxVolumes];
            var volumes = ScaledColliderVolumes(
                scratch: scratch,
                volumes: collider.Volumes
            );
            var sweptPosition = desiredPosition;
            var sweptVelocity = (desiredPosition - previousPosition);

            field.ResolveSweep(
                orientation: in m_orientation,
                position: ref sweptPosition,
                previousPosition: in previousPosition,
                up: in UnitY,
                velocity: ref sweptVelocity,
                volumes: volumes
            );

            var blockCorrection = (sweptPosition - desiredPosition);

            desiredPosition = sweptPosition;

            if (blockCorrection != FixedVector3.Zero) {
                // The carried body's OWN position already took the full sweep correction above — it is never left
                // penetrating. What reaches the CARRIER is bounded to this body's own bounding radius: a legitimate
                // touch-and-block push is well under an object's own size, while a correction larger than the
                // object itself (a carried body posed — or otherwise placed — already embedded in geometry, so the
                // very first sweep reports a one-shot depenetration spanning most of the gap) would otherwise hand
                // the carrier a shove sized by that embedding depth rather than by contact, launching it.
                var correctionLength = blockCorrection.Length;
                var correctionCeiling = RigidBoundingRadius;

                if (
                    (correctionCeiling > FixedQ4816.Zero) &&
                    (correctionLength > correctionCeiling)
                ) {
                    blockCorrection = ((blockCorrection / correctionLength) * correctionCeiling);
                }

                carrier.ApplyDynamicContact(correction: blockCorrection);
            }
        }

        m_position = desiredPosition;
        m_rigidVelocity = carrier.ApproximateWorldVelocity();
        m_angularVelocity = FixedVector3.Zero;
    }
}
