using Puck.Abstractions.Counting;

namespace Puck.World.Server;

/// <summary>
/// The work <see cref="WorldPopulation"/> spends stepping bodies, counted in a
/// <see cref="ShardedWorkCounterSet"/> (each thread a kit partition steps on counts into a shard of its own) and read
/// through <see cref="IWorkCounterSource"/> under the source name
/// <see cref="SourceName"/>: the bodies a tick advances, the sleeping bodies (parked rigid islands and idle kit
/// bodies) a contact-field change woke or left asleep because the change could not reach them, the gyroscopic
/// steps rigid bodies took, refused, or took without their rescale, and what kit bodies' contact phases decided
/// (<see cref="BodyKinematicsWitness"/>): step-ups taken and refused and the tallest, ground snaps, steps that left and
/// met the ground and the longest airborne run, driven steps that did not move and the longest run of them, steps
/// pressed against a face too steep to stand on, and solves that ended still pushing and their deepest push.
/// </summary>
/// <remarks>Each total is monotonic over the ledger's life, never reset; a reader takes a window by reading twice and
/// subtracting. Each peak is the largest value one body step published since the peaks were last cleared. None is part
/// of simulation state: nothing hashes, exports or checkpoints them, and a step a refused sweep undoes has already
/// counted. Each kind declares its class; neighbour-contact counts depend on untaped delivery timing.</remarks>
public sealed class WorldBodyWork : WorkLedger<ShardedWorkCounterSet> {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "physics.bodies";

    /// <summary>Gets the kind counting simulated body advances: one per non-seat body a tick's integration pass steps.</summary>
    public static WorkKind Advances { get; } = new(name: "physics.bodies.advances", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the simulated advances a kit partition ran: the bodies whose step is local, which
    /// <see cref="Advances"/> also counts. The difference is the advances the serial partition ran.</summary>
    public static WorkKind LocalAdvances { get; } = new(name: "physics.bodies.local-advances", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the producer runs a kit partition ran.</summary>
    public static WorkKind LocalSteers { get; } = new(name: "physics.bodies.local-steers", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting kit partitions run: each partition holding a local body counts once a tick.</summary>
    public static WorkKind KitPartitions { get; } = new(name: "physics.bodies.kit-partitions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the kit phases whose partitions the job pool ran as more than one job, so some ran
    /// off the stepping thread. A phase with too few bodies, or a host with one worker, runs its partitions on the
    /// stepping thread and counts none, so the count follows the host's worker count.</summary>
    public static WorkKind KitForks { get; } = new(name: "physics.bodies.kit-forks", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the bodies a tick's curve order sorted again: those that joined the step or crossed
    /// into another cell. The rest of the step bodies kept their place.</summary>
    public static WorkKind CurveResorts { get; } = new(name: "physics.bodies.curve-resorts", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the sections a step copied aside so a refused sweep could put them back: one for
    /// each section behind a holder that the step wrote (<see cref="Puck.State.SlotBinding.BeginRestore"/>). A step
    /// that writes no such section counts none.</summary>
    public static WorkKind StepSaves { get; } = new(name: "physics.bodies.step-saves", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the bytes those copies took.</summary>
    public static WorkKind StepSavedBytes { get; } = new(name: "physics.bodies.step-saved-bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the number of neighbour responses (body pushes and geometry resolution) the local tick budget or
    /// an ownership face shortened, rejected or slowed. Delivery timing is not yet taped, so this count is
    /// pacing-dependent.</summary>
    public static WorkKind AdjacencyCorrectionClamps { get; } = new(name: "physics.bodies.adjacency-correction-clamps", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting sleeping bodies (parked rigid bodies and idle kit bodies) a contact-field change
    /// woke.</summary>
    public static WorkKind FieldWakes { get; } = new(name: "physics.bodies.field-wakes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting sleeping bodies (parked rigid bodies and idle kit bodies) that observed a bounded
    /// contact-field change without waking (a
    /// moving attached solid, a response swap, a field lattice's changed cells), because their bounds miss everything it
    /// changed.</summary>
    public static WorkKind FieldObserved { get; } = new(name: "physics.bodies.field-observed", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting implicit gyroscopic steps applied: one per awake non-spherical spinning rigid body
    /// a tick advances (<see cref="Puck.Physics.FixedGyroscopicStep"/>).</summary>
    public static WorkKind GyroscopicSteps { get; } = new(name: "physics.bodies.gyroscopic-steps", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting gyroscopic steps refused, leaving angular velocity unchanged: a degenerate inertia,
    /// a spin past the step's representable range, or a singular Jacobian.</summary>
    public static WorkKind GyroscopicRefusals { get; } = new(name: "physics.bodies.gyroscopic-refusals", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting applied gyroscopic steps whose restoring rescale was skipped, leaving the Newton
    /// step's dissipation in place: a spin the step brought to zero, or one past the rescale's representable range
    /// (<see cref="Puck.Physics.GyroscopicStepOutcome.AppliedUnrescaled"/>).</summary>
    public static WorkKind GyroscopicRescaleSkips { get; } = new(name: "physics.bodies.gyroscopic-rescale-skips", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting step-ups kit bodies took (<see cref="BodyKinematicsWitness.StepUps"/>).</summary>
    public static WorkKind StepUps { get; } = new(name: "physics.bodies.step-ups", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting step-ups refused: risers cleared at the step height with nothing to stand on
    /// beyond (<see cref="Puck.Physics.FixedStepUpOutcomes.IsRefusal"/>).</summary>
    public static WorkKind StepRefusals { get; } = new(name: "physics.bodies.step-refusals", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the peak counting the tallest riser one step-up took, in micrometres.</summary>
    public static WorkKind StepRisePeak { get; } = new(name: "physics.bodies.step-rise-peak", peak: true, unit: "um", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting ground snaps kit bodies took.</summary>
    public static WorkKind Snaps { get; } = new(name: "physics.bodies.snaps", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting kit steps that left the ground.</summary>
    public static WorkKind Leaves { get; } = new(name: "physics.bodies.leaves", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting kit steps that met the ground.</summary>
    public static WorkKind Landings { get; } = new(name: "physics.bodies.landings", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the peak counting the longest airborne run a kit body was in, in milliseconds.</summary>
    public static WorkKind AirbornePeak { get; } = new(name: "physics.bodies.airborne-peak", peak: true, unit: "ms", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting driven kit steps that moved their body less than
    /// <see cref="WorldBody.StuckDisplacement"/>.</summary>
    public static WorkKind StuckSteps { get; } = new(name: "physics.bodies.stuck-steps", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the peak counting the longest run of such steps one body was in, in milliseconds.</summary>
    public static WorkKind StuckPeak { get; } = new(name: "physics.bodies.stuck-peak", peak: true, unit: "ms", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting driven kit steps that ended pressed against a face facing up too steeply to stand
    /// on.</summary>
    public static WorkKind SlopeRefusals { get; } = new(name: "physics.bodies.slope-refusals", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting kit steps whose contact solve ended still pushing
    /// (<see cref="Puck.Physics.ContactResolution.Residual"/> above zero).</summary>
    public static WorkKind ResidualSteps { get; } = new(name: "physics.bodies.residual-steps", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the peak counting the deepest push such a solve still applied, in micrometres.</summary>
    public static WorkKind PenetrationPeak { get; } = new(name: "physics.bodies.penetration-peak", peak: true, unit: "um", workClass: WorkClass.Deterministic);
    /// <summary>Gets the ledger a host's populations count into. Declared after the kinds, which its counter set reads
    /// as it is created.</summary>
    public static WorldBodyWork Process { get; } = new();

    /// <summary>Initializes a new instance of the <see cref="WorldBodyWork"/> class, every kind at zero.</summary>
    public WorldBodyWork() : base(counters: new(
        kinds: [
            Advances, LocalAdvances, LocalSteers, KitPartitions, KitForks, CurveResorts, StepSaves, StepSavedBytes, FieldWakes, FieldObserved,
            GyroscopicSteps, GyroscopicRefusals, GyroscopicRescaleSkips, AdjacencyCorrectionClamps,
            StepUps, StepRefusals, StepRisePeak, Snaps, Leaves, Landings, AirbornePeak, StuckSteps, StuckPeak, SlopeRefusals, ResidualSteps, PenetrationPeak,
        ],
        name: SourceName
    )) { }

    /// <summary>Adds to one of this ledger's totals.</summary>
    /// <param name="kind">One of this ledger's kinds, a total.</param>
    /// <param name="amount">The count to add; zero adds nothing.</param>
    public void Add(WorkKind kind, long amount) {
        if (amount != 0) { Counters.Add(amount: amount, kind: kind); }
    }
    /// <summary>Raises one of this ledger's peaks to a value one body step published, when it is larger.</summary>
    /// <param name="kind">One of this ledger's kinds, a peak.</param>
    /// <param name="value">The value, in the kind's unit; non-negative.</param>
    public void Raise(WorkKind kind, long value) => Counters.Raise(kind: kind, value: value);
    /// <summary>Returns a length in whole micrometres, the unit the length peaks count in, rounded toward zero.</summary>
    /// <param name="metres">The length, in metres; a negative length reads as zero.</param>
    /// <returns>The micrometres.</returns>
    public static long Micrometres(Puck.Maths.FixedQ4816 metres) => ((metres.Value <= 0L)
        ? 0L
        : (long)(((Int128)metres.Value * 1_000_000) >> Puck.Maths.FixedQ4816.FractionBitCount));
    /// <summary>Returns a span of engine ticks in whole milliseconds, the unit the time peaks count in, rounded toward
    /// zero.</summary>
    /// <param name="engineTicks">The engine ticks.</param>
    /// <returns>The milliseconds.</returns>
    public static long Milliseconds(ulong engineTicks) => (long)(((UInt128)engineTicks * 1000UL) / Puck.Maths.FixedTickConversion.TicksPerSecond);
}
