using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    private FixedVector3? m_diagnosticManifoldPoint;
    /// <summary>The actual last analytic support solve, absent when ungrounded or not evaluated by that provider.</summary>
    public ContactSupportWitness? DiagnosticSupport { get; private set; }
    /// <summary>The authored snap search reach, information only, never a diagnostic hover tolerance.</summary>
    public double DiagnosticSnapReach => (double)GroundSnap;
    /// <summary>Whether the solved normal velocity is within one fixed-point unit of rest.</summary>
    public bool DiagnosticSettled => DiagnosticSupport is { } support &&
        FixedQ4816.Abs(FixedVector3.Dot(IsRigid ? m_rigidVelocity : m_planarVelocity + m_up * m_verticalVelocity,
            support.Push.Normal)) <= FixedQ4816.Epsilon;
    // Position/planar contact response applies to ANY collider-bearing body regardless of body motion program — a flying
    // body still shouldn't clip through a wall. The vertical WRITE-BACK (m_verticalVelocity, m_planarVelocity, the
    // grounded position-accumulator reset) is gated on CompiledBodyMotionProgram.OwnsVerticalContactState — see its
    // own remarks for which programs cede the channel and which keep it. m_grounded/m_lastContactCount stay
    // informational for every model (RunActionTriggers' ActionFact.Grounded/Airborne reads them under any program),
    // since they never feed back into an integration.
    private void ResolveProgramContacts(ref BodyMotionScratch scratch) {
        DiagnosticSupport = null;
        if (
            (m_contactField is { } field) &&
            (m_collider is { } collider)
        ) {
            var resolvedVelocity = scratch.Velocity;
            var intended = scratch.NextPosition;
            var step = new BodyContactStep(
                entityIndex: scratch.EntityIndex,
                field: field,
                orientation: scratch.Orientation,
                up: scratch.Up,
                volumes: ScaledColliderVolumes()
            );
            var contactResolution = step.Sweep(
                position: ref scratch.NextPosition,
                previousPosition: m_position,
                velocity: ref resolvedVelocity
            );

            // A refused step writes nothing from its resolution; everything the step wrote is restored once it ends.
            if (contactResolution.Refusal != ContactRefusal.None) {
                NoteSweepRefusal(refusal: contactResolution.Refusal);

                return;
            }

            // STEP-UP. A grounded body (m_grounded is still last tick's verdict here) whose step met a non-walkable
            // face retries it over the face, through the same sweep; FixedStepUp keeps the retry only when it lands
            // walkable within the kit's step height.
            if (
                m_grounded &&
                (contactResolution.ObstructionNormal != FixedVector3.Zero) &&
                (m_tuning.StepHeight > FixedQ4816.Zero) &&
                m_bodyMotionProgram.OwnsVerticalContactState &&
                !HoldOwnsVerticalChannel &&
                FixedStepUp.TryStep(
                    requiredStepHeight: contactResolution.RequiredStepHeight,
                    blocked: in scratch.NextPosition,
                    intended: in intended,
                    result: out var stepped,
                    start: m_position,
                    step: ref step,
                    stepHeight: StepHeight,
                    up: in scratch.Up,
                    velocity: in scratch.Velocity
                )
            ) {
                scratch.NextPosition = stepped.Position;
                resolvedVelocity = stepped.Velocity;
                contactResolution = stepped.Resolution;
            }

            // Keep walking contact across crests and descending slopes. A jump or deliberate launch clears
            // m_grounded before integration; direct upward input also owns its departure.
            if (m_grounded && m_bodyMotionProgram.OwnsVerticalContactState && !HoldOwnsVerticalChannel &&
                (scratch.DirectVerticalVelocity <= FixedQ4816.Zero) && !contactResolution.Grounded &&
                !contactResolution.Unproved &&
                FixedStepUp.TrySnap(step: ref step, start: in scratch.NextPosition, velocity: in resolvedVelocity,
                    up: in scratch.Up, distance: GroundSnap, result: out var snapped)) {
                scratch.NextPosition = snapped.Position;
                resolvedVelocity = snapped.Velocity;
                contactResolution = snapped.Resolution with { ObstructionNormal = contactResolution.ObstructionNormal };
            }

            m_grounded = contactResolution.Grounded;
            DiagnosticSupport = contactResolution.Support;
            if (m_grounded && m_bodyMotionProgram.OwnsVerticalContactState && !HoldOwnsVerticalChannel) {
                resolvedVelocity = resolvedVelocity.RejectFrom(unitAxis: contactResolution.GroundNormal);
            }

            // Under SurfaceFollowing, a standing body's up is the SURFACE it stands on, not the direction its field
            // pulls. The two differ wherever a floor is not perpendicular to the field — a flat floor under a field
            // tilted by distant attractors is the ordinary case — and walking the field's tangent instead of the
            // floor's carries the body off the floor a little further every tick.
            //
            // The velocity is carried into the new frame by the SAME rotation. Decomposing motion that was tangent to
            // the old surface against a rotated up reads part of it as climbing, and the write-back below stores that
            // as ballistic velocity: on a sphere that is a launch, and the faster the body runs the harder it is
            // thrown off.
            // Only under SurfaceFollowing: a measured normal is a fact about the surface, and only a body policy that
            // admits surface-following may let it move the axis — a rounded lip or a blended corner tilts the normal,
            // and adopting that tilt under Ambient pitches the body over and lets the face beside it read as
            // ground. And only where this body participates in an authored solved field:
            // outside every area in an area-only world the up axis has a single source already (the field provider's
            // own per-sample gradient), and adopting a measured contact normal on top would make it wobble, which a
            // marginal handoff — an adjacency seam strip — cannot absorb.
            if (
                m_grounded &&
                (m_upPolicy == WorldBodyUpPolicy.SurfaceFollowing) &&
                TrySolvedGravity(acceleration: out _) &&
                (contactResolution.GroundNormal != FixedVector3.Zero)
            ) {
                // BOUNDED, for the same reason the field's axis is: a measured normal is continuous only where the
                // surface is. The analytic collider approximates a creation as a UNION of its primitives and carries
                // none of the authored blend, so wherever two blend in the render — a planetoid's outcrops into its
                // core — the walked surface has a crease the seen surface does not, and the normal jumps across it.
                // Adopting that jump whole rotates the velocity with it, so a body running over a crease is kicked
                // sideways by tens of degrees in a single tick.
                //
                // The ceiling is far above any real curvature (a body at full sprint on the tightest planetoid turns
                // its normal an order of magnitude slower), so ordinary running still tracks the surface exactly and
                // only a discontinuity is spread — over a few ticks, which reads as instant.
                FixedQuaternion transport;

                if (m_upNeedsReseat) {
                    m_upNeedsReseat = false;
                    transport = FixedQuaternion.FromTo(
                        from: m_up,
                        to: contactResolution.GroundNormal
                    );
                    SetUp(next: contactResolution.GroundNormal);
                } else {
                    transport = SteerUpToward(
                        accumulator: ref m_contactUpTurnAccumulator,
                        halfRate: ContactUpTurnHalfRate,
                        stepTicks: scratch.StepTicks,
                        target: contactResolution.GroundNormal
                    );
                }

                resolvedVelocity = transport.Rotate(vector: resolvedVelocity);
                scratch.Up = m_up;
            }
            m_lastContactCount = (m_grounded
                ? 1
                : 0
            );
            // scratch.Intent's raw MoveAdvance/MoveStrafe roles are the idle signal — resolved once by NextIntent
            // before ANY op runs, so it is available and current at this exact point regardless of the compiled
            // program's op order (unlike scratch.TargetVelocity/scratch.Velocity, which a Compute*TargetVelocity op
            // may not have written yet this tick depending on where contact resolution sits in that order, and which
            // — once written — is the RESPONSE-RAMPED result the wall itself just clipped: using either would risk
            // a feedback loop, a wall stopping the body read back as "input released").
            UpdateObstructionWitness(
                rawObstruction: contactResolution.ObstructionNormal,
                intent: in scratch.Intent,
                position: scratch.NextPosition,
                stepTicks: scratch.StepTicks
            );

            if (
                !m_bodyMotionProgram.OwnsVerticalContactState ||
                HoldOwnsVerticalChannel
            ) {
                // A grip owns the whole tangent-plane velocity, vertical component included — splitting it against
                // the body's up axis and storing the remainder as ballistic velocity would leave the climb's own
                // rise to be re-added by gravity the tick the hold ends.
                return;
            }

            var resolvedNormal = FixedVector3.Dot(
                left: resolvedVelocity,
                right: scratch.Up
            );

            m_planarVelocity = (resolvedVelocity - (scratch.Up * resolvedNormal));

            // The direct term is one tick of authored input, not persistent motion state. Never fold contact's
            // clipped drive into the ballistic channel: holding down against a floor would otherwise store an equal
            // upward launch for the frame the trigger was released.
            if (scratch.DirectVerticalVelocity != FixedQ4816.Zero) {
                m_verticalVelocity = FixedQ4816.Zero;
                m_verticalVelocityAccumulator.Reset();
                if (m_grounded) {
                    m_positionAccumulator.ResetY();
                }
                return;
            }

            var settled = resolvedNormal;

            if (settled != m_verticalVelocity) {
                m_verticalVelocity = settled;
                m_verticalVelocityAccumulator.Reset();
            }

            if (m_grounded) {
                m_positionAccumulator.ResetY();
            }
        } else {
            m_grounded = false;
            m_lastContactCount = 0;
            m_obstructionWitness = FixedVector3.Zero;
            m_obstructionWitnessGraceTicks = 0;
        }
    }

    // The embedding escape's search: eighth-metre steps out to eight metres, and the push a free pose may still take
    // (a body resting on a floor is pushed by up to its contact skin).
    private const int EmbeddingEscapeSteps = 64;

    private static readonly FixedQ4816 EmbeddingEscapeStep = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 3)));
    private static readonly FixedQ4816 EmbeddingFreePushSquared = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 8)));

    // Moves a body whose kit just handed it a different collider out of any static geometry that collider finds it
    // embedded in: a body leaving a volumeless kit inside a wall, or one whose new volume is taller than the room it
    // stands in. The ordinary solve pushes each volume out along its shallowest axis, which from deep inside a box can
    // be down through the floor beneath it, so this searches the nearest free pose up the body's axis and along world
    // X and Z, never down, and takes the first at the least distance (up wins a tie). A body nowhere free within the
    // search keeps its pose and the ordinary solve resolves it.
    private void EscapeEmbedding() {
        if (
            (m_contactField is not { } field) ||
            (m_collider is null) ||
            IsRigid ||
            !IsEmbeddedAt(field: field, position: m_position)
        ) {
            return;
        }

        ReadOnlySpan<FixedVector3> directions = [m_up, FixedVector3.UnitX, -FixedVector3.UnitX, FixedVector3.UnitZ, -FixedVector3.UnitZ];

        for (var step = 1; (step <= EmbeddingEscapeSteps); step++) {
            var distance = (EmbeddingEscapeStep * FixedQ4816.FromInteger(value: step));

            foreach (var direction in directions) {
                var candidate = (m_position + (direction * distance));

                if (!IsEmbeddedAt(field: field, position: candidate)) {
                    WakeUp();
                    m_position = candidate;

                    return;
                }
            }
        }
    }
    private static bool SameVolumes(FixedWorldCollider? left, FixedWorldCollider? right) =>
        (((left is { } leftCollider) && (right is { } rightCollider))
            ? leftCollider.Volumes.SequenceEqual(other: rightCollider.Volumes)
            : (left.HasValue == right.HasValue));
    // Whether the static solve pushes this body's volumes further than a resting contact would at a pose.
    private bool IsEmbeddedAt(IContactField field, FixedVector3 position) {
        var resolved = position;
        var velocity = FixedVector3.Zero;

        _ = field.Resolve(
            orientation: in m_orientation,
            position: ref resolved,
            up: in m_up,
            velocity: ref velocity,
            volumes: ScaledColliderVolumes()
        );

        return ((resolved - position).LengthSquared > EmbeddingFreePushSquared);
    }

    /// <summary>Applies one deterministic body-contact depenetration without turning it into a teleport. A body whose
    /// sweep was refused this tick (<see cref="SweepRefusedThisTick"/>) is immovable until the tick ends, so the
    /// correction is not applied.</summary>
    internal void ApplyDynamicContact(FixedVector3 correction) {
        if (
            (correction == FixedVector3.Zero) ||
            m_sweepRefusedThisTick
        ) {
            return;
        }

        // A peer pushed this body: its program must run again to carry the push.
        WakeUp();

        m_position += correction;
        var normal = correction.Normalize();
        var velocity = ComposedVelocity();
        var inward = FixedVector3.Dot(
            left: velocity,
            right: normal
        );

        if (inward < FixedQ4816.Zero) {
            SplitVelocity(
                resetVerticalRemainder: true,
                velocity: (velocity - (normal * inward))
            );
        }
    }
    // Carries a body that stood on an attached solid last tick along with it: the field reports how far the platform's
    // carrier moved between its last two refreshes, and the body moves by the same before its own step, so a rising
    // platform lifts it without a push and a falling one keeps it standing rather than dropping it a tick at a time.
    // Only a grounded locomotion body rides; a rigid or carried body has its own contact path.
    internal void RideAttachedSupport(WorldColliderSet field) {
        if (
            !m_grounded ||
            (m_collider is null) ||
            IsRigid ||
            (m_carriedByIndex >= 0) ||
            m_sweepRefusedThisTick ||
            !field.TryRideDisplacement(
                up: in m_up,
                displacement: out var displacement,
                orientation: in m_orientation,
                position: m_position,
                volumes: ScaledColliderVolumes()
            )
        ) {
            return;
        }

        WakeUp();
        m_position += displacement;
    }

    // One body's swept contact solve, bound to its pose and volumes: the ordinary step and every step-up move run
    // through it, so an entity-aware field resolves each of them against the other bodies too.
    private readonly ref struct BodyContactStep(IContactField field, int entityIndex, FixedQuaternion orientation, FixedVector3 up, ReadOnlySpan<FixedBodyColliderVolume> volumes) : IFixedContactStep {
        private readonly IContactField m_field = field;
        private readonly int m_entityIndex = entityIndex;
        private readonly FixedQuaternion m_orientation = orientation;
        private readonly FixedVector3 m_axis = up;
        private readonly ReadOnlySpan<FixedBodyColliderVolume> m_volumes = volumes;

        public ContactResolution Sweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity) =>
            ((m_field is IEntityContactField entityField)
                ? entityField.ResolveEntitySweep(
                    entityIndex: m_entityIndex,
                    orientation: in m_orientation,
                    position: ref position,
                    previousPosition: in previousPosition,
                    up: in m_axis,
                    velocity: ref velocity,
                    volumes: m_volumes
                )
                : m_field.ResolveSweep(
                    orientation: in m_orientation,
                    position: ref position,
                    previousPosition: in previousPosition,
                    up: in m_axis,
                    velocity: ref velocity,
                    volumes: m_volumes
                )
            );
    }

    // A walking body's velocity in world space: its planar velocity, tangent to the body's own up, plus its vertical
    // velocity along that up, the frame it integrates in (IntegratePlanarAndVerticalVelocity). Every write that moves
    // the velocity in world space composes it here and splits it back with SplitVelocity, so none splits it against
    // world Y, which differs from the body's up wherever the field or the surface tilts it.
    private FixedVector3 ComposedVelocity() => (m_planarVelocity + (m_up * m_verticalVelocity));
    // Splits a world-space velocity into the planar and vertical channels against the body's own up. A vertical velocity
    // that changes starts its rate remainder afresh only when the caller says so; the paths that never reset it keep
    // their remainder as before.
    private void SplitVelocity(FixedVector3 velocity, bool resetVerticalRemainder) {
        var vertical = FixedVector3.Dot(
            left: velocity,
            right: m_up
        );

        m_planarVelocity = velocity.RejectFrom(unitAxis: m_up);

        if (m_verticalVelocity != vertical) {
            m_verticalVelocity = vertical;

            if (resetVerticalRemainder) {
                m_verticalVelocityAccumulator.Reset();
            }
        }
    }
}
