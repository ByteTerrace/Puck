using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>A world's shade programs, each held once under its computed identity (<see cref="ShadeReference.KeyOf"/>),
/// and the one resolution of every use a document makes of them (a palette row's <c>shade</c>, a look case's, the
/// world's <c>render.shade</c>).</summary>
/// <remarks>
/// <para>The table is derived, never a second source of truth: what a definition writes as <c>shades</c>
/// (<see cref="WorldDefinition.ShadesRaw"/>) is, for every use it holds, the use's key and program, so a row no use
/// names is never written and an edit that adds a use (a mutation carrying a whole program) carries its program into
/// the next serialization. A use the table does not hold dangles and is refused at resolution.</para>
/// <para>Every door that turns bytes into a live world resolves it through <see cref="WorldStateDocumentValues.TryResolve"/>,
/// which runs <see cref="TryResolve{T}"/> first, so a use reaches a consumer holding its program
/// (<see cref="ShadeReference.Definition"/>, which throws on a use no door resolved). A row's key must be its
/// program's identity, so a table cannot hold one program under another's name, and a creation's canonical hash,
/// which covers its uses' keys, covers what they shade with.</para>
/// </remarks>
public static class WorldShadeTable {
    private static readonly WorldDocumentWalker Walker = new(surfaces: [typeof(ShadeReference)]);
    // The table a definition writes, once per definition: a definition never changes once built.
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldDefinition, Box> Tables = new();

    private sealed record Box(IReadOnlyDictionary<string, SdfShadeDefinition>? Table);

    /// <summary>Returns the table a definition writes: every program its uses name, under its key, in ordinal key
    /// order; a use's own program where it holds one, else the row its document's table carried.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The table, or <see langword="null"/> when the definition uses no program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyDictionary<string, SdfShadeDefinition>? Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Tables.GetValue(key: definition, derive: static definition => new Box(Table: Derive(graph: definition, stored: definition.StoredShades))).Table;
    }
    /// <summary>Returns every use in <paramref name="graph"/> holding its program from <paramref name="source"/>'s table.</summary>
    /// <typeparam name="T">The graph's type: a whole definition, or a row a mutation submits before it joins one.</typeparam>
    /// <param name="source">The definition whose table answers the uses; <paramref name="graph"/> itself for a whole
    /// definition.</param>
    /// <param name="graph">The graph to resolve; left as it is.</param>
    /// <param name="resolved">The graph with every use resolved, sharing every path no unresolved use lies on, or
    /// <paramref name="graph"/> itself on a refusal.</param>
    /// <param name="reason">The refusal, naming the use's path and key, or empty.</param>
    /// <returns><see langword="false"/> when a use names a program the table does not hold, or a row holds a program
    /// under a key that is not its identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="graph"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryResolve<T>(WorldDefinition source, T graph, out T resolved, out string reason) where T : class {
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: graph);

        var resolver = new Resolver(table: (Of(definition: source) ?? Empty));

        resolved = Walker.Rewrite(root: graph, rewriter: resolver);
        reason = resolver.Reason;

        return !resolver.Stopped;
    }

    private static readonly IReadOnlyDictionary<string, SdfShadeDefinition> Empty = new Dictionary<string, SdfShadeDefinition>();

    private static IReadOnlyDictionary<string, SdfShadeDefinition>? Derive(object graph, IReadOnlyDictionary<string, SdfShadeDefinition>? stored) {
        var collector = new Collector(stored: stored);

        Walker.Walk(root: graph, visitor: collector);

        return ((collector.Table.Count == 0) ? null : collector.Table);
    }

    // Collects each use's key and program; a held program wins over the stored row, which a use only named reads.
    private sealed class Collector(IReadOnlyDictionary<string, SdfShadeDefinition>? stored) : IWorldDocumentVisitor {
        public SortedDictionary<string, SdfShadeDefinition> Table { get; } = new(comparer: StringComparer.Ordinal);

        public bool Enter(object value, WorldDocumentSite site) {
            if (value is not ShadeReference use) {
                return true;
            }
            if (use.IsResolved) {
                Table[use.Key] = use.Definition;
            } else if (!Table.ContainsKey(key: use.Key) && (stored?.TryGetValue(key: use.Key, value: out var row) == true)) {
                Table[use.Key] = row;
            }

            return false;
        }
    }
    private sealed class Resolver(IReadOnlyDictionary<string, SdfShadeDefinition> table) : IWorldDocumentRewriter {
        public string Reason { get; private set; } = string.Empty;
        public bool Stopped { get; private set; }

        public object Replace(object value, WorldDocumentSite site) {
            var use = (ShadeReference)value;

            if (use.IsResolved) {
                return value;
            }
            if (!table.TryGetValue(key: use.Key, value: out var program)) {
                return Refuse(message: $"{site.Path} names shade program '{use.Key}', which the world's shades table does not hold", value: value);
            }
            if (!use.TryResolve(definition: program, resolved: out var resolved)) {
                return Refuse(message: $"the shades table holds another program under '{use.Key}', which {site.Path} names; a row's key is its program's identity", value: value);
            }

            return resolved;
        }

        private object Refuse(string message, object value) {
            Reason = message;
            Stopped = true;

            return value;
        }
    }
}
