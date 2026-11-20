using Puck.Abstractions.Documents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;

namespace Puck.World;

/// <summary>The names a document value spells, read through the name registry's grammar
/// (<see cref="WorldModuleNamespace.Respell(JsonNode?, Type, WorldNameRespelling)"/>), so every position the registry
/// lists is read and no field list is kept beside it. The write door's footprint is built from these readings.</summary>
public static class WorldSpelledNames {
    /// <summary>Returns whether a name position names a state row or a declared set of rows: the names a recipient's
    /// write door admits by policy.</summary>
    /// <param name="kind">The position's namespace, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a state row, an ordered zone, a positions row or set, or a cell set.</returns>
    public static bool NamesState(WorldNameKind? kind) => kind is WorldNameKind.State or WorldNameKind.Zone or WorldNameKind.Positions or WorldNameKind.CellSet;
    /// <summary>Returns whether a name read at a position can name a state row or set: the position names state
    /// (<see cref="NamesState"/>) and the name is a <see cref="CellName"/>. A dotted <c>row.key</c> reading, which the
    /// grammar also reports with its row on its own, can name no row, in any world.</summary>
    /// <param name="kind">The position's namespace, or <see langword="null"/>.</param>
    /// <param name="name">The name as read.</param>
    /// <returns><see langword="true"/> when the name can resolve to a state row or a declared set.</returns>
    public static bool NamesStateRow(WorldNameKind? kind, string name) => NamesState(kind: kind) && CellName.TryParse(candidate: name, name: out _, reason: out _);

    /// <summary>Reports every name a typed document value spells, in document order.</summary>
    /// <param name="value">The value; a <see langword="null"/> value spells nothing.</param>
    /// <param name="type">The model type of <paramref name="value"/>.</param>
    /// <param name="found">Receives each name position's registration, namespace and name.</param>
    /// <param name="machines">The host's machine vocabulary, which reads a machine row's configuration
    /// (<see cref="WorldModuleNamespace.RespellMachineReferences"/>), or <see langword="null"/> when the host carries
    /// none and a configuration holds no name the host reads.</param>
    public static void Collect(object? value, Type type, Action<WorldNameField, WorldNameKind?, string> found, IMachineValidationCatalog? machines = null) {
        ArgumentNullException.ThrowIfNull(argument: type);
        ArgumentNullException.ThrowIfNull(argument: found);

        if (value is null) { return; }
        var node = JsonSerializer.SerializeToNode(value: value, jsonTypeInfo: WorldJsonContext.Default.Options.GetTypeInfo(type: type));
        string? Report(WorldNameField site, WorldNameKind? kind, string name, JsonObject? holder) {
            found(site, kind, name);
            return null;
        }
        WorldModuleNamespace.Respell(node: node, type: type, respell: Report);
        if ((machines is not null) && (type == typeof(WorldMachine)) && (node is JsonObject machine)) {
            WorldModuleNamespace.RespellMachineReferences(machine: machine, catalog: machines, respell: Report);
        }
    }
    /// <summary>Returns the distinct state names an installed document value spells (<see cref="NamesState"/>), read
    /// once per value object: an installed placement, machine or kit keeps its reading while the document keeps the
    /// object.</summary>
    /// <param name="value">The installed value, which the caller does not mutate.</param>
    /// <param name="type">The model type of <paramref name="value"/>.</param>
    /// <param name="machines">The host's machine vocabulary, which reads an installed machine's configuration, or
    /// <see langword="null"/>; that reading depends on the host, so it is never kept with the value.</param>
    /// <returns>The names, in ordinal order.</returns>
    public static IReadOnlyList<string> StateNames(object value, Type type, IMachineValidationCatalog? machines = null) {
        ArgumentNullException.ThrowIfNull(argument: value);

        static string[] Read(object installed, Type type, IMachineValidationCatalog? machines) {
            var names = new SortedSet<string>(comparer: StringComparer.Ordinal);

            CollectState(value: installed, type: type, names: names, machines: machines);
            return [.. names];
        }
        // A machine's reading depends on the host's catalog, and a boxed section is a fresh object per read: neither is kept.
        return (((machines is not null) && (value is WorldMachine)) || value.GetType().IsValueType)
            ? Read(installed: value, type: type, machines: machines)
            : Installed.GetValue(key: value, derive: installed => Read(installed: installed, type: type, machines: null));
    }
    private static readonly Puck.Abstractions.CodeDerivedTable<object, string[]> Installed = new();
    /// <summary>Collects the state names a typed document value spells (<see cref="NamesState"/>).</summary>
    /// <param name="value">The value; a <see langword="null"/> value spells nothing.</param>
    /// <param name="type">The model type of <paramref name="value"/>.</param>
    /// <param name="names">Receives each distinct state name.</param>
    /// <param name="machines">The host's machine vocabulary, or <see langword="null"/> (see <see cref="Collect"/>).</param>
    public static void CollectState(object? value, Type type, ISet<string> names, IMachineValidationCatalog? machines = null) {
        ArgumentNullException.ThrowIfNull(argument: names);

        Collect(value: value, type: type, machines: machines, found: (site, kind, name) => {
            if (NamesStateRow(kind: kind, name: name)) { _ = names.Add(item: name); }
        });
    }
}

/// <summary>The state names each state row's declaration spells, and the reverse: which declarations spell a name.
/// A declaration is the row without its cells' keys and values, so no value a row holds enters either relation: the
/// relation is the rows' declared structure, delivered to every replica reader. A cell's own audience policy
/// (<see cref="StateCell.Visibility"/>) is a declaration the row carries, so the names it spells enter the relation:
/// a write into a cell's audience row widens who reads that cell.</summary>
/// <remarks>Built once per state section and kept while the section is; each row's reading is kept while the row
/// object is. It extends <see cref="StateCatalog.DependentRows"/>, the token-domain relation the arena's commit check
/// reads, to every name position a declaration holds (a live audience, a derived board's token and code rows, a
/// knowledge row's source, mask and positions, a phase protocol, a witness), whichever the registry lists.</remarks>
public sealed class WorldRowDeclarationNames {
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldStateRow, string[]> Rows = new();
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldStateSection, WorldRowDeclarationNames> Sections = new();
    private static readonly WorldRowDeclarationNames Empty = new([]);
    private readonly Dictionary<string, string[]> m_spells = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> m_spelledBy = new(comparer: StringComparer.Ordinal);

    private WorldRowDeclarationNames(IReadOnlyList<WorldStateRow> rows) {
        foreach (var row in rows) {
            var names = Rows.GetValue(key: row, derive: Read);
            var name = row.Name.Value;

            m_spells[name] = names;
            foreach (var spelled in names) {
                if (!m_spelledBy.TryGetValue(key: spelled, value: out var by)) { m_spelledBy[spelled] = by = []; }
                by.Add(item: name);
            }
        }
    }

    /// <summary>Returns the relation for a document's state rows.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The relation, shared by every document holding the same state section.</returns>
    public static WorldRowDeclarationNames Of(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return (definition.StateRaw is { } section)
            ? Sections.GetValue(key: section, derive: static section => new(rows: section.World ?? []))
            : Empty;
    }
    /// <summary>Returns the state names a row's declaration spells, its own name included.</summary>
    /// <param name="row">The row's name.</param>
    /// <returns>The names, or an empty list when no row of that name is declared.</returns>
    public IReadOnlyList<string> Spells(string row) => m_spells.TryGetValue(key: row, value: out var names) ? names : [];
    /// <summary>Returns the rows whose declaration spells a state name, the named row itself included.</summary>
    /// <param name="name">The state name.</param>
    /// <returns>The rows, in declaration order; empty when none spells it.</returns>
    public IReadOnlyList<string> SpelledBy(string name) => m_spelledBy.TryGetValue(key: name, value: out var rows) ? rows : [];

    private static string[] Read(WorldStateRow row) {
        var names = new SortedSet<string>(comparer: StringComparer.Ordinal);

        WorldSpelledNames.CollectState(value: row with { Cells = null }, type: typeof(WorldStateRow), names: names);
        foreach (var cell in row.Cells ?? []) {
            WorldSpelledNames.CollectState(value: cell?.Visibility, type: typeof(StateVisibility), names: names);
        }
        return [.. names];
    }
}
