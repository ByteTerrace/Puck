using Puck.Maths;
using Puck.Physics;
using Puck.Physics.Motion;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>The lists a body step emits into for application after every body has advanced: non-self effects,
    /// target-register submissions and staged <c>generate</c> firings. The population owns one for its serial steps and
    /// each kit partition owns one, merged into the population's in partition order at the join.</summary>
    internal sealed class StepOutputs {
        /// <summary>Gets the non-self effects, applied by <see cref="WorldPopulation.CompleteStep"/>.</summary>
        public List<BodyEffectOutput> Effects { get; } = [];
        /// <summary>Gets the target-register submissions.</summary>
        public List<WorldDesignation> Designations { get; } = [];
        /// <summary>Gets the staged <c>generate</c> firings.</summary>
        public List<WorldGeneratorInvocation> Generators { get; } = [];

        /// <summary>Gets whether every list is empty.</summary>
        public bool IsEmpty => ((Effects.Count == 0) && (Designations.Count == 0) && (Generators.Count == 0));

        /// <summary>Appends another set's outputs after this set's own, in their order, and empties the other.</summary>
        /// <param name="other">The outputs to move.</param>
        public void Absorb(StepOutputs other) {
            Effects.AddRange(collection: other.Effects);
            Designations.AddRange(collection: other.Designations);
            Generators.AddRange(collection: other.Generators);
            other.Effects.Clear();
            other.Designations.Clear();
            other.Generators.Clear();
        }
    }

    /// <summary>What a step's first part decided and the later parts read: the merged intent, the engage edge, the
    /// marks a refused step withdraws its outputs to, and whether the step runs inside the input runtime's kit
    /// transaction.</summary>
    internal struct StepStart {
        public PlayerIntent Intent;
        public FixedQuaternion PreviousOrientation;
        public ulong InputEpoch;
        public int EffectOutputCount;
        public int DesignationOutputCount;
        public int GeneratorInvocationCount;
        public bool EngageEdge;
        public bool HadIncomingIntent;
        public bool Transactional;
        public bool Refused;
    }

    // Where this body reports to its population while a kit partition steps it: bound by the partition around the
    // body's step, so a wake or fact change lands in the partition's log and the population applies it at the join.
    private WorldBodyStepLog? m_stepLog;

    internal void BindStepLog(WorldBodyStepLog? log) => m_stepLog = log;

    /// <summary>Advances the body by one exact host-owned simulation step from its single merged intent: a live tape
    /// segment if one is queued, otherwise the tick's submitted intent. The selected program chooses fixed domain
    /// operations while the host retains phase order and the contact point. The three parts (<see cref="TryBeginStep"/>,
    /// <see cref="MoveStep"/>, <see cref="EndStep"/>) run back to back here, inside the input runtime's kit
    /// transaction; a kit partition runs the same parts with the first on the forking thread and the other two in its
    /// partition (<see cref="WorldPopulation"/>'s kit advance split).</summary>
    /// <param name="tick">The explicit simulation tick whose staged durable inputs may enter.</param>
    /// <param name="stepTicks">The exact engine ticks this call advances.</param>
    /// <param name="scratch">The step scratch the caller owns, which this step captures the body into before it begins
    /// and restores from if its sweep is refused (<see cref="StepScratch"/>).</param>
    /// <param name="outputs">Receives the step's effects, designations and generator firings.</param>
    /// <param name="engageProbeOrdinal">The context-sensitive-button interception (the RPG A-button): a channel
    /// ordinal to test for a rising edge against this same tick's composed intent, before any integration runs, or
    /// <see langword="null"/> for no probe. A fired edge here only reports it; the caller performs the engage.</param>
    /// <param name="entityIndex">The source body's population index.</param>
    /// <param name="effectTargets">The pre-step entity target image.</param>
    /// <param name="sleepAfterTicks">The authored <c>bodies.sleepAfterSeconds</c> idle floor, in engine ticks, this body
    /// sleeps under once cleared with no motion and no incoming intent; 0 (the default) never sleeps.</param>
    /// <param name="contactFieldVersion">The population's current <see cref="WorldPopulation.ContactFieldVersion"/>,
    /// folded into this body's own idle bookkeeping so a contact-surface change under it is never mistaken for rest.</param>
    /// <returns><see langword="true"/> when <paramref name="engageProbeOrdinal"/>'s rising edge fired this tick.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    internal bool Advance(ulong tick, ulong stepTicks, StepScratch scratch, StepOutputs outputs, int? engageProbeOrdinal = null, int entityIndex = -1, BodyEffectTargets effectTargets = default, ulong sleepAfterTicks = 0UL, ulong contactFieldVersion = 0UL) {
        if (!TryBeginStep(engageProbeOrdinal: engageProbeOrdinal, entityIndex: entityIndex, outputs: outputs, scratch: scratch, start: out var start, stepTicks: stepTicks, tick: tick, transactional: true)) {
            return false;
        }
        MoveStep(effectTargets: effectTargets, entityIndex: entityIndex, outputs: outputs, start: ref start, stepTicks: stepTicks);
        return EndStep(contactFieldVersion: contactFieldVersion, outputs: outputs, scratch: scratch, sleepAfterTicks: sleepAfterTicks, start: in start, stepTicks: stepTicks, tick: tick);
    }

    /// <summary>The step's first part: admits the producer's observation, captures the body for a refusal, opens the
    /// kit transaction when <paramref name="transactional"/>, and resolves the merged intent. Returns
    /// <see langword="false"/> for a body that takes no ordinary step this tick (parked, carried, or rigid, whose step
    /// is prepared here for the population's solve). Only a body whose step cannot write state
    /// (<see cref="StepIsLocal"/>) runs without the transaction.</summary>
    internal bool TryBeginStep(ulong tick, ulong stepTicks, StepScratch scratch, StepOutputs outputs, int? engageProbeOrdinal, int entityIndex, bool transactional, out StepStart start) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);
        start = default;
        if (PopulationSleeping) { return false; }

        // Captured before ExecuteProgram can move m_position — the swept portal-crossing scan's segment start for this
        // step. A hard teleport between scans overwrites this separately (CommitTeleport).
        m_previousPosition = m_position;
        start.PreviousOrientation = m_orientation;

        // A carried body's pose and rigid velocity are DERIVED from its carrier every tick (WorldBody.FollowCarrier,
        // called from WorldPopulation.UpdateCarriedBodies after both advance passes complete), so its own step is a
        // no-op rather than running gravity/contact against a pose FollowCarrier is about to overwrite.
        if (m_carriedByIndex >= 0) {
            return false;
        }

        // A rigid kit hands the whole step to the population's rigid solve instead of the grounded/free motion program:
        // no intent, action track, or hold list applies to a passive rigid entity. The step is prepared here and solved
        // with every other rigid body once all of them have advanced (WorldPopulation.Rigid.cs).
        if (m_rigid is not null) {
            PrepareRigidStep(
                entityIndex: entityIndex,
                stepTicks: stepTicks
            );

            return false;
        }

        // Producer observations enter history before motion, just like external input. A refused sweep does not
        // erase an accepted observation or turn the same held sample into another press on the following tick.
        var inputRuntime = m_stateLane?.Input;

        if (InputImages.HasProducer && SourceNamesProducer(source: InputImages.Source) && (inputRuntime is not null)) {
            using var admission = Puck.Hosting.HostFrameWork.Measure(Puck.Hosting.HostFrameWorkPart.TickAdmission);

            InputImagesEdit.Producer = inputRuntime.AdmitProgramSnapshot(body: entityIndex, image: InputImages.Producer, tick: tick);
        }

        // A refused sweep is a full block: everything this step writes from here on is undone when its contact phase
        // meets a refusal, and the outputs it emitted are withdrawn (WorldBody.SweepRefusal.cs).
        CaptureMotion(scratch: scratch);
        if (transactional) { inputRuntime?.BeginKitStep(); }
        start.Transactional = transactional;
        start.InputEpoch = InputEpoch;
        start.EffectOutputCount = outputs.Effects.Count;
        start.DesignationOutputCount = outputs.Designations.Count;
        start.GeneratorInvocationCount = outputs.Generators.Count;

        ApplyDurableInput(tick: tick);
        MaterializeDefaultLanePresses(stepTicks: stepTicks);

        // The full merged intent for this sub-step: NextIntent expresses the whole precedence (movement channels —
        // tape > submitted, gated by the possession latch — with the action-track lanes overlaid). StageProducer
        // stages an intent for every body every tick, including a kit with no producer at all (it stages the inert
        // default) — so "had incoming intent" for the sleep floor reads the resolved vector after this call, not the
        // raw pre-resolve flags, which would read true for a body nothing is actually driving.
        var intent = NextIntent(body: entityIndex, stepTicks: stepTicks, tick: tick);

        start.HadIncomingIntent = (intent != default);
        // Captured EVERY step, regardless of the latch — a mirrored application set needs this body's resolved intent
        // for its targets' translation/passthrough even while the avatar keeps integrating.
        InputReadbacksEdit.EngagedIntent = intent;
        // The SAME edge test ProcessLaneActions uses (bit crossing the ordinal's threshold, previous tick's bit clear),
        // computed ahead of integration so a fired edge can preempt it entirely rather than firing a bound action (a
        // jump) the same tick it engages.
        start.EngageEdge = ((engageProbeOrdinal is { } probeOrdinal)
            && (intent[probeOrdinal] >= m_channelThresholds[probeOrdinal])
            && !Action.PreviousBit[probeOrdinal]);
        start.Intent = intent;
        return true;
    }

    /// <summary>The step's second part: runs the motion program and its contact phase on the intent the first part
    /// resolved, unless the intent is captured or diverted into an engage.</summary>
    internal void MoveStep(ref StepStart start, ulong stepTicks, int entityIndex, BodyEffectTargets effectTargets, StepOutputs outputs) {
        // Captured (InputImages.Engaged, i.e. capture:true), OR this tick's press is being diverted into an engage
        // instead of reaching the body: either way the resolved intent never reaches the avatar (no pose integration,
        // so the snapshot holds it stable). The action track still advances in EndStep, so a timed press drains
        // identically whether the intent drives the avatar or the route target.
        if (InputImages.Engaged || start.EngageEdge) {
            return;
        }

        // The tether facet reads its own attach/detach/reel channels directly (never through the kit's action table —
        // see WorldBody.Tether.cs), so it runs ahead of the program dispatch, on the SAME "intent reaches the avatar"
        // gate. A detach that fires this tick falls through to the ordinary program in the SAME tick, already carrying
        // the momentum DetachTether wrote. WorldPopulation.ResolveTethers clamps the result to the rope AFTER every
        // body this tick has advanced.
        ProcessTetherIntent(intent: in start.Intent);
        ProcessTetherReel(
            intent: in start.Intent,
            stepTicks: stepTicks
        );

        // moveSpeed goes through ResolveMoveSpeed: the rate reads live off the seated profile every frame, clamped by
        // the kit's own authored Speed.Envelope when declared, so the sim never observes an unclamped speed.
        var moveSpeed = ResolveMoveSpeed();
        var turnSpeed = ResolveTurnRate();

        m_sweepRefusal = ContactRefusal.None;
        ExecuteProgram(
            designationOutputs: outputs.Designations,
            effectOutputs: outputs.Effects,
            effectTargets: effectTargets,
            entityIndex: entityIndex,
            generatorInvocations: outputs.Generators,
            intent: start.Intent,
            moveSpeed: moveSpeed,
            stepTicks: stepTicks,
            turnSpeed: turnSpeed
        );
        start.Refused = (m_sweepRefusal != ContactRefusal.None);
    }

    /// <summary>The step's last part: restores a refused step whole and withdraws its outputs, or settles an accepted
    /// one's action track, idle floor and kit transaction; then reports a body that moved as awake.</summary>
    /// <returns>Whether the step's engage edge fired.</returns>
    internal bool EndStep(in StepStart start, ulong tick, ulong stepTicks, StepScratch scratch, StepOutputs outputs, ulong sleepAfterTicks, ulong contactFieldVersion) {
        var inputRuntime = (start.Transactional ? m_stateLane?.Input : null);
        var intent = start.Intent;

        if (start.Refused) {
            RestoreMotion(registers: start.Transactional, scratch: scratch);
            inputRuntime?.CompleteKitStep(accepted: false);
            // Selection reconciliation can retune the body; restore the exact captured motion over that old kit.
            RestoreMotion(registers: start.Transactional, scratch: scratch);
            // A refused motion step still consumes its one-tick publications. They are sampled again by their producers
            // next tick; restoring the motion must not turn a missed impulse into a retained request.
            InputImagesEdit.Submitted = default;
            InputImagesEdit.HasSubmitted = false;
            InputImagesEdit.Held = default;
            InputImagesEdit.Producer = default;
            InputImagesEdit.HasProducer = false;
            outputs.Effects.RemoveRange(index: start.EffectOutputCount, count: (outputs.Effects.Count - start.EffectOutputCount));
            outputs.Designations.RemoveRange(index: start.DesignationOutputCount, count: (outputs.Designations.Count - start.DesignationOutputCount));
            outputs.Generators.RemoveRange(index: start.GeneratorInvocationCount, count: (outputs.Generators.Count - start.GeneratorInvocationCount));
        } else {
            // A firing may have installed a different bank. Never write old-slot bits over its continuing baseline.
            if (InputEpoch != start.InputEpoch) { intent = InputReadbacks.EngagedIntent; }
            // The previous-bit image is written once after action evaluation; timed presses drain even under capture.
            for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
                ActionEdit.PreviousBit[ordinal] = (intent[ordinal] >= m_channelThresholds[ordinal]);
            }

            for (var lane = 0; (lane < ActionLaneCount); lane++) {
                ActionEdit.TimerTicks[lane] = SubtractSaturating(
                    value: Action.TimerTicks[lane],
                    amount: stepTicks
                );
            }

            // The held-channel image is a one-tick publish, like the submitted intent: the client republishes it every
            // submission, so a missed tick reads no channels rather than a stale hold.
            InputImagesEdit.Held = default;
            m_affectingSubject = -1;

            UpdateSleepEligibility(
                contactFieldVersion: contactFieldVersion,
                hadIncomingIntent: start.HadIncomingIntent,
                moved: ((m_position != m_previousPosition) || (m_orientation != start.PreviousOrientation)),
                sleepAfterTicks: sleepAfterTicks,
                stepTicks: stepTicks,
                tick: tick
            );
            inputRuntime?.CompleteKitStep(accepted: true);
        }

        if ((m_position != m_previousPosition) || (m_orientation != start.PreviousOrientation)) { NotifyPopulationWake(); }
        return start.EngageEdge;
    }

    /// <summary>Gets whether this body's step is local: it writes only the body's own motion, sections and
    /// producer-side state, emits nothing for other bodies or the document, and needs no input-runtime transaction.
    /// That holds for a locomotion kit with no action bindings, no action-state registers, no holds, carry or tether
    /// facet, not carried, not tethered and not parked, with no tape, timed or pending press, staged durable input,
    /// capture, pending continuum or script controller, whose contact field takes no entity-aware sweep. The kit
    /// partitions run only such bodies; everything else steps serially inside the kit transaction.</summary>
    /// <remarks>Every condition reads the body as compiled now, so a kit that gains a state op or a binding is serial
    /// from the tick it changes; the population adds the producer side (<see cref="WorldPopulation"/>'s
    /// <c>ProducerIsLocal</c>).</remarks>
    internal bool StepIsLocal {
        get {
            if (
                (m_rigid is not null) || (m_carry is not null) || (m_tetherFacet is not null) || (m_tether is not null) ||
                (m_carriedByIndex >= 0) || PopulationSleeping || (m_tapeCount != 0) || (m_pendingContinuum is not null) ||
                InputImages.Engaged || (DurableInput.Tick != 0) || (m_contactField is IEntityContactField) ||
                RequiresFullRateAutonomy
            ) {
                return false;
            }
            foreach (var binding in m_laneBindings) {
                if (binding is not null) { return false; }
            }
            // A hold that spends a register writes it while held.
            foreach (var hold in m_holds) {
                if (hold.SpendState is not null) { return false; }
            }
            if (!RegistersRestThisStep()) {
                return false;
            }
            return ((m_stateLane?.Input is not { } input) || !input.HasBodyScripts(body: m_populationIndex));
        }
    }
    // Whether the step's action-state pass (AdvanceActionState) writes no register: no timer is running, none resets on
    // a fact, and every counter that resets on a fact already holds its initial value. Nothing earlier in a step outside
    // the kit transaction writes a register, so the values read here are the ones that pass reads.
    private bool RegistersRestThisStep() {
        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];

            if (definition.Kind == ActionStateKind.Timer) {
                if ((definition.ResetFact is not null) || (StateTimer(slot: slot) != 0UL)) { return false; }
            } else if ((definition.ResetFact is not null) && (StateCounter(slot: slot).Value != InitialRaw(definition: in definition))) {
                return false;
            }
        }
        return true;
    }
}

/// <summary>What a body stepped in a kit partition reports to its population, in the order it reported it: a wake
/// (<see cref="WorldPopulation"/>'s <c>BodyWoke</c>) or a changed body fact. The population applies a partition's log
/// at the join, partitions in ascending order, so the shared sets it writes see the same sequence at every worker
/// count.</summary>
internal sealed class WorldBodyStepLog {
    private readonly List<(int Index, bool Woke)> m_entries = [];

    /// <summary>Records a wake.</summary>
    public void Woke(int index) => m_entries.Add(item: (index, true));
    /// <summary>Records a changed body fact.</summary>
    public void FactChanged(int index) => m_entries.Add(item: (index, false));
    /// <summary>Gets the records in order.</summary>
    public ReadOnlySpan<(int Index, bool Woke)> Entries => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_entries);
    /// <summary>Forgets every record.</summary>
    public void Clear() => m_entries.Clear();
}
