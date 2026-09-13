using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Units;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lsp;

// Resolves the syntax path against generated schema metadata. Never loads schema URLs or evaluates the document.
internal sealed class PuckSchemaHover {
    private readonly record struct Cursor(JsonNode Node, JsonNode Root);

    private readonly WorldSchema.SplitSchema m_schema = WorldSchema.Export(postRenderExtensions: []);
    private readonly Dictionary<string, List<Cursor>> m_operations = new(comparer: StringComparer.Ordinal);

    private readonly Cursor m_creation;

    internal PuckSchemaHover() {
        var prototypes = m_schema.Sections.First(predicate: section => (section.Name == "prototypes")).Node;

        m_creation = (Property(cursor: Item(cursor: new Cursor(Node: prototypes, Root: prototypes)), name: "document") ?? default);
        foreach (var section in m_schema.Sections) {
            Index(cursor: new Cursor(Node: section.Node, Root: section.Node));
        }
        Index(cursor: new Cursor(Node: m_schema.Common, Root: m_schema.Common));
    }

    internal string? Describe(DocumentNode document, IReadOnlyList<SyntaxNode> path, string word, int offset) {
        if (document.Schema is not null and not "puck.world.def.v1" and not "puck.creation.v1") {
            return null;
        }
        var current = ((Cursor?)((document.Schema == "puck.creation.v1") ? m_creation : new Cursor(Node: m_schema.Root, Root: m_schema.Root)));
        var owner = (document.Schema ?? "document");

        for (var index = 1; (index < path.Count); ++index) {
            var node = path[index];

            switch (node) {
                case BlockNode block:
                    if ((index > 0) && (path[(index - 1)] is TemplateNode)) {
                        continue;
                    }
                    current = block.Identifier switch {
                        "prototype" when (owner == "prototypes") => Item(cursor: current),
                        "shape" => Item(cursor: Property(cursor: current, name: "shapes")),
                        "material" => Item(cursor: Property(cursor: current, name: "materials")),
                        "addon" => Item(cursor: Property(cursor: current, name: "addons")),
                        "placement" => Item(cursor: Property(cursor: current, name: "rows")),
                        _ => Property(cursor: current, name: block.Identifier)
                    };
                    owner = block.Identifier;
                    if ((block.Identifier == "shape") && (block.Target == word) && (offset < (block.Statements.FirstOrDefault()?.Offset ?? (block.Offset + block.Length)))) {
                        return Card(Property(cursor: current, name: "type"), "shape.type", word);
                    }
                    if ((block.Identifier == word) && (offset < (block.Offset + block.Identifier.Length))) {
                        return Card(cursor: current, enumValue: null, name: owner);
                    }
                    break;
                case RuleBlockNode:
                    current = Item(cursor: Property(cursor: current, name: "rules"));
                    owner = "rule";
                    break;
                case PropertyNode property:
                    var fieldOwner = owner;
                    current = Property(cursor: current, name: property.Name);
                    owner = property.Name;
                    if ((word == property.Name) && (offset < property.Value.Offset)) {
                        return Card(current, $"{fieldOwner}.{property.Name}", null);
                    }
                    break;
                case ArrayExpressionNode:
                    current = Item(cursor: current);
                    break;
                case CallExpressionNode call:
                    current = Operation(context: current, name: call.Name);
                    owner = call.Name;
                    break;
                case ArgumentNode argument:
                    current = ((argument.Name is null) ? null : Property(cursor: current, name: argument.Name));
                    if ((argument.Name == word) && (offset < argument.Value.Offset)) {
                        return Card(cursor: current, enumValue: null, name: $"{owner}.{word}");
                    }
                    owner = (argument.Name ?? owner);
                    break;
                case IdentifierExpressionNode identifier when (identifier.Name == word):
                    return Card(cursor: current, enumValue: word, name: owner);
                case LetNode:
                    // An arbitrary compile-time object has no document schema until it is assigned to a field.
                    current = null;
                    break;
            }
        }
        return null;
    }

    private Cursor? Operation(Cursor? context, string name) {
        var matching = Variants(context).FirstOrDefault(predicate: cursor => (cursor.Node["properties"]?["$type"]?["const"]?.ToString() == name));

        if (matching.Node is not null) {
            return matching;
        }
        return ((m_operations.TryGetValue(key: name, value: out var candidates) && (candidates.Count == 1)) ? candidates[0] : null);
    }
    private void Index(Cursor cursor) {
        if (cursor.Node is JsonObject obj) {
            if (obj.ContainsKey(propertyName: "$id")) {
                cursor = new Cursor(Node: obj, Root: obj);
            }
            if (obj["properties"]?["$type"]?["const"]?.ToString() is { } name) {
                if (!m_operations.TryGetValue(key: name, value: out var candidates)) {
                    candidates = [];
                    m_operations.Add(key: name, value: candidates);
                }
                if (!candidates.Any(predicate: candidate => JsonNode.DeepEquals(node1: candidate.Node, node2: obj))) {
                    candidates.Add(item: cursor);
                }
            }
            foreach (var child in obj) {
                if (child.Value is not null) {
                    Index(cursor: new Cursor(Node: child.Value, Root: cursor.Root));
                }
            }
        } else if (cursor.Node is JsonArray array) {
            foreach (var child in array) {
                if (child is not null) {
                    Index(cursor: new Cursor(Node: child, Root: cursor.Root));
                }
            }
        }
    }
    private Cursor? Property(Cursor? cursor, string name) {
        foreach (var variant in Variants(cursor)) {
            if ((variant.Node["properties"] is JsonObject properties) && (properties[name] is { } value)) {
                return new Cursor(Node: value, Root: variant.Root);
            }
        }
        return null;
    }
    private Cursor? Item(Cursor? cursor) {
        foreach (var variant in Variants(cursor)) {
            if (variant.Node["items"] is { } items) {
                return new Cursor(Node: items, Root: variant.Root);
            }
        }
        return null;
    }
    private IEnumerable<Cursor> Variants(Cursor? cursor, int depth = 0) {
        if ((cursor is not { Node: JsonObject obj } value) || (depth > 32)) {
            yield break;
        }
        if (obj.ContainsKey(propertyName: "$id")) {
            value = new Cursor(Node: obj, Root: obj);
        }
        yield return value;
        if ((obj["$ref"]?.ToString() is { } reference) && (Resolve(cursor: value, reference: reference) is { } resolved)) {
            foreach (var variant in Variants(cursor: resolved, depth: (depth + 1))) {
                yield return variant;
            }
        }
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" }) {
            if (obj[key] is JsonArray alternatives) {
                foreach (var alternative in alternatives) {
                    if (alternative is not null) {
                        foreach (var variant in Variants(cursor: new Cursor(Node: alternative, Root: value.Root), depth: (depth + 1))) {
                            yield return variant;
                        }
                    }
                }
            }
        }
    }
    private Cursor? Resolve(Cursor cursor, string reference) {
        var parts = reference.Split('#', 2);
        var root = cursor.Root;

        if (parts[0].EndsWith(comparisonType: StringComparison.Ordinal, value: "common.schema.json")) {
            root = m_schema.Common;
        } else if (parts[0].Length > 0) {
            var file = parts[0].Split('/')[^1];

            root = m_schema.Sections.FirstOrDefault(predicate: section => (file == (section.Name + ".schema.json"))).Node;
        }
        var node = root;

        if (parts.Length == 2) {
            foreach (var segment in parts[1].Split('/').Skip(count: 1)) {
                var decoded = segment.Replace(comparisonType: StringComparison.Ordinal, newValue: "/", oldValue: "~1").Replace(comparisonType: StringComparison.Ordinal, newValue: "~", oldValue: "~0");

                node = ((node is JsonObject obj) ? obj[decoded]
                    : (((node is JsonArray array) && int.TryParse(result: out var index, s: decoded) && (index >= 0) && (index < array.Count)) ? array[index] : null));
            }
        }
        return (((node is null) || (root is null)) ? null : new Cursor(Node: node, Root: root));
    }
    private string? Card(Cursor? cursor, string name, string? enumValue) {
        var variants = Variants(cursor).ToArray();

        if (variants.Length == 0) {
            return null;
        }
        var description = variants.Select(selector: value => value.Node["description"]?.ToString()).FirstOrDefault(predicate: text => !string.IsNullOrWhiteSpace(value: text));
        var enumeration = variants.Select(selector: value => (value.Node["enum"] as JsonArray)).FirstOrDefault(predicate: value => (value is not null));

        if (enumValue is not null) {
            if ((enumeration is null) || !enumeration.Any(predicate: value => (value?.ToString() == enumValue))) {
                return null;
            }
            description = (variants.Select(selector: value => value.Node["x-enumDescriptions"]?[enumValue]?.ToString()).FirstOrDefault(predicate: text => (text is not null)) ?? description);
            name = $"{name}: {enumValue}";
        }
        var type = variants.Select(selector: value => value.Node["type"]?.ToJsonString()).FirstOrDefault(predicate: value => (value is not null));
        var lines = new List<string> { $"**{name}**" };

        if (description is not null) { lines.Add(item: description); }
        if (type is not null) { lines.Add(item: $"Type: `{type}`."); }
        if ((enumeration is not null) && (enumValue is null)) { lines.Add(item: $"Allowed values: {string.Join(separator: ", ", values: enumeration.Select(selector: value => $"`{value}`"))}."); }
        var defaultNode = variants.FirstOrDefault(predicate: value => value.Node.AsObject().ContainsKey(propertyName: "default"));

        if (defaultNode.Node is not null) { lines.Add(item: $"Default: `{(defaultNode.Node["default"]?.ToJsonString() ?? "null")}`."); }
        var dimension = WorldDocumentVocabulary.Instance.ClassifyField(fieldKey: name);

        if (dimension != UnitDimension.None) { lines.Add(item: $"Units: {string.Join(", ", UnitConversion.AcceptedUnits(dimension: dimension))} ({dimension})."); }
        return string.Join(separator: "\n\n", values: lines);
    }
}
