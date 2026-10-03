using Puck.Abstractions.Counting;

namespace Puck.World;

/// <summary>
/// The work an authority does to keep presentation-tier recipients current, counted in a
/// <see cref="WorkCounterSet"/> under the source name <see cref="SourceName"/>: the projections it composes, the whole
/// projections and the member
/// deltas it delivers and their bytes, the clock anchors it sends, and the anchor rows it holds for its recipients,
/// one per recipient per clock, as rows retained and rows released, so a reader takes the rows held as the
/// difference. Each count is a monotonic total; a reader takes a window by reading twice and subtracting. None is
/// simulation state.
/// <para>
/// Every recipient's feed counts into <see cref="Current"/>: the ledger <see cref="Attribute"/> installed for the
/// calling flow, or the process ledger <see cref="Process"/> a host registers. A law attributes a fresh ledger around
/// the work it measures.
/// </para>
/// </summary>
public sealed class WorldProjectionWork : IWorkCounterSource {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "world.projection";

    private static readonly AsyncLocal<WorldProjectionWork?> Attributed = new();

    private readonly WorkCounterSet m_counts = new(
        kinds: Kinds,
        name: SourceName
    );

    /// <summary>Gets the ledger every feed counts into when no flow has attributed one of its own.</summary>
    public static WorldProjectionWork Process => Shared.Ledger;
    /// <summary>Gets the ledger the calling flow counts into: the one <see cref="Attribute"/> installed, else
    /// <see cref="Process"/>.</summary>
    public static WorldProjectionWork Current => (Attributed.Value ?? Process);

    /// <summary>Gets the kind counting whole projections delivered: a recipient's first, and every one a delta could not
    /// carry.</summary>
    public static WorkKind Documents { get; } = new(name: "world.projection.documents", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting projections composed: every disclosure of the document to a presentation-tier
    /// reader, a recipient's delivery and a one-off read alike, whether or not it then owes anything. Fetch authorization
    /// also composes, so this count depends on the recipient cache retained across runs.</summary>
    public static WorkKind Compositions { get; } = new(name: "world.projection.compositions", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting member deltas delivered: the members of a recipient's projection that changed,
    /// and nothing else.</summary>
    public static WorkKind Deltas { get; } = new(name: "world.projection.deltas", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the compact canonical bytes of every projection and delta delivered, as they
    /// travel.</summary>
    public static WorkKind Bytes { get; } = new(name: "world.projection.bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting clock anchors sent: one per clock whose recipient's prediction differed from the
    /// authority's phase.</summary>
    public static WorkKind Anchors { get; } = new(name: "world.projection.anchors", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting anchor rows retained: one per recipient per clock it was first sent an anchor
    /// of.</summary>
    public static WorkKind AnchorRowsRetained { get; } = new(name: "world.projection.anchor-rows.retained", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting anchor rows released: every row a recipient held, when it leaves or loses
    /// disclosure.</summary>
    public static WorkKind AnchorRowsReleased { get; } = new(name: "world.projection.anchor-rows.released", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the count of prototype objects fetched on recipient cache misses.</summary>
    public static WorkKind PrototypeFetches { get; } = new(name: "world.projection.prototype-fetches", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the canonical prototype body bytes delivered by fetches.</summary>
    public static WorkKind PrototypeBytes { get; } = new(name: "world.projection.prototype-bytes", unit: "bytes", workClass: WorkClass.Pacing);

    /// <summary>Gets the ledger's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => Order.Kinds;

    /// <inheritdoc/>
    ReadOnlySpan<WorkKind> IWorkCounterSource.WorkKinds => Kinds;

    /// <summary>Gets the name a counters report heads this source's section with, <see cref="SourceName"/>.</summary>
    public string Name => SourceName;

    /// <summary>Makes <paramref name="work"/> the ledger the calling flow and every flow it starts count into until
    /// the returned scope is disposed, which restores the ledger the flow counted into before.</summary>
    /// <param name="work">The ledger to count into.</param>
    /// <returns>The scope that ends the attribution.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    public static Scope Attribute(WorldProjectionWork work) {
        ArgumentNullException.ThrowIfNull(argument: work);

        var previous = Attributed.Value;

        Attributed.Value = work;

        return new Scope(previous: previous);
    }
    /// <summary>Adds <paramref name="amount"/> units of <paramref name="kind"/> into <see cref="Current"/>.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <param name="amount">The non-negative amount of work.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of <see cref="Kinds"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    public static void Count(WorkKind kind, long amount = 1L) =>
        Current.m_counts.Add(
            amount: amount,
            kind: kind
        );
    /// <summary>Reads the current total of one of this ledger's kinds.</summary>
    /// <param name="kind">The kind to read.</param>
    /// <returns>The total counted so far.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of <see cref="Kinds"/>.</exception>
    public long Read(WorkKind kind) =>
        m_counts.Read(kind: kind);
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) =>
        m_counts.TryRead(
            kind: kind,
            value: out value
        );

    /// <summary>Ends an <see cref="Attribute"/> when disposed.</summary>
    public readonly struct Scope : IDisposable {
        private readonly WorldProjectionWork? m_previous;

        internal Scope(WorldProjectionWork? previous) {
            m_previous = previous;
        }

        /// <summary>Restores the ledger the flow counted into before the attribution.</summary>
        public void Dispose() =>
            Attributed.Value = m_previous;
    }

    // The process ledger sizes itself from Kinds, so it is built only once every kind exists.
    private static class Shared {
        internal static readonly WorldProjectionWork Ledger = new();
    }
    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Order {
        internal static readonly WorkKind[] Kinds = [
            Compositions,
            Documents,
            Deltas,
            Bytes,
            PrototypeFetches,
            PrototypeBytes,
            Anchors,
            AnchorRowsRetained,
            AnchorRowsReleased,
        ];
    }
}
