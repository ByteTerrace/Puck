using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
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
/// own: curated hover text pulled from the assembly's XML documentation (<c>Puck.World.Data.xml</c> beside
/// it), the document ROOT's one deliberate strictness exception — the <see cref="WorldDefinition.Extensions"/>
/// round-trip bag, which admits any <c>$</c>/<c>_</c>-prefixed key (<see cref="DocumentExtensionsPolicy"/>) and so
/// cannot be a flat <c>additionalProperties: false</c> — and the multi-file SPLIT: a small root plus one file per
/// top-level document section under <c>schema/</c>, with every subschema that appears more than once hoisted into
/// <c>schema/common.schema.json</c> under <c>$defs</c> so a person can open <c>kits.schema.json</c> and read the
/// schema for the <c>kits</c> section without wading through the other 43.
/// </summary>
public static class WorldSchema {
    /// <summary>The JSON Schema draft this document declares.</summary>
    public const string DraftUri = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The schema's stable identity — the same tag <see cref="WorldDefinition.SchemaVersion"/> carries.</summary>
    public const string SchemaId = WorldDefinition.SchemaVersion;

    /// <summary>The file name shared shapes live under, inside the sections directory.</summary>
    public const string CommonDefsFileName = "common.schema.json";

    /// <summary>The directory (relative to the root schema file) every section and <see cref="CommonDefsFileName"/> live in.</summary>
    public const string SectionsDirectoryName = "schema";

    private const string XmlDocumentationFileName = "Puck.World.Data.xml";

    // Below this compact-JSON length, a repeated node is left inlined: the $ref text would cost more than the
    // duplicate content saves, and a two/three-word leaf isn't a "shape" a reader benefits from finding by name.
    private const int HoistMinimumLength = 60;

    private static readonly Lazy<IReadOnlyDictionary<string, XElement>?> s_xmlDocIndex = new(valueFactory: LoadXmlDocIndex);

    /// <summary>Gets whether the assembly's XML documentation file was found and loaded. <see langword="false"/>
    /// means <see cref="Export"/> still succeeds but every node's <c>description</c> is omitted — a caller (the
    /// <c>puck schema</c> verb) reports that plainly rather than failing.</summary>
    public static bool HasXmlDocumentation =>
        s_xmlDocIndex.Value is not null;

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

    /// <summary>Exports the split JSON Schema for <see cref="WorldDefinition"/>.</summary>
    public static SplitSchema Export() {
        var (merged, typesByNode) = ExportMergedWithTypes();

        var pathIndex = new Dictionary<string, JsonNode>(comparer: StringComparer.Ordinal);

        IndexPaths(node: merged, path: "#", index: pathIndex);

        // Every $ref target the exporter's OWN output already carried, before any expansion — the set Bundle
        // needs to tell "the exporter recognized this shape as repeated" (one shared copy, referenced by document
        // pointer, is how the un-split generator represents it) apart from "independently regenerated every
        // occurrence" (full duplication is how the un-split generator represents THAT).
        var exporterRefTargets = new HashSet<string>(comparer: StringComparer.Ordinal);

        CollectRefTargets(node: merged, targets: exporterRefTargets);

        var originByNode = new Dictionary<JsonNode, string>(comparer: ReferenceEqualityComparer.Instance);
        var expandedTypes = new Dictionary<JsonNode, Type>(comparer: ReferenceEqualityComparer.Instance);

        // Fully expand every $ref the exporter itself emitted for a repeated (but non-recursive) type, so
        // duplicate content the exporter caught and duplicate content it didn't (polymorphic union arms bypass
        // its cache — see the class doc) both end up as plain inlined text, ready for one uniform dedup pass.
        // A GENUINELY recursive $ref (its own target is an ancestor of itself) cannot be expanded — inlining it
        // would never terminate — so it is left as a marker for the fixup pass below.
        var expanded = (JsonObject)ExpandRefs(
            node: merged,
            pathIndex: pathIndex,
            typesByNode: typesByNode,
            expandedTypes: expandedTypes,
            originByNode: originByNode,
            activePaths: new HashSet<string>(comparer: StringComparer.Ordinal),
            currentPath: "#");

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
            // Every path the exporter's OWN output already referenced via $ref — including a recursive shape's
            // self-reference, which is exactly one such reference — has to be common-def-addressable in the split
            // output too, regardless of whether IsHoistCandidate's content-shape heuristic would otherwise have
            // picked it up (a bare List<string> "rows" wrapper, for instance, carries neither "properties" nor
            // "enum" nor "anyOf", but the exporter's own TypeInfo cache still shares it via $ref).
            ExporterRefTargets = exporterRefTargets,
        };

        CollectHoistGroups(node: expanded, state: state);

        foreach (var (candidateNode, text) in state.CandidateText) {
            var forced = state.OriginByNode.TryGetValue(key: candidateNode, value: out var candidateOrigin) && state.ExporterRefTargets.Contains(item: candidateOrigin);

            if ((state.RawTextCounts[text] >= 2) || forced) {
                state.NodeGroupKey[candidateNode] = text;
            }
        }

        var reduced = (JsonObject)HashConsWalk(node: expanded, state: state);

        FixupCyclicMarkers(node: reduced, state: state);
        FixupCyclicMarkers(node: state.CommonDefs, state: state);

        FinalizeRefs(node: reduced, insideCommon: false);
        FinalizeRefs(node: state.CommonDefs, insideCommon: true);

        // Several origins can map to the same def name — every member of the group gets an entry in
        // OriginalPathToDefName, not just the exporter's own canonical one. Prefer whichever origin the exporter
        // ITSELF had already pointed a $ref at (there is at most one per group in practice); fall back to null
        // (always fully duplicate at bundle time) only when none of the group's origins qualify.
        var defAnchors = new Dictionary<string, string?>(comparer: StringComparer.Ordinal);

        foreach (var (origin, name) in state.OriginalPathToDefName) {
            if (exporterRefTargets.Contains(item: origin)) {
                defAnchors[name] = origin;
            } else if (!defAnchors.ContainsKey(key: name)) {
                defAnchors[name] = null;
            }
        }

        return Split(reduced: reduced, common: state.CommonDefs, defAnchors: defAnchors);
    }

    // Every $ref target anywhere in node, regardless of what other keywords sit alongside "$ref" on the same
    // node (see TryGetAbsoluteRefTarget) — walked over the document exactly as the exporter produced it, before
    // any expansion.
    private static void CollectRefTargets(JsonNode node, HashSet<string> targets) {
        if (node is JsonObject obj) {
            if (TryGetAbsoluteRefTarget(refObj: obj, target: out var target)) {
                targets.Add(item: target);
            }

            foreach (var (key, value) in obj) {
                if (!string.Equals(a: key, b: "$ref", comparisonType: StringComparison.Ordinal) && (value is not null)) {
                    CollectRefTargets(node: value, targets: targets);
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var value in arr) {
                if (value is not null) {
                    CollectRefTargets(node: value, targets: targets);
                }
            }
        }
    }

    /// <summary>Exports a node's canonical text form: UTF-8 with no BOM, LF newlines, two-space indentation, and
    /// exactly one trailing newline — the same conventions <see cref="WorldDefinitionSerialization.Save"/> uses for
    /// a world document, so a checked-in artifact stays diffable and git-friendly, and two runs over an unchanged
    /// model produce byte-identical text.</summary>
    public static string ToCanonicalText(JsonNode node) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: stream, options: new JsonWriterOptions { Indented = true, NewLine = "\n" })) {
            node.WriteTo(writer: writer);
        }

        stream.WriteByte(value: (byte)'\n');

        return Encoding.UTF8.GetString(bytes: stream.ToArray());
    }

    /// <summary>Re-inlines a <see cref="SplitSchema"/> into the single-file equivalent it was split from, reproducing
    /// exactly what the un-split generator itself would emit for the same model. Every section <c>$ref</c> is
    /// substituted with its content. A shared-shape <c>$ref</c> follows <see cref="SplitSchema.DefAnchors"/>: a def
    /// the exporter itself recognized as repeated (its anchor is non-null) gets exactly one physical copy, at the
    /// document-absolute position its anchor names, with every OTHER reference — including a recursive shape's own
    /// self-reference — repointed at that position by plain JSON pointer, precisely how the un-split generator's own
    /// TypeInfo cache already expresses it; a def with no anchor (an independently-regenerated polymorphic union
    /// arm, e.g. <c>ActionPredicate.CompareState</c>, which the exporter's cache never catches) is fully duplicated
    /// at every site instead, matching what the un-split generator actually produced there.</summary>
    public static JsonObject Bundle(SplitSchema split) {
        var root = (JsonObject)split.Root.DeepClone()!;
        var sectionsByName = split.Sections.ToDictionary(keySelector: s => s.Name, elementSelector: s => s.Node, comparer: StringComparer.Ordinal);
        var propertiesObject = (JsonObject)root["properties"]!;
        var defs = (JsonObject)((JsonObject)split.Common.DeepClone()!)["$defs"]!;

        foreach (var name in propertiesObject.Select(selector: kv => kv.Key).ToList()) {
            propertiesObject.Remove(propertyName: name);

            var sectionContent = (JsonNode)sectionsByName[name].DeepClone()!;

            propertiesObject[name] = InlineBundleRefs(node: sectionContent, currentPath: $"#/properties/{EscapePointerSegment(segment: name)}", defs: defs, anchors: split.DefAnchors);
        }

        return root;
    }

    // Runs once per exported node, bottom-up (children before parents). Attaches a description resolved from the
    // assembly's XML documentation, and — at the document root only — the Extensions bag's reserved-prefix carve-out.
    private static JsonNode Transform(JsonSchemaExporterContext context, IReadOnlyDictionary<string, XElement>? index, JsonNode node, Dictionary<JsonNode, Type> typesByNode) {
        if ((node is JsonObject alreadyRef) && alreadyRef.ContainsKey(propertyName: "$ref")) {
            // Already deduplicated to an earlier occurrence (a recursive or a structurally repeated type, e.g.
            // ActionPredicate.All nesting ActionPredicate) — nothing of its own left to describe or constrain.
            return node;
        }

        var description = (index is null) ? null : ResolveDescription(context: context, index: index);
        var obj = AsObjectNode(node: ref node);

        if (obj is null) {
            // A `false` schema (never matches) — nothing sensible to annotate.
            return node;
        }

        typesByNode[obj] = context.TypeInfo.Type;

        if (description is not null) {
            Prepend(obj: obj, propertyName: "description", value: description);
        }

        if (context.Path.Length == 0) {
            // The document root's one deliberate strictness exception. Extensions itself never appears as a
            // mapped `properties` entry — STJ routes a [JsonExtensionData] member around ordinary property
            // emission — so additionalProperties:false (already derived by the exporter from WorldJsonContext's
            // UnmappedMemberHandling.Disallow) would otherwise refuse the exact keys the loader accepts. This
            // pattern mirrors DocumentExtensionsPolicy.IsReservedKey by hand — JSON Schema has no way to name a
            // predicate, so keep the two in sync on sight.
            obj["patternProperties"] = new JsonObject {
                ["^[$_]"] = true,
            };
        }

        return node;
    }

    // Normalizes a schema node to an annotatable object, promoting a permissive `true` leaf (an unconstrained
    // schema — e.g. a custom-converted member the exporter cannot introspect, like Vector3) to `{}` in place so a
    // description can still attach without narrowing what the node accepts. Returns null for anything else (a
    // `false` schema), which nothing here needs to touch.
    private static JsonObject? AsObjectNode(ref JsonNode node) {
        if (node is JsonObject obj) {
            return obj;
        }

        if ((node is JsonValue value) && value.TryGetValue<bool>(value: out var isUnconstrained) && isUnconstrained) {
            var replacement = new JsonObject();

            node = replacement;

            return replacement;
        }

        return null;
    }

    // Rebuilds obj with propertyName first, for human-readable output (a description reads best leading an
    // object, ahead of its type/properties/required keywords). JsonObject preserves insertion order, and Clear()
    // detaches every child so each can be re-added to the same object without a "node already has a parent" error.
    private static void Prepend(JsonObject obj, string propertyName, JsonNode value) {
        var existing = obj.ToList();

        obj.Clear();
        obj.Add(propertyName: propertyName, value: value);

        foreach (var (key, existingValue) in existing) {
            obj.Add(propertyName: key, value: existingValue);
        }
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
            if (TryGetSummary(index: index, memberDocId: MemberDocId(member: member), text: out var ownSummary)) {
                return ownSummary;
            }

            if (TryGetParam(index: index, parameterName: member.Name, typeDocId: TypeDocId(type: member.DeclaringType!), text: out var paramSummary)) {
                return paramSummary;
            }

            return null;
        }

        return TryGetSummary(index: index, memberDocId: TypeDocId(type: context.TypeInfo.Type), text: out var typeSummary) ? typeSummary : null;
    }

    private static string MemberDocId(MemberInfo member) =>
        $"P:{FormatDeclaringType(type: member.DeclaringType!)}.{member.Name}";

    private static string TypeDocId(Type type) =>
        $"T:{FormatDeclaringType(type: type)}";

    // XML doc member IDs join a nested type with '.', while reflection's Type.FullName joins one with '+'.
    private static string FormatDeclaringType(Type type) =>
        (type.FullName ?? type.Name).Replace(oldChar: '+', newChar: '.');

    private static bool TryGetSummary(IReadOnlyDictionary<string, XElement> index, string memberDocId, out string? text) {
        if (index.TryGetValue(key: memberDocId, value: out var member) && (member.Element(name: "summary") is { } summary)) {
            text = RenderDocText(root: summary);

            return true;
        }

        text = null;

        return false;
    }

    private static bool TryGetParam(IReadOnlyDictionary<string, XElement> index, string parameterName, string typeDocId, out string? text) {
        if (index.TryGetValue(key: typeDocId, value: out var type)) {
            var param = type.Elements(name: "param")
                .FirstOrDefault(predicate: element => string.Equals(a: (string?)element.Attribute(name: "name"), b: parameterName, comparisonType: StringComparison.OrdinalIgnoreCase));

            if (param is not null) {
                text = RenderDocText(root: param);

                return true;
            }
        }

        text = null;

        return false;
    }

    // Strips XML doc markup down to hover-readable prose: <see cref="T:X.Y"/>/<see langword="null"/> become their
    // short name/word, <paramref name="X"/> becomes X, <para> becomes a paragraph break collapsed to one space
    // alongside everything else, and any other tag (<c>, <code>, <list>, ...) is dropped in favor of its own text.
    private static string RenderDocText(XElement root) {
        var builder = new StringBuilder();

        AppendChildren(builder: builder, element: root);

        return CollapseWhitespace(text: builder.ToString());
    }

    private static void AppendDocNode(StringBuilder builder, XNode node) {
        switch (node) {
            case XText text:
                builder.Append(value: text.Value);
                break;
            case XElement element:
                AppendDocElement(builder: builder, element: element);
                break;
        }
    }

    private static void AppendDocElement(StringBuilder builder, XElement element) {
        switch (element.Name.LocalName) {
            case "see":
            case "seealso":
                if ((string?)element.Attribute(name: "cref") is { } cref) {
                    builder.Append(value: ShortCrefName(cref: cref));
                } else if ((string?)element.Attribute(name: "langword") is { } langword) {
                    builder.Append(value: langword);
                } else {
                    AppendChildren(builder: builder, element: element);
                }
                break;
            case "paramref":
            case "typeparamref":
                if ((string?)element.Attribute(name: "name") is { } name) {
                    builder.Append(value: name);
                }
                break;
            case "para":
                builder.Append(value: ' ');
                AppendChildren(builder: builder, element: element);
                builder.Append(value: ' ');
                break;
            default:
                AppendChildren(builder: builder, element: element);
                break;
        }
    }

    private static void AppendChildren(StringBuilder builder, XElement element) {
        foreach (var child in element.Nodes()) {
            AppendDocNode(builder: builder, node: child);
        }
    }

    private static string ShortCrefName(string cref) {
        var colon = cref.IndexOf(value: ':');
        var body = (colon >= 0) ? cref[(colon + 1)..] : cref;
        var paren = body.IndexOf(value: '(');

        if (paren >= 0) {
            body = body[..paren];
        }

        var lastDot = body.LastIndexOf(value: '.');

        return (lastDot >= 0) ? body[(lastDot + 1)..] : body;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(separator: ' ', values: text.Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries)).Trim();

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
                if ((string?)member.Attribute(name: "name") is { } name) {
                    index[name] = member;
                }
            }

            return index;
        } catch (Exception exception) when ((exception is IOException) || (exception is System.Xml.XmlException) || (exception is UnauthorizedAccessException)) {
            return null;
        }
    }

    // Beside AppContext.BaseDirectory covers every real caller (puck.exe's own output directory, where a
    // referenced project's generated XML doc file is copied alongside its DLL — the same pattern
    // Puck.Maths.xml already rides for this CLI); the assembly's own location is the fallback for a host that
    // loads this assembly from elsewhere.
    private static string? LocateXmlDocumentationFile() {
        var beside = Path.Combine(AppContext.BaseDirectory, XmlDocumentationFileName);

        if (File.Exists(path: beside)) {
            return beside;
        }

        var assemblyLocation = typeof(WorldDefinition).Assembly.Location;

        if (string.IsNullOrEmpty(value: assemblyLocation)) {
            return null;
        }

        var besideAssembly = Path.Combine(Path.GetDirectoryName(path: assemblyLocation) ?? string.Empty, XmlDocumentationFileName);

        return File.Exists(path: besideAssembly) ? besideAssembly : null;
    }

    // ---- split / hoist machinery ---------------------------------------------------------------------------

    // Hash-consing bookkeeping threaded through one Export() call, across CollectHoistGroups (decides group
    // membership from raw, pre-dedup text) and HashConsWalk (the mutating pass that actually promotes one member
    // per group to a def and points every member at it). OriginalPathToDefName lets the cyclic-marker fixup pass
    // repoint a leftover recursive $ref (still carrying its ORIGINAL absolute document pointer) at whatever def
    // its target ended up becoming.
    private sealed class HoistState {
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
        public required Dictionary<JsonNode, string> CandidateText { get; init; }
        public required Dictionary<string, int> RawTextCounts { get; init; }
        public required Dictionary<string, string> GroupToDefName { get; init; }
        public required JsonObject CommonDefs { get; init; }
        public required Dictionary<JsonNode, Type> TypesByNode { get; init; }
        public required Dictionary<JsonNode, string> OriginByNode { get; init; }
        public required Dictionary<string, string> OriginalPathToDefName { get; init; }
        public required HashSet<string> UsedNames { get; init; }

        // "Must have a def, regardless of IsHoistCandidate/size" is an ORIGIN property (the exporter's own $ref
        // pointed at this document-absolute path), not a property of any one clone — a recursive
        // List<ActionPredicate> "predicates" wrapper is reached naturally at motion's own gate, AND via three
        // separate $ref expansions at onPress/onFact/rules (four independent clones, one shared origin), so
        // testing membership by NODE INSTANCE would catch at most one of the four. Checking the ORIGIN through
        // OriginByNode instead catches all of them uniformly.
        public required HashSet<string> ExporterRefTargets { get; init; }
    }

    private static (JsonObject Root, Dictionary<JsonNode, Type> TypesByNode) ExportMergedWithTypes() {
        var index = s_xmlDocIndex.Value;
        var typesByNode = new Dictionary<JsonNode, Type>(comparer: ReferenceEqualityComparer.Instance);
        var exporterOptions = new JsonSchemaExporterOptions {
            TransformSchemaNode = (context, node) => Transform(context: context, index: index, node: node, typesByNode: typesByNode),
        };
        var schema = WorldJsonContext.Default.Options.GetJsonSchemaAsNode(type: typeof(WorldDefinition), exporterOptions: exporterOptions);
        var root = schema.AsObject();
        var generated = root.ToList();

        root.Clear();
        root.Add(propertyName: "$schema", value: DraftUri);
        root.Add(propertyName: "$id", value: SchemaId);
        root.Add(propertyName: "title", value: "Puck world definition (puck.world.def.v1)");

        foreach (var (propertyName, value) in generated) {
            root.Add(propertyName: propertyName, value: value);
        }

        return (root, typesByNode);
    }

    private static void IndexPaths(JsonNode node, string path, Dictionary<string, JsonNode> index) {
        index[path] = node;

        if (node is JsonObject obj) {
            foreach (var (key, value) in obj) {
                if (value is not null) {
                    IndexPaths(node: value, path: $"{path}/{EscapePointerSegment(segment: key)}", index: index);
                }
            }
        } else if (node is JsonArray arr) {
            for (var i = 0; i < arr.Count; i++) {
                var value = arr[i];

                if (value is not null) {
                    IndexPaths(node: value, path: $"{path}/{i}", index: index);
                }
            }
        }
    }

    private static string EscapePointerSegment(string segment) =>
        ((segment.IndexOf(value: '~') >= 0) || (segment.IndexOf(value: '/') >= 0))
            ? segment.Replace(oldValue: "~", newValue: "~0").Replace(oldValue: "/", newValue: "~1")
            : segment;

    private static bool IsRefOnly(JsonObject obj) =>
        (obj.Count == 1) && (obj["$ref"] is JsonValue value) && value.TryGetValue<string>(value: out _);

    // Unlike IsRefOnly, tolerates sibling keywords — used for the raw, still-document-absolute markers Phase 2
    // can leave behind (a genuine cycle, possibly carrying an occurrence-specific "default" alongside "$ref").
    // Every $defs/-placeholder or finalized cross-file $ref THIS generator itself produces is always clean and
    // single-key, so IsRefOnly remains the right test everywhere else.
    private static bool ContainsRefKey(JsonObject obj) =>
        (obj["$ref"] is JsonValue value) && value.TryGetValue<string>(value: out _);

    // A $ref node the exporter emits can carry sibling keywords alongside "$ref" — draft 2020-12 allows it, and
    // the exporter uses it: a cached concrete TypeInfo's memoized $ref plus an occurrence-specific "default" (an
    // optional property's own default value, e.g. WorldPlacement.FaceSources = null). "$ref" alone is never a
    // safe test for "is this a reference".
    private static bool TryGetAbsoluteRefTarget(JsonObject refObj, out string target) {
        if ((refObj["$ref"] is JsonValue value) && value.TryGetValue<string>(value: out var text) && text.StartsWith(value: "#/", comparisonType: StringComparison.Ordinal)) {
            target = text;

            return true;
        }

        target = string.Empty;

        return false;
    }

    // Depth-first expansion of every $ref, with cycle detection via activePaths — the FULL ancestry chain of
    // document-absolute paths currently being expanded, ordinary nesting and $ref jumps alike (every call pushes
    // its own currentPath for its duration, not just a ref-jump target). A $ref whose target is already on that
    // chain is genuinely recursive (its target is an ancestor of itself, reached either by nesting down to it
    // directly or by jumping through one or more other $refs first) and is left as-is, an opaque marker for
    // FixupCyclicMarkers to repoint later. Every other $ref — including ones the exporter emitted purely because
    // the SAME concrete TypeInfo recurred in an unrelated branch — is fully substituted, so category-1
    // (exporter-deduplicated) and category-2 (independently regenerated, e.g. a polymorphic union arm) duplicates
    // both end up as plain inline text for one uniform dedup pass.
    private static JsonNode ExpandRefs(
        JsonNode node,
        Dictionary<string, JsonNode> pathIndex,
        Dictionary<JsonNode, Type> typesByNode,
        Dictionary<JsonNode, Type> expandedTypes,
        Dictionary<JsonNode, string> originByNode,
        HashSet<string> activePaths,
        string currentPath) {
        if ((node is JsonObject refObj) && TryGetAbsoluteRefTarget(refObj: refObj, target: out var targetPath)) {
            var siblings = refObj.Where(predicate: kv => !string.Equals(a: kv.Key, b: "$ref", comparisonType: StringComparison.Ordinal)).ToList();

            if (activePaths.Contains(item: targetPath)) {
                // A genuine cycle — preserve any sibling keywords (e.g. an occurrence-specific "default") next to
                // the still-raw marker; FixupCyclicMarkers repoints only the "$ref" value once its target has a def.
                var marker = new JsonObject { ["$ref"] = targetPath };

                // A sibling's own value may be a literal JSON null (e.g. an optional property's own
                // "default": null) — System.Text.Json.Nodes represents that as a C# null reference, not a
                // JsonValue wrapping null, so the key still has to be written, just without recursing into it.
                foreach (var (key, value) in siblings) {
                    marker[key] = (value is not null)
                        ? ExpandRefs(
                            node: value,
                            pathIndex: pathIndex,
                            typesByNode: typesByNode,
                            expandedTypes: expandedTypes,
                            originByNode: originByNode,
                            activePaths: activePaths,
                            currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}")
                        : null;
                }

                return marker;
            }

            if (!pathIndex.TryGetValue(key: targetPath, value: out var targetNode)) {
                throw new InvalidOperationException(message: $"schema: $ref '{targetPath}' does not resolve within the generated document.");
            }

            // The recursive call pushes targetPath itself (as its own currentPath) for the duration of expanding
            // the target — no separate push here, so a ref chain and ordinary nesting share the exact same
            // ancestry bookkeeping. Any sibling keywords on THIS occurrence (not part of the shared target) are
            // merged on top of the target's own clone afterward — an occurrence-specific "default" overrides
            // (harmlessly, when it agrees, as it always has so far) whatever the target's own natural position
            // already carries.
            var expandedTarget = ExpandRefs(
                node: targetNode,
                pathIndex: pathIndex,
                typesByNode: typesByNode,
                expandedTypes: expandedTypes,
                originByNode: originByNode,
                activePaths: activePaths,
                currentPath: targetPath);

            if ((siblings.Count == 0) || (expandedTarget is not JsonObject targetObj)) {
                return expandedTarget;
            }

            foreach (var (key, value) in siblings) {
                targetObj[key] = (value is not null)
                    ? ExpandRefs(
                        node: value,
                        pathIndex: pathIndex,
                        typesByNode: typesByNode,
                        expandedTypes: expandedTypes,
                        originByNode: originByNode,
                        activePaths: activePaths,
                        currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}")
                    : null;
            }

            return targetObj;
        }

        var pushed = activePaths.Add(item: currentPath);
        JsonNode result;

        try {
            if (node is JsonObject obj) {
                var newObj = new JsonObject();

                foreach (var (key, value) in obj) {
                    newObj[key] = (value is not null)
                        ? ExpandRefs(
                            node: value,
                            pathIndex: pathIndex,
                            typesByNode: typesByNode,
                            expandedTypes: expandedTypes,
                            originByNode: originByNode,
                            activePaths: activePaths,
                            currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}")
                        : null;
                }

                result = newObj;
            } else if (node is JsonArray arr) {
                var newArr = new JsonArray();

                for (var i = 0; i < arr.Count; i++) {
                    var value = arr[i];

                    newArr.Add(item: (value is not null)
                        ? ExpandRefs(
                            node: value,
                            pathIndex: pathIndex,
                            typesByNode: typesByNode,
                            expandedTypes: expandedTypes,
                            originByNode: originByNode,
                            activePaths: activePaths,
                            currentPath: $"{currentPath}/{i}")
                        : null);
                }

                result = newArr;
            } else {
                result = node.DeepClone()!;
            }
        } finally {
            if (pushed) {
                activePaths.Remove(item: currentPath);
            }
        }

        if (typesByNode.TryGetValue(key: node, value: out var type)) {
            expandedTypes[result] = type;
        }

        originByNode[result] = currentPath;

        return result;
    }

    // A hoist candidate is a genuine named SHAPE — an object type (has "properties"), an enum, or a $type union
    // (has "anyOf") — never a bare leaf (a plain {"type":"string"} with a coincidentally-matching description
    // isn't a shared concept worth a name), and long enough that a $ref costs fewer bytes than it saves.
    private static bool IsHoistCandidate(JsonObject obj) =>
        obj.ContainsKey(propertyName: "properties") || obj.ContainsKey(propertyName: "enum") || obj.ContainsKey(propertyName: "anyOf");

    private static string CompactSerialize(JsonNode node) {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(utf8Json: stream)) {
            node.WriteTo(writer: writer);
        }

        return Encoding.UTF8.GetString(bytes: stream.ToArray());
    }

    // Non-mutating pre-pass: computes every hoist candidate's RAW (pre-dedup) compact text and tallies how many
    // times each raw text occurs across the whole tree. A candidate whose ORIGIN is one of the exporter's own
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
                    CollectHoistGroups(node: child, state: state);
                }
            }

            var forced = state.OriginByNode.TryGetValue(key: obj, value: out var candidateOrigin) && state.ExporterRefTargets.Contains(item: candidateOrigin);

            if (!forced && !IsHoistCandidate(obj: obj)) {
                return;
            }

            var text = CompactSerialize(node: obj);

            if (!forced && (text.Length < HoistMinimumLength)) {
                return;
            }

            state.CandidateText[obj] = text;
            state.RawTextCounts[text] = state.RawTextCounts.GetValueOrDefault(key: text) + 1;
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is JsonObject or JsonArray) {
                    CollectHoistGroups(node: child, state: state);
                }
            }
        }
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
                    var replaced = HashConsWalk(node: child, state: state);

                    if (!ReferenceEquals(objA: replaced, objB: child)) {
                        obj[key] = replaced;
                    }
                }
            }

            if (!state.NodeGroupKey.TryGetValue(key: obj, value: out var groupKey)) {
                return obj;
            }

            if (!state.GroupToDefName.TryGetValue(key: groupKey, value: out var name)) {
                name = ChooseDefName(node: obj, state: state);
                state.CommonDefs[name] = obj.DeepClone();
                state.GroupToDefName[groupKey] = name;
            }

            if (state.OriginByNode.TryGetValue(key: obj, value: out var origin)) {
                state.OriginalPathToDefName[origin] = name;
            }

            return new JsonObject { ["$ref"] = $"$defs/{name}" };
        }

        if (node is JsonArray arr) {
            for (var i = 0; i < arr.Count; i++) {
                var child = arr[i];

                if (child is JsonObject or JsonArray) {
                    var replaced = HashConsWalk(node: child, state: state);

                    if (!ReferenceEquals(objA: replaced, objB: child)) {
                        arr[i] = replaced;
                    }
                }
            }

            return arr;
        }

        return node;
    }

    private static string ChooseDefName(JsonNode node, HoistState state) {
        var baseName = state.TypesByNode.TryGetValue(key: node, value: out var type) ? FriendlyTypeName(type: type) : "Shape";

        if (state.UsedNames.Add(item: baseName)) {
            return baseName;
        }

        for (var suffix = 2; ; suffix++) {
            var candidate = $"{baseName}{suffix}";

            if (state.UsedNames.Add(item: candidate)) {
                return candidate;
            }
        }
    }

    private static string FriendlyTypeName(Type type) {
        var underlying = Nullable.GetUnderlyingType(type);

        if (underlying is not null) {
            type = underlying;
        }

        if (!type.IsGenericType) {
            return type.Name;
        }

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();

        if ((arguments.Length == 1) && IsCollectionLike(genericDefinition: definition)) {
            return $"{FriendlyTypeName(type: arguments[0])}List";
        }

        var name = type.Name;
        var tick = name.IndexOf(value: '`');

        if (tick >= 0) {
            name = name[..tick];
        }

        return name + string.Concat(values: arguments.Select(selector: FriendlyTypeName));
    }

    private static bool IsCollectionLike(Type genericDefinition) =>
        (genericDefinition == typeof(List<>)) ||
        (genericDefinition == typeof(IReadOnlyList<>)) ||
        (genericDefinition == typeof(IReadOnlyCollection<>)) ||
        (genericDefinition == typeof(IEnumerable<>)) ||
        (genericDefinition == typeof(ICollection<>)) ||
        (genericDefinition == typeof(IList<>));

    // Repoints a leftover recursive marker — a $ref that still carries its ORIGINAL absolute document pointer,
    // because HashConsWalk skips ref-only nodes rather than treating their pointer text as hashable content — at
    // whatever def its target was hoisted to. Every such target is one of ExporterRefTargets, and CollectHoistGroups
    // forces exactly those into a def regardless of count, so a match here is guaranteed by construction.
    private static void FixupCyclicMarkers(JsonNode node, HoistState state) {
        if (node is JsonObject obj) {
            if (ContainsRefKey(obj: obj)) {
                var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

                if (!value.StartsWith(value: "$defs/", comparisonType: StringComparison.Ordinal)) {
                    if (!state.OriginalPathToDefName.TryGetValue(key: value, value: out var name)) {
                        throw new InvalidOperationException(message: $"schema: cyclic $ref '{value}' was never hoisted to a common definition.");
                    }

                    obj["$ref"] = $"$defs/{name}";
                }

                foreach (var (key, child) in obj.ToList()) {
                    if (!string.Equals(a: key, b: "$ref", comparisonType: StringComparison.Ordinal) && (child is not null)) {
                        FixupCyclicMarkers(node: child, state: state);
                    }
                }

                return;
            }

            foreach (var (_, child) in obj.ToList()) {
                if (child is not null) {
                    FixupCyclicMarkers(node: child, state: state);
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is not null) {
                    FixupCyclicMarkers(node: child, state: state);
                }
            }
        }
    }

    // Turns the internal "$defs/Name" placeholder every hoist produces into its final, file-aware form: a bare
    // same-document pointer for a reference that itself lives inside common.schema.json, a relative cross-file
    // $ref for one that lives in a section (or the root).
    private static void FinalizeRefs(JsonNode node, bool insideCommon) {
        if (node is JsonObject obj) {
            if (IsPlaceholderRef(obj: obj, name: out var name)) {
                obj["$ref"] = insideCommon ? $"#/$defs/{name}" : $"./{CommonDefsFileName}#/$defs/{name}";

                foreach (var (key, child) in obj.ToList()) {
                    if (!string.Equals(a: key, b: "$ref", comparisonType: StringComparison.Ordinal) && (child is not null)) {
                        FinalizeRefs(node: child, insideCommon: insideCommon);
                    }
                }

                return;
            }

            foreach (var (_, child) in obj.ToList()) {
                if (child is not null) {
                    FinalizeRefs(node: child, insideCommon: insideCommon);
                }
            }
        } else if (node is JsonArray arr) {
            foreach (var child in arr) {
                if (child is not null) {
                    FinalizeRefs(node: child, insideCommon: insideCommon);
                }
            }
        }
    }

    private static bool IsPlaceholderRef(JsonObject obj, out string name) {
        if (ContainsRefKey(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(value: "$defs/", comparisonType: StringComparison.Ordinal)) {
                name = value["$defs/".Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }

    private static SplitSchema Split(JsonObject reduced, JsonObject common, Dictionary<string, string?> defAnchors) {
        var propsObj = (JsonObject)reduced["properties"]!;
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
            if (string.Equals(a: key, b: "properties", comparisonType: StringComparison.Ordinal)) {
                finalRoot["properties"] = rootProperties;
                reduced.Remove(propertyName: "properties");

                continue;
            }

            var value = reduced[key];

            reduced.Remove(propertyName: key);
            finalRoot[key] = value;
        }

        return new SplitSchema(Root: finalRoot, Sections: sections, Common: new JsonObject { ["$defs"] = common }, DefAnchors: defAnchors);
    }

    // ---- bundling --------------------------------------------------------------------------------------------

    private static bool IsCommonFileRef(JsonObject obj, out string name) {
        const string prefix = $"./{CommonDefsFileName}#/$defs/";

        if (IsRefOnly(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)) {
                name = value[prefix.Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }

    private static bool IsLocalDefRef(JsonObject obj, out string name) {
        const string prefix = "#/$defs/";

        if (IsRefOnly(obj: obj)) {
            var value = ((JsonValue)obj["$ref"]!).GetValue<string>();

            if (value.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)) {
                name = value[prefix.Length..];

                return true;
            }
        }

        name = string.Empty;

        return false;
    }

    // Inlines every reference to a NON-recursive def at every site it appears (full duplication, undoing the
    // split's dedup — exactly what the un-split generator would have produced). A recursive def gets exactly one
    // physical expansion (the first the walk reaches); every other reference to it, including its own internal
    // self-reference, is repointed at that one location's absolute path — the same shape the un-split generator's
    // own cycle-breaking $ref already takes.
    private static JsonNode InlineBundleRefs(JsonNode node, string currentPath, JsonObject defs, IReadOnlyDictionary<string, string?> anchors) {
        if (node is JsonObject obj) {
            string? name = null;

            if (IsCommonFileRef(obj: obj, name: out var commonName)) {
                name = commonName;
            } else if (IsLocalDefRef(obj: obj, name: out var localName)) {
                name = localName;
            }

            if (name is not null) {
                var anchor = anchors.TryGetValue(key: name, value: out var anchorPath) ? anchorPath : null;

                // An anchored def gets exactly one physical expansion, at the document-absolute position its
                // anchor names — everywhere else (including its own nested self-reference, whose currentPath is
                // necessarily somewhere UNDER the anchor, never equal to it) becomes a plain pointer to that one
                // position. An unanchored def (the exporter never recognized it as repeated) is fully duplicated
                // at every site instead.
                if ((anchor is not null) && !string.Equals(a: anchor, b: currentPath, comparisonType: StringComparison.Ordinal)) {
                    var refNode = new JsonObject { ["$ref"] = anchor };

                    // The exporter's own $ref sites carry occurrence-specific keywords alongside "$ref" (e.g. an
                    // optional property's own "default": null — see ExpandRefs) that Phase 2 merged INTO the
                    // shared def's content rather than keeping separately per site. Reproduce the sibling here too
                    // — it lives at the same key on the def's own (now fully-reduced) content.
                    if ((defs[name] is JsonObject defObj) && defObj.TryGetPropertyValue(propertyName: "default", out var defaultValue)) {
                        refNode["default"] = defaultValue?.DeepClone();
                    }

                    return refNode;
                }

                return InlineBundleRefs(node: defs[name]!, currentPath: currentPath, defs: defs, anchors: anchors);
            }

            var newObj = new JsonObject();

            foreach (var (key, value) in obj) {
                newObj[key] = (value is not null)
                    ? InlineBundleRefs(node: value, currentPath: $"{currentPath}/{EscapePointerSegment(segment: key)}", defs: defs, anchors: anchors)
                    : null;
            }

            return newObj;
        }

        if (node is JsonArray arr) {
            var newArr = new JsonArray();

            for (var i = 0; i < arr.Count; i++) {
                var value = arr[i];

                newArr.Add(item: (value is not null)
                    ? InlineBundleRefs(node: value, currentPath: $"{currentPath}/{i}", defs: defs, anchors: anchors)
                    : null);
            }

            return newArr;
        }

        return node.DeepClone()!;
    }
}
