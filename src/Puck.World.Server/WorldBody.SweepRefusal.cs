using Puck.Abstractions.Memory;
using Puck.Maths;
using Puck.Physics;

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

    /// <summary>Gets the sections this body's steps have copied aside over its life, and their bytes: what its restore
    /// points saved. A reader takes a step's share by reading before and after it.</summary>
    internal (long Saves, long Bytes) RestoreSaves => (m_action.Binding.RestoreSaves, m_action.Binding.RestoreSavedBytes);

    /// <summary>Ends this body's tick for the sweep refusal: a body refused this tick may be moved again by the next
    /// tick's pair passes.</summary>
    internal void EndSweepTick() {
        if (m_sweepRefusedThisTick) { m_sweepRefusedThisTick = false; }
    }

    // Everything a step writes, captured before it begins and restored when its sweep is refused: the body's own
    // sections into reusable scratch arrays, its register file, and the process-local observation and read-back
    // witnesses that sit beside the sections.
    /// <summary>The scratch a step restores from when its sweep is refused: one snapshot, captured when a step begins and
    /// read only until that step ends. It belongs to whoever runs the steps, not to a body: a population steps its
    /// bodies one at a time and owns one, which each step it runs takes. A capture writes into the arrays the last one
    /// left, so steady state allocates nothing.</summary>
    internal sealed class StepScratch {
        internal WorldBodySections Sections;
        internal ulong[] MotionRecency = [];
        internal ulong[] LaneRecency = [];
        internal WorldRegisterWriteImage[] RegisterWrites = [];
        internal long[] Registers = [];
        internal int RegisterCount;
        internal WorldTapeSegmentImage[] Tape = [];
        internal ulong LastContactFieldVersion;
        internal int LastContactCount;
        internal FixedVector3 ObstructionWitness;
        internal ulong ObstructionWitnessGraceTicks;
        internal FixedVector3 ObstructionWitnessPosition;
        // Whether the capture left the sections behind holders to the body's restore point (SlotBinding.BeginRestore)
        // instead of copying them here: the scratch then holds no image of them, and a restore puts them back from
        // what each holder saved when the step first wrote it.
        internal bool HeldByRestorePoint;
    }

    // Captures this body into the scratch, overwriting whatever the last step left there. Every local is written before
    // it is read, so the frame skips the zero fill.
    //
    // A step's own capture (restorePoint) copies only what is not behind a holder: the pose, motion, clocks, program,
    // tape and registers. The sections behind holders (the input images and read-backs, the action track, the lane
    // clocks, the register writes, the durable input and the script actors) are left to a restore point on the body's
    // binding, so each is copied when the step first writes it and a section the step leaves alone is never copied.
    // Any other capture (a carry attempt, another body's kit transaction) copies everything: it may run inside a step
    // whose restore point is open, and its own restore writes through the same doors.
    [System.Runtime.CompilerServices.SkipLocalsInit]
    internal void CaptureMotion(StepScratch scratch, bool restorePoint = false) {
        ref var sections = ref scratch.Sections;

        CaptureCoreSections(clocks: out sections.Clocks, motion: out sections.Motion, pose: out sections.Pose, program: out sections.Program);
        var motionRecencyLength = m_motionRecency.Length;
        var motionRecency = ArrayCapacity.EnsureScratch(ref scratch.MotionRecency, motionRecencyLength);
        m_motionRecency.AsSpan().CopyTo(destination: motionRecency);
        sections.MotionRecency = new(Clocks: motionRecency.AsMemory(length: motionRecencyLength, start: 0));

        scratch.HeldByRestorePoint = restorePoint;
        if (restorePoint) {
            m_action.Binding.BeginRestore();
        } else {
            sections.InputImages = InputImages;
            sections.InputReadbacks = InputReadbacks;
            sections.Action = Action;

            var laneRecencyLength = m_laneRecency.Count;
            var laneRecency = ArrayCapacity.EnsureScratch(ref scratch.LaneRecency, laneRecencyLength);
            m_laneRecency.Read.CopyTo(destination: laneRecency);
            sections.LaneRecency = new(Clocks: laneRecency.AsMemory(length: laneRecencyLength, start: 0));

            var registerWritesLength = m_registerWrites.Count;
            var registerWrites = ArrayCapacity.EnsureScratch(ref scratch.RegisterWrites, registerWritesLength);
            m_registerWrites.Read.CopyTo(destination: registerWrites);
            sections.RegisterWrites = new(Rows: registerWrites.AsMemory(length: registerWritesLength, start: 0));

            sections.DurableInput = DurableInput;
            sections.ScriptActors = ScriptActors;
        }

        var tape = ArrayCapacity.EnsureScratch(ref scratch.Tape, m_tapeCount);
        for (var offset = 0; (offset < m_tapeCount); offset++) {
            ref readonly var segment = ref m_tape[((m_tapeHead + offset) % m_tape.Length)];

            tape[offset] = new(Actor: segment.Actor, Intent: segment.Intent, RemainingTicks: segment.RemainingTicks);
        }
        sections.Tape = new(Segments: tape.AsMemory(length: m_tapeCount, start: 0));
        sections.Contacts = default;

        var registerCount = m_actionStateDefinitions.Length;
        var registers = ArrayCapacity.EnsureScratch(ref scratch.Registers, registerCount);
        for (var slot = 0; (slot < registerCount); slot++) {
            registers[slot] = ((m_stateLane is { } lane) ? lane.Read(ordinal: m_stateOrdinal, slot: slot) : 0L);
        }
        scratch.RegisterCount = registerCount;
        scratch.LastContactFieldVersion = m_lastContactFieldVersion;
        scratch.LastContactCount = m_lastContactCount;
        scratch.ObstructionWitness = m_obstructionWitness;
        scratch.ObstructionWitnessGraceTicks = m_obstructionWitnessGraceTicks;
        scratch.ObstructionWitnessPosition = m_obstructionWitnessPosition;
    }

    // A step outside the kit transaction (StepIsLocal) wrote no register, so its restore leaves the arena's lanes alone:
    // they still hold what the capture read.
    internal void RestoreMotion(StepScratch scratch, bool registers = true) {
        if (m_scale != scratch.Sections.Pose.Scale) {
            SetScale(value: scratch.Sections.Pose.Scale);
        }
        RestoreSections(
            contactFieldObservationCurrent: true,
            contactFieldVersion: scratch.LastContactFieldVersion,
            heldByRestorePoint: scratch.HeldByRestorePoint,
            registers: (registers ? scratch.Registers.AsSpan(length: scratch.RegisterCount, start: 0) : []),
            sections: in scratch.Sections
        );
        m_lastContactCount = scratch.LastContactCount;
        m_obstructionWitness = scratch.ObstructionWitness;
        m_obstructionWitnessGraceTicks = scratch.ObstructionWitnessGraceTicks;
        m_obstructionWitnessPosition = scratch.ObstructionWitnessPosition;
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
