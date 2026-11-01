using System.Text.Json;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>Resolves state-backed values embedded anywhere in a world document against its Text state cells.</summary>
/// <remarks>
/// The walk deliberately operates at the completed-document boundary: an embedded creation is a document family in
/// its own right and cannot know the world state that contains it while it is being deserialized. Literal values are
/// untouched. Bound values retain their reference token, so serialization writes the reference back and a later
/// mutation of that state row can re-resolve a fresh candidate without mutating the live document's value objects.
/// <para>Every door that turns bytes into a live document runs <see cref="TryResolve"/>, so a delivered document is
/// indistinguishable from a file-loaded one. A document leaving for a boundary that does not carry the state table
/// itself runs <see cref="TryFlatten"/> instead, since a reference the receiver cannot answer is a dangling
/// pointer.</para>
/// <para>Each operation is a visitor of the one document walk (<see cref="WorldDocumentWalker"/>) whose surface is
/// <see cref="IDocumentStateValue"/>: the walk enters only branches that can hold a bound value, reads document contents
/// afresh (polymorphic and mutable collections included), and never enters a value holder's own members.</para>
/// </remarks>
public static class WorldStateDocumentValues {
    private static readonly WorldDocumentWalker Walker = new(isSurface: static type => typeof(IDocumentStateValue).IsAssignableFrom(c: type));
    // The rows each definition's document values name, collected once per definition. A value holder can drop its
    // reference (Detach) but never gain one, so a set collected earlier is never missing a row the definition names.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldDefinition, HashSet<string>> ReferencedRowSets = new();

    /// <summary>Creates an owned projection whose bound values read the live arena. Seed rows remain declarations.</summary>
    public static WorldDefinition BindLive(WorldDefinition definition, StateArena arena, Func<ArenaTime> clock) {
        if (ReferencedRows(definition: definition).Count == 0) { return definition with { }; }
        var owned = (JsonSerializer.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition),
            jsonTypeInfo: WorldJsonContext.Default.WorldDefinition) ?? throw new InvalidOperationException(message: "The binding projection did not deserialize."))
            with { StateRaw = definition.StateRaw, DocumentDirectory = definition.DocumentDirectory };

        if (!Run(
            definition: owned,
            live: new LiveBindings(arena, clock),
            reason: out var reason,
            root: "definition",
            value: owned,
            walk: Walk.Bind
        )) {
            throw new InvalidOperationException(message: reason);
        }

        return owned;
    }

    // What the one walk does when it reaches a bound value.
    private enum Walk {
        // Read the referenced cell and fill the value, leaving the reference attached for canonical write-back.
        Resolve,
        // Report whether a reference is present (any reference, or one naming a sought row), touching nothing.
        Find,
        // Add every referenced row's name to a set, touching nothing and never stopping early: the one walk a
        // batch of state writes runs so each member asks a set membership instead of walking the graph again.
        Collect,
        // Resolve, then drop the reference: the flattening an egress document performs so a receiver that was never
        // handed the state table holds a literal rather than a dangling pointer.
        Flatten,
        Bind,
    }

    private sealed record LiveBindings(StateArena Arena, Func<ArenaTime> Clock);
    private sealed class LiveValue(StateArena arena, StateHandle row, CellName name, Func<ArenaTime> clock) {
        private ulong m_generation = ulong.MaxValue;
        private ulong m_tick = ulong.MaxValue;
        private string? m_text;
        public string? Read() {
            var time = clock();
            var generation = arena.RowGeneration(rowOrdinal: row.Ordinal);
            var tick = arena.Layout[row.Ordinal].HasTraits ? time.EngineTick : 0UL;
            if ((m_generation == generation) && (m_tick == tick)) { return m_text; }
            m_generation = generation; m_tick = tick; m_text = null;
            if (!arena.Keys.TryResolve(name: name, key: out var key) || !arena.TryReadLive(rowOrdinal: row.Ordinal, key: key, time: in time, value: out var value)) { return null; }
            m_text = value.Kind switch {
                CellKind.Text => value.AsText,
                CellKind.Fixed => FixedQ4816.FromRawBits(value: value.AsFixed).ToString(),
                CellKind.Bool => value.AsBool ? "1" : "0",
                CellKind.Int => value.AsInt.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            return m_text;
        }
    }
    // Reads or fills each document-value holder the walk reaches, according to the operation; the walk never enters
    // a holder's members, and stops at the first refusal or, for a search, the first hit.
    private sealed class Visitor(
        Walk walk,
        WorldDefinition? definition,
        string root,
        string? soughtRow,
        ISet<string>? collected,
        bool deferDrawSites,
        LiveBindings? live
    ) : IWorldDocumentVisitor {
        private bool m_found;
        private bool m_refused;

        public bool Found => m_found;
        public string Reason { get; private set; } = string.Empty;
        public bool Refused => m_refused;
        public bool Stopped => (m_refused || m_found);

        public bool Enter(object value, WorldDocumentSite site) {
            if (value is IDocumentStateValue stateValue) {
                Read(
                    site: site,
                    stateValue: stateValue
                );

                return false;
            }

            return true;
        }

        // The diagnostic path is formatted only for a refusal; a search or a collection never builds one.
        private string Refusal(WorldDocumentSite site, string reference, string message) {
            var path = site.Path;

            return $"{((path.Length == 0) ? root : $"{root}.{path}")} reference '{reference}' {message}";
        }
        private void Read(WorldDocumentSite site, IDocumentStateValue stateValue) {
            if (stateValue.Reference is not { } reference) {
                return;
            }

            if (StateBinding.Parse(token: reference) is not (var row, var key, _)) {
                // A reference that names no row is still a reference the document retains: a search for any reference
                // finds it, so its holder is resolved and refused by name rather than read as though it held a value.
                if (walk == Walk.Find) {
                    m_found = (soughtRow is null);

                    return;
                }

                Refuse(reason: Refusal(message: "must be state.<row>[.<key>]", reference: reference, site: site));

                return;
            }

            if (walk == Walk.Find) {
                m_found = ((soughtRow is null) || string.Equals(
                    a: row,
                    b: soughtRow,
                    comparisonType: StringComparison.Ordinal
                ));

                return;
            }

            if (walk == Walk.Collect) {
                _ = collected!.Add(item: row);

                return;
            }

            if (walk == Walk.Bind) {
                var name = default(CellName);
                var handle = default(StateHandle);
                var nameReason = string.Empty;

                if (
                    (live is null) ||
                    !live.Arena.Catalog.TryResolve(lane: StateLane.Document, name: row, handle: out handle) ||
                    !CellName.TryParse(candidate: (key ?? StateRow.SlotKey.Value), name: out name, reason: out nameReason)
                ) {
                    Refuse(reason: Refusal(message: $"names no live document row ({nameReason})", reference: reference, site: site));

                    return;
                }

                stateValue.Bind(read: new LiveValue(live!.Arena, handle, name, live.Clock).Read);

                return;
            }

            if (!WorldStateReader.TryRead(
                arena: live?.Arena,
                definition: definition!,
                engineTick: live?.Clock().EngineTick ?? 0UL,
                key: key,
                rawValue: out var rawValue,
                row: out var stateRow,
                rowName: row,
                text: out var text,
                tick: live?.Clock().Tick ?? 0UL
            )) {
                Refuse(reason: Refusal(message: "must name a declared state cell", reference: reference, site: site));

                return;
            }

            // A numeric cell is offered as its decimal spelling — the same text the document would carry literally
            // — so a scalar binds an int or fixed cell (a drawn or advancing one included) and a vector still
            // refuses it by expectation.
            // A draw site fills at boot, after this walk runs at parse: a reference to it stays attached and
            // resolves on the post-draw pass (WorldDefinitionLoader), so an empty drawn cell is deferred, not refused.
            if (
                deferDrawSites &&
                (stateRow.Draw is not null) &&
                (rawValue is null) &&
                (text is null)
            ) {
                return;
            }
            if (stateRow.Kind == CellKind.Vector) {
                Refuse(reason: Refusal(message: $"names vector row '{stateRow.Name}'; vector rows cannot be resolved as document values", reference: reference, site: site));

                return;
            }
            if (stateRow.Kind != CellKind.Text) {
                if (rawValue is not { } raw) {
                    Refuse(reason: Refusal(message: "names a cell that holds no value", reference: reference, site: site));

                    return;
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
                Refuse(reason: Refusal(message: "names a cell that holds no value", reference: reference, site: site));

                return;
            }

            if (!stateValue.TryResolve(
                reason: out var parseReason,
                text: text
            )) {
                Refuse(reason: Refusal(message: $"must hold {stateValue.ExpectedValue}: {parseReason}", reference: reference, site: site));

                return;
            }

            if (walk == Walk.Flatten) {
                stateValue.Detach();
            }
        }
        private void Refuse(string reason) {
            Reason = reason;
            m_refused = true;
        }
    }

    // Runs one operation over a value; false when a reference was refused, with the named reason.
    private static bool Run(
        object value,
        Walk walk,
        string root,
        out string reason,
        WorldDefinition? definition = null,
        string? soughtRow = null,
        ISet<string>? collected = null,
        bool deferDrawSites = false,
        LiveBindings? live = null
    ) => Run(
        collected: collected,
        definition: definition,
        deferDrawSites: deferDrawSites,
        found: out _,
        live: live,
        reason: out reason,
        root: root,
        soughtRow: soughtRow,
        value: value,
        walk: walk
    );
    private static bool Run(
        object value,
        Walk walk,
        string root,
        out bool found,
        out string reason,
        WorldDefinition? definition = null,
        string? soughtRow = null,
        ISet<string>? collected = null,
        bool deferDrawSites = false,
        LiveBindings? live = null
    ) {
        var visitor = new Visitor(
            collected: collected,
            definition: definition,
            deferDrawSites: deferDrawSites,
            live: live,
            root: root,
            soughtRow: soughtRow,
            walk: walk
        );

        Walker.Walk(
            root: value,
            visitor: visitor
        );
        found = visitor.Found;
        reason = visitor.Reason;

        return !visitor.Refused;
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
    // A state row holds literal cells and never a document value, so a definition that differs from another only in
    // its state section names the same rows, and the set already collected for one serves the other.
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

        _ = Run(
            collected: rows,
            reason: out _,
            root: "definition",
            value: graph,
            walk: Walk.Collect
        );
    }
    /// <summary>Reports whether <paramref name="graph"/> retains any document-value reference at all.</summary>
    /// <param name="graph">The object graph to walk — a definition, or an egress document composed over one.</param>
    /// <returns><see langword="true"/> when at least one bound value is present.</returns>
    public static bool HasReference(object graph) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        _ = Run(
            found: out var found,
            reason: out _,
            root: "document",
            value: graph,
            walk: Walk.Find
        );

        return found;
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

        _ = Run(
            definition: definition,
            found: out var found,
            reason: out _,
            root: "definition",
            soughtRow: rowName,
            value: graph,
            walk: Walk.Find
        );

        return found;
    }
    /// <summary>
    /// Resolves every document-value reference in <paramref name="graph"/> against <paramref name="source"/>'s Text
    /// state cells and then drops the reference, so the result carries literals only.
    /// </summary>
    /// <remarks>
    /// The caller owns <paramref name="graph"/> exclusively: flattening mutates the value holders it reaches, and
    /// the authored reference a live document keeps for canonical write-back would be lost. An egress composer
    /// rehydrates a private copy first.
    /// </remarks>
    /// <param name="source">The document whose state answers the references.</param>
    /// <param name="arena">The live store, or null at a seed boundary.</param>
    /// <param name="time">The live clock coordinate.</param>
    /// <param name="graph">The exclusively-owned graph to flatten.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when every reference resolved and detached.</returns>
    public static bool TryFlatten(WorldDefinition source, object graph, out string reason, StateArena? arena = null, ArenaTime? time = null) {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: source);

        return Run(
            definition: source,
            live: (arena is null)
                ? null
                : new LiveBindings(arena, () => time ?? ArenaTime.Origin),
            reason: out reason,
            root: "document",
            value: graph,
            walk: Walk.Flatten
        );
    }
    /// <summary>
    /// Rehydrates and resolves a fresh candidate after a referenced state row changes, retaining the input object
    /// unchanged when the row has no consumers: <see cref="ReferencedRows"/> decides, and
    /// <see cref="TryRehydrate"/> does the work.
    /// </summary>
    public static bool TryRefresh(WorldDefinition definition, string rowName, out WorldDefinition refreshed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);

        if (!ReferencedRows(definition: definition).Contains(item: rowName)) {
            refreshed = definition;
            reason = string.Empty;
            return true;
        }

        return TryRehydrate(
            definition: definition,
            reason: out reason,
            refreshed: out refreshed
        );
    }
    /// <summary>
    /// Rehydrates <paramref name="definition"/> through its own serialization and resolves every retained
    /// document-value reference in the copy against the copy's state cells. Rehydration is intentional: record
    /// <c>with</c> composition shares unchanged embedded documents with the live definition, so resolving the
    /// candidate's mutable value holders in place would leak a rejected mutation into the live world.
    /// </summary>
    /// <param name="definition">The candidate whose references read a changed row.</param>
    /// <param name="refreshed">The resolved copy, or <paramref name="definition"/> itself on failure.</param>
    /// <param name="reason">Why the copy could not be parsed or resolved, on failure.</param>
    /// <returns><see langword="true"/> when every reference in the copy resolved.</returns>
    public static bool TryRehydrate(WorldDefinition definition, out WorldDefinition refreshed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        WorldBootWork.Count(kind: WorldBootWork.Parses);

        try {
            refreshed = ((JsonSerializer.Deserialize(
                utf8Json: WorldDefinitionSerialization.Serialize(definition: definition),
                jsonTypeInfo: WorldJsonContext.Default.WorldDefinition
            ) ?? throw new InvalidOperationException(message: "the refreshed world definition deserialized to null.")) with { DocumentDirectory = definition.DocumentDirectory });
        } catch (Exception exception) when (WorldJsonPayload.IsParseFailure(exception: exception)) {
            refreshed = definition;
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }

        if (!TryResolve(
            definition: refreshed,
            reason: out reason
        )) {
            refreshed = definition;
            return false;
        }

        return true;
    }
    /// <summary>Resolves every document-value reference in <paramref name="definition"/> in place.</summary>
    /// <param name="definition">The document to resolve.</param>
    /// <param name="reason">Why a reference could not be resolved, on failure.</param>
    /// <param name="deferDrawSites">Whether a reference into a draw site that has not filled yet is left attached
    /// for a later pass — legitimate only on the loader's first parse, which runs the draw resolver and resolves
    /// again; every other door refuses such a reference by name, since nothing after it would ever fill the
    /// site.</param>
    public static bool TryResolve(WorldDefinition definition, out string reason, bool deferDrawSites = false) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Run(
            definition: definition,
            deferDrawSites: deferDrawSites,
            reason: out reason,
            root: "definition",
            value: definition,
            walk: Walk.Resolve
        );
    }
    /// <summary>
    /// Resolves every document-value reference in <paramref name="graph"/> against <paramref name="source"/>'s
    /// state cells in place, retaining each reference for canonical write-back — the resolve-only sibling of
    /// <see cref="TryFlatten"/>, for a row a mutation submits before it joins a definition: the compose boundary
    /// resolves the submitted row against the current document's state so a validator or canonicalizer reading a
    /// bound value ahead of the whole-candidate rehydration reads the live cell rather than throwing on an
    /// unresolved holder.
    /// </summary>
    /// <remarks>The caller owns <paramref name="graph"/> exclusively, for the reason <see cref="TryRehydrate"/> copies
    /// a whole definition: a submitted row can share value holders with the installed document, and resolving those
    /// in place against a candidate that is then rejected would leak that candidate's state into the live
    /// world.</remarks>
    /// <param name="source">The document whose state answers the references.</param>
    /// <param name="graph">The exclusively-owned graph to resolve.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when every reference resolved.</returns>
    public static bool TryResolveGraph(WorldDefinition source, object graph, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: source);

        return Run(
            definition: source,
            reason: out reason,
            root: "document",
            value: graph,
            walk: Walk.Resolve
        );
    }
}
