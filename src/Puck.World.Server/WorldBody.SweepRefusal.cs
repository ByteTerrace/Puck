using Puck.Maths;
using Puck.Physics;
using Puck.World.Protocol;

namespace Puck.World.Server;

// A REFUSED SWEEP IS A FULL BLOCK: THE BODY DOES NOT MOVE THIS TICK. A contact field refuses a step whose sweep it
// cannot run (ContactRefusal: arithmetic the carrier cannot hold, or a capsule core past its piece ceiling). Every field
// the step writes is captured before the step begins and restored when its sweep is refused, so the body reads exactly
// as it did before the tick: its pose, attitude, velocity, integrator remainders, rate accumulators, frame, input tape,
// timers, action state and contact facts. The outputs a refused locomotion step emitted (effects, designations,
// generator firings) are withdrawn with it, so a tick the body did not take fires nothing either. A refused body is
// immovable for the rest of its tick (WorldPopulation's pair passes resolve against it as static), and a carrier is never
// corrected through it. The population narrates each transition, refused and recovered, once on the body.sweep channel,
// and body.where carries the refusal while it holds.
public sealed partial class WorldBody {
    // The refusal the body's most recent swept step met, or None when its last swept step was resolved.
    private ContactRefusal m_sweepRefusal;
    // The refusal the population last narrated for this body, so each transition is reported once.
    private ContactRefusal m_reportedSweepRefusal;
    // Whether any swept step this body took this tick was refused; the population clears it once the tick completes.
    private bool m_sweepRefusedThisTick;

    /// <summary>Gets why this body's most recent swept step was refused, or <see cref="ContactRefusal.None"/> when it
    /// was resolved. A refused step leaves the body exactly where it was.</summary>
    public ContactRefusal SweepRefusal => m_sweepRefusal;
    /// <summary>Gets whether a swept step this body took this tick was refused. Such a body is immovable for the rest
    /// of the tick: a body pair resolves against it as static, and nothing corrects a carrier through it.</summary>
    public bool SweepRefusedThisTick => m_sweepRefusedThisTick;

    // Records a refused swept step: the latch body.where and the narration read, and the tick's immovability.
    private void NoteSweepRefusal(ContactRefusal refusal) {
        m_sweepRefusal = refusal;
        m_sweepRefusedThisTick = true;
    }

    /// <summary>Ends this body's tick for the sweep refusal: a body refused this tick may be moved again by the next
    /// tick's pair passes.</summary>
    internal void EndSweepTick() => m_sweepRefusedThisTick = false;

    // Everything a step writes, captured before it begins and restored when its sweep is refused. The snapshot is the
    // body's two whole state records, not a list of fields: the transfer state (velocities, attitude, rate
    // accumulators, followers, tape, timers, action state) and the integration residue (previous position, the
    // position, rotation and up-turn remainders, up, frame, grounded, sleep, hold, tether and rigid state). A field
    // added to either record is covered here without a change. The rest are the facts both records leave to be
    // re-derived from the pose after a discontinuous restore, which a refused step is not: the pose itself, the contact
    // count, the obstruction witness, the medium facts, and the transfer-held channel image, which ApplyTransferState
    // seeds for an arrival rather than restoring as it stood.
    private readonly record struct MotionSnapshot(
        FixedVector3 Position,
        FixedQ4816 Yaw,
        WorldBodyTransferState Transfer,
        WorldBodyIntegrationResidue Residue,
        int LastContactCount,
        FixedVector3 ObstructionWitness,
        ulong ObstructionWitnessGraceTicks,
        FixedVector3 ObstructionWitnessPosition,
        bool InMedium,
        bool AtMediumBand,
        PlayerIntent TransferHeldChannels,
        bool HasTransferHeldChannels
    );

    // The body's snapshot, overwritten by each capture into the arrays the last one left, so a capture taken every
    // step allocates nothing in steady state. One step holds it at a time, from its capture to its end.
    private MotionSnapshot m_motion;

    private void CaptureMotion() => m_motion = new(
        AtMediumBand: m_atMediumBand,
        HasTransferHeldChannels: m_hasTransferHeldChannels,
        InMedium: m_inMedium,
        LastContactCount: m_lastContactCount,
        ObstructionWitness: m_obstructionWitness,
        ObstructionWitnessGraceTicks: m_obstructionWitnessGraceTicks,
        ObstructionWitnessPosition: m_obstructionWitnessPosition,
        Position: m_position,
        Residue: CaptureIntegrationResidue(),
        Transfer: CaptureTransferStateInto(reuse: m_motion.Transfer),
        TransferHeldChannels: m_transferHeldChannels,
        Yaw: m_yaw
    );
    // The order the checkpoint restore uses: the transfer state, then the residue over it (which undoes the transfer
    // state's own wake and latch writes), then the pose and the re-derived facts, which a program switch inside
    // ApplyTransferState may have re-pinned.
    private void RestoreMotion() {
        ref readonly var motion = ref m_motion;

        ApplyTransferState(state: motion.Transfer);
        ApplyIntegrationResidue(residue: motion.Residue);
        m_atMediumBand = motion.AtMediumBand;
        m_hasTransferHeldChannels = motion.HasTransferHeldChannels;
        m_inMedium = motion.InMedium;
        m_lastContactCount = motion.LastContactCount;
        m_obstructionWitness = motion.ObstructionWitness;
        m_obstructionWitnessGraceTicks = motion.ObstructionWitnessGraceTicks;
        m_obstructionWitnessPosition = motion.ObstructionWitnessPosition;
        m_position = motion.Position;
        m_transferHeldChannels = motion.TransferHeldChannels;
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
