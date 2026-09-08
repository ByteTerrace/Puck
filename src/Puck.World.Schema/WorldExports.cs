using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>Which of a module's export lists admits a host reference to one of the module's names — the list a
/// registered name-bearing field binds through (<see cref="WorldNameField.Facet"/>).</summary>
public enum WorldExportFacet : byte {
    /// <summary>The field reads the name: a gate, a comparand, an expression operand, a key indirection, a domain,
    /// a zone table, a placement's board topology. Admitted by <see cref="WorldExports.Reads"/>.</summary>
    Read,
    /// <summary>The field drives the name: a state effect's destination, a transform's target row or zone, a
    /// generate row, a field write, a placement's occupancy row. Admitted by <see cref="WorldExports.Actions"/>.</summary>
    Action,
    /// <summary>The field binds presentation to the name: a HUD element, an overlay predicate, a camera program
    /// scalar, a binding wheel or bar row. Admitted by <see cref="WorldExports.Bindings"/>.</summary>
    Binding,
}

/// <summary>The surface an imported module offers its host: the names the host may read, drive, and bind to. A
/// module's names are private by default — every row, lattice, rule, table, pattern, generator, field, and dynamics
/// row it declares is its own — and a host reference to a name outside these lists refuses at compose by name
/// (<see cref="WorldModuleExports"/>). Each list names the module's own declarations as the module spells them;
/// under an aliased import the alias prefixes them exactly as it prefixes the declarations. Consumed where the
/// module is imported, so a live document never carries the member.</summary>
/// <param name="Reads">The names a host may read: gates, comparands, expression operands, key indirections, domain
/// and zone references, a board facet's topology and read-back rows.</param>
/// <param name="Actions">The names a host may drive: the rows a kit action, a host rule, or a placement's board
/// facet writes into.</param>
/// <param name="Bindings">The names a host's presentation may bind: HUD elements and templates, overlay
/// predicates, camera program scalars, binding wheels and bars.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldExports(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Reads = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Actions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Bindings = null
) {
    /// <summary>The document member carrying the record.</summary>
    public const string MemberName = "exports";

    /// <summary>Gets the list a facet admits through, empty when unauthored.</summary>
    /// <param name="facet">The facet.</param>
    /// <returns>The authored names.</returns>
    public IReadOnlyList<string> Of(WorldExportFacet facet) => (facet switch {
        WorldExportFacet.Action => Actions,
        WorldExportFacet.Binding => Bindings,
        _ => Reads,
    } ?? []);
    /// <summary>The JSON member a facet's list is authored under: <c>exports.reads</c>, <c>exports.actions</c>,
    /// <c>exports.bindings</c>.</summary>
    /// <param name="facet">The facet.</param>
    /// <returns>The member path.</returns>
    public static string MemberOf(WorldExportFacet facet) => facet switch {
        WorldExportFacet.Action => $"{MemberName}.actions",
        WorldExportFacet.Binding => $"{MemberName}.bindings",
        _ => $"{MemberName}.reads",
    };

    /// <summary>Validates the record against the names the module declares: every entry names a declaration, and no
    /// list repeats an entry.</summary>
    /// <param name="declared">Every name the module declares, spelled as the composed document spells it.</param>
    /// <param name="reason">Why the record was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when every export names a declaration once.</returns>
    public bool TryValidate(IReadOnlySet<string> declared, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: declared);

        foreach (var facet in (ReadOnlySpan<WorldExportFacet>)[WorldExportFacet.Read, WorldExportFacet.Action, WorldExportFacet.Binding]) {
            var names = Of(facet: facet);
            var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var name in names) {
                if (string.IsNullOrEmpty(value: name)) {
                    reason = $"{MemberOf(facet: facet)} carries an empty entry; an export names a declaration.";

                    return false;
                }

                if (!seen.Add(item: name)) {
                    reason = $"{MemberOf(facet: facet)} lists '{name}' twice.";

                    return false;
                }

                if (!declared.Contains(item: name)) {
                    reason = $"{MemberOf(facet: facet)} names '{name}', which the module does not declare.";

                    return false;
                }
            }
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Renders the record the way <c>world.imports</c> prints it: <c>reads:a,b actions:c bindings:d</c>,
    /// omitting an unauthored list; <c>none</c> when every list is unauthored.</summary>
    /// <returns>The one-line rendering.</returns>
    public string Describe() {
        var parts = new List<string>(capacity: 3);

        foreach (var facet in (ReadOnlySpan<WorldExportFacet>)[WorldExportFacet.Read, WorldExportFacet.Action, WorldExportFacet.Binding]) {
            var names = Of(facet: facet);

            if (names.Count > 0) {
                parts.Add(item: $"{MemberOf(facet: facet)[(MemberName.Length + 1)..]}:{string.Join(separator: ",", values: names)}");
            }
        }

        return ((parts.Count == 0) ? "none" : string.Join(separator: " ", values: parts));
    }
}
