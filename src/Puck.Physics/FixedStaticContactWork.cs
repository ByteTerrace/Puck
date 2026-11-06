using Puck.Abstractions.Counting;

namespace Puck.Physics;

/// <summary>
/// The deterministic work <see cref="FixedStaticContactSolver"/> spends resolving bodies against analytic colliders,
/// counted in a <see cref="WorkCounterSet"/> and read through <see cref="IWorkCounterSource"/> under the source name
/// <see cref="SourceName"/>: the resolves it runs, the exact collider queries the bounds culls leave, how many of those
/// take the iterative convex query (GJK and EPA over a support map) and the support-map evaluations they spend, and the
/// pushes they return.
/// </summary>
/// <remarks>Each count is a monotonic total over the ledger's life, never reset; a reader takes a window by reading
/// twice and subtracting. None is part of simulation state: nothing hashes, exports or checkpoints them. Every kind is
/// <see cref="WorkClass.Deterministic"/>: the same world and input spend the same work on every run and host, though a
/// process running several solvers (a test host) sums them all.</remarks>
public sealed class FixedStaticContactWork : IWorkCounterSource {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "physics.contact";

    private readonly WorkCounterSet m_counts = new(
        kinds: [Resolves, SupportDistances, Queries, ConvexQueries, Supports, Pushes, FieldQueries, FieldBounds, FieldSamples, FieldReads, FieldUnproved, FieldNarrowed],
        name: SourceName
    );

    /// <summary>Gets the kind counting resolves: one per body move or rest solved against the analytic colliders.</summary>
    public static WorkKind Resolves { get; } = new(name: "physics.contact.resolves", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting support-distance queries: one per ground snap or step-up landing asked of the
    /// analytic colliders, beside the one landing resolve it costs.</summary>
    public static WorkKind SupportDistances { get; } = new(name: "physics.contact.support-distances", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting exact collider queries, one per body volume and collider the bounds culls keep in
    /// each relaxation pass.</summary>
    public static WorkKind Queries { get; } = new(name: "physics.contact.queries", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the exact queries the iterative convex query (a superellipsoid, a chamfered box, or
    /// any finite collider against an ellipsoid volume) iterates for, past its bounds rejection.</summary>
    public static WorkKind ConvexQueries { get; } = new(name: "physics.contact.convex-queries", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the support-map evaluations the convex queries spend.</summary>
    public static WorkKind Supports { get; } = new(name: "physics.contact.supports", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the exact queries that returned a push.</summary>
    public static WorkKind Pushes { get; } = new(name: "physics.contact.pushes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the queries against a field collider, a push or a surface projection each.</summary>
    public static WorkKind FieldQueries { get; } = new(name: "physics.contact.field-queries", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the envelope evaluations the field queries spend proving a body clear.</summary>
    public static WorkKind FieldBounds { get; } = new(name: "physics.contact.field-bounds", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the field evaluations the field queries spend.</summary>
    public static WorkKind FieldSamples { get; } = new(name: "physics.contact.field-samples", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the field queries that read the field at least once: those the envelope did not
    /// prove clear, whose samples <see cref="FieldSamples"/> counts.</summary>
    public static WorkKind FieldReads { get; } = new(name: "physics.contact.field-reads", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the field queries that ended unproved: neither proved clear nor in contact, nor
    /// projected onto the surface (<see cref="FixedFieldContactOutcome.Unproved"/>).</summary>
    public static WorkKind FieldUnproved { get; } = new(name: "physics.contact.field-unproved", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the queries against a field collider that its partition narrowed to one exact
    /// analytic collider or to none of its solid (<see cref="FixedFieldPartition"/>), so no field solve ran: a body its
    /// carves cannot reach.</summary>
    public static WorkKind FieldNarrowed { get; } = new(name: "physics.contact.field-narrowed", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the ledger every analytic solve counts into. Declared after the kinds, which its counter set reads as
    /// it is created.</summary>
    public static FixedStaticContactWork Process { get; } = new();

    /// <inheritdoc/>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => m_counts.WorkKinds;

    /// <summary>Counts one resolve of a body against the analytic colliders.</summary>
    public void CountResolve() => m_counts.Count(kind: Resolves);
    /// <summary>Counts one support-distance query (<see cref="FixedStaticContactSolver.TrySupportDistance"/>).</summary>
    public void CountSupportDistance() => m_counts.Count(kind: SupportDistances);
    /// <summary>Counts one exact collider query.</summary>
    /// <param name="pushed">Whether it returned a push.</param>
    public void CountQuery(bool pushed) {
        m_counts.Count(kind: Queries);

        if (pushed) {
            m_counts.Count(kind: Pushes);
        }
    }
    /// <summary>Counts one iterative convex query and the support-map evaluations it spent.</summary>
    /// <param name="supports">The support-map evaluations.</param>
    public void CountConvex(int supports) {
        m_counts.Count(kind: ConvexQueries);
        m_counts.Add(amount: supports, kind: Supports);
    }
    /// <summary>Counts one query against a field collider and the evaluations it spent.</summary>
    /// <param name="bounds">The envelope evaluations.</param>
    /// <param name="samples">The field evaluations.</param>
    /// <param name="unproved">Whether the query ended unproved.</param>
    public void CountField(int bounds, int samples, bool unproved) {
        m_counts.Count(kind: FieldQueries);
        m_counts.Add(amount: bounds, kind: FieldBounds);
        m_counts.Add(amount: samples, kind: FieldSamples);

        if (samples > 0) {
            m_counts.Count(kind: FieldReads);
        }

        if (unproved) {
            m_counts.Count(kind: FieldUnproved);
        }
    }
    /// <summary>Counts one field-collider query its partition narrowed (<see cref="FieldNarrowed"/>).</summary>
    public void CountNarrowed() => m_counts.Count(kind: FieldNarrowed);
    /// <summary>Reads the current total of one of this ledger's kinds.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <returns>The total counted so far.</returns>
    public long Read(WorkKind kind) => m_counts.Read(kind: kind);
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) => m_counts.TryRead(kind: kind, value: out value);
}
