using Puck.Abstractions.Counting;

namespace Puck.World.Server;

/// <summary>
/// The work <see cref="WorldPopulation"/> spends stepping bodies, counted in a
/// <see cref="WorkCounterSet"/> and read through <see cref="IWorkCounterSource"/> under the source name
/// <see cref="SourceName"/>: the bodies a tick advances, the parked rigid bodies a contact-field change woke or left
/// asleep because the change could not reach them, and the gyroscopic steps rigid bodies took, refused, or took without their rescale.
/// </summary>
/// <remarks>Each count is a monotonic total over the ledger's life, never reset; a reader takes a window by reading
/// twice and subtracting. None is part of simulation state: nothing hashes, exports or checkpoints them. Each kind
/// declares its class; neighbour-contact counts depend on untaped delivery timing.</remarks>
public sealed class WorldBodyWork : IWorkCounterSource {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "physics.bodies";

    private readonly WorkCounterSet m_counts = new(
        kinds: [Advances, FieldWakes, FieldObserved, GyroscopicSteps, GyroscopicRefusals, GyroscopicRescaleSkips, AdjacencyCorrectionClamps],
        name: SourceName
    );

    /// <summary>Gets the kind counting simulated body advances: one per non-seat body a tick's integration pass steps.</summary>
    public static WorkKind Advances { get; } = new(name: "physics.bodies.advances", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the number of neighbour responses (body pushes and geometry resolution) the local tick budget or
    /// an ownership face shortened, rejected or slowed. Delivery timing is not yet taped, so this count is
    /// pacing-dependent.</summary>
    public static WorkKind AdjacencyCorrectionClamps { get; } = new(name: "physics.bodies.adjacency-correction-clamps", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting parked rigid bodies a contact-field change woke.</summary>
    public static WorkKind FieldWakes { get; } = new(name: "physics.bodies.field-wakes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting parked rigid bodies that observed a bounded contact-field change without waking (a
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
    /// <summary>Gets the ledger a host's populations count into. Declared after the kinds, which its counter set reads
    /// as it is created.</summary>
    public static WorldBodyWork Process { get; } = new();

    /// <inheritdoc/>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => m_counts.WorkKinds;

    /// <summary>Adds to one of this ledger's kinds.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <param name="amount">The count to add; zero adds nothing.</param>
    public void Add(WorkKind kind, int amount) {
        if (amount != 0) { m_counts.Add(amount: amount, kind: kind); }
    }
    /// <summary>Reads the current total of one of this ledger's kinds.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <returns>The total counted so far.</returns>
    public long Read(WorkKind kind) => m_counts.Read(kind: kind);
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => m_counts.TryRead(kind: kind, value: out value);
}
