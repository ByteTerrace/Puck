using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Pins the generated schema's completeness: every array declares <c>items</c>, every closed object's
/// property is typed (a residual gap is named explicitly, never silent), the row substrate's own schema tracks
/// <see cref="StateRowJsonConverter{TRow}.Shape"/>, the root and silo self-identify, and the composed island plus
/// every shipped fragment validates against the bundle.</summary>
public sealed class WorldSchemaCompletenessLawTests {
    // A property this walk found with no "type"/"enum"/"const"/"anyOf"/"oneOf"/"$comment"/"$ref" of its own —
    // every one a converter this WP did not touch (DocumentVector2/DocumentVector3/DocumentQuaternion/
    // DocumentScalar/BindableScalar's own free-value arm, and a handful of $type union arms sharing the same
    // gap). Named by pointer rather than silently accepted, so a NEW untyped leaf fails this law and a FIXED one
    // must be removed here (see <see cref="TheAllowlistNamesNoAlreadyTypedLeaf"/>).
    private static readonly HashSet<string> UntypedAllowlist = new(comparer: StringComparer.Ordinal) {
        "#/properties/screens/items/properties/source/anyOf/8/properties/resolution",
        "#/properties/screens/items/properties/magazine/properties/entries/items/anyOf/8/properties/resolution",
        "#/properties/kits/properties/rows/items/properties/actions/additionalProperties/properties/onPress/properties/effects/items/anyOf/23/properties/placement/properties/faceSources/items/properties/source/anyOf/8/properties/resolution",
        "#/properties/kits/properties/rows/items/properties/actions/additionalProperties/properties/onPress/properties/effects/items/anyOf/23/properties/placement/properties/respond/items/properties/when/anyOf/0/properties/value",
        "#/properties/bindingOverlays/items/properties/document/properties/chords/items/properties/page/properties/entries/items/properties/channel",
        "#/properties/bindingOverlays/items/properties/document/properties/chords/items/properties/page/properties/entries/items/properties/value",
        "#/properties/bindingOverlays/items/properties/document/properties/chords/items/properties/command/properties/channel",
        "#/properties/bindingOverlays/items/properties/document/properties/chords/items/properties/command/properties/value",
        "#/properties/prototypes/items/properties/document/properties/shapes/items/properties/domain/items/anyOf/1/properties/limit",
        "#/properties/prototypes/items/properties/document/properties/shapes/items/properties/domain/items/anyOf/3/properties/limit",
        "#/properties/prototypes/items/properties/document/properties/shapes/items/properties/swings/items/properties/phase",
        "#/properties/prototypes/items/properties/document/properties/shapes/items/properties/slides/items/properties/phase",
        "#/properties/prototypes/items/properties/document/properties/shapes/items/properties/joint",
        "#/properties/prototypes/items/properties/document/properties/effectors/items/properties/target/properties/direction",
        "#/properties/prototypes/items/properties/document/properties/effectors/items/properties/target/properties/reach",
        "#/properties/prototypes/items/properties/document/properties/effectors/items/properties/target/properties/standoff",
        "#/properties/prototypes/items/properties/document/properties/effectors/items/properties/target/properties/offset",
        "#/properties/prototypes/items/properties/document/properties/effectors/items/properties/weight",
        "#/properties/state/properties/world/items/properties/draw/properties/secret",
        "#/properties/state/properties/lattices/items/anyOf/6/properties/reactions/items/anyOf/0/properties/rate",
        "#/properties/state/properties/lattices/items/anyOf/6/properties/reactions/items/anyOf/1/properties/rate",
        "#/properties/state/properties/lattices/items/anyOf/6/properties/reactions/items/anyOf/2/properties/when/items/properties/value",
        "#/properties/state/properties/lattices/items/anyOf/6/properties/reactions/items/anyOf/2/properties/then/items/properties/value",
        "#/properties/state/properties/lattices/items/anyOf/6/properties/reactions/items/anyOf/3/properties/amount",
        "#/properties/state/properties/lattices/items/anyOf/6/properties/reactions/items/anyOf/4/properties/value",
        "#/properties/state/properties/lattices/items/anyOf/6/properties/reactions/items/anyOf/5/properties/rate",
    };

    private static readonly Lazy<JsonObject> BundleHolder = new(valueFactory: BuildBundle);
    private static readonly Lazy<JsonSchema> CompiledBundleHolder = new(valueFactory: () => JsonSchema.FromText(jsonText: Bundle.ToJsonString()));

    private static JsonObject Bundle => BundleHolder.Value;
    private static JsonSchema CompiledBundle => CompiledBundleHolder.Value;

    private static JsonObject BuildBundle() {
        var split = Puck.World.WorldSchema.Export(postRenderExtensions: []);

        return Puck.World.WorldSchema.Bundle(split: split);
    }
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static bool IsTyped(JsonObject node) =>
        (node.ContainsKey(propertyName: "type") ||
        node.ContainsKey(propertyName: "enum") ||
        node.ContainsKey(propertyName: "const") ||
        node.ContainsKey(propertyName: "anyOf") ||
        node.ContainsKey(propertyName: "oneOf") ||
        node.ContainsKey(propertyName: "$comment") ||
        node.ContainsKey(propertyName: "$ref") ||
        node.ContainsKey(propertyName: "properties"));
    private static bool IsClosedObject(JsonObject node) =>
        ((node["additionalProperties"] is JsonValue value) && value.TryGetValue<bool>(value: out var closed) && !closed);
    private static bool DeclaresArrayType(JsonObject node) {
        if ((node["type"] is JsonValue single) && single.TryGetValue<string>(value: out var singleName)) {
            return string.Equals(a: singleName, b: "array", comparisonType: StringComparison.Ordinal);
        }

        if (node["type"] is JsonArray many) {
            foreach (var entry in many) {
                if ((entry is JsonValue entryValue) && entryValue.TryGetValue<string>(value: out var entryName) && string.Equals(a: entryName, b: "array", comparisonType: StringComparison.Ordinal)) {
                    return true;
                }
            }
        }

        return false;
    }
    // A bundled $ref is always a plain document-absolute JSON pointer (Bundle() never leaves a "$defs" reference
    // behind) — its target is visited at its OWN position in this same walk, so a $ref node itself never needs
    // recursing into. Cycle-safe by construction: a genuinely recursive shape's own self-reference is exactly such
    // a $ref, never dereferenced here.
    private static void Walk(JsonNode? node, string pointer, HashSet<JsonNode> visited, Action<JsonObject, string> visit) {
        if (node is JsonObject obj) {
            if (obj.ContainsKey(propertyName: "$ref") || !visited.Add(item: obj)) {
                return;
            }

            visit(arg1: obj, arg2: pointer);

            if (obj["properties"] is JsonObject properties) {
                foreach (var (name, value) in properties) {
                    Walk(node: value, pointer: $"{pointer}/properties/{name}", visited: visited, visit: visit);
                }
            }

            if (obj["items"] is { } items) {
                Walk(node: items, pointer: $"{pointer}/items", visited: visited, visit: visit);
            }

            if (obj["additionalProperties"] is JsonObject additionalProperties) {
                Walk(node: additionalProperties, pointer: $"{pointer}/additionalProperties", visited: visited, visit: visit);
            }

            foreach (var key in new[] { "allOf", "anyOf", "oneOf" }) {
                if (obj[key] is JsonArray arms) {
                    for (var index = 0; (index < arms.Count); index++) {
                        Walk(node: arms[index], pointer: $"{pointer}/{key}/{index}", visited: visited, visit: visit);
                    }
                }
            }

            foreach (var key in new[] { "if", "then", "not" }) {
                if (obj[key] is { } conditional) {
                    Walk(node: conditional, pointer: $"{pointer}/{key}", visited: visited, visit: visit);
                }
            }
        } else if (node is JsonArray array) {
            for (var index = 0; (index < array.Count); index++) {
                Walk(node: array[index], pointer: $"{pointer}/{index}", visited: visited, visit: visit);
            }
        }
    }
    private static JsonNode ParseFragment(string path) =>
        (JsonNode.Parse(json: File.ReadAllText(path: path)) ?? throw new InvalidDataException(message: $"{path} did not parse as JSON."));
    private static void AssertValidatesAgainstBundle(JsonNode instance, string label) {
        var results = CompiledBundle.Evaluate(root: instance, options: new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(condition: results.IsValid, userMessage: $"{label} failed schema validation:\n{JsonSerializer.Serialize(value: results, options: new JsonSerializerOptions { WriteIndented = true })}");
    }
    private static IEnumerable<object[]> FragmentPaths(string folder) =>
        Directory.EnumerateFiles(
            path: Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", folder),
            searchPattern: "*.world.json"
        ).OrderBy(keySelector: static path => path, comparer: StringComparer.Ordinal).Select(selector: static path => new object[] { path });

    public static IEnumerable<object[]> GameFragmentPaths() => FragmentPaths(folder: "games");
    public static IEnumerable<object[]> ModuleFragmentPaths() => FragmentPaths(folder: "modules");

    [Fact]
    public void EveryArrayDeclaresItems() {
        var violations = new List<string>();

        Walk(node: Bundle, pointer: "#", visited: new HashSet<JsonNode>(comparer: ReferenceEqualityComparer.Instance), visit: (node, pointer) => {
            if (DeclaresArrayType(node: node) && !node.ContainsKey(propertyName: "items")) {
                violations.Add(item: pointer);
            }
        });

        Assert.True(condition: (violations.Count == 0), userMessage: string.Join(separator: "\n", values: violations));
    }
    [Fact]
    public void EveryClosedObjectPropertyIsTypedOrAllowlisted() {
        var violations = new List<string>();

        Walk(node: Bundle, pointer: "#", visited: new HashSet<JsonNode>(comparer: ReferenceEqualityComparer.Instance), visit: (node, pointer) => {
            if (!IsClosedObject(node: node) || (node["properties"] is not JsonObject properties)) {
                return;
            }

            foreach (var (name, value) in properties) {
                var propertyPointer = $"{pointer}/properties/{name}";

                if ((value is JsonObject propertyObject) && !IsTyped(node: propertyObject) && !UntypedAllowlist.Contains(item: propertyPointer)) {
                    violations.Add(item: propertyPointer);
                }
            }
        });

        Assert.True(condition: (violations.Count == 0), userMessage: string.Join(separator: "\n", values: violations));
    }
    [Fact]
    public void TheAllowlistNamesNoAlreadyTypedLeaf() {
        var typed = new List<string>();

        Walk(node: Bundle, pointer: "#", visited: new HashSet<JsonNode>(comparer: ReferenceEqualityComparer.Instance), visit: (node, pointer) => {
            if (node["properties"] is not JsonObject properties) {
                return;
            }

            foreach (var (name, value) in properties) {
                var propertyPointer = $"{pointer}/properties/{name}";

                if (UntypedAllowlist.Contains(item: propertyPointer) && (value is JsonObject propertyObject) && IsTyped(node: propertyObject)) {
                    typed.Add(item: propertyPointer);
                }
            }
        });

        Assert.True(condition: (typed.Count == 0), userMessage: $"already typed — drop from the allowlist:\n{string.Join(separator: "\n", values: typed)}");
    }
    [Fact]
    public void StateWorldItemsMatchTheRowConverterShape() {
        var converter = (StateRowJsonConverter<WorldStateRow>)(WorldJsonContext.Default.Options.GetConverter(typeToConvert: typeof(WorldStateRow)) ?? throw new InvalidOperationException(message: "WorldStateRow has no registered converter."));
        var shape = converter.Shape;
        var items = (JsonObject)(Bundle["properties"]?["state"]?["properties"]?["world"]?["items"] ?? throw new InvalidOperationException(message: "state.world.items is missing from the bundle."));
        var properties = (JsonObject)(items["properties"] ?? throw new InvalidOperationException(message: "state.world.items carries no properties."));
        var names = properties.Select(selector: static kv => kv.Key).ToList();

        Assert.NotEmpty(collection: names);

        foreach (var name in names) {
            Assert.Contains(expectedSubstring: $"\"{name}\"", actualString: shape);
        }
    }
    [Fact]
    public void RootIsSelfIdentifying() {
        var identity = (JsonObject)(Bundle["x-puck"] ?? throw new InvalidOperationException(message: "the root carries no x-puck block."));

        Assert.Equal(expected: WorldDefinition.SchemaVersion, actual: (string?)identity["schemaVersion"]);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: (string?)identity["generator"]));
        Assert.False(condition: string.IsNullOrWhiteSpace(value: (string?)identity["commit"]));
        Assert.Equal(expected: WorldDefinition.SchemaVersion, actual: (string?)Bundle["properties"]?["schema"]?["const"]);
    }
    [Fact]
    public void SiloRootIsSelfIdentifying() {
        var silo = Puck.World.WorldSchema.ExportSilo();
        var identity = (JsonObject)(silo["x-puck"] ?? throw new InvalidOperationException(message: "the silo root carries no x-puck block."));

        Assert.Equal(expected: WorldSiloDefinition.SchemaVersion, actual: (string?)identity["schemaVersion"]);
        Assert.Equal(expected: WorldSiloDefinition.SchemaVersion, actual: (string?)silo["properties"]?["schema"]?["const"]);
    }
    [Fact]
    public void TheComposedIslandValidatesAgainstTheBundle() {
        var path = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(path: path, tree: out var tree, reason: out var reason), userMessage: reason);
        AssertValidatesAgainstBundle(instance: tree!, label: path);
    }
    [Theory]
    [MemberData(memberName: nameof(GameFragmentPaths))]
    public void EveryGameFragmentValidatesAgainstTheBundle(string path) =>
        AssertValidatesAgainstBundle(instance: ParseFragment(path: path), label: path);
    [Theory]
    [MemberData(memberName: nameof(ModuleFragmentPaths))]
    public void EveryModuleFragmentValidatesAgainstTheBundle(string path) =>
        AssertValidatesAgainstBundle(instance: ParseFragment(path: path), label: path);
}
