using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

/// <summary>Refuses a document the decompiler cannot print as source: a name in the generated form
/// (<see cref="GeneratedName"/>) that no construct it prints would generate. Printing the name as an authored one
/// would write a source the compiler refuses (PUCK113); printing nothing would drop what the document says.</summary>
/// <param name="name">The generated-form name.</param>
/// <param name="pointer">The JSON pointer of the member that declares it.</param>
/// <param name="reason">What generates a name of this form, and where it can be printed from.</param>
public sealed class WorldDecompileRefusedException(string name, string pointer, string reason)
    : InvalidOperationException(message: $"'{name}' at {pointer} is a name Puck generates, and this document does not carry it where a construct prints it back: {reason}") {
    /// <summary>Gets the generated-form name the decompiler refused.</summary>
    public string Name { get; } = name;
    /// <summary>Gets the JSON pointer of the member that declares <see cref="Name"/>.</summary>
    public string Pointer { get; } = pointer;
}
// A name the compiler generates prints back only as a rule scope's member or group, inside `rules` scopes. A ground
// block, a test, a composition's border or door and an aliased module instance write rows no document node prints
// back: they are written in source, and a save leaves them as written and refuses an edit to a row they generate, so
// a name of theirs is refused here by naming the construct.
public static partial class WorldDecompiler {
    // ---- rule scopes ----------------------------------------------------------------------------------------------

    // A rule scope joins its own name to each name it holds (`outer$inner`), so a rule or group whose name carries the
    // joiner prints inside one `rules` scope per part before its own: an empty scope adds no gate, local or property,
    // and the member lowers to the same rule under the same name.
    private static bool TrySplitScopedName(string name, out string[] scopes, out string local) {
        scopes = [];
        local = name;

        if (!GeneratedName.IsGenerated(name: name)) {
            return true;
        }

        var parts = name.Split(separator: GeneratedName.Joiner);

        if (parts.Any(predicate: static part => ((part.Length == 0) || !IsSpellableName(name: part)))) {
            return false;
        }

        scopes = parts[..^1];
        local = parts[^1];

        return true;
    }
    private static void AppendScoped(StringBuilder sb, IReadOnlyList<string> scopes, int indentLevel, Action<int> body) {
        for (var index = 0; (index < scopes.Count); index++) {
            sb.Append(repeatCount: ((indentLevel + index) * 4), value: ' ')
                .Append(value: "rules ")
                .Append(value: PuckPrinter.PrintName(name: scopes[index]))
                .AppendLine(value: " {");
        }

        body(obj: (indentLevel + scopes.Count));

        for (var index = (scopes.Count - 1); (index >= 0); index--) {
            sb.Append(repeatCount: ((indentLevel + index) * 4), value: ' ').AppendLine(value: "}");
        }
    }
    // ---- the sweep ------------------------------------------------------------------------------------------------

    // Every name the document declares, as the leaf that holds it and where it stands: every authored name
    // (WorldAuthoredNames, the walk the compiler and loader read too), the rows a composition's links write, whose names
    // are references rather than declarations but are generated all the same, and each views.graphs row's instance
    // name, which the synthesized root graph's own generated names share a namespace with.
    private static List<(JsonNode Leaf, string Pointer)> NamesToSweep(JsonObject root) {
        var names = new List<(JsonNode Leaf, string Pointer)>();

        // A name one of the document's own aliased imports declares is the composer's, restated or read here in the
        // spelling the document already carries.
        WorldAuthoredNames.Visit(
            document: root,
            visitor: (leaf, name) => {
                if (!WorldDocumentEmitter.IsImportQualified(document: root, name: name)) {
                    names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
                }
            }
        );
        foreach (var (section, member) in new[] { ("destinations", "reference"), ("adjacencies", "destination") }) {
            foreach (var row in ((root[section] as JsonArray) ?? []).OfType<JsonObject>()) {
                if (row[member] is JsonValue leaf) {
                    names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
                }
            }
        }
        foreach (var placement in ((root["placements"]?["rows"] as JsonArray) ?? []).OfType<JsonObject>()) {
            foreach (var face in ((placement["faceSources"] as JsonArray) ?? []).OfType<JsonObject>()) {
                if (face["portal"]?["destination"] is JsonValue leaf) {
                    names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
                }
            }
        }
        foreach (var graph in ((root["views"]?["graphs"] as JsonArray) ?? []).OfType<JsonObject>()) {
            if (graph["name"] is JsonValue leaf) {
                names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
            }
        }

        return names;
    }
    // Refuses the first name in the generated form that no construct about to print spells back: anything but a rule or
    // group name its section prints through scopes.
    private static void RefuseUnprintedGeneratedNames(List<(JsonNode Leaf, string Pointer)> names, bool rulesPrintAsSugar, bool groupsPrintAsSugar) {
        foreach (var (node, pointer) in names) {
            if (
                (node is not JsonValue leaf) ||
                !leaf.TryGetValue<string>(value: out var name) ||
                !GeneratedName.IsGenerated(name: name)
            ) {
                continue;
            }
            if (
                ((rulesPrintAsSugar && IsNameOfRow(pointer: pointer, section: "/rules/")) ||
                (groupsPrintAsSugar && IsNameOfRow(pointer: pointer, section: "/ruleGroups/"))) &&
                TrySplitScopedName(local: out _, name: name, scopes: out _)
            ) {
                continue;
            }

            throw new WorldDecompileRefusedException(
                name: name,
                pointer: pointer,
                reason: ((IsNameOfRow(pointer: pointer, section: "/rules/") || IsNameOfRow(pointer: pointer, section: "/ruleGroups/"))
                    ? "a rule scope joins its own name to each rule and group it holds, and prints back only where every part of the name is one and the section prints as rules"
                    : GeneratedNameOrigin(name: name))
            );
        }
    }
    private static bool IsNameOfRow(string pointer, string section) => (
        pointer.StartsWith(comparisonType: StringComparison.Ordinal, value: section) &&
        pointer.EndsWith(comparisonType: StringComparison.Ordinal, value: "/name") &&
        (pointer.AsSpan(start: section.Length, length: ((pointer.Length - section.Length) - "/name".Length)).IndexOf(value: '/') < 0)
    );
    // What generates a name of this form, read off its first part.
    private static string GeneratedNameOrigin(string name) => name[..Math.Max(val1: 1, val2: name.IndexOf(startIndex: 1, value: GeneratedName.Joiner))] switch {
        WorldDocumentEmitter.GroundHead => "a ground block generates it; the block is written in source and never printed back from a document",
        WorldCompositionLinks.LinkHead or WorldCompositionLinks.ReturnHead => "a border or door between two worlds of one composition generates it; the link is written in source and never printed back from a document",
        WorldDocumentEmitter.ExpectHead => "a test block generates it; the test is written in source and never printed back from a document",
        "$pool" => "the state catalog generates it from a pool declaration while the world runs; no document declares it",
        "identity" or "chat" or "controller" => "the engine seeds it into an owned identity's own document; no world source declares it",
        "arg" => "a module expansion stands it in for a row argument and replaces it before the document is written; no document declares it",
        _ => "a module instance used under an alias declares every name in this form ('use counter as left' declares 'left$score'), and a module's use does not print back from the document it expanded into",
    };
}
