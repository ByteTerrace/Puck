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
    // A property this walk found with no "type"/"enum"/"const"/"anyOf"/"oneOf"/"$comment"/"$ref" of its own — a
    // converter this WP did not touch, left fully permissive by the exporter. Named by the def each now lives
    // under (the bundle's own $defs, not an inline path — see WorldSchema.Bundle), never a raw inline path — a
    // shape reached from several sites now names ONE entry instead of one per site, the bundle's own dedup.
    // Every gap the split's OWN converter-hidden types (DocumentVector2/3, BindableScalar's free-value arm,
    // WorldLatticeScalar, ...) used to leave here is now a NAMED empty def a $ref points at instead — genuinely
    // typed by this law's own IsTyped test, even though the referenced def itself still carries no constraint —
    // so those entries are gone, not silently accepted; only a leaf with NO $ref/anyOf at all remains named here.
    // Named by pointer, so a NEW untyped leaf fails this law and a FIXED one must be removed here (see
    // <see cref="TheAllowlistNamesNoAlreadyTypedLeaf"/>).
    private static readonly HashSet<string> UntypedAllowlist = new(comparer: StringComparer.Ordinal) {
        "#/$defs/WorldPrototype/properties/document/properties/shapes/items/properties/domain/items/anyOf/1/properties/limit",
        "#/$defs/WorldPrototype/properties/document/properties/shapes/items/properties/domain/items/anyOf/3/properties/limit",
        "#/$defs/WorldPrototype/properties/document/properties/shapes/items/properties/swings/items/properties/phase",
        "#/$defs/WorldPrototype/properties/document/properties/shapes/items/properties/slides/items/properties/phase",
        "#/$defs/WorldPrototype/properties/document/properties/shapes/items/properties/joint",
        "#/$defs/WorldPrototype/properties/document/properties/effectors/items/properties/target/properties/direction",
        "#/$defs/WorldPrototype/properties/document/properties/effectors/items/properties/target/properties/reach",
        "#/$defs/WorldPrototype/properties/document/properties/effectors/items/properties/target/properties/standoff",
        "#/$defs/WorldPrototype/properties/document/properties/effectors/items/properties/target/properties/offset",
        "#/$defs/WorldPrototype/properties/document/properties/effectors/items/properties/weight",
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
    // a $ref, never dereferenced here. A node carrying its own "$id" opens a self-contained embedded document
    // schema (e.g. puck.creation.v1 — see WorldSchema.Bundle) the bundle leaves untouched; visited once but never
    // recursed into, matching that.
    private static void Walk(JsonNode? node, string pointer, HashSet<JsonNode> visited, Action<JsonObject, string> visit) {
        if (node is JsonObject obj) {
            if (obj.ContainsKey(propertyName: "$ref") || !visited.Add(item: obj)) {
                return;
            }

            visit(arg1: obj, arg2: pointer);

            if (obj.ContainsKey(propertyName: "$id")) {
                return;
            }

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
    // Walk covers everything a $ref can point at ONLY when it also walks $defs — the bundle's own root now
    // carries just $ref markers at every titled shape's site (see WorldSchema.Bundle), so a walk over the root
    // alone would visit almost nothing.
    private static void WalkBundle(JsonObject bundle, Action<JsonObject, string> visit) {
        var visited = new HashSet<JsonNode>(comparer: ReferenceEqualityComparer.Instance);

        Walk(
            node: bundle,
            pointer: "#",
            visited: visited,
            visit: visit
        );

        if (bundle["$defs"] is JsonObject defs) {
            foreach (var (name, defNode) in defs) {
                Walk(
                    node: defNode,
                    pointer: $"#/$defs/{name}",
                    visited: visited,
                    visit: visit
                );
            }
        }
    }
    private static JsonNode ParseFragment(string path) =>
        (JsonNode.Parse(json: File.ReadAllText(path: path)) ?? throw new InvalidDataException(message: $"{path} did not parse as JSON."));
    // Follows a site straight to its shape: a bare "#/$defs/X" $ref resolves to that def's own content; a
    // nullable site ("anyOf": [{"$ref"}, {"type":"null"}], see WorldSchema.Bundle) resolves through its $ref arm.
    // A node that is neither is already a shape — returned as-is.
    private static JsonObject ResolveDef(JsonNode node) {
        if (node is not JsonObject obj) {
            throw new InvalidOperationException(message: $"{node} is not an object node.");
        }

        if (
            (obj["$ref"] is JsonValue refValue) &&
            refValue.TryGetValue<string>(value: out var pointer)
        ) {
            return ResolvePointer(pointer: pointer);
        }

        if (obj["anyOf"] is JsonArray arms) {
            foreach (var arm in arms) {
                if ((arm is JsonObject armObject) && armObject.ContainsKey(propertyName: "$ref")) {
                    return ResolveDef(node: armObject);
                }
            }
        }

        return obj;
    }
    private static JsonObject ResolvePointer(string pointer) {
        const string Prefix = "#/$defs/";

        if (!pointer.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: Prefix
        )) {
            throw new InvalidOperationException(message: $"'{pointer}' is not a bundle-local $defs pointer.");
        }

        var name = pointer[Prefix.Length..];

        return (JsonObject)((Bundle["$defs"] as JsonObject)?[name] ?? throw new InvalidOperationException(message: $"$defs/{name} is missing from the bundle."));
    }
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

        WalkBundle(bundle: Bundle, visit: (node, pointer) => {
            if (DeclaresArrayType(node: node) && !node.ContainsKey(propertyName: "items")) {
                violations.Add(item: pointer);
            }
        });

        Assert.True(condition: (violations.Count == 0), userMessage: string.Join(separator: "\n", values: violations));
    }
    [Fact]
    public void EveryClosedObjectPropertyIsTypedOrAllowlisted() {
        var violations = new List<string>();

        WalkBundle(bundle: Bundle, visit: (node, pointer) => {
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

        WalkBundle(bundle: Bundle, visit: (node, pointer) => {
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
        var stateSection = ResolveDef(node: Bundle["properties"]?["state"] ?? throw new InvalidOperationException(message: "properties.state is missing from the bundle."));
        var worldProperty = (JsonObject)(stateSection["properties"]?["world"] ?? throw new InvalidOperationException(message: "state.world is missing from the bundle."));
        var items = ResolveDef(node: worldProperty["items"] ?? throw new InvalidOperationException(message: "state.world.items is missing from the bundle."));
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
    [Fact]
    public void EveryRefResolvesToADef() {
        var violations = new List<string>();
        var defs = ((Bundle["$defs"] as JsonObject) ?? throw new InvalidOperationException(message: "the bundle carries no $defs."));

        WalkRefs(node: Bundle, pointer: "#", visit: (refValue, pointer) => {
            const string Prefix = "#/$defs/";

            if (!refValue.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Prefix
            )) {
                violations.Add(item: $"{pointer}: $ref '{refValue}' is not a bundle-local #/$defs/ pointer");

                return;
            }

            var name = refValue[Prefix.Length..];

            if (!defs.ContainsKey(propertyName: name)) {
                violations.Add(item: $"{pointer}: $ref '{refValue}' names no $defs entry");
            }
        });

        Assert.True(condition: (violations.Count == 0), userMessage: string.Join(separator: "\n", values: violations));
    }
    [Fact]
    public void EveryDefTitleEqualsItsKey() {
        var defs = ((Bundle["$defs"] as JsonObject) ?? throw new InvalidOperationException(message: "the bundle carries no $defs."));
        var violations = new List<string>();

        foreach (var (name, defNode) in defs) {
            if ((defNode as JsonObject)?["title"] is not JsonValue titleValue || !titleValue.TryGetValue<string>(value: out var title) || !string.Equals(a: title, b: name, comparisonType: StringComparison.Ordinal)) {
                violations.Add(item: $"$defs/{name} carries title '{(defNode as JsonObject)?["title"]}'");
            }
        }

        Assert.True(condition: (violations.Count == 0), userMessage: string.Join(separator: "\n", values: violations));
    }
    // Every named shape lives in exactly one place: a $defs entry, or the bundle root itself (which keeps its own
    // hand-written title — see WorldSchema.Bundle). An object with "properties" found anywhere else would mean a
    // titled shape the bundle-time hoist missed.
    [Fact]
    public void EveryObjectWithPropertiesIsADefOrTheRoot() {
        var violations = new List<string>();

        WalkBundle(bundle: Bundle, visit: (node, pointer) => {
            if (!node.ContainsKey(propertyName: "properties")) {
                return;
            }

            if (string.Equals(
                a: pointer,
                b: "#",
                comparisonType: StringComparison.Ordinal
            )) {
                return;
            }

            if (
                pointer.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "#/$defs/"
            ) &&
                !pointer["#/$defs/".Length..].Contains(value: '/')
            ) {
                return;
            }

            // A node carrying its own "$id" IS its own schema root (a self-contained embedded document, e.g.
            // puck.creation.v1 — see WorldSchema.Bundle and Walk's own "$id" stop); Walk never recurses past it,
            // so this is the one site such a node is ever visited from.
            if (node.ContainsKey(propertyName: "$id")) {
                return;
            }

            // WorldStateRow's own shape is hand-assembled by StateRowJsonConverter<TRow> (an
            // IJsonSchemaNodeConverter) — its "allOf"/"if"/"then" kind-conditional structure and every property
            // nested under it are constructed directly in C#, never through Transform, so none of it carries a
            // CLR type StampTitle could name. The def itself is still a def; only its OWN interior is exempt.
            if (
                pointer.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "#/$defs/WorldStateRow/"
            )
            ) {
                return;
            }

            violations.Add(item: pointer);
        });

        Assert.True(condition: (violations.Count == 0), userMessage: string.Join(separator: "\n", values: violations));
    }
    // A document the old, fully-inlined bundle refused stays refused: additionalProperties:false at the root
    // (preserved verbatim by both the old inlining and the new $defs form) rejects a key the document model never
    // declared.
    [Fact]
    public void ADocumentTheOldBundleRefusedStillRefuses() {
        var path = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(path: path, tree: out var tree, reason: out var reason), userMessage: reason);

        var corrupted = ((JsonObject)tree!.DeepClone()!);

        corrupted["thisPropertyWasNeverDeclaredByTheDocumentModel"] = true;

        var results = CompiledBundle.Evaluate(root: corrupted, options: new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(condition: results.IsValid, userMessage: "an undeclared top-level property validated — additionalProperties:false regressed.");
    }
    // A $ref node is never recursed into (its target is visited at its own $defs position); everything else is
    // walked looking for its own "$ref" key.
    private static void WalkRefs(JsonNode? node, string pointer, Action<string, string> visit) {
        if (node is JsonObject obj) {
            if (
                (obj["$ref"] is JsonValue refValue) &&
                refValue.TryGetValue<string>(value: out var target)
            ) {
                visit(arg1: target, arg2: pointer);

                return;
            }

            foreach (var (key, value) in obj) {
                if (value is not null) {
                    WalkRefs(
                        node: value,
                        pointer: $"{pointer}/{key}",
                        visit: visit
                    );
                }
            }
        } else if (node is JsonArray array) {
            for (var index = 0; (index < array.Count); index++) {
                WalkRefs(
                    node: array[index],
                    pointer: $"{pointer}/{index}",
                    visit: visit
                );
            }
        }
    }
}
