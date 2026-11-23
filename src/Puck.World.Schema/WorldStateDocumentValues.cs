using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>The store a document's values read once <see cref="WorldStateDocumentValues.BindLive"/> binds them: the
/// arena its owner holds now and the clocks it reads that arena at. A server binds its own document to its live arena;
/// a client binds each delivered document to the arena its deliveries fill, so a value it reads never reaches the
/// authority's store.</summary>
/// <remarks>One per owner, and the binder every value it binds names (<see cref="IDocumentStateValue.Binder"/>). A
/// bound value reads whichever arena its owner holds when it is read, so an arena the owner replaces needs no new
/// binding, and a definition installed again keeps the values bound before. A value bound to another owner is bound
/// again rather than shared.</remarks>
/// <param name="clock">Reads the owner's tick pair and dynamics.</param>
/// <param name="gate">The lock every read of a bound value takes, for an owner whose arena another thread writes under
/// it; <see langword="null"/> when the arena is written only on the thread that reads it.</param>
public sealed class WorldLiveValues(Func<ArenaTime> clock, Lock? gate = null) {
    /// <summary>Gets or sets the arena the owner holds now, or <see langword="null"/> before it builds one.</summary>
    public StateArena? Arena { get; set; }
    /// <summary>Gets the tick pair and dynamics the owner reads its arena at now.</summary>
    public ArenaTime Time => clock();
    /// <summary>Gets the lock a read of a bound value takes, or <see langword="null"/> for none.</summary>
    public Lock? Gate => gate;

    /// <summary>Finds the projection the last bind made of <paramref name="definition"/>.</summary>
    /// <param name="definition">The definition bound before.</param>
    /// <param name="projection">The projection it became, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the last bind met <paramref name="definition"/>.</returns>
    public bool TryProjection(WorldDefinition definition, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldDefinition? projection) {
        projection = (Bound.TryGetValue(key: definition, value: out var bound) ? (bound as WorldDefinition) : null);

        return (projection is not null);
    }

    // What the last bind turned each value it met into: itself for a value that holds no reference it did not bind,
    // and the bound copy for any other. A later bind answers each from here without entering it, so binding a definition
    // composed over the last projection, or delivered again with most of its rows unchanged, walks only what changed and
    // hands back the same copies. Sound because a document value never changes once built (records with init members,
    // immutable holders, lists no composition mutates in place).
    internal Dictionary<object, object> Bound { get; private set; } = new(comparer: ReferenceEqualityComparer.Instance);
    // The memo the next bind fills while it reads Bound, empty: the two trade places at each bind, so a delivery
    // binds into storage the bind before last grew rather than a new table.
    private Dictionary<object, object> Spare { get; set; } = new(comparer: ReferenceEqualityComparer.Instance);

    // The spare memo, emptied of anything a bind that stopped part way left in it.
    internal Dictionary<object, object> EmptySpare() {
        Spare.Clear();
        return Spare;
    }

    // Makes the memo a bind filled the one later binds read, and empties the one it replaces for the bind after.
    internal void Adopt(Dictionary<object, object> kept) {
        var replaced = Bound;

        Bound = kept;
        if (!ReferenceEquals(objA: replaced, objB: kept)) {
            replaced.Clear();
            Spare = replaced;
        }
    }
}
/// <summary>Resolves state-backed values embedded anywhere in a world document against its Text state cells.</summary>
/// <remarks>
/// The walk deliberately operates at the completed-document boundary: an embedded creation is a document family in
/// its own right and cannot know the world state that contains it while it is being deserialized. Literal values are
/// untouched. Bound values retain their reference token, so serialization writes the reference back.
/// <para>Every door that turns bytes into a live document runs <see cref="TryResolve"/>, so a delivered document is
/// indistinguishable from a file-loaded one. A document leaving for a boundary that does not carry the state table
/// itself runs <see cref="TryFlatten{T}"/> instead, since a reference the receiver cannot answer is a dangling
/// pointer.</para>
/// <para>A value holder never changes once built (<see cref="IDocumentStateValue"/>), so a definition one server, a
/// cache or a rejected candidate shares with another is never changed under it. Every operation that resolves, binds
/// or flattens a value is a rewrite of the one document walk (<see cref="WorldDocumentWalker.Rewrite{T}"/>): it puts a
/// new holder where a reference stood and copies only the path from the root to it, so every row and section no
/// reference reaches is the input's own object, and reference-keyed products derived from it (a prototype's canonical
/// hash, a compiled stamp, a palette's emissions) still hold. The reads (<see cref="HasReference"/>,
/// <see cref="ReferencesRow"/>) are visitors of the
/// same walk. Both enter only branches that can hold a bound value and never enter a holder's own members.</para>
/// </remarks>
public static class WorldStateDocumentValues {
    private static readonly WorldDocumentWalker Walker = new(isSurface: static type => typeof(IDocumentStateValue).IsAssignableFrom(c: type));
    [SideTable("The live binder records the authored object it copied without changing its serialized value.")]
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> BindingSources = new();

    internal static object SourceOfBinding(object value) => BindingSources.TryGetValue(value, out var source) ? source : value;
    // The rows each definition's document values name, collected once per definition. A rewrite can drop a reference
    // (a flattening) but never add one, so a set collected earlier is never missing a row the definition names.
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldDefinition, HashSet<string>> ReferencedRowSets = new();

    /// <summary>Returns an owner's own projection of a definition, whose state-bound values read the arena held by
    /// <paramref name="live"/>: a server's live arena, or the arena a client's deliveries fill. Seed rows remain
    /// declarations.</summary>
    /// <remarks>A value already bound to <paramref name="live"/> is kept, so a candidate composed over the projection
    /// an owner installed comes back holding the same rows; any other referenced value, including one bound to another
    /// owner's store, is replaced by a copy bound to <paramref name="live"/> on a copied path. A row or section the
    /// previous bind met is answered with what it became then, without being entered, so the walk costs what changed
    /// and a definition delivered again keeps the copies it was handed before. The state section is never rewritten,
    /// so the state catalog the arena was built over carries across. The projection is a definition object this owner
    /// alone holds, so a definition another owner or a cache holds is never the one this owner's arena is bound to
    /// (<see cref="WorldStateReader.Bind"/>), unless <paramref name="shareUnchanged"/> lets a definition that needed no
    /// new binding and is bound to no arena come back as itself.</remarks>
    /// <param name="definition">The definition to project.</param>
    /// <param name="live">The owner's store.</param>
    /// <param name="shareUnchanged">Whether a definition holding no value bound to another store, and bound to no
    /// arena, is answered as itself: right for an owner that never binds its projection to an arena (a client), wrong
    /// for one that does (a server).</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference names no row of the live arena.</exception>
    public static WorldDefinition BindLive(WorldDefinition definition, WorldLiveValues live, bool shareUnchanged = false) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: live);

        if (live.Bound.TryGetValue(key: definition, value: out var earlier)) {
            // A definition bound before (one delivered again) answers with the projection it became, allocating nothing.
            if (!ReferenceEquals(objA: earlier, objB: definition) || (shareUnchanged && !WorldStateReader.TryBoundArena(definition: definition, arena: out _))) {
                return (WorldDefinition)earlier;
            }

            // This owner's own projection, bound again: every value in it is already bound, so it needs only a new
            // object, which replaces it in the memo so the memo keeps its size.
            var again = (definition with { });

            CarryReferencedRows(source: definition, target: again);
            _ = live.Bound.Remove(key: definition);
            live.Bound[again] = again;

            return again;
        }

        var bound = definition;

        // A set already collected answers a definition that names no reference without a walk; nothing collects one here.
        if (!ReferencedRowSets.TryGetValue(key: definition, value: out var named) || (named.Count != 0)) {
            var binder = new Binder(live: live);

            bound = Walker.Rewrite(
                rewriter: binder,
                root: definition
            );

            if (binder.Stopped) {
                throw new InvalidOperationException(message: binder.Reason);
            }

            live.Adopt(kept: binder.Kept);
        } else {
            // A definition that names no reference has nothing else worth remembering.
            live.Bound.Clear();
        }

        var owned = (ReferenceEquals(objA: bound, objB: definition)
            ? ((shareUnchanged && !WorldStateReader.TryBoundArena(definition: definition, arena: out _)) ? definition : (definition with { }))
            : bound);

        if (!ReferenceEquals(objA: owned, objB: definition)) {
            CarryReferencedRows(source: definition, target: owned);
        }
        live.Bound[definition] = owned;
        live.Bound[owned] = owned;

        return owned;
    }

    private sealed class LiveValue(WorldLiveValues live, string row, CellName name) {
        private Memo? m_memo;

        public string? Read() {
            if (live.Gate is not { } gate) {
                return ReadArena();
            }

            lock (gate) {
                return ReadArena();
            }
        }

        private string? ReadArena() {
            if (live.Arena is not { } arena) {
                return null;
            }

            var memo = Volatile.Read(location: ref m_memo);
            var ordinal = -1;

            if ((memo is not null) && ReferenceEquals(objA: memo.Arena, objB: arena)) {
                ordinal = memo.Ordinal;
            } else if (arena.Catalog.TryResolve(lane: StateLane.Document, name: row, handle: out var handle)) {
                ordinal = handle.Ordinal;
            }
            if (ordinal < 0) {
                return null;
            }

            var time = live.Time;
            var generation = arena.RowGeneration(rowOrdinal: ordinal);
            var tick = (arena.Layout[ordinal].HasTraits ? time.EngineTick : 0UL);

            if ((memo is not null) && ReferenceEquals(objA: memo.Arena, objB: arena) && (memo.Generation == generation) && (memo.Tick == tick)) {
                return memo.Text;
            }

            string? text = null;

            if (arena.Keys.TryResolve(name: name, key: out var key) && arena.TryReadLive(rowOrdinal: ordinal, key: key, time: in time, value: out var value)) {
                text = value.Kind switch {
                    CellKind.Text => value.AsText,
                    CellKind.Fixed or CellKind.Bool or CellKind.Int => NumberText(kind: value.Kind, raw: value.Raw),
                    _ => null,
                };
            }

            // One reference swapped whole, so a reader on another thread sees an arena, a generation and its text together.
            Volatile.Write(location: ref m_memo, value: new Memo(Arena: arena, Generation: generation, Ordinal: ordinal, Text: text, Tick: tick));

            return text;
        }

        private sealed record Memo(StateArena Arena, int Ordinal, ulong Generation, ulong Tick, string? Text);
    }
    // The text a numeric cell offers a value bound to it, the spelling a document carries literally, whether the value
    // is resolved once or read live: a decimal for int and fixed, true or false for bool.
    private static string NumberText(CellKind kind, long raw) => kind switch {
        CellKind.Fixed => FixedQ4816.FromRawBits(value: raw).ToString(),
        CellKind.Bool => (raw != 0L) ? "true" : "false",
        _ => raw.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
    };
    // The diagnostic path is formatted only for a refusal.
    private static string Refusal(string root, WorldDocumentSite site, string reference, string message) {
        var path = site.Path;

        return $"{((path.Length == 0) ? root : $"{root}.{path}")} reference '{reference}' {message}";
    }

    // Binds every referenced value not already bound to the owner's store, answering each value the last bind met from
    // what it became then and recording, for the next, what each value this one meets becomes. The state section is
    // kept whole: its lattice origins stay the resolved seeds the catalog was compiled beside.
    private sealed class Binder(WorldLiveValues live) : IWorldDocumentRewriter {
        public Dictionary<object, object> Kept { get; } = live.EmptySpare();
        public string Reason { get; private set; } = string.Empty;
        public bool Stopped { get; private set; }

        public object? Reuse(object value) {
            if (!live.Bound.TryGetValue(key: value, value: out var bound)) {
                return null;
            }

            Keep(value: value, rewritten: bound);

            return bound;
        }
        public bool Enters(object value, WorldDocumentSite site) => (value is not WorldStateSection);
        public void Left(object value, object rewritten) {
            if (!rewritten.GetType().IsValueType) {
                Keep(value: value, rewritten: rewritten);
                if (!ReferenceEquals(value, rewritten)) {
                    BindingSources.AddOrUpdate(rewritten, SourceOfBinding(value));
                }
            }
        }
        // A value and what it became both answer the next bind: the source when it is delivered or composed again, the
        // copy when the projection itself is bound again.
        private void Keep(object value, object rewritten) {
            Kept[value] = rewritten;
            Kept[rewritten] = rewritten;
        }
        public object Replace(object value, WorldDocumentSite site) {
            var holder = (IDocumentStateValue)value;

            if (
                (holder.Reference is not { } reference) ||
                ReferenceEquals(objA: holder.Binder, objB: live)
            ) {
                return value;
            }
            if (live.Bound.TryGetValue(key: value, value: out var bound)) {
                Keep(value: value, rewritten: bound);

                return bound;
            }

            var name = default(CellName);
            var nameReason = string.Empty;

            if (
                (StateBinding.Parse(token: reference) is not (var row, var key, _)) ||
                (live.Arena is not { } arena) ||
                !arena.Catalog.TryResolve(lane: StateLane.Document, name: row, handle: out _) ||
                !CellName.TryParse(candidate: (key ?? StateRow.SlotKey.Value), name: out name, reason: out nameReason)
            ) {
                Reason = Refusal(message: $"names no live document row ({nameReason})", reference: reference, root: "definition", site: site);
                Stopped = true;

                return value;
            }

            WorldBootWork.Count(kind: WorldBootWork.DocumentValueCopies);

            var rebound = holder.Bound(binder: live, read: new LiveValue(live: live, name: name, row: row).Read);

            Keep(value: value, rewritten: rebound);

            return rebound;
        }
    }
    // Resolves every referenced value (or every one naming a sought row) against a document's state cells, or the live
    // arena when one is given, into a copy that keeps its reference or, flattening, drops it. A search for one row
    // passes over every holder that does not name it, a malformed reference included, which cannot name it.
    private sealed class Resolver(WorldDefinition definition, string root, string? soughtRow, bool deferDrawSites, bool detach, StateArena? arena, ArenaTime? time) : IWorldDocumentRewriter {
        public string Reason { get; private set; } = string.Empty;
        public bool Stopped { get; private set; }

        public object Replace(object value, WorldDocumentSite site) {
            var holder = (IDocumentStateValue)value;

            if (holder.Reference is not { } reference) {
                return value;
            }
            if (StateBinding.Parse(token: reference) is not (var row, var key, _)) {
                return (soughtRow is null) ? Refuse(message: "must be state.<row>[.<key>]", reference: reference, site: site, value: value) : value;
            }
            if (
                (soughtRow is not null) &&
                !string.Equals(a: row, b: soughtRow, comparisonType: StringComparison.Ordinal)
            ) {
                return value;
            }
            if (ResolveHolder(arena: arena, definition: definition, deferDrawSites: deferDrawSites, detach: detach, holder: holder, key: key, resolved: out var resolved, row: row, time: time) is { } message) {
                return Refuse(message: message, reference: reference, site: site, value: value);
            }
            if (!ReferenceEquals(objA: resolved, objB: holder)) {
                WorldBootWork.Count(kind: WorldBootWork.DocumentValueCopies);
            }

            return resolved;
        }

        private object Refuse(string message, string reference, WorldDocumentSite site, object value) {
            Reason = Refusal(message: message, reference: reference, root: root, site: site);
            Stopped = true;

            return value;
        }
    }
    // Checks every value bound to one row reads that row's cell, rewriting nothing: the bound-write check, which reads
    // the written row alone. A holder naming another row, or no row, is not its business; the state section binds no
    // value, and a dealt child carries its template's values, which the template's own holders answer for.
    private sealed class BoundChecker(WorldDefinition definition, string soughtRow, StateArena arena, ArenaTime time) : IWorldDocumentVisitor {
        public string Reason { get; private set; } = string.Empty;
        public bool Stopped { get; private set; }

        public bool Enter(object value, WorldDocumentSite site) {
            if ((value is WorldStateSection) || ((value is WorldPlacement placement) && WorldPlacementDeal.TrySplitChildId(id: placement.Id, template: out _, key: out _))) {
                return false;
            }
            if (value is not IDocumentStateValue holder) {
                return true;
            }
            if (
                (holder.Reference is { } reference) &&
                (StateBinding.Parse(token: reference) is (var row, var key, _)) &&
                string.Equals(a: row, b: soughtRow, comparisonType: StringComparison.Ordinal) &&
                (ResolveHolder(arena: arena, definition: definition, deferDrawSites: false, detach: true, holder: holder, key: key, resolved: out _, row: row, time: time) is { } message)
            ) {
                Reason = Refusal(message: message, reference: reference, root: "definition", site: site);
                Stopped = true;
            }

            return false;
        }
    }
    // Reads the cell a reference names, against a document's state cells or the live arena when one is given, and
    // resolves the holder against it: the one reading behind every resolution and the bound-write check. Returns the
    // refusal's message, or null with the resolved copy, or the holder itself for a draw site the loader's first parse
    // defers.
    private static string? ResolveHolder(IDocumentStateValue holder, string row, string? key, WorldDefinition definition, StateArena? arena, ArenaTime? time, bool deferDrawSites, bool detach, out IDocumentStateValue resolved) {
        resolved = holder;
        if (!WorldStateReader.TryRead(
            arena: arena,
            definition: definition,
            engineTick: (time?.EngineTick ?? 0UL),
            key: key,
            rawValue: out var rawValue,
            row: out var stateRow,
            rowName: row,
            text: out var text,
            tick: (time?.Tick ?? 0UL)
        )) {
            return "must name a declared state cell";
        }

        // A numeric cell is offered as its decimal spelling — the same text the document would carry literally — so a
        // scalar binds an int or fixed cell (a drawn or advancing one included) and a vector still refuses it by
        // expectation. A draw site fills at boot, after this walk runs at parse: a reference to it stays unresolved and
        // resolves on the post-draw pass (WorldDefinitionLoader), so an empty drawn cell is deferred, not refused.
        if (
            deferDrawSites &&
            (stateRow.Draw is not null) &&
            (rawValue is null) &&
            (text is null)
        ) {
            return null;
        }
        if (stateRow.Kind == CellKind.Vector) {
            return $"names vector row '{stateRow.Name}'; vector rows cannot be resolved as document values";
        }
        if (stateRow.Kind != CellKind.Text) {
            if (rawValue is not { } raw) {
                return "names a cell that holds no value";
            }

            text = NumberText(kind: stateRow.Kind, raw: raw);
        }
        if (text is null) {
            return "names a cell that holds no value";
        }

        return holder.TryResolved(detach: detach, reason: out var parseReason, resolved: out resolved, text: text)
            ? null
            : $"must hold {holder.ExpectedValue}: {parseReason}";
    }
    // Reports whether a reference is present (any reference, or one naming a sought row), or collects every
    // referenced row's name, touching nothing; a search stops at its first hit, a collection never stops early.
    private sealed class Finder(string? soughtRow, ISet<string>? collected) : IWorldDocumentVisitor {
        public bool Found { get; private set; }
        public bool Stopped => Found;

        public bool Enter(object value, WorldDocumentSite site) {
            if (value is not IDocumentStateValue holder) {
                return true;
            }
            if (holder.Reference is not { } reference) {
                return false;
            }

            // A reference that names no row is still a reference the document retains: a search for any reference
            // finds it, so its holder is resolved and refused by name rather than read as though it held a value.
            if (StateBinding.Parse(token: reference) is not (var row, _, _)) {
                Found = ((collected is null) && (soughtRow is null));

                return false;
            }
            if (collected is not null) {
                _ = collected.Add(item: row);
            } else {
                Found = ((soughtRow is null) || string.Equals(a: row, b: soughtRow, comparisonType: StringComparison.Ordinal));
            }

            return false;
        }
    }

    private static bool Find(object graph, string? soughtRow) {
        var finder = new Finder(collected: null, soughtRow: soughtRow);

        Walker.Walk(
            root: graph,
            visitor: finder
        );

        return finder.Found;
    }
    private static bool TryRewrite<T>(T graph, Resolver resolver, out T rewritten, out string reason) where T : class {
        rewritten = Walker.Rewrite(
            rewriter: resolver,
            root: graph
        );
        reason = resolver.Reason;

        return !resolver.Stopped;
    }

    /// <summary>Gets the name of every state row a retained document-value reference in
    /// <paramref name="definition"/> names, collected by one walk the first time a definition asks and kept for its
    /// lifetime.</summary>
    /// <param name="definition">The document whose references are read.</param>
    /// <returns>The referenced row names; the caller never modifies the set.</returns>
    public static IReadOnlySet<string> ReferencedRows(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ReferencedRowSets.GetValue(
            derive: static candidate => {
                var rows = new HashSet<string>(comparer: StringComparer.Ordinal);

                CollectReferencedRows(
                    graph: candidate,
                    rows: rows
                );

                return rows;
            },
            key: definition
        );
    }
    // A definition that names the same references as another (a state-only replacement, a holding projection, a
    // resolution or a binding of it) carries the set already collected for the other.
    internal static void CarryReferencedRows(WorldDefinition source, WorldDefinition target) {
        if (ReferencedRowSets.TryGetValue(key: source, value: out var rows)) {
            ReferencedRowSets.AddOrUpdate(key: target, value: rows);
        }
    }
    /// <summary>Adds the name of every state row a retained document-value reference in <paramref name="graph"/>
    /// — one section or value holder of a definition — names to <paramref name="rows"/>, in one walk.</summary>
    /// <param name="graph">The sub-graph to walk.</param>
    /// <param name="rows">The set every referenced row name is added to; existing members are kept.</param>
    public static void CollectReferencedRows(object graph, ISet<string> rows) {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: rows);

        Walker.Walk(
            root: graph,
            visitor: new Finder(collected: rows, soughtRow: null)
        );
    }
    /// <summary>Reports whether <paramref name="graph"/> retains any document-value reference at all. A graph object's
    /// answer is walked once per instance and kept for its lifetime: a document graph is immutable, and a rewrite that
    /// drops or resolves a reference is a new instance. A boxed value is walked each time, since every box is new.</summary>
    /// <param name="graph">The object graph to walk — a definition, or an egress document composed over one.</param>
    /// <returns><see langword="true"/> when at least one bound value is present.</returns>
    public static bool HasReference(object graph) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        if (graph.GetType().IsValueType) { return Find(graph: graph, soughtRow: null); }
        return ReferenceEquals(objA: ReferenceVerdicts.GetValue(key: graph, derive: static held => (Find(graph: held, soughtRow: null) ? Referenced : Literal)), objB: Referenced);
    }

    private static readonly Puck.Abstractions.CodeDerivedTable<object, object> ReferenceVerdicts = new();
    private static readonly object Referenced = new();
    private static readonly object Literal = new();
    /// <summary>Reports whether <paramref name="graph"/> — one section or value holder of
    /// <paramref name="definition"/> — carries a document-value reference naming <paramref name="rowName"/>.</summary>
    /// <param name="definition">The document the references resolve against.</param>
    /// <param name="graph">The sub-graph to walk.</param>
    /// <param name="rowName">The state row sought.</param>
    /// <returns><see langword="true"/> when a reference in <paramref name="graph"/> names the row.</returns>
    public static bool ReferencesRow(WorldDefinition definition, object graph, string rowName) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);

        return Find(graph: graph, soughtRow: rowName);
    }
    /// <summary>Returns <paramref name="graph"/> with every document-value reference resolved against
    /// <paramref name="source"/>'s state cells and then dropped, so the result carries literals only.</summary>
    /// <remarks>The rewrite copies the paths to the references it resolves and leaves <paramref name="graph"/> as it
    /// is, so a live document's own rows, whose holders keep the authored reference for canonical write-back, can be
    /// flattened for egress directly.</remarks>
    /// <typeparam name="T">The graph's type.</typeparam>
    /// <param name="source">The document whose state answers the references.</param>
    /// <param name="graph">The graph to flatten.</param>
    /// <param name="flattened">The flattened graph, or <paramref name="graph"/> itself on a refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <param name="arena">The live store, or null at a seed boundary.</param>
    /// <param name="time">The live clock coordinate.</param>
    /// <returns><see langword="true"/> when every reference resolved and detached.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="graph"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryFlatten<T>(WorldDefinition source, T graph, out T flattened, out string reason, StateArena? arena = null, ArenaTime? time = null) where T : class {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: source);

        return TryRewrite(
            graph: graph,
            reason: out reason,
            resolver: new Resolver(arena: arena, definition: source, deferDrawSites: false, detach: true, root: "document", soughtRow: null, time: (time ?? ((arena is null) ? null : ArenaTime.Origin))),
            rewritten: out flattened
        );
    }
    /// <summary>Returns a candidate whose document values bound to <paramref name="rowName"/> are resolved against
    /// the candidate's own state cells, every other row and value kept: what a write to that row composes, so a
    /// validator or a derived rebuild reads the value the write proposes rather than the live one.</summary>
    /// <param name="definition">The candidate the row was written into.</param>
    /// <param name="rowName">The written row.</param>
    /// <param name="refreshed">The refreshed candidate: <paramref name="definition"/> itself when no value reads the
    /// row, or on a refusal.</param>
    /// <param name="reason">Why a value bound to the row could not be resolved, on failure.</param>
    /// <returns><see langword="true"/> when every value bound to the row resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rowName"/> is null or empty.</exception>
    public static bool TryRefresh(WorldDefinition definition, string rowName, out WorldDefinition refreshed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);

        if (!ReferencedRows(definition: definition).Contains(item: rowName)) {
            refreshed = definition;
            reason = string.Empty;
            return true;
        }

        return TryResolveCandidate(
            definition: definition,
            reason: out reason,
            refreshed: out refreshed,
            soughtRow: rowName
        );
    }
    /// <summary>Checks that every document value bound to <paramref name="rowName"/> reads the row's current cell in
    /// the live store as a value of its own shape, reading no other row: the one check a cell write into a bound row
    /// owes its bindings, scoped to the row it wrote.</summary>
    /// <remarks>Nothing is rewritten: the walk visits holders in place. It reads no holder that names another row (a
    /// malformed reference included), never enters the state section, and judges a dealt child by its template's
    /// holders, so a unit the write does not reach cannot refuse it. A refusal's text carries the document path of
    /// the first holder that refused, so a caller answering a recipient words its own refusal.</remarks>
    /// <param name="definition">The live document whose values are checked.</param>
    /// <param name="rowName">The written row.</param>
    /// <param name="arena">The live store holding the written cell.</param>
    /// <param name="time">The live clock coordinate.</param>
    /// <param name="reason">Why a value bound to the row could not read it, on failure.</param>
    /// <returns><see langword="true"/> when every value bound to the row reads it.</returns>
    public static bool TryCheckBound(WorldDefinition definition, string rowName, StateArena arena, ArenaTime time, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);
        ArgumentNullException.ThrowIfNull(argument: arena);

        var checker = new BoundChecker(arena: arena, definition: definition, soughtRow: rowName, time: time);

        Walker.Walk(root: definition, visitor: checker);
        reason = checker.Reason;

        return !checker.Stopped;
    }
    /// <summary>Reports whether <paramref name="rowName"/> names an entity other units refer to: a creation's id or a
    /// look's name bound to the row, so that writing it renames the entity rather than moves a value, and every unit
    /// naming the entity is reached. A reference to an entity (a look's creation, an assignment's rows, a binding
    /// group) is not an identity: writing it changes what its own unit refers to, which that unit's checks judge.</summary>
    /// <param name="definition">The document whose values are read.</param>
    /// <param name="rowName">The row sought.</param>
    /// <returns><see langword="true"/> when a creation's id or a look's name is bound to the row.</returns>
    public static bool BindsIdentity(WorldDefinition definition, string rowName) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);

        foreach (var creation in definition.Creations) {
            if (Names(identifier: creation?.Id, rowName: rowName)) { return true; }
        }
        foreach (var look in definition.Looks) {
            if (Names(identifier: look?.Name, rowName: rowName)) { return true; }
        }
        return false;

        static bool Names(DocumentIdentifier? identifier, string rowName) =>
            (identifier?.Reference is { } reference) && (StateBinding.Parse(token: reference) is (var row, _, _)) &&
            string.Equals(a: row, b: rowName, comparisonType: StringComparison.Ordinal);
    }
    /// <summary>Returns a candidate whose every document value is resolved against the candidate's own state cells:
    /// what a row write that introduces a bound value composes, so validation and every derived rebuild read a
    /// resolved holder.</summary>
    /// <param name="definition">The candidate.</param>
    /// <param name="refreshed">The refreshed candidate, or <paramref name="definition"/> itself on a refusal.</param>
    /// <param name="reason">Why a value could not be resolved, on failure.</param>
    /// <returns><see langword="true"/> when every value resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryRefresh(WorldDefinition definition, out WorldDefinition refreshed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return TryResolveCandidate(
            definition: definition,
            reason: out reason,
            refreshed: out refreshed,
            soughtRow: null
        );
    }
    private static bool TryResolveCandidate(WorldDefinition definition, string? soughtRow, out WorldDefinition refreshed, out string reason) {
        if (!TryRewrite(
            graph: definition,
            reason: out reason,
            resolver: new Resolver(arena: null, definition: definition, deferDrawSites: false, detach: false, root: "definition", soughtRow: soughtRow, time: null),
            rewritten: out refreshed
        )) {
            return false;
        }

        CarryReferencedRows(source: definition, target: refreshed);

        return true;
    }
    /// <summary>Returns <paramref name="definition"/> with every shade program use resolved against its shades table
    /// (<see cref="WorldShadeTable.TryResolve{T}"/>) and every document value resolved against its own state cells.</summary>
    /// <param name="definition">The document to resolve.</param>
    /// <param name="resolved">The resolved document: <paramref name="definition"/> itself when it carries no reference,
    /// or on a refusal.</param>
    /// <param name="reason">Why a reference could not be resolved, on failure.</param>
    /// <param name="deferDrawSites">Whether a reference into a draw site that has not filled yet is left unresolved
    /// for a later pass — legitimate only on the loader's first parse, which runs the draw resolver and resolves
    /// again; every other door refuses such a reference by name, since nothing after it would ever fill the
    /// site.</param>
    /// <returns><see langword="true"/> when every reference resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryResolve(WorldDefinition definition, out WorldDefinition resolved, out string reason, bool deferDrawSites = false) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!WorldShadeTable.TryResolve(source: definition, graph: definition, resolved: out var shaded, reason: out reason)) {
            resolved = definition;

            return false;
        }
        if (!TryRewrite(
            graph: shaded,
            reason: out reason,
            resolver: new Resolver(arena: null, definition: shaded, deferDrawSites: deferDrawSites, detach: false, root: "definition", soughtRow: null, time: null),
            rewritten: out resolved
        )) {
            return false;
        }

        CarryReferencedRows(source: definition, target: resolved);

        return true;
    }
    /// <summary>
    /// Returns <paramref name="graph"/> with every document-value reference resolved against
    /// <paramref name="source"/>'s state cells, each reference retained for canonical write-back — the resolve-only
    /// sibling of <see cref="TryFlatten{T}"/>, for a row a mutation submits before it joins a definition: the compose
    /// boundary resolves the submitted row against the current document's state so a validator or canonicalizer
    /// reading a bound value ahead of the whole-candidate resolution reads the proposed cell rather than throwing on
    /// an unresolved holder.
    /// </summary>
    /// <remarks>The rewrite leaves <paramref name="graph"/> as it is, so a submitted row that shares holders with the
    /// installed document can never carry a rejected candidate's state into the live world.</remarks>
    /// <typeparam name="T">The graph's type.</typeparam>
    /// <param name="source">The document whose state answers the references.</param>
    /// <param name="graph">The graph to resolve.</param>
    /// <param name="resolved">The resolved graph, or <paramref name="graph"/> itself on a refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when every reference resolved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="graph"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryResolveGraph<T>(WorldDefinition source, T graph, out T resolved, out string reason) where T : class {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: source);

        if (!WorldShadeTable.TryResolve(source: source, graph: graph, resolved: out var shaded, reason: out reason)) {
            resolved = graph;

            return false;
        }

        return TryRewrite(
            graph: shaded,
            reason: out reason,
            resolver: new Resolver(arena: null, definition: source, deferDrawSites: false, detach: false, root: "document", soughtRow: null, time: null),
            rewritten: out resolved
        );
    }
}
