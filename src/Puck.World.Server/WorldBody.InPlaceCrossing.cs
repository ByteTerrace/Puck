using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Carries this body through a portal pair whose two faces stand in its own world: every world-space
    /// point the body holds moves through the pair's isometry (<see cref="WorldFrameIsometry.MapPoint"/>) and every
    /// direction and rate turns with it (<see cref="WorldFrameIsometry.MapVector"/>), its previous position included,
    /// so the crossing is continuous motion rather than a teleport. Its momentum, motion followers, integration
    /// remainders, holds, facts and action state carry on untouched, and the snapshot reports
    /// <see cref="EntityContinuityKind.Carry"/> so presentation carries what it latched the same way. Every value is
    /// fixed point. The pair must map world up onto itself (<see cref="WorldFrameIsometry.PreservesWorldUp"/>), which
    /// leaves the vertical velocity as it was.</summary>
    /// <param name="source">The frame of the face the body entered.</param>
    /// <param name="destination">The frame of that face's counterpart.</param>
    public void CrossInPlace(in WorldFaceFrame source, in WorldFaceFrame destination) {
        var from = source;
        var to = destination;

        FixedVector3 Point(FixedVector3 point) => WorldFrameIsometry.MapPoint(
            destination: in to,
            point: point,
            source: in from
        );
        FixedVector3 Direction(FixedVector3 direction) => WorldFrameIsometry.MapVector(
            destination: in to,
            source: in from,
            value: direction
        );

        m_position = Point(point: m_position);
        m_previousPosition = Point(point: m_previousPosition);
        m_holdAnchor = Point(point: m_holdAnchor);
        m_obstructionWitnessPosition = Point(point: m_obstructionWitnessPosition);

        // A pair that turns nothing (each world axis maps onto itself) leaves every direction, the attitude and the
        // remainders exactly as they are; only a turning pair rotates them.
        var turns = (
            (Direction(direction: FixedVector3.UnitX) != FixedVector3.UnitX) ||
            (Direction(direction: FixedVector3.UnitZ) != FixedVector3.UnitZ)
        );

        if (turns) {
            var yawDelta = WorldFrameIsometry.YawDelta(
                destination: in to,
                source: in from
            );
            var rotation = WorldFrameIsometry.Rotation(
                destination: in to,
                source: in from
            );

            m_planarVelocity = Direction(direction: m_planarVelocity);
            m_planarPreviousTarget = Direction(direction: m_planarPreviousTarget);
            m_overlayVelocity = Direction(direction: m_overlayVelocity);
            m_rigidVelocity = Direction(direction: m_rigidVelocity);
            m_angularVelocity = Direction(direction: m_angularVelocity);
            m_obstructionWitness = Direction(direction: m_obstructionWitness);
            m_holdNormal = Direction(direction: m_holdNormal);
            m_attitudeUp = Direction(direction: m_attitudeUp);
            m_up = Direction(direction: m_up);
            m_frame = (rotation * m_frame).Normalize();
            m_orientation = (rotation * m_orientation).Normalize();
            m_yaw += yawDelta;
            // A remainder is a per-axis numerator; a turned axis no longer owns it, and it is below one raw unit.
            m_positionAccumulator.Reset();
            m_overlayAccumulator.Reset();
        }

        WakeUp();
        m_continuity = m_continuity.Then(later: EntityContinuity.Carry(carried: WorldPoseCarry.FromFrames(
            destination: in to,
            source: in from
        )));
    }
}
