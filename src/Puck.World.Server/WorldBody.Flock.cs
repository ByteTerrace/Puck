using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // Tick-local binding, rebuilt before Advance; no domain object or diagnostic counter belongs in a checkpoint.
    private WorldNavigationRuntime.Domain? m_flockMovementDomain;
    internal bool FlockMotionChecked { get; private set; }
    internal bool FlockMotionRefused { get; private set; }

    internal void SetFlockMovementDomain(WorldNavigationRuntime.Domain? domain) {
        m_flockMovementDomain = domain;
        FlockMotionChecked = false;
        FlockMotionRefused = false;
    }

    private void ConstrainFlockLocomotion(ref BodyMotionScratch scratch) {
        if (m_flockMovementDomain is not { } domain || scratch.NextPosition == m_position) { return; }
        FlockMotionChecked = true;
        if (domain.AdmitsLocomotion(m_position, scratch.NextPosition)) { return; }
        FlockMotionRefused = true;
        scratch.NextPosition = m_position;
        m_positionAccumulator.Reset();
        ResetTranslationMomentum();
    }

    private void ProduceFlockIntent(ref BodyMotionScratch scratch) {
        var desired = scratch.ProducerSensors.FlockDesired;
        FixedVector3 facing;
        FixedVector3 right;
        FixedVector3 up;
        if (m_bodyMotionProgram.Contains(BodyMotionOp.ComputeLocalTargetVelocity)) {
            facing = m_tuning.MoveFrame == MotionMoveFrame.World ? -UnitZ : m_orientation.Rotate(-UnitZ);
            right = m_tuning.MoveFrame == MotionMoveFrame.World ? UnitX : m_orientation.Rotate(UnitX);
            up = m_tuning.MoveFrame == MotionMoveFrame.World ? UnitY : m_orientation.Rotate(UnitY);
        } else {
            // The same transported translation basis ResolveYawAttitudeAndPlanarFrame consumes. A producer's
            // preference is in world axes; a World-frame kit on a wall still consumes its transported tangent axes.
            var basis = m_tuning.MoveFrame == MotionMoveFrame.World ? m_frame : m_orientation;
            facing = basis.Rotate(-UnitZ);
            right = basis.Rotate(UnitX);
            up = m_up;
        }
        scratch.Intent = m_roleOrdinals.Intent(
            moveAdvance: FixedVector3.Dot(desired, facing),
            moveStrafe: FixedVector3.Dot(desired, right),
            moveUp: FixedVector3.Dot(desired, up));
    }
}
