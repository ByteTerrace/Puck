using Puck.Abstractions;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// Generates the JSON Schema for <c>puck.world.definition.v1</c> (<see cref="WorldDefinition"/>) directly from the live
/// C# model and its XML documentation — never hand-maintained, so an editor's completion, enum values, <c>$type</c>
/// union arms, and hover text always match the code that actually parses a world document. The walk runs over
/// <see cref="WorldJsonContext"/>'s own source-generated metadata via <see cref="JsonSchemaExporter"/>, which
/// already carries every strictness rule the loader enforces: <c>additionalProperties: false</c> on every object
/// node (derived from the context's <see cref="System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow"/>
/// policy), a <c>$type</c>-discriminated union as <c>anyOf</c> with a <c>const</c> arm per
/// <see cref="System.Text.Json.Serialization.JsonDerivedTypeAttribute"/>, and a named-value <c>enum</c> for every
/// <see cref="StrictEnumConverter{TEnum}"/> member. This generator adds only what the exporter cannot infer on its
/// own: curated hover text pulled from the XML documentation of the model assemblies
/// (the World.Schema, State, World.Authoring, and SignedDistance XML files beside their assemblies), a <c>type</c>/<c>enum</c> constraint for a member whose <see cref="JsonConverter{T}"/> the exporter cannot
/// introspect and that opts in via <see cref="IJsonSchemaTypeConverter"/> or
/// <see cref="IJsonSchemaStringConverter"/> (<see cref="ApplyConverterVocabulary"/>), a whole node — required
/// members, kind-conditional types, exclusivity rules — for a converter that opts into
/// <see cref="IJsonSchemaNodeConverter"/>, <c>items</c>/<c>additionalProperties</c> for a collection whose own
/// element/value type the exporter left unconstrained, a <c>$comment</c> marking a raw <see cref="JsonElement"/>
/// slot whose shape an id named elsewhere in the document decides, the document root's one deliberate strictness
/// exception — the <see cref="WorldDefinition.Extensions"/>
/// round-trip bag, which admits any <c>$</c>/<c>_</c>-prefixed key (<see cref="DocumentExtensionsPolicy"/>) and so
/// cannot be a flat <c>additionalProperties: false</c> — <c>x-puck</c>/<c>properties.schema.const</c>
/// self-identification, and the multi-file split: a small root plus one file per
/// top-level document section under <c>schema/</c>, with every subschema that appears more than once hoisted into
/// <c>schema/common.schema.json</c> under <c>$defs</c> so a person can open <c>kits.schema.json</c> and read the
/// schema for the <c>kits</c> section without wading through the other 43.
/// </summary>
public static partial class WorldSchema {
    // Below this compact-JSON length, a repeated node is left inlined: the $ref text would cost more than the
    // duplicate content saves, and a two/three-word leaf isn't a "shape" a reader benefits from finding by name.
    private const int HoistMinimumLength = 60;

    private static readonly ConcurrentDictionary<string, Lazy<SplitSchema>> ExportCache = new(comparer: StringComparer.Ordinal);
    // Every assembly whose types the document embeds; each one's generated XML doc file rides beside the DLL.
    private static readonly (string FileName, Type Anchor)[] XmlDocumentationFiles = [
        ("Puck.World.Schema.xml", typeof(WorldDefinition)),
        ("Puck.State.xml", typeof(ExpressionProgram)),
        ("Puck.State.Rules.xml", typeof(Puck.State.Rules.RuleGroupDeclaration)),
        ("Puck.State.Topology.xml", typeof(PatternRow)),
        ("Puck.State.Generators.xml", typeof(TableDocument)),
        ("Puck.World.Authoring.xml", typeof(Puck.World.Authoring.CreationDocument)),
        ("Puck.SignedDistance.xml", typeof(Puck.SignedDistance.SdfSolidPrimitive)),
    ];

    /// <summary>The file name shared shapes live under, inside the sections directory.</summary>
    public const string CommonDefsFileName = "common.schema.json";
    /// <summary>The counters report schema's stable identity — the tag <see cref="WorldCountersReport.SchemaVersion"/>
    /// carries.</summary>
    public const string CountersReportSchemaId = WorldCountersReport.SchemaVersion;
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

    private static readonly Lazy<XmlDocumentation?> XmlDocIndex = new(valueFactory: static () => LoadXmlDocIndex(files: XmlDocumentationFiles));
    // Every type's converter and metadata as WorldJsonContext resolves them, null where the context has none (a miss
    // is an exception, so each type is asked once). The context's options are process-wide and immutable once used.
    private static readonly ConcurrentDictionary<Type, JsonConverter?> Converters = new();
    private static readonly ConcurrentDictionary<Type, string> FriendlyTypeNames = new();
    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> PropertiesByJsonName = new();
    private static readonly ConcurrentDictionary<Type, JsonTypeInfo?> TypeInfos = new();

    /// <summary>Gets whether every XML documentation file the schema draws on was found and loaded. <see langword="false"/>
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
    public sealed record SplitSchema(JsonObject Root, IReadOnlyList<(string Name, JsonNode Node)> Sections, JsonObject Common);
    /// <summary>One shipped post-render extension — a shader set's id and its config JSON Schema — spliced into
    /// <c>render.extensions[]</c> so an entry's <c>config</c> validates by its <c>id</c>.</summary>
    /// <param name="Id">The extension id a document's <c>render.extensions[].id</c> names.</param>
    /// <param name="ConfigSchema">The set's config JSON Schema (<c>Puck.Shaders.ShaderSetManifest.ConfigJsonSchema</c>).</param>
    public sealed record PostRenderExtensionSchema(string Id, JsonObject ConfigSchema);

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
        // An empty catalog narrows nothing: an "enum"/"allOf" built from zero entries would validate NO document
        // rather than every document, the opposite of "nothing shipped yet".
        if (extensions.Count == 0) {
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
    private static void ApplyConverterVocabulary(JsonObject obj, Type propertyType, ExportRun run) {
        if (
            obj.ContainsKey(propertyName: "type") ||
            obj.ContainsKey(propertyName: "enum") ||
            obj.ContainsKey(propertyName: "anyOf")
        ) {
            return;
        }

        // A raw JsonElement slot (render.extensions[].config, probes[].config, metadata.custom's dictionary
        // values) carries no CLR shape this generator could ever describe — its actual contract is decided by an
        // id this document names elsewhere (a shipped shader manifest, a probe extension) that this generator has
        // no way to resolve for an as-yet-unauthored id. Left fully permissive, with a $comment naming why, rather
        // than narrowed to a shape that would refuse a legitimate payload.
        if ((Nullable.GetUnderlyingType(nullableType: propertyType) ?? propertyType) == typeof(JsonElement)) {
            obj["$comment"] = "Open payload: this member's shape is decided by an id named elsewhere in the document, never by this generator.";

            return;
        }

        if (TryGetNodeConverter(
            converter: out var nodeConverter,
            propertyType: propertyType
        )) {
            // The converter describes its own node; an object arm it references is exported through the same
            // transform so its members are described and hoisted like any other shape.
            var shape = BuildConverterShape(
                converter: nodeConverter,
                propertyType: propertyType,
                run: run
            );

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
    // A member-level converter narrows one member below its type's own vocabulary — a comparison is an ExpressionOp
    // restricted to six names — so its tokens replace whatever the type's node listed, and the node is recorded as
    // the converter's own shape: titled, hoisted, described and typed for the converter
    // (ExpressionComparisonJsonConverter is ExpressionComparison), never as the whole enum.
    private static bool TryApplyMemberVocabulary(JsonObject obj, Type propertyType, JsonConverter? memberConverter, ExportRun run) {
        if (memberConverter is not IJsonSchemaStringConverter { SchemaTokens: { Count: > 0 } tokens }) {
            return false;
        }

        var nullable = (Nullable.GetUnderlyingType(nullableType: propertyType) is not null);
        var enumArray = new JsonArray();

        foreach (var token in tokens) {
            enumArray.Add(item: token);
        }

        if (nullable) {
            enumArray.Add(item: null);
        }

        obj.Remove(propertyName: "anyOf");
        obj["type"] = (nullable
            ? new JsonArray(
                "string",
                "null"
            )
            : "string"
        );
        obj["enum"] = enumArray;
        run.TypesByNode[obj] = memberConverter.GetType();
        StampTitle(
            obj: obj,
            type: memberConverter.GetType()
        );

        return true;
    }
    // A node converter's shape, owned by the caller. In the split export each (type, open types) shape is built once
    // and every occurrence takes a typed copy; the unsplit exports build every occurrence, since their nested exports
    // record where each one first appears.
    private static JsonObject BuildConverterShape(IJsonSchemaNodeConverter converter, Type propertyType, ExportRun run) {
        if (run.Nested is not null) {
            return converter.BuildSchema(exportType: type => ExportNested(
                run: run,
                type: type
            ));
        }

        var memos = (CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: run.InlineShapes,
            exists: out _,
            key: (Nullable.GetUnderlyingType(nullableType: propertyType) ?? propertyType)
        ) ??= []);
        JsonObject? shape = null;

        foreach (var (open, known) in memos) {
            if (open.SetEquals(other: run.InlineOpen)) {
                shape = known;

                break;
            }
        }

        if (shape is null) {
            shape = converter.BuildSchema(exportType: type => ExportNested(
                run: run,
                type: type
            ));
            memos.Add(item: (run.InlineOpen.ToHashSet(), shape));
        }

        return ((JsonObject)CloneTyped(
            node: shape,
            typesByNode: run.TypesByNode
        ));
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
    private static string ChooseDefName(Type? type, bool allowsNull, HashSet<string> usedNames) {
        var baseName = ((type is not null)
            ? FriendlyTypeName(type: type)
            : "Shape"
        );

        if (usedNames.Add(item: baseName)) {
            return baseName;
        }

        // The exporter materializes T and nullable T as distinct shapes but reports the same TypeInfo.Type for
        // both. Give that meaningful distinction a meaningful name instead of leaking traversal order through a
        // numeric suffix in the generated schema.
        var qualifiedName = $"{baseName}{(allowsNull
            ? "Nullable"
            : "NonNullable")}";

        if (usedNames.Add(item: qualifiedName)) {
            return qualifiedName;
        }

        for (var suffix = 2; ; suffix++) {
            var candidate = $"{qualifiedName}{suffix}";

            if (usedNames.Add(item: candidate)) {
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
    private static (JsonObject Root, ExportRun Run) ExportMergedWithTypes() {
        var run = new ExportRun(
            index: XmlDocIndex.Value,
            nested: null
        );
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            node: node,
            run: run
        ),
        };
        var schema = WorldJsonContext.Default.Options.GetJsonSchemaAsNode(
            exporterOptions: exporterOptions,
            type: typeof(WorldDefinition)
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
            value: "Puck world definition (puck.world.definition.v1)"
        );

        foreach (var (propertyName, value) in generated) {
            root.Add(
                propertyName: propertyName,
                value: value
            );
        }

        return (root, run);
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
    // Keyed by the type alone so the reflection walk runs on a parameter (IL2070, which the browser publish records)
    // rather than on a tuple field (IL2080, which it does not); the first property to claim a name keeps it.
    private static PropertyInfo? FindPropertyByJsonName(Type ownerType, string jsonName) =>
        PropertiesByJsonName.GetOrAdd(
            key: ownerType,
            valueFactory: static owner => {
                var properties = new Dictionary<string, PropertyInfo>(comparer: StringComparer.Ordinal);

                foreach (var property in owner.GetProperties(bindingAttr: BindingFlags.Public | BindingFlags.Instance)) {
                    properties.TryAdd(
                        key: JsonNamingPolicy.CamelCase.ConvertName(name: property.Name),
                        value: property
                    );
                }

                return properties;
            }
        ).GetValueOrDefault(key: jsonName);
    // Repoints a leftover recursive marker — a $ref that still carries its ORIGINAL absolute document pointer,
    // because the hoist treats reference nodes as opaque rather than as hashable content — at whatever def its
    // target was hoisted to. Every such target is an exporter $ref target, and the hoist forces exactly those into
    // a def regardless of count, so a match here is guaranteed by construction.
    private static void FixupCyclicMarkers(JsonNode node, Dictionary<string, string> targetDefNames) {
        if (node is JsonObject obj) {
            if (ContainsRefKey(obj: obj)) {
                var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

                if (!value.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "$defs/"
                )) {
                    if (!targetDefNames.TryGetValue(
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
                            targetDefNames: targetDefNames
                        );
                    }
                }

                return;
            }

            foreach (var (_, child) in obj.ToList()) {
                if (child is not null) {
                    FixupCyclicMarkers(
                        node: child,
                        targetDefNames: targetDefNames
                    );
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is not null) {
                    FixupCyclicMarkers(
                        node: child,
                        targetDefNames: targetDefNames
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
    private static string FriendlyTypeName(Type type) =>
        FriendlyTypeNames.GetOrAdd(
            key: type,
            valueFactory: FriendlyTypeNameUncached
        );
    private static string FriendlyTypeNameUncached(Type type) {
        var underlying = Nullable.GetUnderlyingType(nullableType: type);

        if (underlying is not null) {
            type = underlying;
        }

        if (
            typeof(IJsonSchemaStringConverter).IsAssignableFrom(c: type) &&
            type.Name.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: nameof(JsonConverter)
            )
        ) {
            // A member-level vocabulary converter's own shape is named for what it reads (see TryApplyMemberVocabulary).
            return type.Name[..^nameof(JsonConverter).Length];
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
    private static bool IsCollectionLike(Type genericDefinition) =>
        ((genericDefinition == typeof(List<>)) ||
        (genericDefinition == typeof(IReadOnlyList<>)) ||
        (genericDefinition == typeof(IReadOnlyCollection<>)) ||
        (genericDefinition == typeof(IEnumerable<>)) ||
        (genericDefinition == typeof(ICollection<>)) ||
        (genericDefinition == typeof(IList<>)));
    // ---- bundling --------------------------------------------------------------------------------------------
    // Bundle() and its own titled-shape hoist live in WorldSchema.Bundle.cs.

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
    // The universe StampTitle draws a def-worthy name from: a document-model type this repository declares, never
    // a collection (its element gets its own node and title), a generic instantiation, an array, or a BCL/System.Text.Json
    // type (JsonElement, JsonNode, string, DateTime, Guid, ...) — none of which live under the Puck namespace.
    private static bool IsTitledType(Type type) {
        var underlying = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        return (
            !underlying.IsGenericType &&
            !underlying.IsArray &&
            (underlying.Namespace is { } ns) &&
            ns.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "Puck"
        ) &&
            (underlying.IsClass || underlying.IsValueType || underlying.IsEnum)
        );
    }
    // Every listed file must load: a missing one would silently drop every description its assembly owns, and
    // the schema check would then fail on content rather than name the absent file.
    private static XmlDocumentation? LoadXmlDocIndex(IReadOnlyList<(string FileName, Type Anchor)> files) {
        var index = new Dictionary<string, XElement>(comparer: StringComparer.Ordinal);

        foreach (var (fileName, anchor) in files) {
            var path = LocateXmlDocumentationFile(
                anchor: anchor,
                fileName: fileName
            );

            if (path is null) {
                return null;
            }

            try {
                var document = XDocument.Load(uri: path);
                var members = document.Root?.Element(name: "members")?.Elements(name: "member");

                if (members is null) {
                    return null;
                }

                foreach (var member in members) {
                    if (((string?)member.Attribute(name: "name")) is { } name) {
                        index[name] = member;
                    }
                }
            } catch (Exception exception) when (((exception is IOException) || (exception is System.Xml.XmlException) || (exception is UnauthorizedAccessException))) {
                return null;
            }
        }

        return new XmlDocumentation(members: index);
    }
    // Beside the executable (PuckPaths.Shipped) covers every real caller (puck.exe's own output directory, where a
    // referenced project's generated XML doc file is copied alongside its DLL — the same pattern
    // Puck.Maths.xml already rides for this CLI); the owning assembly's own location is the fallback for a host
    // that loads it from elsewhere.
    private static string? LocateXmlDocumentationFile(Type anchor, string fileName) {
        var beside = PuckPaths.Shipped(relativePath: fileName);

        if (File.Exists(path: beside)) {
            return beside;
        }

        var assemblyLocation = anchor.Assembly.Location;

        if (string.IsNullOrEmpty(value: assemblyLocation)) {
            return null;
        }

        var besideAssembly = Path.Combine(
            path1: (Path.GetDirectoryName(path: assemblyLocation) ?? string.Empty),
            path2: fileName
        );

        return (File.Exists(path: besideAssembly)
            ? besideAssembly
            : null
        );
    }
    private static string MemberDocId(MemberInfo member) =>
        $"P:{FormatDeclaringType(type: member.DeclaringType!)}.{member.Name}";
    // The disambiguated spelling ResolveTitleCollisions falls back to when two distinct CLR types share a
    // FriendlyTypeName — deterministic from the type's own identity, never from where StampTitle happened to visit
    // it first, so the same collision resolves to the same two names on every run.
    private static string NamespacePrefixedTitle(Type type) {
        var segment = ((type.Namespace ?? string.Empty)
            .Split(separator: '.')
            .LastOrDefault(predicate: static part => (part.Length > 0)) ?? string.Empty);

        return $"{segment}{FriendlyTypeName(type: type)}";
    }
    // Rebuilds obj with propertyName first, for human-readable output (a description reads best leading an
    // object, ahead of its type/properties/required keywords). JsonObject preserves insertion order, and Clear()
    // detaches every child so each can be re-added to the same object without a "node already has a parent" error.
    private static void Prepend(JsonObject obj, string propertyName, JsonNode value) {
        // A converter may already supply this annotation; the outer property site replaces it.
        obj.Remove(propertyName: propertyName);
        obj.Insert(
            index: 0,
            propertyName: propertyName,
            value: value
        );
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
    private static string? ResolveDescription(JsonSchemaExporterContext context, XmlDocumentation index) {
        if (context.PropertyInfo is { AttributeProvider: MemberInfo member }) {
            return ResolveDescriptionForMember(
                index: index,
                member: member
            );
        }

        return TypeSummary(
            index: index,
            type: context.TypeInfo.Type
        );
    }
    // The property-branch half of ResolveDescription's own resolution order, factored out so
    // RestoreSkippedPropertyAnnotations — which has a reflected MemberInfo but no JsonSchemaExporterContext, since
    // the exporter never called back for the node it is fixing up — can resolve a description the SAME way.
    private static string? ResolveDescriptionForMember(MemberInfo member, XmlDocumentation index) =>
        index.MemberDescriptions.GetOrAdd(
            factoryArgument: index,
            key: member,
            valueFactory: static (member, index) => (TryGetSummary(
                index: index,
                memberDocId: MemberDocId(member: member),
                text: out var ownSummary
            )
                ? ownSummary
                : (TryGetParam(
                    index: index,
                    parameterName: member.Name,
                    typeDocId: TypeDocId(type: member.DeclaringType!),
                    text: out var paramSummary
                )
                    ? paramSummary
                    : null))
        );
    // The type's own <summary>, or null when it has none.
    private static string? TypeSummary(Type type, XmlDocumentation index) =>
        index.TypeSummaries.GetOrAdd(
            factoryArgument: index,
            key: type,
            valueFactory: static (type, index) => (TryGetSummary(
                index: index,
                memberDocId: TypeDocId(type: type),
                text: out var summary
            )
                ? summary
                : null)
        );
    // Runs once, over the whole merged document, after every node has its provisional StampTitle spelling —
    // a bare FriendlyTypeName, blind to any other type sharing it. Groups every stamped title by the DISTINCT
    // CLR types it names (Nullable<T> and T unwrap to the same type, so a nullable/non-nullable pair of one type
    // never counts as a collision); a group naming more than one type gets every one of its members rewritten to
    // NamespacePrefixedTitle, so the collision resolves the same way regardless of which occurrence the walk
    // happened to reach first. A prefixed spelling that still collides names two types this scheme cannot tell
    // apart — refused rather than silently handing one title to both.
    // A derived record is emitted in two shapes: as a polymorphic arm (a site declared as the base type carries the
    // "$type" const the discriminator adds) and bare (a site declared as the derived type itself carries none).
    // Both are the same CLR type, so ResolveTitleCollisions sees no collision, yet the bundle cannot name two
    // shapes with one title: the bare shape takes the suffix "Bare", and the arm keeps the type's own name.
    private static void ResolveDiscriminatorSplits(ExportRun run) {
        var nodesByTitle = new Dictionary<string, List<JsonObject>>(comparer: StringComparer.Ordinal);

        foreach (var (node, _) in run.TypesByNode) {
            if (
                (node is not JsonObject obj) ||
                (obj["title"] is not JsonValue titleValue) ||
                !titleValue.TryGetValue<string>(value: out var title)
            ) {
                continue;
            }

            if (!nodesByTitle.TryGetValue(
                key: title,
                value: out var list
            )) {
                list = [];
                nodesByTitle[title] = list;
            }

            list.Add(item: obj);
        }

        foreach (var (title, nodes) in nodesByTitle) {
            if (
                !nodes.Any(predicate: obj => CarriesDiscriminator(
                    obj: obj,
                    run: run
                )) ||
                nodes.All(predicate: obj => CarriesDiscriminator(
                    obj: obj,
                    run: run
                ))
            ) {
                continue;
            }

            foreach (var obj in nodes) {
                if (!CarriesDiscriminator(
                    obj: obj,
                    run: run
                )) {
                    obj["title"] = $"{title}Bare";
                }
            }
        }
    }
    private static bool CarriesDiscriminator(JsonObject obj, ExportRun run) =>
        ((ResolveInline(
            node: obj["properties"],
            run: run
        ) is JsonObject properties) && (ResolveInline(
            node: properties["$type"],
            run: run
        ) is JsonObject discriminator) && discriminator.ContainsKey(propertyName: "const"));
    private static void ResolveTitleCollisions(Dictionary<JsonNode, Type> typesByNode) {
        var typesByTitle = new Dictionary<string, HashSet<Type>>(comparer: StringComparer.Ordinal);

        foreach (var (node, type) in typesByNode) {
            if (
                (node is not JsonObject obj) ||
                (obj["title"] is not JsonValue titleValue) ||
                !titleValue.TryGetValue<string>(value: out var title)
            ) {
                continue;
            }

            var underlying = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

            if (!typesByTitle.TryGetValue(
                key: title,
                value: out var set
            )) {
                set = [];
                typesByTitle[title] = set;
            }

            set.Add(item: underlying);
        }

        var colliding = typesByTitle
            .Where(predicate: kv => (kv.Value.Count > 1))
            .Select(selector: kv => kv.Key)
            .ToHashSet(comparer: StringComparer.Ordinal);

        if (colliding.Count == 0) {
            return;
        }

        var resolved = new Dictionary<Type, string>();

        foreach (var (node, type) in typesByNode) {
            if (
                (node is not JsonObject obj) ||
                (obj["title"] is not JsonValue titleValue) ||
                !titleValue.TryGetValue<string>(value: out var title) ||
                !colliding.Contains(item: title)
            ) {
                continue;
            }

            var underlying = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

            if (!resolved.TryGetValue(
                key: underlying,
                value: out var newTitle
            )) {
                newTitle = NamespacePrefixedTitle(type: underlying);
                resolved[underlying] = newTitle;
            }

            obj["title"] = newTitle;
        }

        foreach (var group in resolved.GroupBy(
            keySelector: kv => kv.Value,
            comparer: StringComparer.Ordinal
        )) {
            if (group.Count() > 1) {
                throw new InvalidOperationException(message: $"schema: types {string.Join(
                    separator: ", ",
                    values: group.Select(selector: kv => kv.Key.FullName)
                )} all disambiguate to the same title '{group.Key}' — rename one of the CLR types.");
            }
        }
    }
    private static void RestoreSkippedProperty(JsonObject propertyObject, Type ownerType, string jsonName, ExportRun run) {
        var property = FindPropertyByJsonName(
            jsonName: jsonName,
            ownerType: ownerType
        );

        if (property is null) {
            return;
        }

        run.TypesByNode[propertyObject] = property.PropertyType;
        StampTitle(
            obj: propertyObject,
            type: property.PropertyType
        );

        ApplyCollectionVocabulary(
            obj: propertyObject,
            propertyType: property.PropertyType,
            run: run
        );

        if (!TryApplyMemberVocabulary(
            memberConverter: ((property.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType is { } converterType)
                ? (Activator.CreateInstance(type: converterType) as JsonConverter)
                : null
            ),
            obj: propertyObject,
            propertyType: property.PropertyType,
            run: run
        )) {
            ApplyConverterVocabulary(
                obj: propertyObject,
                propertyType: property.PropertyType,
                run: run
            );
        }

        if (
            (run.Index is not null) &&
            (ResolveDescriptionForMember(
            index: run.Index,
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
    private static void RestoreSkippedPropertyAnnotations(JsonNode node, ExportRun run) {
        // An inline export is walked where it is first reached, once for every occurrence.
        if (TryGetInlineRoot(
            node: node,
            root: out var inlineRoot
        )) {
            if (run.InlineRestored.Add(item: inlineRoot)) {
                RestoreSkippedPropertyAnnotations(
                    node: run.InlineRoots[inlineRoot],
                    run: run
                );
            }

            return;
        }

        // Creation documents own their serializer and annotation walk; WorldJsonContext's converter repairs do not apply.
        if (
            (node is JsonObject creation) &&
            (creation["$id"]?.ToString() == Puck.World.Authoring.CreationDocument.CurrentSchema)
        ) {
            return;
        }
        if (node is JsonObject obj) {
            if (
                run.TypesByNode.TryGetValue(
                key: obj,
                value: out var ownerType
            ) &&
                (obj["properties"] is JsonObject propertiesObject)
            ) {
                for (var index = 0; (index < propertiesObject.Count); index++) {
                    var (jsonName, propertyValue) = propertiesObject.GetAt(index: index);

                    if (
                        (ResolveInline(
                        node: propertyValue,
                        run: run
                    ) is JsonObject propertyObject) &&
                        !ContainsRefKey(obj: propertyObject) &&
                        !run.TypesByNode.ContainsKey(key: propertyObject)
                    ) {
                        // An inline export's root is annotated by Transform like any exported node, and one shared
                        // by occurrences under different owners could not take a per-owner repair.
                        if (!ReferenceEquals(
                            objA: propertyObject,
                            objB: propertyValue
                        )) {
                            throw new InvalidOperationException(message: $"schema: the inline export at '{jsonName}' was left unannotated by the exporter.");
                        }

                        RestoreSkippedProperty(
                            jsonName: jsonName,
                            ownerType: ownerType,
                            propertyObject: propertyObject,
                            run: run
                        );
                    }
                }
            }

            for (var index = 0; (index < obj.Count); index++) {
                if (obj.GetAt(index: index).Value is { } child) {
                    RestoreSkippedPropertyAnnotations(
                        node: child,
                        run: run
                    );
                }
            }
        } else if (node is JsonArray arr) {
            for (var index = 0; (index < arr.Count); index++) {
                if (arr[index] is { } child) {
                    RestoreSkippedPropertyAnnotations(
                        node: child,
                        run: run
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
    private static SplitSchema Split(JsonObject reduced, JsonObject common) {
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
            Common: new JsonObject { ["$defs"] = common }
        );
    }
    // Names a node after the CLR type it came from, so the TypeScript types (via the bundle's own $defs, see Bundle
    // and ToTypeScript) and a person reading the schema both see WorldStateRow rather than an anonymous literal. Every
    // occurrence of a titled type is stamped alike; ResolveTitleCollisions corrects a same-name clash across
    // distinct types once the whole document is known. Left untouched for anything IsTitledType refuses — a
    // collection, a primitive, or a converter-hidden shape with no CLR identity of its own worth naming.
    private static void StampTitle(JsonObject obj, Type type) {
        var underlying = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        if (IsTitledType(type: underlying)) {
            obj["title"] = FriendlyTypeName(type: underlying);
        }
    }
    // Runs once per exported node, bottom-up (children before parents). Attaches a description resolved from the
    // assembly's XML documentation, teaches a custom-converted node its own "type"/"enum" (see
    // ApplyConverterVocabulary), and — at the document root only, when the root type has one — the Extensions bag's
    // reserved-prefix carve-out.
    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node, ExportRun run) {
        if (
            (node is JsonObject alreadyRef) &&
            alreadyRef.ContainsKey(propertyName: "$ref")
        ) {
            // Already deduplicated to an earlier occurrence (a recursive or a structurally repeated type, e.g.
            // ActionPredicate.All nesting ActionPredicate) — nothing of its own left to describe or constrain.
            return node;
        }

        var description = ((run.Index is null)
            ? null
            : ResolveDescription(
                context: context,
                index: run.Index
            )
        );
        var obj = AsObjectNode(node: ref node);

        if (obj is null) {
            // A `false` schema (never matches) — nothing sensible to annotate.
            return node;
        }

        run.TypesByNode[obj] = context.TypeInfo.Type;

        // The document root carries its own hand-written title ("Puck world definition (puck.world.definition.v1)" and
        // its projection/silo counterparts, added after Transform runs) — StampTitle would collide with it.
        if (context.Path.Length > 0) {
            StampTitle(
                obj: obj,
                type: context.TypeInfo.Type
            );
        }

        ApplyCollectionVocabulary(
            obj: obj,
            run: run,
            typeInfo: context.TypeInfo
        );

        if (!TryApplyMemberVocabulary(
            memberConverter: context.PropertyInfo?.CustomConverter,
            obj: obj,
            propertyType: context.TypeInfo.Type,
            run: run
        )) {
            ApplyConverterVocabulary(
                obj: obj,
                propertyType: context.TypeInfo.Type,
                run: run
            );
        }

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
    private static bool TryGetParam(XmlDocumentation index, string parameterName, string typeDocId, out string? text) {
        if (index.Members.TryGetValue(
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

    // The model assemblies' XML documentation by member id, with each rendered text memoized: the exporter asks for
    // the same member's text at every occurrence of its type.
    private sealed class XmlDocumentation(Dictionary<string, XElement> members) {
        public ConcurrentDictionary<MemberInfo, string?> MemberDescriptions { get; } = new();
        public Dictionary<string, XElement> Members { get; } = members;
        public ConcurrentDictionary<string, string?> Summaries { get; } = new(comparer: StringComparer.Ordinal);
        public ConcurrentDictionary<Type, string?> TypeSummaries { get; } = new();
    }
    // One export's state: the XML documentation, the CLR type Transform recorded for every node it annotated, and
    // how a node converter's nested export is shared (see ExportNested).
    private sealed class ExportRun(XmlDocumentation? index, NestedExports? nested) {
        public XmlDocumentation? Index { get; } = index;
        // Split export only: every inline export by the types it was made inside of, as an index into InlineRoots.
        public Dictionary<Type, List<(HashSet<Type> Open, int Root)>> InlineExports { get; } = [];
        // The types whose inline export is being built.
        public HashSet<Type> InlineOpen { get; } = [];
        // Which inline exports RestoreSkippedPropertyAnnotations has already walked.
        public HashSet<int> InlineRestored { get; } = [];
        public List<JsonNode> InlineRoots { get; } = [];
        // A node converter's shape by the (Nullable-unwrapped) type it describes and the types it was built inside
        // of; like an inline export, it is a pure function of the two.
        public Dictionary<Type, List<(HashSet<Type> Open, JsonObject Shape)>> InlineShapes { get; } = [];
        public NestedExports? Nested { get; } = nested;
        public Dictionary<JsonNode, Type> TypesByNode { get; } = new(comparer: ReferenceEqualityComparer.Instance);
    }
    // A nested export's occurrences within one unsplit document: the first exports in full and every later one is
    // a placeholder ResolveNestedRefs repoints at it by JSON pointer once the tree is final — the same device the
    // exporter's own cache uses for a repeated type. The split export shares inline exports instead (ExportNested).
    private sealed class NestedExports {
        public Dictionary<Type, JsonNode> First { get; } = [];
        public List<(Type Type, JsonObject Placeholder)> Later { get; } = [];
        // The types whose first export is still being built. A shape that reaches itself through its own members (a
        // channel reference's expression argument reads channel references) refers back to that export.
        public HashSet<Type> Open { get; } = [];
    }

    // Exports type's schema for a node converter's arm. The unsplit exports share one first export per type through
    // run.Nested. The split export inlines every occurrence; an inline export is a pure function of the types it is
    // already inside of, so each (type, open types) export is made once and every occurrence is a placeholder for it.
    private static JsonNode ExportNested(Type type, ExportRun run) {
        if (run.Nested is { } nested) {
            if (nested.First.ContainsKey(key: type) || nested.Open.Contains(item: type)) {
                var placeholder = new JsonObject();

                nested.Later.Add(item: (type, placeholder));

                return placeholder;
            }

            nested.Open.Add(item: type);

            var first = ExportType(
                run: run,
                type: type
            );

            nested.Open.Remove(item: type);
            nested.First.Add(
                key: type,
                value: first
            );

            return first;
        }

        // A shape that reaches itself inside an inline export has no first export to point back at, so it is left
        // open at the point it recurs and described where it first appears.
        if (run.InlineOpen.Contains(item: type)) {
            return new JsonObject { ["$comment"] = $"{type.Name} holds itself here; its shape is the one described where it first appears." };
        }

        var memos = (CollectionsMarshal.GetValueRefOrAddDefault(
            dictionary: run.InlineExports,
            exists: out _,
            key: type
        ) ??= []);

        foreach (var (open, root) in memos) {
            if (open.SetEquals(other: run.InlineOpen)) {
                return InlinePlaceholder(root: root);
            }
        }

        var entryOpen = run.InlineOpen.ToHashSet();

        run.InlineOpen.Add(item: type);

        var exported = ExportType(
            run: run,
            type: type
        );

        run.InlineOpen.Remove(item: type);
        run.InlineRoots.Add(item: exported);
        memos.Add(item: (entryOpen, (run.InlineRoots.Count - 1)));

        return InlinePlaceholder(root: (run.InlineRoots.Count - 1));
    }

    // An inline export's occurrence: a one-member object naming the export in ExportRun.InlineRoots. The export
    // itself is never attached to the document, so every occurrence shares it; each pass reads through the
    // placeholder (ResolveInline), and the hoist expands it where it stands.
    private const string InlineExportKey = "$puck:inline";

    private static JsonObject InlinePlaceholder(int root) =>
        new() { [InlineExportKey] = root };
    private static bool TryGetInlineRoot(JsonNode? node, out int root) {
        if (
            (node is JsonObject { Count: 1 } obj) &&
            (obj.GetAt(index: 0) is { Key: InlineExportKey, Value: JsonValue id })
        ) {
            root = id.GetValue<int>();

            return true;
        }

        root = -1;

        return false;
    }
    private static JsonNode? ResolveInline(JsonNode? node, ExportRun run) =>
        (TryGetInlineRoot(
            node: node,
            root: out var root
        )
            ? run.InlineRoots[root]
            : node
        );
    private static JsonNode ExportType(Type type, ExportRun run) {
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            node: node,
            run: run
        ),
            TreatNullObliviousAsNonNullable = true,
        };

        return WorldJsonContext.Default.Options.GetJsonSchemaAsNode(
            exporterOptions: exporterOptions,
            type: type
        );
    }
    // A deep copy of node in which every copy carries its original's recorded type.
    private static JsonNode CloneTyped(JsonNode node, Dictionary<JsonNode, Type> typesByNode) {
        JsonNode clone;

        if (node is JsonObject obj) {
            var copy = new JsonObject();

            for (var index = 0; (index < obj.Count); index++) {
                var (name, value) = obj.GetAt(index: index);

                copy[name] = ((value is not null)
                    ? CloneTyped(
                        node: value,
                        typesByNode: typesByNode
                    )
                    : null
                );
            }

            clone = copy;
        } else if (node is JsonArray arr) {
            var copy = new JsonArray();

            for (var index = 0; (index < arr.Count); index++) {
                var value = arr[index];

                copy.Add(item: ((value is not null)
                    ? CloneTyped(
                        node: value,
                        typesByNode: typesByNode
                    )
                    : null));
            }

            clone = copy;
        } else {
            clone = node.DeepClone();
        }

        if (typesByNode.TryGetValue(
            key: node,
            value: out var type
        )) {
            typesByNode[clone] = type;
        }

        return clone;
    }
    private static void ResolveNestedRefs(JsonObject root, NestedExports nested) {
        foreach (var (type, placeholder) in nested.Later) {
            var pointer = (JsonPointerOf(
                node: nested.First[type],
                root: root
            ) ?? throw new InvalidOperationException(message: $"the first export of {type.Name} is no longer in the document"));

            placeholder["$ref"] = $"#{pointer}";
        }
    }
    // The document-absolute JSON pointer of node under root, by parent-chain walk; null when node is not in root.
    private static string? JsonPointerOf(JsonNode node, JsonObject root) {
        var segments = new List<string>();
        var cursor = node;

        while (!ReferenceEquals(
            objA: cursor,
            objB: root
        )) {
            var parent = cursor.Parent;

            if (parent is null) {
                return null;
            }

            segments.Add(item: ((parent is JsonArray array)
                ? array.IndexOf(item: cursor).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                : cursor.GetPropertyName().Replace(
                    comparisonType: StringComparison.Ordinal,
                    newValue: "~0",
                    oldValue: "~"
                ).Replace(
                    comparisonType: StringComparison.Ordinal,
                    newValue: "~1",
                    oldValue: "/"
                )));
            cursor = parent;
        }

        segments.Reverse();

        return string.Concat(values: segments.Select(selector: static segment => ("/" + segment)));
    }
    private static JsonConverter? ResolveConverter(Type type) =>
        Converters.GetOrAdd(
            key: (Nullable.GetUnderlyingType(nullableType: type) ?? type),
            valueFactory: static effectiveType => {
                try {
                    return WorldJsonContext.Default.Options.GetConverter(typeToConvert: effectiveType);
                } catch (NotSupportedException) {
                    return null;
                }
            }
        );
    private static JsonTypeInfo? ResolveTypeInfo(Type type) =>
        TypeInfos.GetOrAdd(
            key: type,
            valueFactory: static type => {
                try {
                    return WorldJsonContext.Default.Options.GetTypeInfo(type: type);
                } catch (NotSupportedException) {
                    return null;
                }
            }
        );
    private static bool TryGetNodeConverter(Type propertyType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IJsonSchemaNodeConverter? converter) {
        var resolved = ResolveConverter(type: propertyType);

        if (resolved is null) {
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
        var converter = ResolveConverter(type: propertyType);

        if (converter is null) {
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
        var converter = ResolveConverter(type: propertyType);

        if (converter is null) {
            types = [];

            return false;
        }

        if (
            (converter is not IJsonSchemaTypeConverter vocabulary) ||
            (vocabulary.SchemaTypes.Count == 0)
        ) {
            types = [];

            return false;
        }

        types = vocabulary.SchemaTypes;

        return true;
    }
    private static bool TryGetSummary(XmlDocumentation index, string memberDocId, out string? text) {
        text = index.Summaries.GetOrAdd(
            factoryArgument: index,
            key: memberDocId,
            valueFactory: static (memberDocId, index) => ((index.Members.TryGetValue(
                key: memberDocId,
                value: out var member
            ) && (member.Element(name: "summary") is { } summary))
                ? RenderDocText(root: summary)
                : null)
        );

        return (text is not null);
    }
    private static string TypeDocId(Type type) =>
        $"T:{FormatDeclaringType(type: type)}";

    /// <summary>Exports the split JSON Schema for <see cref="WorldDefinition"/>.</summary>
    /// <param name="postRenderExtensions">The shipped post-render extensions: <c>render.extensions[].id</c> becomes an
    /// enum over their ids and each entry's <c>config</c> validates against the schema of the set its id names.</param>
    /// <returns>A split the caller owns and may mutate. The schema is a pure function of the loaded model and the
    /// extensions, so it is generated once per process and extension set, and every call returns a deep copy.</returns>
    public static SplitSchema Export(IReadOnlyList<PostRenderExtensionSchema> postRenderExtensions) {
        ArgumentNullException.ThrowIfNull(postRenderExtensions);

        var key = string.Join(
            separator: '\n',
            values: postRenderExtensions.Select(selector: static extension => $"{extension.Id}\0{extension.ConfigSchema.ToJsonString()}")
        );
        var split = ExportCache.GetOrAdd(
            key: key,
            valueFactory: _ => new Lazy<SplitSchema>(valueFactory: () => ExportUncached(postRenderExtensions: postRenderExtensions))
        ).Value;

        return new SplitSchema(
            Common: split.Common.DeepClone().AsObject(),
            Root: split.Root.DeepClone().AsObject(),
            Sections: [.. split.Sections.Select(selector: static section => (section.Name, section.Node.DeepClone()))]
        );
    }

    private static SplitSchema ExportUncached(IReadOnlyList<PostRenderExtensionSchema> postRenderExtensions) {
        var (merged, run) = ExportMergedWithTypes();

        ResolveTitleCollisions(typesByNode: run.TypesByNode);

        ResolveDiscriminatorSplits(run: run);

        ApplySelfIdentification(
            root: merged,
            schemaVersion: SchemaId
        );

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
            run: run
        );

        // Fully expand every $ref the exporter itself emitted for a repeated (but non-recursive) type, so duplicate
        // content the exporter caught and duplicate content it didn't (polymorphic union arms bypass its cache — see
        // the class doc) are grouped alike, then hoist every repeated shape into common.schema.json.
        var hoist = new HoistPass(
            merged: merged,
            run: run
        );
        var reduced = hoist.Run(merged: merged);
        var targetDefNames = hoist.TargetDefNames();

        FixupCyclicMarkers(
            node: reduced,
            targetDefNames: targetDefNames
        );
        FixupCyclicMarkers(
            node: hoist.CommonDefs,
            targetDefNames: targetDefNames
        );

        FinalizeRefs(
            insideCommon: false,
            node: reduced
        );
        FinalizeRefs(
            node: hoist.CommonDefs,
            insideCommon: true
        );

        return Split(
            reduced: reduced,
            common: hoist.CommonDefs
        );
    }

    /// <summary>Exports the JSON Schema for <see cref="WorldProjectionDocument"/> as one document. Unsplit,
    /// deliberately: the projection has no top-level section a person opens on its own, so the split
    /// <see cref="WorldDefinition"/> takes buys nothing here.</summary>
    /// <param name="postRenderExtensions">The shipped post-render extensions, applied as in <see cref="Export"/>.</param>
    /// <returns>The generated schema root.</returns>
    public static JsonObject ExportProjection(IReadOnlyList<PostRenderExtensionSchema> postRenderExtensions) {
        ArgumentNullException.ThrowIfNull(postRenderExtensions);

        var nested = new NestedExports();
        var run = new ExportRun(
            index: XmlDocIndex.Value,
            nested: nested
        );
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            node: node,
            run: run
        ),
        };
        var schema = WorldJsonContext.Default.Options.GetJsonSchemaAsNode(
            exporterOptions: exporterOptions,
            type: typeof(WorldProjectionDocument)
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
            node: root,
            run: run
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
    /// <summary>Exports the JSON Schema for <see cref="WorldCountersReport"/> as one document.</summary>
    /// <returns>The generated schema root.</returns>
    public static JsonObject ExportCountersReport() =>
        ExportDocument(
            schemaId: CountersReportSchemaId,
            title: $"Puck counters report ({CountersReportSchemaId})",
            type: typeof(WorldCountersReport)
        );
    /// <summary>Exports the JSON Schema for <see cref="WorldSiloDefinition"/> as one document. Unsplit, like
    /// <see cref="ExportProjection"/>: a six-field document has no section large enough to earn a file of its
    /// own.</summary>
    /// <returns>The generated schema root.</returns>
    public static JsonObject ExportSilo() =>
        ExportDocument(
            schemaId: SiloSchemaId,
            title: $"Puck world silo ({SiloSchemaId})",
            type: typeof(WorldSiloDefinition)
        );

    // Exports one unsplit document family carried by WorldJsonContext: identity first, then the generated shape with
    // its descriptions, nested exports resolved, and the self-identification block.
    private static JsonObject ExportDocument(Type type, string schemaId, string title, JsonSerializerOptions? options = null, XmlDocumentation? index = null) {
        var nested = new NestedExports();
        var run = new ExportRun(
            index: (index ?? XmlDocIndex.Value),
            nested: nested
        );
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(
            context: context,
            node: node,
            run: run
        ),
        };
        var schema = (options ?? WorldJsonContext.Default.Options).GetJsonSchemaAsNode(
            exporterOptions: exporterOptions,
            type: type
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
            value: schemaId
        );
        root.Add(
            propertyName: "title",
            value: title
        );

        foreach (var (propertyName, value) in generated) {
            root.Add(
                propertyName: propertyName,
                value: value
            );
        }

        RestoreSkippedPropertyAnnotations(
            node: root,
            run: run
        );
        ResolveNestedRefs(
            nested: nested,
            root: root
        );
        ApplySelfIdentification(
            root: root,
            schemaVersion: schemaId
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
}
