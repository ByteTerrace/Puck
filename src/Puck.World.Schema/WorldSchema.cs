using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// Generates the JSON Schema for <c>puck.world.def.v1</c> (<see cref="WorldDefinition"/>) directly from the live
/// C# model and its XML documentation — never hand-maintained, so an editor's completion, enum values, <c>$type</c>
/// union arms, and hover text always match the code that actually parses a world document. The walk runs over
/// <see cref="WorldJsonContext"/>'s own source-generated metadata via <see cref="JsonSchemaExporter"/>, which
/// already carries every strictness rule the loader enforces: <c>additionalProperties: false</c> on every object
/// node (derived from the context's <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow"/>
/// policy), a <c>$type</c>-discriminated union as <c>anyOf</c> with a <c>const</c> arm per
/// <see cref="System.Text.Json.Serialization.JsonDerivedTypeAttribute"/>, and a named-value <c>enum</c> for every
/// <see cref="StrictEnumConverter{TEnum}"/> member. This generator adds only what the exporter cannot infer on its
/// own: curated hover text pulled from the assembly's XML documentation (<c>Puck.World.Schema.xml</c> beside
/// it), a <c>type</c>/<c>enum</c> constraint for a member whose <see cref="JsonConverter{T}"/> the exporter cannot
/// introspect and that opts in via <see cref="IJsonSchemaTypeConverter"/> or
/// <see cref="IJsonSchemaStringConverter"/> (<see cref="ApplyConverterVocabulary"/>),
/// the document root's one deliberate strictness exception — the <see cref="WorldDefinition.Extensions"/>
/// round-trip bag, which admits any <c>$</c>/<c>_</c>-prefixed key (<see cref="DocumentExtensionsPolicy"/>) and so
/// cannot be a flat <c>additionalProperties: false</c> — and the multi-file split: a small root plus one file per
/// top-level document section under <c>schema/</c>, with every subschema that appears more than once hoisted into
/// <c>schema/common.schema.json</c> under <c>$defs</c> so a person can open <c>kits.schema.json</c> and read the
/// schema for the <c>kits</c> section without wading through the other 43.
/// </summary>
public static class WorldSchema {
    // Below this compact-JSON length, a repeated node is left inlined: the $ref text would cost more than the
    // duplicate content saves, and a two/three-word leaf isn't a "shape" a reader benefits from finding by name.
    private const int HoistMinimumLength = 60;
    private const string XmlDocumentationFileName = "Puck.World.Schema.xml";

    /// <summary>The file name shared shapes live under, inside the sections directory.</summary>
    public const string CommonDefsFileName = "common.schema.json";
    /// <summary>The JSON Schema draft this document declares.</summary>
    public const string DraftUri = "https://json-schema.org/draft/2020-12/schema";
    /// <summary>The projection schema's stable identity — the tag <see cref="WorldProjectionDocument.SchemaVersion"/>
    /// carries.</summary>
    public const string ProjectionSchemaId = WorldProjectionDocument.SchemaVersion;
    /// <summary>The schema's stable identity — the same tag <see cref="WorldDefinition.SchemaVersion"/> carries.</summary>
    public const string SchemaId = WorldDefinition.SchemaVersion;
    /// <summary>The directory (relative to the root schema file) every section and <see cref="CommonDefsFileName"/> live in.</summary>
    public const string SectionsDirectoryName = "schema";
    /// <summary>The silo schema's stable identity — the same tag <see cref="WorldSiloDefinition.SchemaVersion"/> carries.</summary>
    public const string SiloSchemaId = WorldSiloDefinition.SchemaVersion;

    private static readonly Lazy<IReadOnlyDictionary<string, XElement>?> XmlDocIndex = new(valueFactory: LoadXmlDocIndex);

    /// <summary>Gets whether the assembly's XML documentation file was found and loaded. <see langword="false"/>
    /// means <see cref="Export"/> still succeeds but every node's <c>description</c> is omitted — a caller (the
    /// <c>puck schema</c> verb) reports that plainly rather than failing.</summary>
    public static bool HasXmlDocumentation =>
        (XmlDocIndex.Value is not null);

    /// <summary>The generated schema, split into the small root, one node per top-level document section, and the
    /// common definitions document every section that needs a shared shape references.</summary>
    /// <param name="Root">The document root: <c>$schema</c>/<c>$id</c>/<c>title</c>/<c>description</c>/<c>type</c>/
    /// <c>required</c>/<c>additionalProperties</c>/<c>patternProperties</c>, plus one <c>$ref</c> per top-level
    /// property pointing at its section file.</param>
    /// <param name="Sections">One entry per top-level <see cref="WorldDefinition"/> property, in declaration order —
    /// <c>Name</c> is the JSON property name (and the section's file-name stem); <c>Node</c> is its schema.</param>
    /// <param name="Common">The <c>common.schema.json</c> document: a single <c>$defs</c> object holding every
    /// subschema referenced from more than one place, named after the CLR type it came from where that is
    /// recoverable.</param>
    /// <param name="DefAnchors">Per-def bundling hint, keyed by the same name <see cref="Common"/> uses: the
    /// document-absolute JSON pointer <see cref="Bundle"/> re-inlines that def's content at (reproducing exactly
    /// where the un-split generator originally put it) when the exporter itself recognized the shape as repeated,
    /// or <see langword="null"/> when it did not (a polymorphic union arm regenerated fresh at every occurrence,
    /// e.g. <c>ActionPredicate.CompareState</c>) — <see cref="Bundle"/> then fully duplicates that def's content at
    /// every site instead of sharing one copy, matching what the un-split generator actually produced.</param>
    public sealed record SplitSchema(JsonObject Root, IReadOnlyList<(string Name, JsonNode Node)> Sections, JsonObject Common, IReadOnlyDictionary<string, string?> DefAnchors);
    /// <summary>One shipped post-render extension — a shader set's id and its config JSON Schema — spliced into
    /// <c>render.extensions[]</c> so an entry's <c>config</c> validates by its <c>id</c>.</summary>
    /// <param name="Id">The extension id a document's <c>render.extensions[].id</c> names.</param>
    /// <param name="ConfigSchema">The set's config JSON Schema (<c>Puck.Shaders.ShaderSetManifest.ConfigJsonSchema</c>).</param>
    public sealed record PostRenderExtensionSchema(string Id, JsonObject ConfigSchema);

    private static bool AllowsNull(JsonNode node) {
        if (node is not JsonObject obj) {
            return false;
        }
        if (
            (obj["type"] is JsonValue scalarType) &&
            scalarType.TryGetValue<string>(value: out var typeName) &&
            string.Equals(
            a: typeName,
            b: "null",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return true;
        }

        foreach (var key in new[] { "type", "enum", "anyOf", "oneOf" }) {
            if (obj[key] is not JsonArray values) {
                continue;
            }
            foreach (var value in values) {
                if (value is null) {
                    return true;
                }
                if (
                    (value is JsonValue token) &&
                    token.TryGetValue<string>(value: out var text) &&
                    string.Equals(
                    a: text,
                    b: "null",
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    return true;
                }
                if (
                    (value is JsonObject arm) &&
                    (arm["type"] is JsonValue armType) &&
                    armType.TryGetValue<string>(value: out var armTypeName) &&
                    string.Equals(
                    a: armTypeName,
                    b: "null",
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    return true;
                }
            }
        }

        return false;
    }
    private static void AppendChildren(StringBuilder builder, XElement element) {
        foreach (var child in element.Nodes()) {
            AppendDocNode(
                builder: builder,
                node: child
            );
        }
    }
    private static void AppendDocElement(StringBuilder builder, XElement element) {
        switch (element.Name.LocalName) {
            case "see":
            case "seealso":
                if (((string?)element.Attribute(name: "cref")) is { } cref) {
                    builder.Append(value: ShortCrefName(cref: cref));
                } else if (((string?)element.Attribute(name: "langword")) is { } langword) {
                    builder.Append(value: langword);
                } else {
                    AppendChildren(
                        builder: builder,
                        element: element
                    );
                }
                break;
            case "paramref":
            case "typeparamref":
                if (((string?)element.Attribute(name: "name")) is { } name) {
                    builder.Append(value: name);
                }
                break;
            case "para":
                builder.Append(value: ' ');
                AppendChildren(
                    builder: builder,
                    element: element
                );
                builder.Append(value: ' ');
                break;
            default:
                AppendChildren(
                    builder: builder,
                    element: element
                );
                break;
        }
    }
    private static void AppendDocNode(StringBuilder builder, XNode node) {
        switch (node) {
            case XText text:
                builder.Append(value: text.Value);
                break;
            case XElement element:
                AppendDocElement(
                    builder: builder,
                    element: element
                );
                break;
        }
    }
    // Splices the shipped extension vocabulary into #/properties/render/properties/extensions/items: `id` becomes an
    // enum over the shipped ids, and one `allOf` arm per id constrains `config` to that set's own schema when `id`
    // matches. The exporter cannot know the vocabulary — it is a deploy fact (which manifests ship), not a type
    // fact — so the caller supplies it.
    private static void ApplyPostRenderExtensions(IReadOnlyList<PostRenderExtensionSchema> extensions, JsonObject root) {
        if (root["properties"]?["render"]?["properties"]?["extensions"]?["items"] is not JsonObject items) {
            return;
        }
        if (items["properties"]?["id"] is not JsonObject id) {
            return;
        }

        var ids = new JsonArray();
        var arms = new JsonArray();

        foreach (var extension in extensions) {
            ids.Add(item: ((JsonNode)JsonValue.Create(value: extension.Id)));
            arms.Add(item: ((JsonNode)new JsonObject {
                ["if"] = new JsonObject {
                    ["properties"] = new JsonObject {
                        ["id"] = new JsonObject { ["const"] = extension.Id },
                    },
                    ["required"] = new JsonArray("id"),
                },
                ["then"] = new JsonObject {
                    ["properties"] = new JsonObject {
                        ["config"] = extension.ConfigSchema.DeepClone(),
                    },
                },
            }));
        }

        id["enum"] = ids;
        items["allOf"] = arms;
    }
    // DEFECT: the exporter emits no "type"/"enum" for a member whose JsonConverter it cannot introspect (a fully
    // custom JsonConverter<T> — the schema shows the fully permissive `true`, promoted to `{}` by AsObjectNode),
    // which means the generated schema admits a document the loader refuses (e.g. "durability":"fresh" — a string
    // the loader's own WorldDestinationDurabilityJsonConverter would reject, but an unconstrained schema accepts).
    // Fixed here by asking the RESOLVED converter (via JsonSerializerOptions.GetConverter, the same resolution the
    // loader itself uses — closed generics, [JsonConverter] attributes, and the context's own Converters array all
    // resolve through one call) whether it opts into IJsonSchemaTypeConverter or IJsonSchemaStringConverter; a
    // converter that does not is one this generator has no mechanical way to describe (an object shape or an open
    // grammar like GrantSubject's "body:<n>" — see WorldSchema's own sweep notes) and is left exactly as the
    // exporter produced it. Never widens an ALREADY-typed node (a $type union arm, a native enum) — only ever adds to the fully
    // permissive `{}` AsObjectNode just promoted.
    private static void ApplyConverterVocabulary(JsonObject obj, Type propertyType, IReadOnlyDictionary<string, XElement>? index, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        if (
            obj.ContainsKey(propertyName: "type") ||
            obj.ContainsKey(propertyName: "enum") ||
            obj.ContainsKey(propertyName: "anyOf")
        ) {
            return;
        }

        if (TryGetNodeConverter(
            propertyType: propertyType,
            converter: out var nodeConverter
        )) {
            // The converter describes its own node; an object arm it references is exported through the same
            // transform so its members are described and hoisted like any other shape.
            var shape = nodeConverter.BuildSchema(exportType: type => ExportNested(
                index: index,
                nested: nested,
                type: type,
                typesByNode: typesByNode
            ));

            foreach (var (name, value) in shape.ToList()) {
                shape.Remove(propertyName: name);
                obj[name] = value;
            }

            return;
        }

        if (TryGetTypeVocabulary(
            propertyType: propertyType,
            types: out var types
        )) {
            var typeArray = new JsonArray();

            foreach (var type in types) {
                typeArray.Add(item: type);
            }

            if (Nullable.GetUnderlyingType(nullableType: propertyType) is not null) {
                typeArray.Add(item: "null");
            }

            obj["type"] = typeArray;

            return;
        }

        if (!TryGetStringVocabulary(
            propertyType: propertyType,
            tokens: out var tokens
        )) {
            return;
        }

        var nullable = (Nullable.GetUnderlyingType(nullableType: propertyType) is not null);

        obj["type"] = (nullable
            ? new JsonArray(
                "string",
                "null"
            )
            : "string"
        );

        if (tokens is { Count: > 0 }) {
            var enumArray = new JsonArray();

            foreach (var token in tokens) {
                enumArray.Add(item: token);
            }

            if (nullable) {
                enumArray.Add(item: null);
            }

            obj["enum"] = enumArray;
        }
    }
    // Normalizes a schema node to an annotatable object, promoting a permissive `true` leaf (an unconstrained
    // schema — e.g. a custom-converted member the exporter cannot introspect, like Vector3) to `{}` in place so a
    // description can still attach without narrowing what the node accepts. Returns null for anything else (a
    // `false` schema), which nothing here needs to touch.
    private static JsonObject? AsObjectNode(ref JsonNode node) {
        if (node is JsonObject obj) {
            return obj;
        }

        if (
            (node is JsonValue value) &&
            value.TryGetValue<bool>(value: out var isUnconstrained) &&
            isUnconstrained
        ) {
            var replacement = new JsonObject();

            node = replacement;

            return replacement;
        }

        return null;
    }
    private static string ChooseDefName(JsonNode node, HoistState state) {
        var baseName = (state.TypesByNode.TryGetValue(
            key: node,
            value: out var type
        )
            ? FriendlyTypeName(type: type)
            : "Shape"
        );

        if (state.UsedNames.Add(item: baseName)) {
            return baseName;
        }

        // The exporter materializes T and nullable T as distinct shapes but reports the same TypeInfo.Type for
        // both. Give that meaningful distinction a meaningful name instead of leaking traversal order through a
        // numeric suffix in the generated schema.
        var qualifiedName = $"{baseName}{(AllowsNull(node: node)
            ? "Nullable"
            : "NonNullable")}";

        if (state.UsedNames.Add(item: qualifiedName)) {
            return qualifiedName;
        }

        for (var suffix = 2; ; suffix++) {
            var candidate = $"{qualifiedName}{suffix}";

            if (state.UsedNames.Add(item: candidate)) {
                return candidate;
            }
        }
    }
    private static string CollapseWhitespace(string text) =>
        string.Join(
            separator: ' ',
            values: text.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: ((char[]?)null)
            )
        ).Trim();
    // Non-mutating pre-pass: computes every hoist candidate's group-key text (GroupKeyText — its RAW, pre-dedup
    // content minus the occurrence annotations) and tallies how many
    // times each key occurs across the whole tree. A candidate whose ORIGIN is one of the exporter's own
    // $ref targets (see ExporterRefTargets) is included regardless of count or IsHoistCandidate's shape test — a
    // recursive shape's own target needs a def no matter how many times its content otherwise repeats. The result
    // is a per-NODE decision — group membership — fixed before any mutation happens, so the actual hoisting walk
    // (HashConsWalk) never has to recompute a node's "does this repeat" question from text that a sibling's
    // earlier mutation may have already made stale.
    private static void CollectHoistGroups(JsonNode node, HoistState state) {
        if (node is JsonObject obj) {
            if (ContainsRefKey(obj: obj)) {
                return;
            }

            foreach (var (_, child) in obj) {
                if (child is JsonObject or JsonArray) {
                    CollectHoistGroups(
                        node: child,
                        state: state
                    );
                }
            }

            var forced = (state.OriginByNode.TryGetValue(
                key: obj,
                value: out var candidateOrigin
            ) && state.ExporterRefTargets.Contains(item: candidateOrigin));

            if (
                !forced &&
                !IsHoistCandidate(obj: obj)
            ) {
                return;
            }

            var text = GroupKeyText(
                obj: obj,
                state: state
            );

            if (
                !forced &&
                (text.Length < HoistMinimumLength)
            ) {
                return;
            }

            state.CandidateText[obj] = text;
            state.RawTextCounts[text] = (state.RawTextCounts.GetValueOrDefault(key: text) + 1);
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is JsonObject or JsonArray) {
                    CollectHoistGroups(
                        node: child,
                        state: state
                    );
                }
            }
        }
    }
    // Every $ref target anywhere in node, regardless of what other keywords sit alongside "$ref" on the same
    // node (see TryGetAbsoluteRefTarget) — walked over the document exactly as the exporter produced it, before
    // any expansion.
    private static void CollectRefTargets(JsonNode node, HashSet<string> targets) {
        if (node is JsonObject obj) {
            if (TryGetAbsoluteRefTarget(
                refObj: obj,
                target: out var target
            )) {
                targets.Add(item: target);
            }

            foreach (var (key, value) in obj) {
                if (
                    !string.Equals(
                    a: key,
                    b: "$ref",
                    comparisonType: StringComparison.Ordinal
                ) &&
                    (value is not null)
                ) {
                    CollectRefTargets(
                        node: value,
                        targets: targets
                    );
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var value in arr) {
                if (value is not null) {
                    CollectRefTargets(
                        node: value,
                        targets: targets
                    );
                }
            }
        }
    }
    private static string CompactSerialize(JsonNode node) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: stream)) {
            node.WriteTo(writer: writer);
        }

        return Encoding.UTF8.GetString(bytes: stream.ToArray());
    }
    // A reference node in any of this generator's own forms — placeholder, finalized, or raw cyclic marker — all
    // of which may carry the re-sited occurrence annotations ("description"/"default") beside "$ref", exactly as
    // the exporter's own $ref sites do (draft 2020-12 keeps sibling keywords meaningful).
    private static bool ContainsRefKey(JsonObject obj) =>
        ((obj["$ref"] is JsonValue value) && value.TryGetValue<string>(value: out _));
    private static string EscapePointerSegment(string segment) =>
        (((segment.IndexOf(value: '~') >= 0) || (segment.IndexOf(value: '/') >= 0))
            ? segment.Replace(
                newValue: "~0",
                oldValue: "~"
            ).Replace(
                newValue: "~1",
                oldValue: "/"
            )
            : segment
        );
    // Depth-first expansion of every $ref, with cycle detection via activePaths — the FULL ancestry chain of
    // document-absolute paths currently being expanded, ordinary nesting and $ref jumps alike (every call pushes
    // its own currentPath for its duration, not just a ref-jump target) — and via activeTypes, the CLR types of
    // the nodes on that same chain. A $ref is genuinely recursive when its target is an ancestor of itself by
    // path, OR when its target's type is already being expanded: a polymorphic union whose arms each hold the
    // union again is regenerated by the exporter once per recursive member before it starts emitting $refs, so
    // every level's refs point at a DIFFERENT path of the same type, and by path alone each level multiplies the
    // expansion by its ref count — exponential in the member count. Either way the ref is left as-is, an opaque
    // marker for FixupCyclicMarkers to repoint later. Every other $ref — including ones the exporter emitted
    // purely because the SAME concrete TypeInfo recurred in an unrelated branch — is fully substituted, so
    // category-1 (exporter-deduplicated) and category-2 (independently regenerated, e.g. a polymorphic union arm)
    // duplicates both end up as plain inline text for one uniform dedup pass.
    private static JsonNode ExpandRefs(
        JsonNode node,
        Dictionary<string, JsonNode> pathIndex,
        Dictionary<JsonNode, Type> typesByNode,
        Dictionary<JsonNode, Type> expandedTypes,
        Dictionary<JsonNode, string> originByNode,
        HashSet<string> activePaths,
        HashSet<Type> activeTypes,
        string currentPath) {
        if (
            (node is JsonObject refObj) &&
            TryGetAbsoluteRefTarget(
            refObj: refObj,
            target: out var targetPath
        )
        ) {
            var siblings = refObj.Where(predicate: kv => !string.Equals(
                a: kv.Key,
                b: "$ref",
                comparisonType: StringComparison.Ordinal
            )).ToList();

            var recursiveByType = (pathIndex.TryGetValue(
                key: targetPath,
                value: out var targetForType
            ) && typesByNode.TryGetValue(
                key: targetForType,
                value: out var targetType
            ) && activeTypes.Contains(item: targetType));

            if (
                activePaths.Contains(item: targetPath) ||
                recursiveByType
            ) {
                // A genuine cycle — preserve any sibling keywords (e.g. an occurrence-specific "default") next to
                // the still-raw marker; FixupCyclicMarkers repoints only the "$ref" value once its target has a def.
                var marker = new JsonObject { ["$ref"] = targetPath };

                // A sibling's own value may be a literal JSON null (e.g. an optional property's own
                // "default": null) — System.Text.Json.Nodes represents that as a C# null reference, not a
                // JsonValue wrapping null, so the key still has to be written, just without recursing into it.
                foreach (var (key, value) in siblings) {
                    marker[key] = ((value is not null)
                        ? ExpandRefs(
                            node: value,
                            pathIndex: pathIndex,
                            typesByNode: typesByNode,
                            expandedTypes: expandedTypes,
                            originByNode: originByNode,
                            activePaths: activePaths,
                activeTypes: activeTypes,
                            currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}"
                        )
                        : null
                    );
                }

                return marker;
            }

            if (!pathIndex.TryGetValue(
                key: targetPath,
                value: out var targetNode
            )) {
                throw new InvalidOperationException(message: $"schema: $ref '{targetPath}' does not resolve within the generated document.");
            }

            // The recursive call pushes targetPath itself (as its own currentPath) for the duration of expanding
            // the target — no separate push here, so a ref chain and ordinary nesting share the exact same
            // ancestry bookkeeping. Any sibling keywords on THIS occurrence (not part of the shared target) are
            // merged on top of the target's own clone afterward — an occurrence-specific "default" overrides
            // (harmlessly, when it agrees, as it always has so far) whatever the target's own natural position
            // already carries.
            var expandedTarget = ExpandRefs(
                activePaths: activePaths,
                activeTypes: activeTypes,
                currentPath: targetPath,
                expandedTypes: expandedTypes,
                node: targetNode,
                originByNode: originByNode,
                pathIndex: pathIndex,
                typesByNode: typesByNode
            );

            if (
                (siblings.Count == 0) ||
                (expandedTarget is not JsonObject targetObj)
            ) {
                return expandedTarget;
            }

            foreach (var (key, value) in siblings) {
                targetObj[key] = ((value is not null)
                    ? ExpandRefs(
                        node: value,
                        pathIndex: pathIndex,
                        typesByNode: typesByNode,
                        expandedTypes: expandedTypes,
                        originByNode: originByNode,
                        activePaths: activePaths,
                activeTypes: activeTypes,
                        currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}"
                    )
                    : null
                );
            }

            return targetObj;
        }

        var pushed = activePaths.Add(item: currentPath);
        var pushedType = (typesByNode.TryGetValue(
            key: node,
            value: out var ownType
        ) && activeTypes.Add(item: ownType));
        JsonNode result;

        try {
            if (node is JsonObject obj) {
                var newObj = new JsonObject();

                foreach (var (key, value) in obj) {
                    newObj[key] = ((value is not null)
                        ? ExpandRefs(
                            node: value,
                            pathIndex: pathIndex,
                            typesByNode: typesByNode,
                            expandedTypes: expandedTypes,
                            originByNode: originByNode,
                            activePaths: activePaths,
                activeTypes: activeTypes,
                            currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}"
                        )
                        : null
                    );
                }

                result = newObj;
            } else if (node is JsonArray arr) {
                var newArr = new JsonArray();

                for (var i = 0; (i < arr.Count); i++) {
                    var value = arr[i];

                    newArr.Add(item: ((value is not null)
                        ? ExpandRefs(
                            activePaths: activePaths,
                activeTypes: activeTypes,
                            currentPath: $"{currentPath}/{i}",
                            expandedTypes: expandedTypes,
                            node: value,
                            originByNode: originByNode,
                            pathIndex: pathIndex,
                            typesByNode: typesByNode
                        )
                        : null));
                }

                result = newArr;
            } else {
                result = node.DeepClone()!;
            }
        } finally {
            if (pushed) {
                activePaths.Remove(item: currentPath);
            }
            if (pushedType) {
                activeTypes.Remove(item: ownType!);
            }
        }

        if (ownType is not null) {
            expandedTypes[result] = ownType;
        }

        originByNode[result] = currentPath;

        return result;
    }
    private static (JsonObject Root, Dictionary<JsonNode, Type> TypesByNode) ExportMergedWithTypes() {
        var index = XmlDocIndex.Value;
        var typesByNode = new Dictionary<JsonNode, Type>(comparer: ReferenceEqualityComparer.Instance);
        NestedExports? nested = null;
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            index: index,
            nested: nested,
            node: node,
            typesByNode: typesByNode
        ),
        };
        var schema = WorldJsonContext.Default.Options.GetJsonSchemaAsNode(
            type: typeof(WorldDefinition),
            exporterOptions: exporterOptions
        );
        var root = schema.AsObject();
        var generated = root.ToList();

        root.Clear();
        root.Add(
            propertyName: "$schema",
            value: DraftUri
        );
        root.Add(
            propertyName: "$id",
            value: SchemaId
        );
        root.Add(
            propertyName: "title",
            value: "Puck world definition (puck.world.def.v1)"
        );

        foreach (var (propertyName, value) in generated) {
            root.Add(
                propertyName: propertyName,
                value: value
            );
        }

        return (root, typesByNode);
    }
    // Turns the internal "$defs/Name" placeholder every hoist produces into its final, file-aware form: a bare
    // same-document pointer for a reference that itself lives inside common.schema.json, a relative cross-file
    // $ref for one that lives in a section (or the root).
    private static void FinalizeRefs(JsonNode node, bool insideCommon) {
        if (node is JsonObject obj) {
            if (IsPlaceholderRef(
                name: out var name,
                obj: obj
            )) {
                obj["$ref"] = (insideCommon
                    ? $"#/$defs/{name}"
                    : $"./{CommonDefsFileName}#/$defs/{name}"
                );

                foreach (var (key, child) in obj.ToList()) {
                    if (
                        !string.Equals(
                        a: key,
                        b: "$ref",
                        comparisonType: StringComparison.Ordinal
                    ) &&
                        (child is not null)
                    ) {
                        FinalizeRefs(
                            insideCommon: insideCommon,
                            node: child
                        );
                    }
                }

                return;
            }

            foreach (var (_, child) in obj.ToList()) {
                if (child is not null) {
                    FinalizeRefs(
                        insideCommon: insideCommon,
                        node: child
                    );
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is not null) {
                    FinalizeRefs(
                        insideCommon: insideCommon,
                        node: child
                    );
                }
            }
        }
    }
    // The SAME PropertyNamingPolicy (CamelCase) WorldJsonContext itself is configured with — matched by comparing
    // EVERY public instance property's own camelCased name, never assuming the JSON name lowercases its first
    // character alone (a policy change would silently break an assumption like that; this asks the policy itself).
    private static PropertyInfo? FindPropertyByJsonName(Type ownerType, string jsonName) {
        foreach (var property in ownerType.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance)) {
            if (string.Equals(
                a: JsonNamingPolicy.CamelCase.ConvertName(name: property.Name),
                b: jsonName,
                comparisonType: StringComparison.Ordinal
            )) {
                return property;
            }
        }

        return null;
    }
    // Repoints a leftover recursive marker — a $ref that still carries its ORIGINAL absolute document pointer,
    // because HashConsWalk skips ref-only nodes rather than treating their pointer text as hashable content — at
    // whatever def its target was hoisted to. Every such target is one of ExporterRefTargets, and CollectHoistGroups
    // forces exactly those into a def regardless of count, so a match here is guaranteed by construction.
    private static void FixupCyclicMarkers(JsonNode node, HoistState state) {
        if (node is JsonObject obj) {
            if (ContainsRefKey(obj: obj)) {
                var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

                if (!value.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "$defs/"
                )) {
                    if (!state.OriginalPathToDefName.TryGetValue(
                        key: value,
                        value: out var name
                    )) {
                        throw new InvalidOperationException(message: $"schema: cyclic $ref '{value}' was never hoisted to a common definition.");
                    }

                    obj["$ref"] = $"$defs/{name}";
                }

                foreach (var (key, child) in obj.ToList()) {
                    if (
                        !string.Equals(
                        a: key,
                        b: "$ref",
                        comparisonType: StringComparison.Ordinal
                    ) &&
                        (child is not null)
                    ) {
                        FixupCyclicMarkers(
                            node: child,
                            state: state
                        );
                    }
                }

                return;
            }

            foreach (var (_, child) in obj.ToList()) {
                if (child is not null) {
                    FixupCyclicMarkers(
                        node: child,
                        state: state
                    );
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is not null) {
                    FixupCyclicMarkers(
                        node: child,
                        state: state
                    );
                }
            }
        }
    }
    // XML doc member IDs join a nested type with '.', while reflection's Type.FullName joins one with '+'.
    private static string FormatDeclaringType(Type type) =>
        (type.FullName ?? type.Name).Replace(
            newChar: '.',
            oldChar: '+'
        );
    private static string FriendlyTypeName(Type type) {
        var underlying = Nullable.GetUnderlyingType(nullableType: type);

        if (underlying is not null) {
            type = underlying;
        }

        if (!type.IsGenericType) {
            // A nested type (a $type union's arm) qualifies with its declaring type: the bare arm names collide
            // across unions (every union has a None), and a name that identifies its union reads better than the
            // collision fallback's numeric suffix.
            return ((type.IsNested && (type.DeclaringType is { } declaring))
                ? $"{FriendlyTypeName(type: declaring)}{type.Name}"
                : type.Name
            );
        }

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();

        if (
            (arguments.Length == 1) &&
            IsCollectionLike(genericDefinition: definition)
        ) {
            return $"{FriendlyTypeName(type: arguments[0])}List";
        }

        var name = type.Name;
        var tick = name.IndexOf(value: '`');

        if (tick >= 0) {
            name = name[..tick];
        }

        return (name + string.Concat(values: arguments.Select(selector: FriendlyTypeName)));
    }
    private static JsonNode GroupKeyNode(JsonNode node, HoistState state, bool isRoot) {
        if (node is JsonObject obj) {
            string? token = null;

            if (
                (obj["$ref"] is JsonValue value) &&
                value.TryGetValue<string>(value: out var pointer)
            ) {
                token = (state.MarkerKeyByPointer.TryGetValue(
                    key: pointer,
                    value: out var markerKey
                )
                    ? markerKey
                    : pointer
                );
            } else if (
                !isRoot &&
                state.OriginByNode.TryGetValue(
                key: obj,
                value: out var origin
            ) &&
                state.ExporterRefTargets.Contains(item: origin)
            ) {
                token = (state.TypesByNode.TryGetValue(
                    key: obj,
                    value: out var targetType
                )
                    ? $"cycle:{FriendlyTypeName(type: targetType)}"
                    : origin
                );
            }

            if (token is not null) {
                // The site annotations stay beside the token — they are what the collapsed subtree's own hoist
                // will leave beside its $ref, so two parents unify exactly when their reduced contents will.
                var collapsed = new JsonObject { ["$ref"] = token };

                if (obj["description"] is JsonValue siteDescription) {
                    collapsed["description"] = siteDescription.DeepClone();
                }

                if (obj.TryGetPropertyValue(
                    jsonNode: out var siteDefault,
                    propertyName: "default"
                )) {
                    collapsed["default"] = siteDefault?.DeepClone();
                }

                return collapsed;
            }

            var result = new JsonObject();

            foreach (var (key, child) in obj) {
                if (
                    isRoot &&
                    (key is "description" or "default")
                ) {
                    continue;
                }

                result[key] = ((child is null)
                    ? null
                    : GroupKeyNode(
                        isRoot: false,
                        node: child,
                        state: state
                    )
                );
            }

            return result;
        }

        if (node is JsonArray arr) {
            var result = new JsonArray();

            foreach (var child in arr) {
                result.Add(item: ((child is null)
                    ? null
                    : GroupKeyNode(
                        isRoot: false,
                        node: child,
                        state: state
                    )));
            }

            return result;
        }

        return node.DeepClone()!;
    }
    // A candidate's group key: its content with the occurrence annotations removed and every recursion-involved
    // shape spelled canonically. "description" and "default" on the candidate's ROOT come from the property site
    // where the type is USED (Transform resolves the site's own <summary> first), so two occurrences of one shared
    // shape differ exactly there — the hoist moves both keywords to the $ref site instead, and the key must be
    // equally blind. A recursive shape appears CUT at a path-dependent depth (one occurrence keeps a raw marker
    // where another carries a full extra unrolling), so both a marker and an inline forced-def subtree collapse to
    // the same cycle:{TypeName} token — the spelling the reduced content converges to after its own hoist. Inner
    // property annotations stay in the key: they come from the shape's own declaration and are part of it.
    private static string GroupKeyText(JsonObject obj, HoistState state) =>
        CompactSerialize(node: GroupKeyNode(
            isRoot: true,
            node: obj,
            state: state
        ));
    // Whether a document root type declares an [JsonExtensionData] member — the root patternProperties carve-out
    // below applies only to a family that actually has an extension bag to carve a hole for.
    private static bool HasJsonExtensionData(Type rootType) {
        foreach (var property in rootType.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance)) {
            if (property.IsDefined(attributeType: typeof(JsonExtensionDataAttribute))) {
                return true;
            }
        }

        return false;
    }
    // Post-order: children are hash-consed (and possibly replaced with a $defs placeholder) before a parent that
    // is ITSELF a group member gets promoted, so a promoted def's stored content already reflects any child that
    // was itself hoisted — the standard maximal-sharing behavior (compareState's five copies collapse to one def
    // that itself references the one ComparandKey def, rather than five copies of ComparandKey's full body).
    // Group MEMBERSHIP itself, though, was already decided by CollectHoistGroups — this pass only decides, per
    // member, whether it is the first (creates the def) or a later one (references it already exists).
    private static JsonNode HashConsWalk(JsonNode node, HoistState state) {
        if (node is JsonObject obj) {
            if (ContainsRefKey(obj: obj)) {
                return obj;
            }

            foreach (var key in obj.Select(selector: kv => kv.Key).ToList()) {
                var child = obj[key];

                if (child is JsonObject or JsonArray) {
                    var replaced = HashConsWalk(
                        node: child,
                        state: state
                    );

                    if (!ReferenceEquals(
                        objA: replaced,
                        objB: child
                    )) {
                        obj[key] = replaced;
                    }
                }
            }

            if (!state.NodeGroupKey.TryGetValue(
                key: obj,
                value: out var groupKey
            )) {
                return obj;
            }

            if (!state.GroupToDefName.TryGetValue(
                key: groupKey,
                value: out var name
            )) {
                name = ChooseDefName(
                    node: obj,
                    state: state
                );

                // The def keeps the SHAPE only: the occurrence annotations ride each $ref site instead (see
                // GroupKeyText), and the def's own description — when the type declares one — is the type-level
                // <summary>, the doc that is true at every site.
                var content = ((JsonObject)obj.DeepClone());

                content.Remove(propertyName: "description");
                content.Remove(propertyName: "default");

                if (
                    state.TypesByNode.TryGetValue(
                    key: obj,
                    value: out var defType
                ) &&
                    (XmlDocIndex.Value is { } index) &&
                    TryGetSummary(
                    index: index,
                    memberDocId: TypeDocId(type: defType),
                    text: out var typeSummary
                ) &&
                    (typeSummary is not null)
                ) {
                    Prepend(
                        obj: content,
                        propertyName: "description",
                        value: typeSummary
                    );
                }

                state.CommonDefs[name] = content;
                state.GroupToDefName[groupKey] = name;
            }

            if (state.OriginByNode.TryGetValue(
                key: obj,
                value: out var origin
            )) {
                state.OriginalPathToDefName[origin] = name;
            }

            var placeholder = new JsonObject { ["$ref"] = $"$defs/{name}" };

            // The occurrence annotations, re-sited beside the reference (draft 2020-12 keeps keywords beside
            // "$ref" meaningful). A site description matching the def's own is dropped — it was the type-summary
            // fallback, already stated once on the def.
            if (
                (obj["description"] is JsonValue siteDescription) &&
                !((state.CommonDefs[name] is JsonObject defContent) && JsonNode.DeepEquals(
                node1: defContent["description"],
                node2: siteDescription
            ))
            ) {
                placeholder["description"] = siteDescription.DeepClone();
            }

            if (obj.TryGetPropertyValue(
                jsonNode: out var siteDefault,
                propertyName: "default"
            )) {
                placeholder["default"] = siteDefault?.DeepClone();
            }

            return placeholder;
        }

        if (node is JsonArray arr) {
            for (var i = 0; (i < arr.Count); i++) {
                var child = arr[i];

                if (child is JsonObject or JsonArray) {
                    var replaced = HashConsWalk(
                        node: child,
                        state: state
                    );

                    if (!ReferenceEquals(
                        objA: replaced,
                        objB: child
                    )) {
                        arr[i] = replaced;
                    }
                }
            }

            return arr;
        }

        return node;
    }
    private static void IndexPaths(JsonNode node, string path, Dictionary<string, JsonNode> index) {
        index[path] = node;

        if (node is JsonObject obj) {
            foreach (var (key, value) in obj) {
                if (value is not null) {
                    IndexPaths(
                        node: value,
                        path: $"{path}/{EscapePointerSegment(segment: key)}",
                        index: index
                    );
                }
            }
        } else if (node is JsonArray arr) {
            for (var i = 0; (i < arr.Count); i++) {
                var value = arr[i];

                if (value is not null) {
                    IndexPaths(
                        index: index,
                        node: value,
                        path: $"{path}/{i}"
                    );
                }
            }
        }
    }
    // Inlines every reference to a NON-recursive def at every site it appears (full duplication, undoing the
    // split's dedup — exactly what the un-split generator would have produced). A recursive def gets exactly one
    // physical expansion (the first the walk reaches); every other reference to it, including its own internal
    // self-reference, is repointed at that one location's absolute path — the same shape the un-split generator's
    // own cycle-breaking $ref already takes.
    private static JsonNode InlineBundleRefs(JsonNode node, string currentPath, JsonObject defs, IReadOnlyDictionary<string, string?> anchors, Dictionary<string, string> liveAnchors) {
        if (node is JsonObject obj) {
            string? name = null;

            if (IsCommonFileRef(
                name: out var commonName,
                obj: obj
            )) {
                name = commonName;
            } else if (IsLocalDefRef(
                name: out var localName,
                obj: obj
            )) {
                name = localName;
            }

            if (name is not null) {
                var anchored = (anchors.TryGetValue(
                    key: name,
                    value: out var anchorPath
                ) && (anchorPath is not null));

                // An anchored def (exporter-recognized as repeated) gets exactly one physical expansion, at its
                // first encounter in walk order — everywhere else (including its own nested self-reference, which
                // by then finds the live anchor set) becomes a plain pointer to that one position. An unanchored
                // def is fully duplicated at every site instead.
                if (
                    anchored &&
                    liveAnchors.TryGetValue(
                    key: name,
                    value: out var liveAnchor
                )
                ) {
                    // A ref site carries its own re-sited occurrence annotations ("description"/"default") beside
                    // "$ref" — exactly how the exporter's un-split output spells the same site.
                    var refNode = new JsonObject { ["$ref"] = liveAnchor };

                    foreach (var (key, value) in obj) {
                        if (!string.Equals(
                            a: key,
                            b: "$ref",
                            comparisonType: StringComparison.Ordinal
                        )) {
                            refNode[key] = value?.DeepClone();
                        }
                    }

                    return refNode;
                }

                if (anchored) {
                    liveAnchors[name] = currentPath;
                }

                var inlined = InlineBundleRefs(
                    node: defs[name]!,
                    currentPath: currentPath,
                    defs: defs,
                    anchors: anchors,
                    liveAnchors: liveAnchors
                );

                // A fully-expanded site takes its own occurrence annotations back onto the inlined content (a site
                // description replaces the def's type-level one, matching what the un-split exporter would have
                // annotated in place).
                if (inlined is JsonObject inlinedObj) {
                    foreach (var (key, value) in obj) {
                        if (string.Equals(
                            a: key,
                            b: "$ref",
                            comparisonType: StringComparison.Ordinal
                        )) {
                            continue;
                        }

                        inlinedObj.Remove(propertyName: key);

                        if (
                            string.Equals(
                            a: key,
                            b: "description",
                            comparisonType: StringComparison.Ordinal
                        ) &&
                            (value is not null)
                        ) {
                            Prepend(
                                obj: inlinedObj,
                                propertyName: "description",
                                value: value.GetValue<string>()
                            );
                        } else {
                            inlinedObj[key] = value?.DeepClone();
                        }
                    }
                }

                return inlined;
            }

            var newObj = new JsonObject();

            foreach (var (key, value) in obj) {
                newObj[key] = ((value is not null)
                    ? InlineBundleRefs(
                        node: value,
                        currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}",
                        defs: defs,
                        anchors: anchors,
                        liveAnchors: liveAnchors
                    )
                    : null
                );
            }

            return newObj;
        }

        if (node is JsonArray arr) {
            var newArr = new JsonArray();

            for (var i = 0; (i < arr.Count); i++) {
                var value = arr[i];

                newArr.Add(item: ((value is not null)
                    ? InlineBundleRefs(
                        anchors: anchors,
                        currentPath: $"{currentPath}/{i}",
                        defs: defs,
                        liveAnchors: liveAnchors,
                        node: value
                    )
                    : null));
            }

            return newArr;
        }

        return node.DeepClone()!;
    }
    private static bool IsCollectionLike(Type genericDefinition) =>
        ((genericDefinition == typeof(List<>)) ||
        (genericDefinition == typeof(IReadOnlyList<>)) ||
        (genericDefinition == typeof(IReadOnlyCollection<>)) ||
        (genericDefinition == typeof(IEnumerable<>)) ||
        (genericDefinition == typeof(ICollection<>)) ||
        (genericDefinition == typeof(IList<>)));
    // ---- bundling --------------------------------------------------------------------------------------------

    private static bool IsCommonFileRef(JsonObject obj, out string name) {
        const string Prefix = $"./{CommonDefsFileName}#/$defs/";

        if (ContainsRefKey(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Prefix
            )) {
                name = value[Prefix.Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }
    // A hoist candidate is a genuine named SHAPE — an object type (has "properties"), an enum, or a $type union
    // (has "anyOf") — never a bare leaf (a plain {"type":"string"} with a coincidentally-matching description
    // isn't a shared concept worth a name), and long enough that a $ref costs fewer bytes than it saves.
    private static bool IsHoistCandidate(JsonObject obj) =>
        (obj.ContainsKey(propertyName: "properties") || obj.ContainsKey(propertyName: "enum") || obj.ContainsKey(propertyName: "anyOf"));
    private static bool IsLocalDefRef(JsonObject obj, out string name) {
        const string Prefix = "#/$defs/";

        if (ContainsRefKey(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Prefix
            )) {
                name = value[Prefix.Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }
    private static bool IsPlaceholderRef(JsonObject obj, out string name) {
        if (ContainsRefKey(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "$defs/"
            )) {
                name = value["$defs/".Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }
    private static IReadOnlyDictionary<string, XElement>? LoadXmlDocIndex() {
        var path = LocateXmlDocumentationFile();

        if (path is null) {
            return null;
        }

        try {
            var document = XDocument.Load(uri: path);
            var members = document.Root?.Element(name: "members")?.Elements(name: "member");

            if (members is null) {
                return null;
            }

            var index = new Dictionary<string, XElement>(comparer: StringComparer.Ordinal);

            foreach (var member in members) {
                if (((string?)member.Attribute(name: "name")) is { } name) {
                    index[name] = member;
                }
            }

            return index;
        } catch (Exception exception) when (((exception is IOException) || (exception is System.Xml.XmlException) || (exception is UnauthorizedAccessException))) {
            return null;
        }
    }
    // Beside AppContext.BaseDirectory covers every real caller (puck.exe's own output directory, where a
    // referenced project's generated XML doc file is copied alongside its DLL — the same pattern
    // Puck.Maths.xml already rides for this CLI); the assembly's own location is the fallback for a host that
    // loads this assembly from elsewhere.
    private static string? LocateXmlDocumentationFile() {
        var beside = Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: XmlDocumentationFileName
        );

        if (File.Exists(path: beside)) {
            return beside;
        }

        var assemblyLocation = typeof(WorldDefinition).Assembly.Location;

        if (string.IsNullOrEmpty(value: assemblyLocation)) {
            return null;
        }

        var besideAssembly = Path.Combine(
            path1: (Path.GetDirectoryName(path: assemblyLocation) ?? string.Empty),
            path2: XmlDocumentationFileName
        );

        return (File.Exists(path: besideAssembly)
            ? besideAssembly
            : null
        );
    }
    private static string MemberDocId(MemberInfo member) =>
        $"P:{FormatDeclaringType(type: member.DeclaringType!)}.{member.Name}";
    // Rebuilds obj with propertyName first, for human-readable output (a description reads best leading an
    // object, ahead of its type/properties/required keywords). JsonObject preserves insertion order, and Clear()
    // detaches every child so each can be re-added to the same object without a "node already has a parent" error.
    private static void Prepend(JsonObject obj, string propertyName, JsonNode value) {
        var existing = obj.ToList();

        obj.Clear();
        obj.Add(
            propertyName: propertyName,
            value: value
        );

        foreach (var (key, existingValue) in existing) {
            obj.Add(
                propertyName: key,
                value: existingValue
            );
        }
    }
    // Strips XML doc markup down to hover-readable prose: <see cref="T:X.Y"/>/<see langword="null"/> become their
    // short name/word, <paramref name="X"/> becomes X, <para> becomes a paragraph break collapsed to one space
    // alongside everything else, and any other tag (<c>, <code>, <list>, ...) is dropped in favor of its own text.
    private static string RenderDocText(XElement root) {
        var builder = new StringBuilder();

        AppendChildren(
            builder: builder,
            element: root
        );

        return CollapseWhitespace(text: builder.ToString());
    }
    // The resolution order the world documents' authoring style demands: most members are documented as a
    // <param> on the CONTAINING RECORD's declaration (positional records), not as a <summary> on the property —
    // and Roslyn synthesizes a property <summary> from that <param> automatically wherever the property is
    // actually DECLARED (including on a base record a derived arm's primary constructor merely forwards into, the
    // common case for a $type union's shared fields). So: try the property's own <summary> first (covering both a
    // hand-written one and Roslyn's synthesized one), then the DECLARING type's own <param> of the same name (the
    // case a positional parameter shadows rather than reuses an inherited property, where Roslyn does not
    // synthesize one), then — for a node with no containing property at all (an array's item schema, a $type
    // union's own arm) — the node's OWN type <summary>.
    private static string? ResolveDescription(JsonSchemaExporterContext context, IReadOnlyDictionary<string, XElement> index) {
        if (context.PropertyInfo is { AttributeProvider: MemberInfo member }) {
            return ResolveDescriptionForMember(
                index: index,
                member: member
            );
        }

        return (TryGetSummary(
            index: index,
            memberDocId: TypeDocId(type: context.TypeInfo.Type),
            text: out var typeSummary
        )
            ? typeSummary
            : null
        );
    }
    // The property-branch half of ResolveDescription's own resolution order, factored out so
    // RestoreSkippedPropertyAnnotations — which has a reflected MemberInfo but no JsonSchemaExporterContext, since
    // the exporter never called back for the node it is fixing up — can resolve a description the SAME way.
    private static string? ResolveDescriptionForMember(MemberInfo member, IReadOnlyDictionary<string, XElement> index) {
        if (TryGetSummary(
            index: index,
            memberDocId: MemberDocId(member: member),
            text: out var ownSummary
        )) {
            return ownSummary;
        }

        return (TryGetParam(
            index: index,
            parameterName: member.Name,
            typeDocId: TypeDocId(type: member.DeclaringType!),
            text: out var paramSummary
        )
            ? paramSummary
            : null
        );
    }
    private static void RestoreSkippedProperty(JsonObject propertyObject, Type ownerType, string jsonName, IReadOnlyDictionary<string, XElement>? index, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        var property = FindPropertyByJsonName(
            jsonName: jsonName,
            ownerType: ownerType
        );

        if (property is null) {
            return;
        }

        typesByNode[propertyObject] = property.PropertyType;

        ApplyConverterVocabulary(
            index: index,
            nested: nested,
            obj: propertyObject,
            propertyType: property.PropertyType,
            typesByNode: typesByNode
        );

        if (
            (index is not null) &&
            (ResolveDescriptionForMember(
            index: index,
            member: property
        ) is { } description)
        ) {
            Prepend(
                obj: propertyObject,
                propertyName: "description",
                value: description
            );
        }
    }
    // Walks the RAW merged schema (before $ref expansion/hoisting — a description/type/enum fix-up never changes
    // tree SHAPE, only annotates existing leaf objects in place) looking for the exporter's own default-skip gap
    // (see Export's remarks): a "properties" entry whose value Transform never touched, recognizable because
    // typesByNode carries no entry for it (Transform unconditionally records one for every node it visits, even a
    // node with no resolvable description). Every other node in typesByNode was already fully annotated by
    // Transform itself and is left alone.
    private static void RestoreSkippedPropertyAnnotations(JsonNode node, IReadOnlyDictionary<string, XElement>? index, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        if (node is JsonObject obj) {
            if (
                typesByNode.TryGetValue(
                key: obj,
                value: out var ownerType
            ) &&
                (obj["properties"] is JsonObject propertiesObject)
            ) {
                foreach (var (jsonName, propertyValue) in propertiesObject) {
                    if (
                        (propertyValue is JsonObject propertyObject) &&
                        !ContainsRefKey(obj: propertyObject) &&
                        !typesByNode.ContainsKey(key: propertyObject)
                    ) {
                        RestoreSkippedProperty(
                            index: index,
                            jsonName: jsonName,
                            nested: nested,
                            ownerType: ownerType,
                            propertyObject: propertyObject,
                            typesByNode: typesByNode
                        );
                    }
                }
            }

            foreach (var (_, child) in obj) {
                if (child is not null) {
                    RestoreSkippedPropertyAnnotations(
                        index: index,
                        nested: nested,
                        node: child,
                        typesByNode: typesByNode
                    );
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is not null) {
                    RestoreSkippedPropertyAnnotations(
                        index: index,
                        nested: nested,
                        node: child,
                        typesByNode: typesByNode
                    );
                }
            }
        }
    }
    private static string ShortCrefName(string cref) {
        var colon = cref.IndexOf(value: ':');
        var body = ((colon >= 0)
            ? cref[(colon + 1)..]
            : cref
        );
        var paren = body.IndexOf(value: '(');

        if (paren >= 0) {
            body = body[..paren];
        }

        var lastDot = body.LastIndexOf(value: '.');

        return ((lastDot >= 0)
            ? body[(lastDot + 1)..]
            : body
        );
    }
    private static SplitSchema Split(JsonObject reduced, JsonObject common, Dictionary<string, string?> defAnchors) {
        var propsObj = ((JsonObject)reduced["properties"]!);
        var sectionNames = propsObj.Select(selector: kv => kv.Key).ToList();
        var sections = new List<(string Name, JsonNode Node)>(capacity: sectionNames.Count);
        var rootProperties = new JsonObject();

        foreach (var name in sectionNames) {
            var value = propsObj[name];

            propsObj.Remove(propertyName: name);

            if (value is null) {
                continue;
            }

            sections.Add(item: (name, value));
            rootProperties[name] = new JsonObject { ["$ref"] = $"./{SectionsDirectoryName}/{name}.schema.json" };
        }

        var finalRoot = new JsonObject();

        foreach (var key in reduced.Select(selector: kv => kv.Key).ToList()) {
            if (string.Equals(
                a: key,
                b: "properties",
                comparisonType: StringComparison.Ordinal
            )) {
                finalRoot["properties"] = rootProperties;
                reduced.Remove(propertyName: "properties");

                continue;
            }

            var value = reduced[key];

            reduced.Remove(propertyName: key);
            finalRoot[key] = value;
        }

        return new SplitSchema(
            Root: finalRoot,
            Sections: sections,
            Common: new JsonObject { ["$defs"] = common },
            DefAnchors: defAnchors
        );
    }
    // Runs once per exported node, bottom-up (children before parents). Attaches a description resolved from the
    // assembly's XML documentation, teaches a custom-converted node its own "type"/"enum" (see
    // ApplyConverterVocabulary), and — at the document root only, when the root type has one — the Extensions bag's
    // reserved-prefix carve-out.
    private static JsonNode Transform(JsonSchemaExporterContext context, IReadOnlyDictionary<string, XElement>? index, JsonNode node, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        if (
            (node is JsonObject alreadyRef) &&
            alreadyRef.ContainsKey(propertyName: "$ref")
        ) {
            // Already deduplicated to an earlier occurrence (a recursive or a structurally repeated type, e.g.
            // ActionPredicate.All nesting ActionPredicate) — nothing of its own left to describe or constrain.
            return node;
        }

        var description = ((index is null)
            ? null
            : ResolveDescription(
                context: context,
                index: index
            )
        );
        var obj = AsObjectNode(node: ref node);

        if (obj is null) {
            // A `false` schema (never matches) — nothing sensible to annotate.
            return node;
        }

        typesByNode[obj] = context.TypeInfo.Type;

        ApplyConverterVocabulary(
            index: index,
            nested: nested,
            obj: obj,
            propertyType: context.TypeInfo.Type,
            typesByNode: typesByNode
        );

        if (description is not null) {
            Prepend(
                obj: obj,
                propertyName: "description",
                value: description
            );
        }

        if (
            (context.Path.Length == 0) &&
            HasJsonExtensionData(rootType: context.TypeInfo.Type)
        ) {
            // The document root's one deliberate strictness exception. Extensions itself never appears as a
            // mapped `properties` entry — STJ routes a [JsonExtensionData] member around ordinary property
            // emission — so additionalProperties:false (already derived by the exporter from WorldJsonContext's
            // UnmappedMemberHandling.Disallow) would otherwise refuse the exact keys the loader accepts. This
            // pattern mirrors DocumentExtensionsPolicy.IsReservedKey by hand — JSON Schema has no way to name a
            // predicate, so keep the two in sync on sight. Gated to a root type that actually declares the member —
            // a document family with no extension bag (e.g. WorldSiloDefinition) must not advertise one.
            obj["patternProperties"] = new JsonObject {
                ["^[$_]"] = true,
            };
        }

        return node;
    }
    // A $ref node the exporter emits can carry sibling keywords alongside "$ref" — draft 2020-12 allows it, and
    // the exporter uses it: a cached concrete TypeInfo's memoized $ref plus an occurrence-specific "default" (an
    // optional property's own default value, e.g. WorldPlacement.FaceSources = null). "$ref" alone is never a
    // safe test for "is this a reference".
    private static bool TryGetAbsoluteRefTarget(JsonObject refObj, out string target) {
        if (
            (refObj["$ref"] is JsonValue value) &&
            value.TryGetValue<string>(value: out var text) &&
            text.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "#/"
        )
        ) {
            target = text;

            return true;
        }

        target = string.Empty;

        return false;
    }
    private static bool TryGetParam(IReadOnlyDictionary<string, XElement> index, string parameterName, string typeDocId, out string? text) {
        if (index.TryGetValue(
            key: typeDocId,
            value: out var type
        )) {
            var param = type.Elements(name: "param")
                .FirstOrDefault(predicate: element => string.Equals(
                a: ((string?)element.Attribute(name: "name")),
                b: parameterName,
                comparisonType: StringComparison.OrdinalIgnoreCase
            ));

            if (param is not null) {
                text = RenderDocText(root: param);

                return true;
            }
        }

        text = null;

        return false;
    }
    // A nested export's occurrences within one unsplit document: the first exports in full and every later one is
    // a placeholder ResolveNestedRefs repoints at it by JSON pointer once the tree is final — the same device the
    // exporter's own cache uses for a repeated type. The split export passes no cache: its hoist dedups by content.
    private sealed class NestedExports {
        public Dictionary<Type, JsonNode> First { get; } = [];
        public List<(Type Type, JsonObject Placeholder)> Later { get; } = [];
    }
    private static JsonNode ExportNested(Type type, IReadOnlyDictionary<string, XElement>? index, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        if ((nested is not null) && nested.First.ContainsKey(key: type)) {
            var placeholder = new JsonObject();

            nested.Later.Add(item: (type, placeholder));

            return placeholder;
        }

        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            index: index,
            nested: nested,
            node: node,
            typesByNode: typesByNode
        ),
            TreatNullObliviousAsNonNullable = true,
        };
        var exported = WorldJsonContext.Default.Options.GetJsonSchemaAsNode(
            type: type,
            exporterOptions: exporterOptions
        );

        nested?.First.Add(
            key: type,
            value: exported
        );

        return exported;
    }
    private static void ResolveNestedRefs(JsonObject root, NestedExports nested) {
        foreach (var (type, placeholder) in nested.Later) {
            var pointer = JsonPointerOf(
                node: nested.First[type],
                root: root
            ) ?? throw new InvalidOperationException(message: $"the first export of {type.Name} is no longer in the document");

            placeholder["$ref"] = $"#{pointer}";
        }
    }
    // The document-absolute JSON pointer of node under root, by parent-chain walk; null when node is not in root.
    private static string? JsonPointerOf(JsonNode node, JsonObject root) {
        var segments = new List<string>();
        var cursor = node;

        while (!ReferenceEquals(cursor, root)) {
            var parent = cursor.Parent;

            if (parent is null) {
                return null;
            }

            segments.Add(item: (parent is JsonArray array)
                ? array.IndexOf(item: cursor).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                : cursor.GetPropertyName().Replace(oldValue: "~", newValue: "~0", comparisonType: StringComparison.Ordinal).Replace(oldValue: "/", newValue: "~1", comparisonType: StringComparison.Ordinal));
            cursor = parent;
        }

        segments.Reverse();

        return string.Concat(values: segments.Select(selector: static segment => "/" + segment));
    }
    private static bool TryGetNodeConverter(Type propertyType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IJsonSchemaNodeConverter? converter) {
        var effectiveType = (Nullable.GetUnderlyingType(nullableType: propertyType) ?? propertyType);
        JsonConverter? resolved;

        try {
            resolved = WorldJsonContext.Default.Options.GetConverter(typeToConvert: effectiveType);
        } catch (NotSupportedException) {
            converter = null;

            return false;
        }

        converter = (resolved as IJsonSchemaNodeConverter);

        return (converter is not null);
    }
    // Resolves propertyType's OWN registered converter — unwrapping Nullable<T> first, since a value type's
    // nullable annotation is a distinct CLR type (System.Nullable<T>) the Converters array never names directly —
    // and reports its IJsonSchemaStringConverter opt-in, if any. The single mechanical lookup DEFECT 1 asks for:
    // never a generator-side map from CLR type to token list, so a new closed-vocabulary converter needs only the
    // interface, not a matching edit here.
    private static bool TryGetStringVocabulary(Type propertyType, out IReadOnlyList<string>? tokens) {
        var effectiveType = (Nullable.GetUnderlyingType(nullableType: propertyType) ?? propertyType);
        JsonConverter? converter;

        try {
            converter = WorldJsonContext.Default.Options.GetConverter(typeToConvert: effectiveType);
        } catch (NotSupportedException) {
            tokens = null;

            return false;
        }

        if (converter is not IJsonSchemaStringConverter vocabulary) {
            tokens = null;

            return false;
        }

        tokens = vocabulary.SchemaTokens;

        return true;
    }
    // Resolves a custom converter that accepts more than one JSON primitive representation (for example,
    // BindableScalar's number-or-string wire form). This runs before the string-only vocabulary seam above.
    private static bool TryGetTypeVocabulary(Type propertyType, out IReadOnlyList<string> types) {
        var effectiveType = (Nullable.GetUnderlyingType(nullableType: propertyType) ?? propertyType);
        JsonConverter? converter;

        try {
            converter = WorldJsonContext.Default.Options.GetConverter(typeToConvert: effectiveType);
        } catch (NotSupportedException) {
            types = [];

            return false;
        }

        if ((converter is not IJsonSchemaTypeConverter vocabulary) || (vocabulary.SchemaTypes.Count == 0)) {
            types = [];

            return false;
        }

        types = vocabulary.SchemaTypes;

        return true;
    }
    private static bool TryGetSummary(IReadOnlyDictionary<string, XElement> index, string memberDocId, out string? text) {
        if (
            index.TryGetValue(
            key: memberDocId,
            value: out var member
        ) &&
            (member.Element(name: "summary") is { } summary)
        ) {
            text = RenderDocText(root: summary);

            return true;
        }

        text = null;

        return false;
    }
    private static string TypeDocId(Type type) =>
        $"T:{FormatDeclaringType(type: type)}";

    /// <summary>Re-inlines a <see cref="SplitSchema"/> into the single-file equivalent — the un-split generator's
    /// own representation style. Every section <c>$ref</c> is substituted with its content. A shared-shape
    /// <c>$ref</c> follows <see cref="SplitSchema.DefAnchors"/>: a def the exporter itself recognized as repeated
    /// (its anchor entry is non-null) gets exactly one physical copy, at its first encounter in walk order, with
    /// every other reference — including a recursive shape's own self-reference — repointed at that position by
    /// plain JSON pointer, the same device the un-split generator's own TypeInfo cache uses; a def with no anchor
    /// (an independently-regenerated polymorphic union arm, e.g. <c>ActionPredicate.CompareState</c>, which the
    /// exporter's cache never catches) is fully duplicated at every site instead.</summary>
    public static JsonObject Bundle(SplitSchema split) {
        var root = ((JsonObject)split.Root.DeepClone()!);
        var sectionsByName = split.Sections.ToDictionary(
            keySelector: s => s.Name,
            elementSelector: s => s.Node,
            comparer: StringComparer.Ordinal
        );
        var propertiesObject = ((JsonObject)root["properties"]!);
        var defs = ((JsonObject)((JsonObject)split.Common.DeepClone()!)["$defs"]!);
        // An anchored def's single physical position is assigned LAZILY, at its first encounter in walk order, so
        // the position is reachable by construction — one def can serve several recursion roots, and a
        // pre-computed origin path can sit inside a subtree the walk pointer-replaces (a path that would then
        // never materialize).
        var liveAnchors = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var name in propertiesObject.Select(selector: kv => kv.Key).ToList()) {
            propertiesObject.Remove(propertyName: name);

            var sectionContent = ((JsonNode)sectionsByName[name].DeepClone()!);

            propertiesObject[name] = InlineBundleRefs(
                node: sectionContent,
                currentPath: $"#/properties/{EscapePointerSegment(segment: name)}",
                defs: defs,
                anchors: split.DefAnchors,
                liveAnchors: liveAnchors
            );
        }

        return root;
    }
    /// <summary>Exports the split JSON Schema for <see cref="WorldDefinition"/>.</summary>
    /// <param name="postRenderExtensions">The shipped post-render extensions: <c>render.extensions[].id</c> becomes an
    /// enum over their ids and each entry's <c>config</c> validates against the schema of the set its id names.</param>
    public static SplitSchema Export(IReadOnlyList<PostRenderExtensionSchema> postRenderExtensions) {
        ArgumentNullException.ThrowIfNull(postRenderExtensions);

        var (merged, typesByNode) = ExportMergedWithTypes();

        ApplyPostRenderExtensions(
            extensions: postRenderExtensions,
            root: merged
        );

        // The exporter's OWN shortcut for an optional property whose schema Transform would otherwise see as the
        // fully permissive `true` (a custom-converted member — see IJsonSchemaStringConverter's remarks): when such
        // a property ALSO carries a declared default (null or not), the exporter emits `{"default": <value>}`
        // directly and never invokes TransformSchemaNode for that one property at all — description, "type", and
        // "enum" alike never get a chance to attach. RestoreSkippedPropertyAnnotations finds every such orphaned
        // node (recognizable as a "properties" entry Transform never touched — typesByNode never gained an entry
        // for it) and applies the SAME annotation Transform would have, via reflection against the owning CLR type
        // (already known from typesByNode) since there is no JsonSchemaExporterContext left to ask.
        RestoreSkippedPropertyAnnotations(
            node: merged,
            index: XmlDocIndex.Value,
            nested: null,
            typesByNode: typesByNode
        );

        var pathIndex = new Dictionary<string, JsonNode>(comparer: StringComparer.Ordinal);

        IndexPaths(
            index: pathIndex,
            node: merged,
            path: "#"
        );

        // Every $ref target the exporter's OWN output already carried, before any expansion — the set Bundle
        // needs to tell "the exporter recognized this shape as repeated" (one shared copy, referenced by document
        // pointer, is how the un-split generator represents it) apart from "independently regenerated every
        // occurrence" (full duplication is how the un-split generator represents THAT).
        var exporterRefTargets = new HashSet<string>(comparer: StringComparer.Ordinal);

        CollectRefTargets(
            node: merged,
            targets: exporterRefTargets
        );

        var originByNode = new Dictionary<JsonNode, string>(comparer: ReferenceEqualityComparer.Instance);
        var expandedTypes = new Dictionary<JsonNode, Type>(comparer: ReferenceEqualityComparer.Instance);

        // Fully expand every $ref the exporter itself emitted for a repeated (but non-recursive) type, so
        // duplicate content the exporter caught and duplicate content it didn't (polymorphic union arms bypass
        // its cache — see the class doc) both end up as plain inlined text, ready for one uniform dedup pass.
        // A GENUINELY recursive $ref (its own target is an ancestor of itself) cannot be expanded — inlining it
        // would never terminate — so it is left as a marker for the fixup pass below.
        var expanded = ((JsonObject)ExpandRefs(
            node: merged,
            pathIndex: pathIndex,
            typesByNode: typesByNode,
            expandedTypes: expandedTypes,
            originByNode: originByNode,
            activePaths: new HashSet<string>(comparer: StringComparer.Ordinal),
            activeTypes: [],
            currentPath: "#"
        ));

        // Group keys spell a recursive marker by its target's TYPE name (unresolvable targets keep the raw
        // pointer, degrading to the per-path grouping such a marker had anyway).
        var markerKeyByPointer = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var target in exporterRefTargets) {
            markerKeyByPointer[target] = ((pathIndex.TryGetValue(
                key: target,
                value: out var targetNode
            ) && typesByNode.TryGetValue(
                key: targetNode,
                value: out var targetType
            ))
                ? $"cycle:{FriendlyTypeName(type: targetType)}"
                : target
            );
        }

        var state = new HoistState {
            NodeGroupKey = new Dictionary<JsonNode, string>(comparer: ReferenceEqualityComparer.Instance),
            CandidateText = new Dictionary<JsonNode, string>(comparer: ReferenceEqualityComparer.Instance),
            RawTextCounts = new Dictionary<string, int>(comparer: StringComparer.Ordinal),
            GroupToDefName = new Dictionary<string, string>(comparer: StringComparer.Ordinal),
            CommonDefs = new JsonObject(),
            TypesByNode = expandedTypes,
            OriginByNode = originByNode,
            OriginalPathToDefName = new Dictionary<string, string>(comparer: StringComparer.Ordinal),
            UsedNames = new HashSet<string>(comparer: StringComparer.Ordinal),
            MarkerKeyByPointer = markerKeyByPointer,
            // Every path the exporter's OWN output already referenced via $ref — including a recursive shape's
            // self-reference, which is exactly one such reference — has to be common-def-addressable in the split
            // output too, regardless of whether IsHoistCandidate's content-shape heuristic would otherwise have
            // picked it up (a bare List<string> "rows" wrapper, for instance, carries neither "properties" nor
            // "enum" nor "anyOf", but the exporter's own TypeInfo cache still shares it via $ref).
            ExporterRefTargets = exporterRefTargets,
        };

        CollectHoistGroups(
            node: expanded,
            state: state
        );

        foreach (var (candidateNode, text) in state.CandidateText) {
            var forced = (state.OriginByNode.TryGetValue(
                key: candidateNode,
                value: out var candidateOrigin
            ) && state.ExporterRefTargets.Contains(item: candidateOrigin));

            if (
                (state.RawTextCounts[text] >= 2) ||
                forced
            ) {
                state.NodeGroupKey[candidateNode] = text;
            }
        }

        var reduced = ((JsonObject)HashConsWalk(
            node: expanded,
            state: state
        ));

        FixupCyclicMarkers(
            node: reduced,
            state: state
        );
        FixupCyclicMarkers(
            node: state.CommonDefs,
            state: state
        );

        FinalizeRefs(
            insideCommon: false,
            node: reduced
        );
        FinalizeRefs(
            node: state.CommonDefs,
            insideCommon: true
        );

        // Several origins can map to the same def name — every member of the group gets an entry in
        // OriginalPathToDefName, not just the exporter's own canonical one. Prefer whichever origin the exporter
        // ITSELF had already pointed a $ref at; fall back to null (always fully duplicate at bundle time) only
        // when none of the group's origins qualify. When a group holds SEVERAL exporter origins (one def serving
        // two recursion roots), the FIRST in insertion order wins — insertion follows document order, and first
        // expansions nest consistently (a later origin's subtree is pointer-replaced at bundle time, so an anchor
        // inside it would name a path that never materializes).
        var defAnchors = new Dictionary<string, string?>(comparer: StringComparer.Ordinal);

        foreach (var (origin, name) in state.OriginalPathToDefName) {
            if (exporterRefTargets.Contains(item: origin)) {
                if (!(defAnchors.TryGetValue(
                    key: name,
                    value: out var anchored
                ) && (anchored is not null))) {
                    defAnchors[name] = origin;
                }
            } else if (!defAnchors.ContainsKey(key: name)) {
                defAnchors[name] = null;
            }
        }

        return Split(
            reduced: reduced,
            common: state.CommonDefs,
            defAnchors: defAnchors
        );
    }
    /// <summary>Exports the JSON Schema for <see cref="WorldProjectionDocument"/> as one document. Unsplit,
    /// deliberately: the projection has no top-level section a person opens on its own, so the split
    /// <see cref="WorldDefinition"/> takes buys nothing here.</summary>
    /// <param name="postRenderExtensions">The shipped post-render extensions, applied as in <see cref="Export"/>.</param>
    /// <returns>The generated schema root.</returns>
    public static JsonObject ExportProjection(IReadOnlyList<PostRenderExtensionSchema> postRenderExtensions) {
        ArgumentNullException.ThrowIfNull(postRenderExtensions);

        var index = XmlDocIndex.Value;
        var typesByNode = new Dictionary<JsonNode, Type>(comparer: ReferenceEqualityComparer.Instance);
        var nested = new NestedExports();
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            index: index,
            nested: nested,
            node: node,
            typesByNode: typesByNode
        ),
        };
        var schema = WorldJsonContext.Default.Options.GetJsonSchemaAsNode(
            type: typeof(WorldProjectionDocument),
            exporterOptions: exporterOptions
        );
        var root = schema.AsObject();
        var generated = root.ToList();

        root.Clear();
        root.Add(
            propertyName: "$schema",
            value: DraftUri
        );
        root.Add(
            propertyName: "$id",
            value: ProjectionSchemaId
        );
        root.Add(
            propertyName: "title",
            value: "Puck world projection (puck.world.projection.v1)"
        );

        foreach (var (propertyName, value) in generated) {
            root.Add(
                propertyName: propertyName,
                value: value
            );
        }

        RestoreSkippedPropertyAnnotations(
            index: index,
            nested: nested,
            node: root,
            typesByNode: typesByNode
        );
        ApplyPostRenderExtensions(
            extensions: postRenderExtensions,
            root: root
        );
        ResolveNestedRefs(
            nested: nested,
            root: root
        );

        return root;
    }
    /// <summary>Exports the JSON Schema for <see cref="WorldSiloDefinition"/> as one document. Unsplit, like
    /// <see cref="ExportProjection"/>: a six-field document has no section large enough to earn a file of its
    /// own.</summary>
    /// <returns>The generated schema root.</returns>
    public static JsonObject ExportSilo() {
        var index = XmlDocIndex.Value;
        var typesByNode = new Dictionary<JsonNode, Type>(comparer: ReferenceEqualityComparer.Instance);
        var nested = new NestedExports();
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            index: index,
            nested: nested,
            node: node,
            typesByNode: typesByNode
        ),
        };
        var schema = WorldJsonContext.Default.Options.GetJsonSchemaAsNode(
            type: typeof(WorldSiloDefinition),
            exporterOptions: exporterOptions
        );
        var root = schema.AsObject();
        var generated = root.ToList();

        root.Clear();
        root.Add(
            propertyName: "$schema",
            value: DraftUri
        );
        root.Add(
            propertyName: "$id",
            value: SiloSchemaId
        );
        root.Add(
            propertyName: "title",
            value: "Puck world silo (puck.silo.def.v1)"
        );

        foreach (var (propertyName, value) in generated) {
            root.Add(
                propertyName: propertyName,
                value: value
            );
        }

        RestoreSkippedPropertyAnnotations(
            index: index,
            nested: nested,
            node: root,
            typesByNode: typesByNode
        );
        ResolveNestedRefs(
            nested: nested,
            root: root
        );

        return root;
    }
    /// <summary>Exports a node's canonical text form: UTF-8 with no BOM, LF newlines, two-space indentation, and
    /// exactly one trailing newline — the same conventions <see cref="WorldDefinitionSerialization.Save"/> uses for
    /// a world document, so a checked-in artifact stays diffable and git-friendly, and two runs over an unchanged
    /// model produce byte-identical text.</summary>
    public static string ToCanonicalText(JsonNode node) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(
            utf8Json: stream,
            options: new JsonWriterOptions { Indented = true, NewLine = "\n" }
        )) {
            node.WriteTo(writer: writer);
        }

        stream.WriteByte(value: ((byte)'\n'));

        return Encoding.UTF8.GetString(bytes: stream.ToArray());
    }

    // ---- split / hoist machinery ---------------------------------------------------------------------------

    // Hash-consing bookkeeping threaded through one Export() call, across CollectHoistGroups (decides group
    // membership from raw, pre-dedup text) and HashConsWalk (the mutating pass that actually promotes one member
    // per group to a def and points every member at it). OriginalPathToDefName lets the cyclic-marker fixup pass
    // repoint a leftover recursive $ref (still carrying its ORIGINAL absolute document pointer) at whatever def
    // its target ended up becoming.
    private sealed class HoistState {
        public required Dictionary<JsonNode, string> CandidateText { get; init; }
        public required JsonObject CommonDefs { get; init; }
        // "Must have a def, regardless of IsHoistCandidate/size" is an ORIGIN property (the exporter's own $ref
        // pointed at this document-absolute path), not a property of any one clone — a recursive
        // List<ActionPredicate> "predicates" wrapper is reached naturally at motion's own gate, AND via three
        // separate $ref expansions at onPress/onFact/rules (four independent clones, one shared origin), so
        // testing membership by NODE INSTANCE would catch at most one of the four. Checking the ORIGIN through
        // OriginByNode instead catches all of them uniformly.
        public required HashSet<string> ExporterRefTargets { get; init; }
        public required Dictionary<string, string> GroupToDefName { get; init; }
        // The canonical group-key spelling of a genuinely recursive $ref marker, keyed by its document-absolute
        // pointer: two clones of the same recursive shape reached through DIFFERENT expansion paths carry markers
        // with different pointer text, which would split one shared shape into per-path groups (each minting a
        // suffixed def name) even though every marker resolves to the same def after FixupCyclicMarkers. Keying
        // the marker by its TARGET'S type name instead makes the group key blind to the path.
        public required Dictionary<string, string> MarkerKeyByPointer { get; init; }
        // Per-node group membership, decided ENTIRELY up front (CollectHoistGroups) from each candidate's RAW,
        // pre-dedup text — never recomputed mid-walk. A node's OWN reduced text changes the moment any of its
        // children gets hoisted, so deciding group membership from a text recomputed during the same mutating
        // walk is order-dependent: two nodes that are byte-identical BEFORE any hoisting (e.g. onPress's and
        // onRelease's copies of the same effect, onRelease's being a deep clone of onPress's) can end up compared
        // at different MOMENTS in that mutation, with one already child-reduced and the other not, so their
        // CURRENT text no longer matches even though they are the same shape. Group membership fixed against raw
        // text sidesteps that entirely; only the def's stored CONTENT (built the first time a member of a group is
        // reached) needs to reflect already-reduced children, which post-order recursion guarantees regardless.
        public required Dictionary<JsonNode, string> NodeGroupKey { get; init; }
        public required Dictionary<JsonNode, string> OriginByNode { get; init; }
        public required Dictionary<string, string> OriginalPathToDefName { get; init; }
        public required Dictionary<string, int> RawTextCounts { get; init; }
        public required Dictionary<JsonNode, Type> TypesByNode { get; init; }
        public required HashSet<string> UsedNames { get; init; }
    }
}
