using Puck.Abstractions.Counting;

namespace Puck.World;

/// <summary>
/// The deterministic work a world boot does before its first tick, counted in a <see cref="WorkCounterSet"/> and read
/// through <see cref="IWorkCounterSource"/> under the source name <see cref="SourceName"/>: the documents read, the
/// <c>.puck</c> sources compiled and the ones the compile cache answered, the basis-and-imports merges, the strict
/// parses and validations, and the builds and loads that follow. Each count is a monotonic total over the ledger's
/// life, never reset; a reader takes a window by reading twice and subtracting. None is part of simulation state:
/// nothing hashes, exports, or checkpoints them. Every kind is <see cref="WorkClass.Deterministic"/> but the five the
/// caches split, which are <see cref="WorkClass.Pacing"/>: compiles, <c>.puck</c> parses and cache hits, which depend
/// on what the per-user compile cache already holds, and compiled-world hits and chunk derivations, which depend on the
/// compiled worlds a boot finds.
/// <para>
/// The work sites are static paths shared by every host (the boot loader, the composer, <c>world.reload</c>, the
/// neighbour resolver), so they count into <see cref="Current"/>: the ledger <see cref="Attribute"/> installed for the
/// calling flow, or the process ledger <see cref="Process"/> when none is. A host registers <see cref="Process"/>;
/// a law attributes a fresh ledger around the work it measures, so a sibling test running in parallel cannot move
/// its counts.
/// </para>
/// </summary>
public sealed class WorldBootWork : IWorkCounterSource {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "world.boot";

    private static readonly AsyncLocal<WorldBootWork?> Attributed = new();

    private readonly WorkCounterSet m_counts = new(
        kinds: Kinds,
        name: SourceName
    );

    /// <summary>Gets the ledger every work site counts into when no flow has attributed one of its own — the one a
    /// host registers as its <c>world.boot</c> source.</summary>
    public static WorldBootWork Process =>
        Shared.Ledger;
    /// <summary>Gets the ledger the calling flow counts into: the one <see cref="Attribute"/> installed, else
    /// <see cref="Process"/>.</summary>
    public static WorldBootWork Current => (Attributed.Value ?? Process);

    /// <summary>Gets the kind counting world document loads: one per document a file or byte load door admits
    /// work for, whatever it goes on to read, compose, parse and validate.</summary>
    public static WorkKind Loads { get; } = new(name: "world.boot.loads", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the documents a load or a composition reads: one per root file and one per
    /// basis or import read through an <see cref="IWorldDocumentSource"/>, whether it arrives as a file's bytes or as
    /// a <c>.puck</c> source lowered to its document.</summary>
    public static WorkKind DocumentsRead { get; } = new(name: "world.boot.documents-read", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting basis-and-imports merges: one per document whose graph was merged, counting
    /// every document a composition walked into.</summary>
    public static WorkKind Compositions { get; } = new(name: "world.boot.compositions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the compositions answered from an image already composed in this process
    /// rather than merged again.</summary>
    public static WorkKind CompositionsShared { get; } = new(name: "world.boot.compositions-shared", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting <c>.puck</c> compilations that ran: one per source parsed, walked and
    /// lowered, whichever door asked.</summary>
    public static WorkKind Compiles { get; } = new(name: "world.boot.compiles", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the <c>.puck</c> texts compilations parsed: the compiled source and each
    /// module its import walk read.</summary>
    public static WorkKind PuckParses { get; } = new(name: "world.boot.puck-parses", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the compilations the compile cache answered without compiling, because every
    /// file the held compile read still holds the bytes it read.</summary>
    public static WorkKind PuckCacheHits { get; } = new(name: "world.boot.puck-cache-hits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting strict document parses: one per composed JSON text bound to a
    /// <see cref="WorldDefinition"/>.</summary>
    public static WorkKind Parses { get; } = new(name: "world.boot.parses", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting whole-document validations: one per run of the validator over a
    /// <see cref="WorldDefinition"/>, local or with its adjacency proofs. One admitted load counts one; an environment
    /// completion over an existing receipt counts none.</summary>
    public static WorkKind Validations { get; } = new(name: "world.boot.validations", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting rule compilations: one per <see cref="WorldRuleCompilation"/> produced, whether
    /// a validation retained it or a host compiled it directly. A load that reuses its receipt counts one.</summary>
    public static WorkKind RuleCompilations { get; } = new(name: "world.boot.rule-compilations", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting neighbour resolutions: one per neighbour document a border proof or a
    /// derived corner reads through a file-backed resolver.</summary>
    public static WorkKind NeighbourResolves { get; } = new(name: "world.boot.neighbour-resolves", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting curvature-spline derivations: one per distinct curve shape the process
    /// compiles, since a shape compiled once is shared by every row and instance of it.</summary>
    public static WorkKind CurveCompiles { get; } = new(name: "world.boot.curve-compiles", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the signed-distance programs built from a world's shapes: the server's static
    /// solid field and each static scene a frame presenter emits from the prototypes and placements.</summary>
    public static WorkKind ShapeBuilds { get; } = new(name: "world.boot.shape-builds", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting asset loads: one per music, tune, patch or audio document an asset row
    /// resolves off disk, and one per machine content image a machine host prepares.</summary>
    public static WorkKind AssetLoads { get; } = new(name: "world.boot.asset-loads", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the boots whose drawn definition came from a compiled world's <c>DEFN</c> chunk
    /// rather than a fresh draw.</summary>
    public static WorkKind CompiledHits { get; } = new(name: "world.boot.compiled-hits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting compiled-world chunks derived fresh: one per chunk a boot or a compile derived
    /// because no compiled world held it.</summary>
    public static WorkKind ChunkDerivations { get; } = new(name: "world.boot.chunk-derivations", unit: "count", workClass: WorkClass.Pacing);

    /// <summary>Gets the ledger's kinds, in the order a report lists them.</summary>
    public static ReadOnlySpan<WorkKind> Kinds =>
        Order.Kinds;

    /// <inheritdoc/>
    ReadOnlySpan<WorkKind> IWorkCounterSource.WorkKinds =>
        Kinds;

    /// <summary>Gets the name a counters report heads this source's section with, <see cref="SourceName"/>.</summary>
    public string Name => SourceName;

    /// <summary>Makes <paramref name="work"/> the ledger the calling flow and every flow it starts count into until
    /// the returned scope is disposed, which restores the ledger the flow counted into before.</summary>
    /// <param name="work">The ledger to count into.</param>
    /// <returns>The scope that ends the attribution.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    public static Scope Attribute(WorldBootWork work) {
        ArgumentNullException.ThrowIfNull(argument: work);

        var previous = Attributed.Value;

        Attributed.Value = work;

        return new Scope(previous: previous);
    }
    /// <summary>Counts one unit of <paramref name="kind"/> into <see cref="Current"/>.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of <see cref="Kinds"/>.</exception>
    public static void Count(WorkKind kind) =>
        Current.Add(amount: 1L, kind: kind);
    /// <summary>Adds <paramref name="amount"/> units of <paramref name="kind"/>. Several flows may count at once.</summary>
    /// <param name="kind">One of this ledger's kinds.</param>
    /// <param name="amount">The non-negative amount of work.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of <see cref="Kinds"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    public void Add(WorkKind kind, long amount) =>
        m_counts.Add(
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
        private readonly WorldBootWork? m_previous;

        internal Scope(WorldBootWork? previous) {
            m_previous = previous;
        }

        /// <summary>Restores the ledger the flow counted into before the attribution.</summary>
        public void Dispose() =>
            Attributed.Value = m_previous;
    }

    // The process ledger sizes itself from Kinds, so it is built only once every kind exists, whatever order the
    // members are declared in.
    private static class Shared {
        internal static readonly WorldBootWork Ledger = new();
    }
    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Order {
        internal static readonly WorkKind[] Kinds = [
            Loads,
            DocumentsRead,
            Compositions,
            CompositionsShared,
            Compiles,
            PuckParses,
            PuckCacheHits,
            Parses,
            Validations,
            RuleCompilations,
            NeighbourResolves,
            CurveCompiles,
            ShapeBuilds,
            AssetLoads,
            CompiledHits,
            ChunkDerivations,
        ];
    }
}
