using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>The live store a server's document values read once <see cref="WorldStateDocumentValues.BindLive"/> binds
/// them: the arena the server holds now and the clocks it reads that arena at.</summary>
/// <remarks>One per server, and the binder every value it binds names (<see cref="IDocumentStateValue.Binder"/>). A
/// bound value reads whichever arena its server holds when it is read, so an arena the server replaces needs no new
/// binding, and a definition the server installs again keeps the values it bound before. A value bound to another
/// server is bound again rather than shared.</remarks>
/// <param name="clock">Reads the server's tick pair and dynamics.</param>
public sealed class WorldLiveValues(Func<ArenaTime> clock) {
    /// <summary>Gets or sets the arena the server holds now, or <see langword="null"/> before it builds one.</summary>
    public StateArena? Arena { get; set; }
    /// <summary>Gets the tick pair and dynamics the server reads its arena at now.</summary>
    public ArenaTime Time => clock();

    // The values the last bind left holding no reference it did not bind: a later bind keeps each whole without
    // entering it, so binding a definition composed over the last projection walks only what the composition replaced.
    // Sound because a document value never changes once built (records with init members, immutable holders, lists no
    // composition mutates in place).
    internal HashSet<object> Bound { get; set; } = new(comparer: ReferenceEqualityComparer.Instance);
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
/// <see cref="ReferencesRow"/>, <see cref="CollectReferencedRows(WorldDefinition, ISet{string})"/>) are visitors of the
/// same walk. Both enter only branches that can hold a bound value and never enter a holder's own members.</para>
/// </remarks>
public static class WorldStateDocumentValues {
    private static readonly WorldDocumentWalker Walker = new(isSurface: static type => typeof(IDocumentStateValue).IsAssignableFrom(c: type));
    // The rows each definition's document values name, collected once per definition. A rewrite can drop a reference
    // (a flattening) but never add one, so a set collected earlier is never missing a row the definition names.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldDefinition, HashSet<string>> ReferencedRowSets = new();

    /// <summary>Returns the server's own projection of a definition, whose state-bound values read the live arena
    /// through <paramref name="live"/>. Seed rows remain declarations.</summary>
    /// <remarks>A value already bound to <paramref name="live"/> is kept, so a candidate composed over the projection
    /// a server installed comes back holding the same rows; any other referenced value is replaced by a copy bound to
    /// <paramref name="live"/> on a copied path. A row or section the previous bind left fully bound is kept without
    /// being entered, so the walk costs what the composition replaced. The state section is never rewritten, so the
    /// state catalog the arena was built over carries across. The projection is always a new definition object, so a
    /// definition another server or a cache holds is never the one this server's arena is bound to
    /// (<see cref="WorldStateReader.Bind"/>).</remarks>
    /// <param name="definition">The definition to project.</param>
    /// <param name="live">The server's live store.</param>
    /// <returns>The projection.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A reference names no row of the live arena.</exception>
    public static WorldDefinition BindLive(WorldDefinition definition, WorldLiveValues live) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: live);

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

            live.Bound = binder.Kept;
        }

        var owned = (ReferenceEquals(objA: bound, objB: definition)
            ? (definition with { })
            : bound);

        CarryReferencedRows(source: definition, target: owned);

        return owned;
    }

    private sealed class LiveValue(WorldLiveValues live, string row, CellName name) {
        private Memo? m_memo;

        public string? Read() {
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
                    CellKind.Fixed => FixedQ4816.FromRawBits(value: value.AsFixed).ToString(),
                    CellKind.Bool => value.AsBool ? "1" : "0",
                    CellKind.Int => value.AsInt.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                    _ => null,
                };
            }

            // One reference swapped whole, so a reader on another thread sees an arena, a generation and its text together.
            Volatile.Write(location: ref m_memo, value: new Memo(Arena: arena, Generation: generation, Ordinal: ordinal, Text: text, Tick: tick));

            return text;
        }

        private sealed record Memo(StateArena Arena, int Ordinal, ulong Generation, ulong Tick, string? Text);
    }
    // The diagnostic path is formatted only for a refusal.
    private static string Refusal(string root, WorldDocumentSite site, string reference, string message) {
        var path = site.Path;

        return $"{((path.Length == 0) ? root : $"{root}.{path}")} reference '{reference}' {message}";
    }

    // Binds every referenced value not already bound to the server's store, keeping whole each value the last bind
    // left fully bound and recording, for the next, each value this one leaves so. The state section is kept whole:
    // its lattice origins stay the resolved seeds the catalog was compiled beside.
    private sealed class Binder(WorldLiveValues live) : IWorldDocumentRewriter {
        public HashSet<object> Kept { get; } = new(comparer: ReferenceEqualityComparer.Instance);
        public string Reason { get; private set; } = string.Empty;
        public bool Stopped { get; private set; }

        public bool Enters(object value, WorldDocumentSite site) {
            if (value is WorldStateSection) {
                return false;
            }
            if (value.GetType().IsValueType || !live.Bound.Contains(item: value)) {
                return true;
            }

            _ = Kept.Add(item: value);

            return false;
        }
        public void Left(object value, object rewritten) {
            if (!rewritten.GetType().IsValueType) {
                _ = Kept.Add(item: rewritten);
            }
        }
        public object Replace(object value, WorldDocumentSite site) {
            var holder = (IDocumentStateValue)value;

            if (
                (holder.Reference is not { } reference) ||
                ReferenceEquals(objA: holder.Binder, objB: live)
            ) {
                return value;
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

            return holder.Bound(binder: live, read: new LiveValue(live: live, name: name, row: row).Read);
        }
    }
    // Resolves every referenced value (or every one naming a sought row) against a document's state cells, or the live
    // arena when one is given, into a copy that keeps its reference or, flattening, drops it.
    private sealed class Resolver(WorldDefinition definition, string root, string? soughtRow, bool deferDrawSites, bool detach, StateArena? arena, ArenaTime? time) : IWorldDocumentRewriter {
        public string Reason { get; private set; } = string.Empty;
        public bool Stopped { get; private set; }

        public object Replace(object value, WorldDocumentSite site) {
            var holder = (IDocumentStateValue)value;

            if (holder.Reference is not { } reference) {
                return value;
            }
            if (StateBinding.Parse(token: reference) is not (var row, var key, _)) {
                return Refuse(message: "must be state.<row>[.<key>]", reference: reference, site: site, value: value);
            }
            if (
                (soughtRow is not null) &&
                !string.Equals(a: row, b: soughtRow, comparisonType: StringComparison.Ordinal)
            ) {
                return value;
            }
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
                return Refuse(message: "must name a declared state cell", reference: reference, site: site, value: value);
            }

            // A numeric cell is offered as its decimal spelling — the same text the document would carry literally
            // — so a scalar binds an int or fixed cell (a drawn or advancing one included) and a vector still
            // refuses it by expectation.
            // A draw site fills at boot, after this walk runs at parse: a reference to it stays unresolved and
            // resolves on the post-draw pass (WorldDefinitionLoader), so an empty drawn cell is deferred, not refused.
            if (
                deferDrawSites &&
                (stateRow.Draw is not null) &&
                (rawValue is null) &&
                (text is null)
            ) {
                return value;
            }
            if (stateRow.Kind == CellKind.Vector) {
                return Refuse(message: $"names vector row '{stateRow.Name}'; vector rows cannot be resolved as document values", reference: reference, site: site, value: value);
            }
            if (stateRow.Kind != CellKind.Text) {
                if (rawValue is not { } raw) {
                    return Refuse(message: "names a cell that holds no value", reference: reference, site: site, value: value);
                }

                text = stateRow.Kind switch {
                    CellKind.Fixed => FixedQ4816.FromRawBits(value: raw).ToString(),
                    CellKind.Bool => ((raw != 0L)
                    ? "true"
                    : "false"),
                    CellKind.Int => raw.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                    _ => raw.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                };
            }
            if (text is null) {
                return Refuse(message: "names a cell that holds no value", reference: reference, site: site, value: value);
            }
            if (!holder.TryResolved(
                detach: detach,
                reason: out var parseReason,
                resolved: out var resolved,
                text: text
            )) {
                return Refuse(message: $"must hold {holder.ExpectedValue}: {parseReason}", reference: reference, site: site, value: value);
            }

            WorldBootWork.Count(kind: WorldBootWork.DocumentValueCopies);

            return resolved;
        }

        private object Refuse(string message, string reference, WorldDocumentSite site, object value) {
            Reason = Refusal(message: message, reference: reference, root: root, site: site);
            Stopped = true;

            return value;
        }
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

    /// <summary>Adds the name of every state row a retained document-value reference in <paramref name="definition"/>
    /// names to <paramref name="rows"/>, in one walk.</summary>
    /// <remarks>A cell write carries a value, never a reference, and references live in the sections around the
    /// state table, so a batch of cell writes collects this set once and asks each member's row against it, where
    /// <see cref="ReferencesRow(WorldDefinition, object, string)"/> over the whole definition would walk the graph per member. A member that
    /// re-declares a row or edits any other section is where the set can change; the caller collects again after
    /// one.</remarks>
    /// <param name="definition">The document to walk.</param>
    /// <param name="rows">The set every referenced row name is added to; existing members are kept.</param>
    public static void CollectReferencedRows(WorldDefinition definition, ISet<string> rows) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        CollectReferencedRows(
            graph: definition,
            rows: rows
        );
    }
    /// <summary>Gets the name of every state row a retained document-value reference in
    /// <paramref name="definition"/> names, collected by one walk the first time a definition asks and kept for its
    /// lifetime.</summary>
    /// <param name="definition">The document whose references are read.</param>
    /// <returns>The referenced row names; the caller never modifies the set.</returns>
    public static IReadOnlySet<string> ReferencedRows(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ReferencedRowSets.GetValue(
            createValueCallback: static candidate => {
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
    /// <summary>Reports whether <paramref name="graph"/> retains any document-value reference at all.</summary>
    /// <param name="graph">The object graph to walk — a definition, or an egress document composed over one.</param>
    /// <returns><see langword="true"/> when at least one bound value is present.</returns>
    public static bool HasReference(object graph) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        return Find(graph: graph, soughtRow: null);
    }
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
    /// <summary>Returns <paramref name="definition"/> with every document value resolved against its own state cells.</summary>
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

        if (!TryRewrite(
            graph: definition,
            reason: out reason,
            resolver: new Resolver(arena: null, definition: definition, deferDrawSites: deferDrawSites, detach: false, root: "definition", soughtRow: null, time: null),
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

        return TryRewrite(
            graph: graph,
            reason: out reason,
            resolver: new Resolver(arena: null, definition: source, deferDrawSites: false, detach: false, root: "document", soughtRow: null, time: null),
            rewritten: out resolved
        );
    }
}
