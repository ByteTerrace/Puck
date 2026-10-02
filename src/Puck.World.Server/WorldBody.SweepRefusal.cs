using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

// A REFUSED SWEEP IS A FULL BLOCK: THE BODY DOES NOT MOVE THIS TICK. A contact field refuses a step whose sweep it
// cannot run (ContactRefusal: arithmetic the carrier cannot hold, or a capsule core past its piece ceiling). The body's
// motion state is captured before the step integrates and restored after it, so its position, attitude, velocity,
// integrator remainders and contact facts read exactly as they did before. Everything else the step did stands: action
// triggers fired against the pre-step pose, timers and followers advanced, because time does pass and nothing they did
// depends on motion that did not happen. A refusal is local to the refused body; it never reaches a carrier or any other
// body. The population narrates each transition, refused and recovered, once on the body.sweep channel, and body.where
// carries the refusal while it holds.
public sealed partial class WorldBody {
    // The refusal the body's most recent swept step met, or None when its last swept step was resolved.
    private ContactRefusal m_sweepRefusal;
    // The refusal the population last narrated for this body, so each transition is reported once.
    private ContactRefusal m_reportedSweepRefusal;

    /// <summary>Gets why this body's most recent swept step was refused, or <see cref="ContactRefusal.None"/> when it
    /// was resolved. A refused step leaves the body exactly where it was.</summary>
    public ContactRefusal SweepRefusal => m_sweepRefusal;

    // The body's motion state, captured before a step integrates and restored when its sweep is refused. It holds every
    // field the motion program, the timed overlay or the rigid solver integrates or writes from a contact:
    //   position, orientation, yaw, drive pitch, up axis and its reseat flag;
    //   planar and vertical velocity, rigid linear and angular velocity;
    //   the position, rotation and overlay accumulators and the vertical-velocity accumulator;
    //   grounded and the contact count; the obstruction witness, its grace ticks and its position;
    //   the rigid ground and obstruction contact latches and their miss streaks.
    // A field a later change adds to those integrators belongs here, or a refused step moves it. Value types only: a
    // capture allocates nothing.
    private readonly record struct MotionSnapshot(
        FixedVector3 Position,
        FixedQuaternion Orientation,
        FixedQ4816 Yaw,
        FixedQ4816 DrivePitch,
        FixedVector3 Up,
        bool UpNeedsReseat,
        FixedVector3 PlanarVelocity,
        FixedQ4816 VerticalVelocity,
        FixedVector3 RigidVelocity,
        FixedVector3 AngularVelocity,
        FixedVector3RateAccumulator PositionAccumulator,
        FixedVector3RateAccumulator RotationAccumulator,
        FixedVector3RateAccumulator OverlayAccumulator,
        FixedRateAccumulator VerticalVelocityAccumulator,
        bool Grounded,
        int LastContactCount,
        FixedVector3 ObstructionWitness,
        ulong ObstructionWitnessGraceTicks,
        FixedVector3 ObstructionWitnessPosition,
        bool RigidGroundContacting,
        bool RigidObstructionContacting,
        int RigidGroundMissStreak,
        int RigidObstructionMissStreak
    );

    private MotionSnapshot CaptureMotion() => new(
        AngularVelocity: m_angularVelocity,
        DrivePitch: m_drivePitch,
        Grounded: m_grounded,
        LastContactCount: m_lastContactCount,
        ObstructionWitness: m_obstructionWitness,
        ObstructionWitnessGraceTicks: m_obstructionWitnessGraceTicks,
        ObstructionWitnessPosition: m_obstructionWitnessPosition,
        Orientation: m_orientation,
        OverlayAccumulator: m_overlayAccumulator,
        PlanarVelocity: m_planarVelocity,
        Position: m_position,
        PositionAccumulator: m_positionAccumulator,
        RigidGroundContacting: m_rigidGroundContacting,
        RigidGroundMissStreak: m_rigidGroundMissStreak,
        RigidObstructionContacting: m_rigidObstructionContacting,
        RigidObstructionMissStreak: m_rigidObstructionMissStreak,
        RigidVelocity: m_rigidVelocity,
        RotationAccumulator: m_rotationAccumulator,
        Up: m_up,
        UpNeedsReseat: m_upNeedsReseat,
        VerticalVelocity: m_verticalVelocity,
        VerticalVelocityAccumulator: m_verticalVelocityAccumulator,
        Yaw: m_yaw
    );
    private void RestoreMotion(in MotionSnapshot motion) {
        m_angularVelocity = motion.AngularVelocity;
        m_drivePitch = motion.DrivePitch;
        m_grounded = motion.Grounded;
        m_lastContactCount = motion.LastContactCount;
        m_obstructionWitness = motion.ObstructionWitness;
        m_obstructionWitnessGraceTicks = motion.ObstructionWitnessGraceTicks;
        m_obstructionWitnessPosition = motion.ObstructionWitnessPosition;
        m_orientation = motion.Orientation;
        m_overlayAccumulator = motion.OverlayAccumulator;
        m_planarVelocity = motion.PlanarVelocity;
        m_position = motion.Position;
        m_positionAccumulator = motion.PositionAccumulator;
        m_rigidGroundContacting = motion.RigidGroundContacting;
        m_rigidGroundMissStreak = motion.RigidGroundMissStreak;
        m_rigidObstructionContacting = motion.RigidObstructionContacting;
        m_rigidObstructionMissStreak = motion.RigidObstructionMissStreak;
        m_rigidVelocity = motion.RigidVelocity;
        m_rotationAccumulator = motion.RotationAccumulator;
        m_up = motion.Up;
        m_upNeedsReseat = motion.UpNeedsReseat;
        m_verticalVelocity = motion.VerticalVelocity;
        m_verticalVelocityAccumulator = motion.VerticalVelocityAccumulator;
        m_yaw = motion.Yaw;
    }

    /// <summary>Takes the transition this body's sweep refusal made since the population last reported it, marking it
    /// reported: the refusal it now holds, or <see cref="ContactRefusal.None"/> for a recovery.</summary>
    /// <param name="refusal">The refusal now held, <see cref="ContactRefusal.None"/> once recovered.</param>
    /// <returns><see langword="true"/> when the refusal changed since the last report.</returns>
    internal bool TryTakeSweepTransition(out ContactRefusal refusal) {
        refusal = m_sweepRefusal;

        if (m_sweepRefusal == m_reportedSweepRefusal) {
            return false;
        }

        m_reportedSweepRefusal = m_sweepRefusal;

        return true;
    }

    // The body.where suffix: the refusal while one holds, nothing otherwise, so an ordinary read-back is unchanged.
    private string DescribeSweepRefusal() => m_sweepRefusal switch {
        ContactRefusal.None => string.Empty,
        ContactRefusal.UnrepresentableSweep => " sweep=refused(unrepresentable)",
        ContactRefusal.OversizedCapsuleCore => " sweep=refused(oversized-capsule-core)",
        _ => $" sweep=refused({m_sweepRefusal})",
    };
}
