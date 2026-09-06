using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <para>Traversal metadata is shared by runtime type. Branches whose sealed types cannot carry a bound value
/// are omitted; document contents are always read afresh, including polymorphic and mutable collections.</para>
/// </remarks>
public static class WorldStateDocumentValues {
    private const int MaxShapeDepth = 64;
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
    private static readonly ConcurrentDictionary<Type, Traversal> TraversalCache = new();

    private sealed record Traversal(bool Skip, PropertyInfo[] Properties);

    // What the one walk does when it reaches a bound value.
    private enum Walk {
        // Read the referenced cell and fill the value, leaving the reference attached for canonical write-back.
        Resolve,
        // Report whether a reference is present (any reference, or one naming a sought row), touching nothing.
        Find,
        // Resolve, then drop the reference: the flattening an egress document performs so a receiver that was never
        // handed the state table holds a literal rather than a dangling pointer.
        Flatten,
    }

    // Whether a property is DERIVED rather than document data, and so no part of what a document-value reference can
    // be retained in.
    //
    // An unconditional [JsonIgnore] is exactly that marker: the member is computed from the document, never written
    // to it and never read back from it. Visiting one is not merely redundant work over data the walk already covers
    // through the member it derives from — it can be an outright fault, because a derived member is free to refuse a
    // read that has no meaning. WorldDefinition.PopulationReconnectGraceTicks is the live case: at rate 0 a positive
    // authored grace compiles to CompiledTickDuration.Never, whose Ticks throws BY DESIGN so no caller can read a
    // plausible-but-wrong tick count out of it. Walking it made every rate-0 world with a positive grace fail to
    // construct at all.
    //
    // A CONDITIONAL ignore is not this marker and must still be visited: the raw backing members that carry a
    // document's own absent-vs-present distinction are written [JsonIgnore(Condition = WhenWritingNull)], and they
    // are the document.
    private static bool IsDerived(PropertyInfo property) => (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always });
    private static bool IsLeaf(Type type) =>
        (type.IsPrimitive ||
        type.IsEnum ||
        type.IsPointer ||
        (type == typeof(string)) ||
        (type == typeof(decimal)) ||
        (type == typeof(DateTime)) ||
        (type == typeof(DateTimeOffset)) ||
        (type == typeof(Guid)) ||
        (type == typeof(JsonElement)));
    private static PropertyInfo[] Properties(Type type) => PropertyCache.GetOrAdd(key: type, valueFactory: static type =>
        [.. type.GetProperties(bindingAttr: BindingFlags.Instance | BindingFlags.Public)
            .Where(predicate: static property => (property.CanRead && (property.GetIndexParameters().Length == 0) && !IsDerived(property: property)))]);

    // Cache type shapes, never the contents of a document: mutable collections and value holders may acquire a
    // reference between walks. A sealed property's shape can prove an entire literal branch irrelevant. Open
    // types and recursive shapes stay conservative, since a subtype or a later link may contain a bound value.
    private static bool CanContainValue(Type type, HashSet<Type> path, bool exactType = false) {
        if (typeof(IDocumentStateValue).IsAssignableFrom(c: type)) {
            return true;
        }
        if (IsLeaf(type: type)) {
            return false;
        }
        if (!exactType && !type.IsSealed && !type.IsValueType) {
            return true;
        }
        // Recursive generic properties can expand into a new closed type at every level, so type identity alone
        // does not bound shape discovery. Beyond this depth keep walking actual values conservatively.
        if ((path.Count >= MaxShapeDepth) || !path.Add(item: type)) {
            return true;
        }

        try {
            if (type.IsArray) {
                return CanContainValue(type: type.GetElementType()!, path: path);
            }
            if (typeof(IEnumerable).IsAssignableFrom(c: type)) {
                // These exact BCL implementations enumerate their declared element types through IEnumerable.
                // An arbitrary generic enumerable may implement its non-generic enumeration differently.
                if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>))) {
                    return CanContainValue(type: type.GenericTypeArguments[0], path: path);
                }
                if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>))) {
                    return CanContainValue(type: type.GenericTypeArguments[0], path: path) ||
                        CanContainValue(type: type.GenericTypeArguments[1], path: path);
                }
                return true;
            }
            return Properties(type: type).Any(predicate: property => CanContainValue(type: property.PropertyType, path: path));
        } finally {
            path.Remove(item: type);
        }
    }

    private static Traversal Plan(Type type) => TraversalCache.GetOrAdd(key: type, valueFactory: static type => {
        if (!CanContainValue(type: type, path: [], exactType: true)) {
            return new Traversal(Skip: true, Properties: []);
        }
        return new Traversal(Skip: false, Properties: typeof(IEnumerable).IsAssignableFrom(c: type)
            ? []
            : [.. Properties(type: type).Where(predicate: static property => CanContainValue(type: property.PropertyType, path: []))]);
    });
    private static bool TryVisit(object? value, string path, WorldDefinition definition, Walk walk, string? soughtRow, HashSet<object> seen, bool deferDrawSites, out bool found, out string reason) {
        found = false;
        reason = string.Empty;

        if (value is null) {
            return true;
        }

        if (value is IDocumentStateValue stateValue) {
            if (stateValue.Reference is not { } reference) {
                return true;
            }

            if (!WorldColor.TryParseBinding(
                key: out var key,
                row: out var row,
                value: reference
            )) {
                reason = $"{path} reference '{reference}' must be state.<row>[.<key>]";
                return false;
            }

            if (walk == Walk.Find) {
                found = ((soughtRow is null) || string.Equals(
                    a: row,
                    b: soughtRow,
                    comparisonType: StringComparison.Ordinal
                ));
                return true;
            }

            if (!WorldStateReader.TryRead(
                definition: definition,
                key: key,
                rawValue: out var rawValue,
                row: out var stateRow,
                rowName: row,
                text: out var text,
                tick: 0UL
            )) {
                reason = $"{path} reference '{reference}' must name a declared state cell";
                return false;
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
                return true;
            }
            if (stateRow.Kind != CellKind.Text) {
                if (rawValue is not { } raw) {
                    reason = $"{path} reference '{reference}' names a cell that holds no value";
                    return false;
                }

                text = stateRow.Kind switch {
                    CellKind.Fixed => FixedQ4816.FromRawBits(value: raw).ToString(),
                    CellKind.Bool => ((raw != 0L) ? "true" : "false"),
                    _ => raw.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                };
            }
            if (text is null) {
                reason = $"{path} reference '{reference}' names a cell that holds no value";
                return false;
            }

            if (!stateValue.TryResolve(
                reason: out var parseReason,
                text: text
            )) {
                reason = $"{path} reference '{reference}' must hold {stateValue.ExpectedValue}: {parseReason}";
                return false;
            }

            if (walk == Walk.Flatten) {
                stateValue.Detach();
            }

            return true;
        }

        var type = value.GetType();

        var traversal = Plan(type: type);

        if (traversal.Skip) {
            return true;
        }

        if (
            !type.IsValueType &&
            !seen.Add(item: value)
        ) {
            return true;
        }

        if (value is IEnumerable sequence) {
            var index = 0;

            foreach (var item in sequence) {
                if (!TryVisit(
                    definition: definition,
                    found: out var itemFound,
                    path: walk == Walk.Find ? string.Empty : $"{path}[{index}]",
                    reason: out reason,
                    seen: seen,
                    deferDrawSites: deferDrawSites,
                    soughtRow: soughtRow,
                    value: item,
                    walk: walk
                )) {
                    return false;
                }

                if (itemFound) {
                    found = true;
                    return true;
                }
                index++;
            }
            return true;
        }

        foreach (var property in traversal.Properties) {
            var child = property.GetValue(obj: value);
            if (child is null) {
                continue;
            }
            if (!TryVisit(
                value: child,
                path: walk == Walk.Find ? string.Empty : $"{path}.{char.ToLowerInvariant(c: property.Name[0])}{property.Name[1..]}",
                definition: definition,
                soughtRow: soughtRow,
                seen: seen,
                deferDrawSites: deferDrawSites,
                found: out var propertyFound,
                reason: out reason,
                walk: walk
            )) {
                return false;
            }

            if (propertyFound) {
                found = true;
                return true;
            }
        }

        return true;
    }

    /// <summary>Reports whether <paramref name="graph"/> retains any document-value reference at all.</summary>
    /// <param name="graph">The object graph to walk — a definition, or an egress document composed over one.</param>
    /// <returns><see langword="true"/> when at least one bound value is present.</returns>
    public static bool HasReference(object graph) {
        ArgumentNullException.ThrowIfNull(argument: graph);
        return (
            TryVisit(
            value: graph,
            path: "document",
            definition: null!,
            walk: Walk.Find,
            soughtRow: null,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance),
            deferDrawSites: false,
            found: out var found,
            reason: out _
        ) &&
            found
        );
    }
    /// <summary>Reports whether any retained document-value reference reads <paramref name="rowName"/>.</summary>
    public static bool ReferencesRow(WorldDefinition definition, string rowName) =>
        ReferencesRow(
            definition: definition,
            graph: definition,
            rowName: rowName
        );
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
        return (
            TryVisit(
            value: graph,
            path: "definition",
            definition: definition,
            walk: Walk.Find,
            soughtRow: rowName,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance),
            deferDrawSites: false,
            found: out var found,
            reason: out _
        ) &&
            found
        );
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
    /// <param name="graph">The exclusively-owned graph to flatten.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when every reference resolved and detached.</returns>
    public static bool TryFlatten(WorldDefinition source, object graph, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: graph);
        ArgumentNullException.ThrowIfNull(argument: source);
        return TryVisit(
            value: graph,
            path: "document",
            definition: source,
            walk: Walk.Flatten,
            soughtRow: null,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance),
            deferDrawSites: false,
            found: out _,
            reason: out reason
        );
    }
    /// <summary>
    /// Rehydrates and resolves a fresh candidate after a referenced state row changes, retaining the input object
    /// unchanged when the row has no consumers. Rehydration is intentional: record <c>with</c> composition shares
    /// unchanged embedded documents with the live definition, so resolving the candidate's mutable value holders in
    /// place would leak a rejected mutation into the live world.
    /// </summary>
    public static bool TryRefresh(WorldDefinition definition, string rowName, out WorldDefinition refreshed, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentException.ThrowIfNullOrEmpty(argument: rowName);

        if (!ReferencesRow(
            definition: definition,
            rowName: rowName
        )) {
            refreshed = definition;
            reason = string.Empty;
            return true;
        }

        try {
            refreshed = (JsonSerializer.Deserialize(
                utf8Json: WorldDefinitionSerialization.Serialize(definition: definition),
                jsonTypeInfo: WorldJsonContext.Default.WorldDefinition
            ) ?? throw new InvalidOperationException(message: "the refreshed world definition deserialized to null."));
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
        return TryVisit(
            value: definition,
            path: "definition",
            definition: definition,
            walk: Walk.Resolve,
            soughtRow: null,
            seen: new HashSet<object>(comparer: ReferenceEqualityComparer.Instance),
            deferDrawSites: deferDrawSites,
            found: out _,
            reason: out reason
        );
    }
}
