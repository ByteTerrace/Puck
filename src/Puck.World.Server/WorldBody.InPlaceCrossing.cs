using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Carries this body through a portal pair whose two faces stand in its own world: every world-space
    /// point the body holds moves through the pair's map (<see cref="WorldFrameIsometry.MapPoint"/>), every rate turns
    /// and scales with it (<see cref="WorldFrameIsometry.MapRate"/>) and every direction turns
    /// (<see cref="WorldFrameIsometry.MapVector"/>), its previous position included, so the crossing is continuous
    /// motion rather than a teleport. Its motion followers, holds, facts and action state carry on untouched, and the
    /// snapshot reports <see cref="EntityContinuityKind.Carry"/> so presentation carries what it latched the same way.
    /// Every value is fixed point. The pair must map world up onto itself
    /// (<see cref="WorldFrameIsometry.PreservesWorldUp"/>). The body's own size is not written here: a scaled pair's
    /// crossing writes it into <c>bodies.scaleRow</c> beside this call (<see cref="WorldServer.TryApplyInPlaceCrossing"/>).</summary>
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
        FixedVector3 Rate(FixedVector3 rate) => WorldFrameIsometry.MapRate(
            destination: in to,
            source: in from,
            value: rate
        );

        m_position = Point(point: m_position);
        m_previousPosition = Point(point: m_previousPosition);
        m_holdAnchor = Point(point: m_holdAnchor);
        m_obstructionWitnessPosition = Point(point: m_obstructionWitnessPosition);

        // A pair that turns nothing (each world axis maps onto itself) and scales by one leaves every direction, rate,
        // the attitude and the remainders exactly as they are; only a turning or scaling pair moves them.
        var scale = WorldFrameIsometry.Scale(
            destination: in to,
            source: in from
        );
        var turns = (
            (Direction(direction: FixedVector3.UnitX) != FixedVector3.UnitX) ||
            (Direction(direction: FixedVector3.UnitZ) != FixedVector3.UnitZ)
        );

        var scales = (scale != FixedQ4816.One);

        if (turns || scales) {
            m_planarVelocity = Rate(rate: m_planarVelocity);
            m_planarPreviousTarget = Rate(rate: m_planarPreviousTarget);
            m_overlayVelocity = Rate(rate: m_overlayVelocity);
            m_rigidVelocity = Rate(rate: m_rigidVelocity);
            // A remainder is a per-axis numerator of a rate the pair just turned or scaled, below one raw unit.
            m_positionAccumulator.Reset();
            m_overlayAccumulator.Reset();
        }
        if (scales) {
            m_verticalVelocity *= scale;
            m_verticalPreviousTarget *= scale;
            m_verticalVelocityAccumulator.Reset();
            m_planarRampAccumulator.Reset();
        }
        if (turns) {
            var yawDelta = WorldFrameIsometry.YawDelta(
                destination: in to,
                source: in from
            );
            var rotation = WorldFrameIsometry.Rotation(
                destination: in to,
                source: in from
            );

            m_angularVelocity = Direction(direction: m_angularVelocity);
            m_obstructionWitness = Direction(direction: m_obstructionWitness);
            m_holdNormal = Direction(direction: m_holdNormal);
            m_attitudeUp = Direction(direction: m_attitudeUp);
            m_up = Direction(direction: m_up);
            m_frame = (rotation * m_frame).Normalize();
            m_orientation = (rotation * m_orientation).Normalize();
            m_yaw += yawDelta;
        }

        WakeUp();
        m_continuity = m_continuity.Then(later: EntityContinuity.Carry(carried: WorldPoseCarry.FromFrames(
            destination: in to,
            source: in from
        )));
    }
}
