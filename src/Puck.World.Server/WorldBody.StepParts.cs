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
    /// operations while the host retains phase order and the contact point. The parts (<see cref="TryBeginStep"/>,
    /// <see cref="MoveStep"/>, <see cref="EndStep"/>) run back to back here, inside the input runtime's kit
    /// transaction; a kit partition admits the observation on the forking thread
    /// (<see cref="AdmitProducerObservation"/>) and runs the rest in its partition, outside the transaction
    /// (<see cref="BeginStepCore"/>), as <see cref="WorldPopulation"/>'s kit advance split does.</summary>
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
        if (!TryBeginStep(engageProbeOrdinal: engageProbeOrdinal, entityIndex: entityIndex, outputs: outputs, scratch: scratch, start: out var start, stepTicks: stepTicks, tick: tick)) {
            return false;
        }
        MoveStep(effectTargets: effectTargets, entityIndex: entityIndex, outputs: outputs, start: ref start, stepTicks: stepTicks);
        return EndStep(contactFieldVersion: contactFieldVersion, outputs: outputs, scratch: scratch, sleepAfterTicks: sleepAfterTicks, start: in start, stepTicks: stepTicks, tick: tick);
    }

    /// <summary>The step's first part for a step inside the kit transaction: admits the producer's observation
    /// (<see cref="AdmitProducerObservation"/>), then captures the body, opens the transaction and resolves the merged
    /// intent (<see cref="BeginStepCore"/>). Returns <see langword="false"/> for a body that takes no ordinary step this
    /// tick (parked, carried, or rigid, whose step is prepared here for the population's solve).</summary>
    internal bool TryBeginStep(ulong tick, ulong stepTicks, StepScratch scratch, StepOutputs outputs, int? engageProbeOrdinal, int entityIndex, out StepStart start) {
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

        if (AdmitsProducerObservation) {
            using var admission = Puck.Hosting.HostFrameWork.Measure(Puck.Hosting.HostFrameWorkPart.TickAdmission);

            AdmitProducerObservation(entityIndex: entityIndex, tick: tick);
        }
        BeginStepCore(engageProbeOrdinal: engageProbeOrdinal, entityIndex: entityIndex, outputs: outputs, scratch: scratch, start: out start, stepTicks: stepTicks, tick: tick, transactional: true);
        return true;
    }

    // Whether the step has a producer observation for the input runtime to admit.
    private bool AdmitsProducerObservation => (InputImages.HasProducer && SourceNamesProducer(source: InputImages.Source) && (m_stateLane?.Input is not null));

    /// <summary>Admits the producer's staged observation through the input runtime: producer observations enter
    /// history before motion, just like external input, and a refused sweep does not erase an accepted observation or
    /// turn the same held sample into another press on the following tick. The one part of a step that touches the
    /// input runtime when the step is local, so a kit partition's bodies admit on the forking thread, in index order.</summary>
    /// <param name="entityIndex">The body's population index.</param>
    /// <param name="tick">The explicit simulation tick.</param>
    internal void AdmitProducerObservation(int entityIndex, ulong tick) {
        if (AdmitsProducerObservation) {
            var admitted = m_stateLane!.Input!.AdmitProgramSnapshot(body: entityIndex, image: InputImages.Producer, tick: tick);

            if (admitted != InputImages.Producer) { InputImagesEdit.Producer = admitted; }
        }
    }

    /// <summary>The first part's remainder, after admission: captures the body for a refusal, opens the kit transaction
    /// when <paramref name="transactional"/>, and resolves the merged intent. A step outside the transaction
    /// (<see cref="StepIsLocal"/>) reaches nothing beyond the body, so a kit partition runs it.</summary>
    internal void BeginStepCore(ulong tick, ulong stepTicks, StepScratch scratch, StepOutputs outputs, int? engageProbeOrdinal, int entityIndex, bool transactional, out StepStart start) {
        start = default;
        m_previousPosition = m_position;
        start.PreviousOrientation = m_orientation;

        // A refused sweep is a full block: everything this step writes from here on is undone when its contact phase
        // meets a refusal, and the outputs it emitted are withdrawn (WorldBody.SweepRefusal.cs).
        CaptureMotion(restorePoint: true, scratch: scratch);
        if (transactional) { m_stateLane?.Input?.BeginKitStep(); }
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
        var intent = NextIntent(body: entityIndex, scripts: transactional, stepTicks: stepTicks, tick: tick);

        start.HadIncomingIntent = (intent != default);
        // Captured EVERY step, regardless of the latch — a mirrored application set needs this body's resolved intent
        // for its targets' translation/passthrough even while the avatar keeps integrating.
        if (InputReadbacks.EngagedIntent != intent) { InputReadbacksEdit.EngagedIntent = intent; }
        // The SAME edge test ProcessLaneActions uses (bit crossing the ordinal's threshold, previous tick's bit clear),
        // computed ahead of integration so a fired edge can preempt it entirely rather than firing a bound action (a
        // jump) the same tick it engages.
        start.EngageEdge = ((engageProbeOrdinal is { } probeOrdinal)
            && (intent[probeOrdinal] >= m_channelThresholds[probeOrdinal])
            && !Action.PreviousBit[probeOrdinal]);
        start.Intent = intent;
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
            intent: in start.Intent,
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
        try {
            return SettleStep(contactFieldVersion: contactFieldVersion, outputs: outputs, scratch: scratch, sleepAfterTicks: sleepAfterTicks, start: in start, stepTicks: stepTicks, tick: tick);
        } finally {
            // The step's restore point closes however the step ends: nothing after it may be put back.
            m_action.Binding.EndRestore();
            scratch.HeldByRestorePoint = false;
        }
    }
    private bool SettleStep(in StepStart start, ulong tick, ulong stepTicks, StepScratch scratch, StepOutputs outputs, ulong sleepAfterTicks, ulong contactFieldVersion) {
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
            // Each lane is written only where its value moves: a write reaches the section's door, which moves its
            // version and, under the step's restore point, copies the whole section aside.
            for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
                var bit = (intent[ordinal] >= m_channelThresholds[ordinal]);

                if (Action.PreviousBit[ordinal] != bit) { ActionEdit.PreviousBit[ordinal] = bit; }
            }

            for (var lane = 0; (lane < ActionLaneCount); lane++) {
                var remaining = SubtractSaturating(
                    value: Action.TimerTicks[lane],
                    amount: stepTicks
                );

                if (Action.TimerTicks[lane] != remaining) { ActionEdit.TimerTicks[lane] = remaining; }
            }

            // The held-channel image is a one-tick publish, like the submitted intent: the client republishes it every
            // submission, so a missed tick reads no channels rather than a stale hold.
            if (InputImages.Held != default) { InputImagesEdit.Held = default; }
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
    /// That holds for a locomotion kit (<see cref="KitIsLocal"/>) whose registers rest this step, not carried,
    /// tethered or parked, with no tape, timed or pending press, staged durable input, capture, pending continuum or
    /// script controller, whose contact field takes no entity-aware sweep. The kit partitions run only such bodies;
    /// everything else steps serially inside the kit transaction.</summary>
    /// <remarks>Every condition reads the body as compiled now, so a kit that gains a state op or a binding is serial
    /// from the tick it changes; the population adds the producer side (<see cref="WorldPopulation"/>'s
    /// <c>ProducerIsLocal</c>).</remarks>
    internal bool StepIsLocal {
        get {
            if (
                !KitIsLocal || (m_tether is not null) || (m_carriedByIndex >= 0) || PopulationSleeping || (m_tapeCount != 0) ||
                (m_pendingContinuum is not null) || InputImages.Engaged || (DurableInput.Tick != 0) ||
                (m_contactField is IEntityContactField) || RequiresFullRateAutonomy || !RegistersRestThisStep()
            ) {
                return false;
            }
            return ((m_stateLane?.Input is not { } input) || !input.HasBodyScripts(body: m_populationIndex));
        }
    }
    // The part of StepIsLocal the compiled kit decides, kept until a write of what it reads marks it stale: SetTuning
    // (holds), CopyChannelBindings, CompileActionState, and the facet assignments in the constructor and RecompileKit.
    private bool m_kitLocal;
    private bool m_kitLocalStale = true;

    private void MarkKitLocalStale() => m_kitLocalStale = true;
    // No rigid, carry or tether facet, no action binding, no hold that spends a register (it writes while held), and
    // no timer that resets on a fact (the reset writes whatever the timer holds).
    private bool KitIsLocal {
        get {
            if (!m_kitLocalStale) { return m_kitLocal; }
            var local = ((m_rigid is null) && (m_carry is null) && (m_tetherFacet is null));

            foreach (var binding in m_laneBindings) { local &= (binding is null); }
            foreach (var hold in m_holds) { local &= (hold.SpendState is null); }
            foreach (var definition in m_actionStateDefinitions) { local &= !((definition.Kind == ActionStateKind.Timer) && (definition.ResetFact is not null)); }
            m_kitLocal = local;
            m_kitLocalStale = false;
            return local;
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
