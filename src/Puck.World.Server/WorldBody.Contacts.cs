using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // Position/planar contact response applies to ANY collider-bearing body regardless of body motion program — a flying
    // body still shouldn't clip through a wall. The vertical WRITE-BACK (m_verticalVelocity, m_planarVelocity, the
    // grounded position-accumulator reset) is gated on CompiledBodyMotionProgram.OwnsVerticalContactState — see its
    // own remarks for which programs cede the channel and which keep it. m_grounded/m_lastContactCount stay
    // informational for every model (RunActionTriggers' ActionFact.Grounded/Airborne reads them under any program),
    // since they never feed back into an integration.
    private void ResolveProgramContacts(ref BodyMotionScratch scratch) {
        if (
            (m_contactField is { } field) &&
            (m_collider is { } collider)
        ) {
            var resolvedVelocity = scratch.Velocity;
            var volumes = ScaledColliderVolumes();
            var contactResolution = ((field is IEntityContactField entityField)
                ? entityField.ResolveEntitySweep(
                    entityIndex: scratch.EntityIndex,
                    orientation: in scratch.Orientation,
                    position: ref scratch.NextPosition,
                    previousPosition: m_position,
                    up: in scratch.Up,
                    velocity: ref resolvedVelocity,
                    volumes: volumes
                )
                : field.ResolveSweep(
                    orientation: in scratch.Orientation,
                    position: ref scratch.NextPosition,
                    previousPosition: m_position,
                    up: in scratch.Up,
                    velocity: ref resolvedVelocity,
                    volumes: volumes
                )
            );

            // A refused step writes nothing from its resolution; the body's motion is restored once the program ends.
            if (contactResolution.Refusal != ContactRefusal.None) {
                m_sweepRefusal = contactResolution.Refusal;

                return;
            }

            m_grounded = contactResolution.Grounded;

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

            // GROUND STICK. Contact removes the velocity driving into a surface, so a standing body carries no inward
            // motion at all — fine on a flat floor, fatal on a convex one: the surface curves away, the body keeps
            // going straight, and it leaves the ground under its own walking speed. A small inward bias while grounded
            // keeps it pressed against whatever it stands on, and depenetration removes the excess exactly as it does
            // for gravity. Released the moment the body stops being grounded, so a jump or a ledge still launches
            // cleanly.
            // Only a surface that is not world-level needs it: on flat ground contact already holds the body, and an
            // imposed inward speed there only eats into the margin a marginal handoff (an adjacency seam strip) has to
            // work with. A level floor keeps its previous behaviour exactly.
            var settled = ((m_grounded && (m_up != FixedVector3.UnitY) && (resolvedNormal > -StickSpeed))
                ? -StickSpeed
                : resolvedNormal
            );

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
}
