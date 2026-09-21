using System.Text;
using System.Runtime.CompilerServices;
using Puck.Abstractions.Machines;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>
/// Composes an imported fragment under an alias (<see cref="WorldImport.As"/>): every name the fragment declares —
/// each <see cref="WorldNameRole.Declares"/> site the <see cref="WorldNameRegistry"/> lists — becomes
/// <c>&lt;alias&gt;_&lt;name&gt;</c>, and every other registered site that spells one of those names is rewritten to
/// match: a bare name position, a reserved <c>$</c> channel's colon segments, a cell key's <c>$cell:</c>/<c>cell:</c>
/// spelling, an infix expression's state reads and topology arguments, a postfix token's row, and a
/// <c>state.&lt;row&gt;</c> binding. A name the fragment does not declare is left as written, so a fragment may
/// still address its host's rows by name. Runs on the fragment's composed raw JSON tree before the strict parse, in
/// the same pass that strips <c>basis</c>/<c>imports</c>.
/// </summary>
public static class WorldModuleNamespace {
    private static readonly JsonSerializerOptions Options = WorldJsonContext.Default.Options;
    // The source-generated context is immutable. Resolve its property registrations once, without retaining
    // document nodes: every walk still observes the current values and lets its visitor rewrite them.
    private static readonly ConditionalWeakTable<JsonTypeInfo, VisitMemberPlan[]> VisitMembers = new();

    private readonly record struct VisitMemberPlan(string Name, Type Type, WorldNameField? Field);

    private static VisitMemberPlan[] BuildVisitMembers(JsonTypeInfo info) {
        var members = new List<VisitMemberPlan>();

        if (typeof(StateRow).IsAssignableFrom(c: info.Type)) {
            foreach (var (jsonName, declaringType, member, propertyType) in WorldNameRegistry.ReflectedRowMembers(type: info.Type)) {
                _ = WorldNameRegistry.TryResolve(declaringType: declaringType, field: out var field, member: member, propertyType: propertyType);
                members.Add(item: new(Field: field, Name: jsonName, Type: propertyType));
            }
        } else {
            foreach (var property in info.Properties) {
                if (property.IsExtensionData || (property.Get is null) || (property.Set is null)) {
                    continue;
                }
                var (declaringType, member) = WorldNameRegistry.ResolveMember(property: property);
                _ = WorldNameRegistry.TryResolve(declaringType: declaringType, member: member, propertyType: property.PropertyType, field: out var field);
                members.Add(item: new(Name: property.Name, Type: property.PropertyType, Field: field));
            }
        }
        return [.. members];
    }
    private static Dictionary<(WorldNameKind Kind, string Name), string> CollectDeclaredNames(JsonObject module, string alias) {
        var declared = new Dictionary<(WorldNameKind Kind, string Name), string>();

        Visit(
            node: module,
            type: typeof(WorldDefinition),
            visitor: (parent, name, value, field, _) => {
                if (
                    (field.Role == WorldNameRole.Declares) &&
                    (value is JsonValue leaf) &&
                    leaf.TryGetValue<string>(value: out var textValue) &&
                    (textValue.Length > 0)
                ) {
                    declared[(field.Kind, textValue)] = ((alias + WorldNameRegistry.AliasSeparator) + textValue);
                }
            }
        );
        return declared;
    }
    private static WorldNameKind ProviderReferenceKind(MachineFieldRole role) => role switch {
        MachineFieldRole.StateReference => WorldNameKind.State,
        MachineFieldRole.MachineReference => WorldNameKind.Machine,
        MachineFieldRole.ScreenReference => WorldNameKind.Screen,
        _ => WorldNameKind.Any
    };
    private static string RelocateAssetPath(string path, string sourceDocumentPath, string targetDocumentPath) {
        if (Path.IsPathRooted(path: path)) {
            return path;
        }

        if (
            Path.IsPathRooted(path: sourceDocumentPath) ||
            Path.IsPathRooted(path: targetDocumentPath)
        ) {
            var source = Path.GetFullPath(path: Path.Combine(
                path1: (Path.GetDirectoryName(path: Path.GetFullPath(path: sourceDocumentPath)) ?? "."),
                path2: path
            ));
            var target = (Path.GetDirectoryName(path: Path.GetFullPath(path: targetDocumentPath)) ?? ".");

            return Path.GetRelativePath(
                path: source,
                relativeTo: target
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );
        }

        var sourceAsset = WorldDefinitionFileSource.CombineRelativeDocumentName(
            name: path,
            referrerName: sourceDocumentPath
        );
        var targetDirectory = targetDocumentPath.Replace(
            newChar: '/',
            oldChar: '\\'
        );
        var slash = targetDirectory.LastIndexOf(value: '/');

        targetDirectory = ((slash >= 0)
            ? targetDirectory[..slash]
            : string.Empty
        );
        var from = targetDirectory.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '/'
        );
        var to = sourceAsset.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '/'
        );
        var common = 0;

        while (
            (common < from.Length) &&
            (common < to.Length) &&
            string.Equals(
            a: from[common],
            b: to[common],
            comparisonType: StringComparison.OrdinalIgnoreCase
        )
        ) {
            common++;
        }

        var segments = new List<string>();

        for (var i = common; (i < from.Length); i++) {
            segments.Add(item: "..");
        }

        for (var i = common; (i < to.Length); i++) {
            segments.Add(item: to[i]);
        }

        return ((segments.Count == 0)
            ? "."
            : string.Join(
                separator: "/",
                values: segments
            )
        );
    }
    private static bool TryRewriteMachineMetadata(JsonObject module, string alias, IMachineValidationCatalog catalog,
        string sourceDocumentPath, string targetDocumentPath, IReadOnlyDictionary<(WorldNameKind Kind, string Name), string> declared, out string reason) {
        if (!TryRelocateConfigurationAssets(
            catalog: catalog,
            module: module,
            reason: out reason,
            sourceDocumentPath: sourceDocumentPath,
            targetDocumentPath: targetDocumentPath
        )) {
            return false;
        }

        if (module["machines"] is not JsonArray machines) {
            reason = string.Empty;
            return true;
        }

        foreach (var node in machines) {
            if (
                (node is not JsonObject machine) ||
                (machine["engine"] is not JsonValue engineValue) ||
                !engineValue.TryGetValue<string>(value: out var engineId) ||
                (machine["configuration"] is not JsonObject configuration) ||
                !catalog.TryDescriptor(
                descriptor: out var descriptor,
                engineId: engineId
            )
            ) {
                continue;
            }

            var descriptorErrors = new List<string>();

            if (!MachineConfigurationFields.TryValidateDescriptor(
                descriptor: descriptor.Configuration,
                errors: descriptorErrors
            )) {
                reason = ((("machine '" + (machine["name"]?.ToString() ?? "(unnamed)")) +
                    "' has invalid provider descriptor: ") + string.Join(
                    separator: " ",
                    values: descriptorErrors
                ));
                return false;
            }

            var local = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
            var localError = string.Empty;

            MachineConfigurationFields.Visit(
                configuration: configuration,
                descriptor: descriptor.Configuration,
                visitor: site => {
                    if (
                        (site.Field.Role != MachineFieldRole.Declaration) ||
                        (site.Value is not JsonValue value) ||
                        !value.TryGetValue<string>(value: out var name) ||
                        (name.Length == 0)
                    ) {
                        return;
                    }

                    if (!local.TryAdd(
                        key: name,
                        value: ((alias + WorldNameRegistry.AliasSeparator) + name)
                    )) {
                        localError = (((("machine '" + (machine["name"]?.ToString() ?? "(unnamed)")) +
                            "' declares duplicate provider-local name '") + name) + "'.");
                    }
                }
            );

            if (localError.Length != 0) {
                reason = localError;
                return false;
            }

            MachineConfigurationFields.Visit(
                configuration: configuration,
                descriptor: descriptor.Configuration,
                visitor: site => {
                    if (
                        (site.Value is not JsonValue value) ||
                        !value.TryGetValue<string>(value: out var textValue)
                    ) {
                        return;
                    }

                    var rewritten = site.Field.Role switch {
                        MachineFieldRole.Declaration or MachineFieldRole.LocalReference =>
                            (local.TryGetValue(
                        key: textValue,
                        value: out var localName
                    )
                        ? localName
                        : textValue),
                        MachineFieldRole.StateReference or MachineFieldRole.MachineReference or MachineFieldRole.ScreenReference =>
                            (declared.TryGetValue(
                        key: (ProviderReferenceKind(role: site.Field.Role), textValue),
                        value: out var worldName
                    )
                        ? worldName
                        : textValue),
                        _ => textValue
                    };

                    if (rewritten != textValue) {
                        site.Value = JsonValue.Create(rewritten);
                    }
                }
            );
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Rewrites one value of a registered role against a declared-name map — the unit
    /// <c>TryApply</c> applies at every site, exposed so a law can hold each spelling on its own.</summary>
    /// <param name="text">The authored value.</param>
    /// <param name="role">How the value carries names.</param>
    /// <param name="declared">Each declared name paired with its prefixed spelling.</param>
    /// <param name="scope">The optional source node whose ancestors establish lexical instance bindings.</param>
    /// <returns>The rewritten value; <paramref name="text"/> unchanged when it spells no declared name.</returns>
    public static string Rewrite(string text, WorldNameRole role, IReadOnlyDictionary<string, string> declared, JsonNode? scope = null) {
        ArgumentNullException.ThrowIfNull(argument: text);
        ArgumentNullException.ThrowIfNull(argument: declared);

        return new Rewriter(declared: declared) { Scope = scope }.Rewrite(
            role: role,
            text: text
        );
    }
    /// <summary>Prefixes every name <paramref name="module"/> declares with <paramref name="alias"/> and rewrites
    /// the module's references to match, in place.</summary>
    /// <param name="module">The fragment's composed tree; mutated.</param>
    /// <param name="alias">The alias, admissible under <see cref="WorldImport.TryValidateAlias"/>.</param>
    /// <param name="reason">The one-line refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the alias was admissible and the rewrite applied.</returns>
    public static bool TryApply(JsonObject module, string alias, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: module);

        if (!WorldImport.TryValidateAlias(
            alias: alias,
            reason: out reason
        )) {
            return false;
        }

        var declared = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        Visit(
            node: module,
            type: typeof(WorldDefinition),
            visitor: (parent, name, value, field, _) => {
                if (
                    (field.Role == WorldNameRole.Declares) &&
                    (value is JsonValue leaf) &&
                    leaf.TryGetValue<string>(value: out var text) &&
                    (text.Length > 0)
                ) {
                    declared[text] = $"{alias}{WorldNameRegistry.AliasSeparator}{text}";
                }
            }
        );

        if (declared.Count == 0) {
            reason = string.Empty;

            return true;
        }

        var rewriter = new Rewriter(declared: declared);

        Visit(
            node: module,
            type: typeof(WorldDefinition),
            visitor: (parent, name, value, field, memberType) => {
                rewriter.Scope = parent;
                switch (WorldChannelNodes.Spelled(
                    memberType: memberType,
                    value: value
                )) {
                    case JsonArray list when (field.Role == WorldNameRole.Names):
                        for (var index = 0; (index < list.Count); index++) {
                            if (
                                (list[index: index] is JsonValue element) &&
                                element.TryGetValue<string>(value: out var item)
                            ) {
                                list[index: index] = rewriter.Rewrite(
                                    text: item,
                                    role: field.Role
                                );
                            }
                        }

                        break;
                    case JsonValue leaf when leaf.TryGetValue<string>(value: out var text):
                        var rewritten = rewriter.Rewrite(
                            text: text,
                            role: field.Role
                        );

                        // A member that holds a call node holds one again, its row arguments renamed.
                        parent[propertyName: name] = ((value is JsonObject)
                            ? WorldChannelNodes.Node(spelling: rewritten)
                            : rewritten
                        );

                        break;
                }
            }
        );

        reason = string.Empty;

        return true;
    }
    /// <summary>Restores compiler-generated reference placeholders to the names captured from the module's caller.</summary>
    /// <param name="module">The lowered module fragment; mutated.</param>
    /// <param name="replacements">Placeholder names paired with their caller-owned names.</param>
    /// <param name="reason">The one-line refusal, currently empty because registered reference restoration is total.</param>
    /// <returns><see langword="true"/> when the registered references were restored.</returns>
    /// <remarks>The replacement is limited to registered reference sites. Declaration sites are deliberately left
    /// unchanged, so a placeholder can never rename a module-owned declaration or arbitrary document text.</remarks>
    public static bool TryRestoreReferences(JsonObject module, IReadOnlyDictionary<string, string> replacements, out string reason) {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(replacements);

        if (replacements.Count == 0) {
            reason = string.Empty;
            return true;
        }

        var rewriter = new Rewriter(declared: replacements);

        Visit(
            node: module,
            type: typeof(WorldDefinition),
            visitor: (parent, name, value, field, memberType) => {
                if (field.Role == WorldNameRole.Declares) {
                    return;
                }

                rewriter.Scope = parent;
                switch (WorldChannelNodes.Spelled(
                    memberType: memberType,
                    value: value
                )) {
                    case JsonArray list when (field.Role == WorldNameRole.Names):
                        for (var index = 0; (index < list.Count); index++) {
                            if ((list[index] is JsonValue element) && element.TryGetValue<string>(value: out var item)) {
                                list[index] = rewriter.Rewrite(item, field.Role);
                            }
                        }
                        break;
                    case JsonValue leaf when leaf.TryGetValue<string>(value: out var text):
                        var rewritten = rewriter.Rewrite(text, field.Role);
                        parent[name] = ((value is JsonObject)
                            ? WorldChannelNodes.Node(spelling: rewritten)
                            : rewritten);
                        break;
                }
            }
        );

        reason = string.Empty;
        return true;
    }
    /// <summary>Applies provider metadata to an imported module.</summary>
    public static bool TryApply(JsonObject module, string alias, IMachineValidationCatalog catalog,
        string sourceDocumentPath, string targetDocumentPath, out string reason) {
        ArgumentNullException.ThrowIfNull(catalog);

        var declared = CollectDeclaredNames(
            alias: alias,
            module: module
        );

        if (!TryApply(
            alias: alias,
            module: module,
            reason: out reason
        )) {
            return false;
        }

        return TryRewriteMachineMetadata(
            alias: alias,
            catalog: catalog,
            declared: declared,
            module: module,
            reason: out reason,
            sourceDocumentPath: sourceDocumentPath,
            targetDocumentPath: targetDocumentPath
        );
    }
    /// <summary>Rebases provider content and asset fields from one document origin to another.</summary>
    public static bool TryRelocateConfigurationAssets(JsonObject module, IMachineValidationCatalog catalog,
        string sourceDocumentPath, string targetDocumentPath, out string reason) {
        ArgumentNullException.ThrowIfNull(catalog);

        if (module["machines"] is not JsonArray machines) {
            reason = string.Empty;
            return true;
        }

        foreach (var node in machines) {
            if (
                (node is not JsonObject machine) ||
                (machine["engine"] is not JsonValue engineValue) ||
                !engineValue.TryGetValue<string>(value: out var engineId) ||
                (machine["configuration"] is not JsonObject configuration) ||
                !catalog.TryDescriptor(
                descriptor: out var descriptor,
                engineId: engineId
            )
            ) {
                continue;
            }

            var descriptorErrors = new List<string>();

            if (!MachineConfigurationFields.TryValidateDescriptor(
                descriptor: descriptor.Configuration,
                errors: descriptorErrors
            )) {
                reason = ((("machine '" + (machine["name"]?.ToString() ?? "(unnamed)")) +
                    "' has invalid provider descriptor: ") + string.Join(
                    separator: " ",
                    values: descriptorErrors
                ));
                return false;
            }

            MachineConfigurationFields.Visit(
                configuration: configuration,
                descriptor: descriptor.Configuration,
                visitor: site => {
                    if (
                        (site.Field.Role is not (MachineFieldRole.ContentPath or MachineFieldRole.AssetPath)) ||
                        (site.Value is not JsonValue pathValue) ||
                        !pathValue.TryGetValue<string>(value: out var path) ||
                        (path.Length == 0)
                    ) {
                        return;
                    }

                    site.Value = JsonValue.Create(RelocateAssetPath(
                        path: path,
                        sourceDocumentPath: sourceDocumentPath,
                        targetDocumentPath: targetDocumentPath
                    ));
                }
            );
        }

        reason = string.Empty;
        return true;
    }

    // The IR's wire shape flattens an instruction's payload onto the instruction object, so the walk reaches a
    // payload's registered names by the operation's payload shape rather than through a JSON type info.
    private static void VisitExpressionProgram(JsonNode node, WorldNameVisitor visitor) {
        VisitInstructions(
            node: node?["instructions"],
            visitor: visitor
        );
        if (node?["subprograms"] is JsonArray subprograms) {
            foreach (var subprogram in subprograms) {
                VisitInstructions(
                    node: subprogram?["instructions"],
                    visitor: visitor
                );
            }
        }
    }
    private static void VisitInstructions(JsonNode? node, WorldNameVisitor visitor) {
        if (node is not JsonArray instructions) {
            return;
        }

        foreach (var instruction in instructions) {
            if (
                (instruction is not JsonObject obj) ||
                (obj["op"] is not JsonValue opValue) ||
                !opValue.TryGetValue<string>(value: out var op) ||
                !Enum.TryParse(
                ignoreCase: false,
                result: out ExpressionOp operation,
                value: op
            )
            ) {
                continue;
            }
            switch (ExpressionOperators.PayloadOf(operation: operation)) {
                case PayloadShape.State:
                    VisitMember(
                        declaringType: typeof(InstructionPayload.State),
                        jsonName: "name",
                        member: nameof(InstructionPayload.State.Name),
                        memberType: typeof(StateChannelRef),
                        obj: obj,
                        visitor: visitor
                    );
                    VisitMember(
                        declaringType: typeof(InstructionPayload.State),
                        jsonName: "key",
                        member: nameof(InstructionPayload.State.Key),
                        memberType: typeof(StateChannelRef),
                        obj: obj,
                        visitor: visitor
                    );
                    break;
                case PayloadShape.Board:
                    VisitMember(
                        declaringType: typeof(InstructionPayload.Board),
                        jsonName: "topology",
                        member: nameof(InstructionPayload.Board.Topology),
                        memberType: typeof(string),
                        obj: obj,
                        visitor: visitor
                    );
                    break;
                case PayloadShape.Fold:
                    VisitMember(
                        declaringType: typeof(InstructionPayload.Fold),
                        jsonName: "family",
                        member: nameof(InstructionPayload.Fold.Family),
                        memberType: typeof(string),
                        obj: obj,
                        visitor: visitor
                    );
                    break;
                case PayloadShape.Vector:
                    VisitVectorOperand(
                        node: obj["left"],
                        visitor: visitor
                    );
                    VisitVectorOperand(
                        node: obj["right"],
                        visitor: visitor
                    );
                    break;
                default:
                    break;
            }
        }
    }
    private static void VisitVectorOperand(JsonNode? node, WorldNameVisitor visitor) {
        if (
            (node is not JsonObject obj) ||
            (obj["$type"] is not JsonValue kindValue) ||
            !kindValue.TryGetValue<string>(value: out var kind) ||
            !string.Equals(
            a: kind,
            b: "cell",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return;
        }
        VisitMember(
            declaringType: typeof(VectorOperand.Cell),
            jsonName: "name",
            member: nameof(VectorOperand.Cell.Name),
            memberType: typeof(StateChannelRef),
            obj: obj,
            visitor: visitor
        );
        VisitMember(
            declaringType: typeof(VectorOperand.Cell),
            jsonName: "key",
            member: nameof(VectorOperand.Cell.Key),
            memberType: typeof(StateChannelRef),
            obj: obj,
            visitor: visitor
        );
    }
    private static void VisitMember(JsonObject obj, string jsonName, Type declaringType, string member, Type memberType, WorldNameVisitor visitor) {
        if (
            (obj[jsonName] is not { } value) ||
            !WorldNameRegistry.TryResolve(
            declaringType: declaringType,
            field: out var field,
            member: member,
            propertyType: memberType
        )
        ) {
            return;
        }
        visitor(
            obj,
            jsonName,
            value,
            field,
            memberType
        );
    }

    /// <summary>Walks a raw tree type-directed, calling <paramref name="visitor"/> at every registered name-bearing
    /// site with the holding object, the member's JSON name, the value, and the registration: each present member
    /// is resolved against the registry by its C# member, a <c>$type</c> discriminator selects the arm, and the two
    /// converter-backed shapes (an expression's instruction list, a reaction scalar's row object) are followed by
    /// hand.</summary>
    /// <remarks>Member registrations are cached against immutable serializer metadata. Document values,
    /// collection contents, and discriminator choices are read afresh on every walk.</remarks>
    /// <param name="node">The tree, or the subtree to walk.</param>
    /// <param name="type">The model type <paramref name="node"/> holds.</param>
    /// <param name="visitor">Called once per registered site, in document order.</param>
    public static void Visit(JsonNode? node, Type type, WorldNameVisitor visitor) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        if (node is null) {
            return;
        }

        if (type == typeof(ExpressionProgram)) {
            if (node is JsonObject) {
                VisitExpressionProgram(
                    node: node,
                    visitor: visitor
                );
            }

            return;
        }

        if (type == typeof(WorldLatticeScalar)) {
            if (
                (node is JsonObject scalar) &&
                scalar.TryGetPropertyValue(
                jsonNode: out var row,
                propertyName: "row"
            ) &&
                (row is not null) &&
                WorldNameRegistry.TryFind(
                declaringType: typeof(WorldLatticeScalar),
                member: nameof(WorldLatticeScalar.Row),
                field: out var scalarField
            )
            ) {
                visitor(
                    scalar,
                    "row",
                    row,
                    scalarField,
                    typeof(string)
                );
            }

            return;
        }

        if (
            type.IsPrimitive ||
            type.IsEnum ||
            (type == typeof(string)) ||
            (type == typeof(decimal))
        ) {
            return;
        }

        JsonTypeInfo typeInfo;

        try {
            typeInfo = Options.GetTypeInfo(type: type);
        } catch (Exception exception) when ((exception is NotSupportedException or InvalidOperationException)) {
            return;
        }

        if (
            typeof(StateRow).IsAssignableFrom(c: type) &&
            (node is JsonObject rowObject)
        ) {
            foreach (var member in VisitMembers.GetValue(createValueCallback: BuildVisitMembers, key: typeInfo)) {
                if (
                    !rowObject.TryGetPropertyValue(
                    jsonNode: out var value,
                    propertyName: member.Name
                ) ||
                    (value is null)
                ) {
                    continue;
                }

                if (member.Field is { } field) {
                    visitor(
                        rowObject,
                        member.Name,
                        value,
                        field,
                        member.Type
                    );
                }

                Visit(
                    node: value,
                    type: member.Type,
                    visitor: visitor
                );
            }

            return;
        }

        switch (typeInfo.Kind) {
            case JsonTypeInfoKind.Object when (node is JsonObject obj):
                if (
                    (typeInfo.PolymorphismOptions is { } polymorphism) &&
                    obj.TryGetPropertyValue(
                    jsonNode: out var discriminatorNode,
                    propertyName: "$type"
                ) &&
                    (discriminatorNode is JsonValue discriminatorValue) &&
                    discriminatorValue.TryGetValue<string>(value: out var discriminator)
                ) {
                    foreach (var derived in polymorphism.DerivedTypes) {
                        if (
                            (derived.TypeDiscriminator is string arm) &&
                            string.Equals(
                            a: arm,
                            b: discriminator,
                            comparisonType: StringComparison.Ordinal
                        ) &&
                            (derived.DerivedType != type)
                        ) {
                            Visit(
                                node: node,
                                type: derived.DerivedType,
                                visitor: visitor
                            );

                            return;
                        }
                    }
                }

                foreach (var member in VisitMembers.GetValue(createValueCallback: BuildVisitMembers, key: typeInfo)) {
                    if (
                        !obj.TryGetPropertyValue(
                        propertyName: member.Name,
                        jsonNode: out var value
                    ) ||
                        (value is null)
                    ) {
                        continue;
                    }

                    if (member.Field is { } field) {
                        visitor(
                            obj,
                            member.Name,
                            value,
                            field,
                            member.Type
                        );
                    }

                    Visit(
                        node: value,
                        type: member.Type,
                        visitor: visitor
                    );
                }

                break;
            case JsonTypeInfoKind.Enumerable when (node is JsonArray list):
                foreach (var element in list) {
                    Visit(
                        node: element,
                        type: typeInfo.ElementType!,
                        visitor: visitor
                    );
                }

                break;
            case JsonTypeInfoKind.Dictionary when (node is JsonObject entries):
                foreach (var entry in entries) {
                    Visit(
                        node: entry.Value,
                        type: typeInfo.ElementType!,
                        visitor: visitor
                    );
                }

                break;
        }
    }

    private sealed class Rewriter(IReadOnlyDictionary<string, string> declared) {
        public JsonNode? Scope { get; set; }

        private const string BindingPrefix = "state.";

        private bool IsInstanceBinding(string name) {
            for (var node = Scope; (node is not null); node = node.Parent) {
                if (node is not JsonObject scope) {
                    continue;
                }
                if ((scope["$type"]?.ToString() is "claim" or "claimPair" or "forEachPool") &&
                    (scope["binding"]?.ToString() == name)) {
                    return true;
                }
                if ((scope["poolForEach"] is JsonObject iteration) && (iteration["binding"]?.ToString() == name)) {
                    return true;
                }
                if ((name is "left" or "right") && scope.ContainsKey(propertyName: "left") && scope.ContainsKey(propertyName: "right") &&
                    scope.ContainsKey(propertyName: "effects") && IsPoolName(name: scope[name]?.ToString())) {
                    return true;
                }
            }
            return false;
        }
        private bool IsPoolName(string? name) {
            if (name is null) {
                return false;
            }
            var root = Scope;

            while (root?.Parent is { } parent) {
                root = parent;
            }
            var mapped = (declared.ContainsKey(key: name) ? Map(name: name) : name);

            foreach (var section in ((ReadOnlySpan<string>)["pools", "pairPools"])) {
                if (root?["state"]?[section] is not JsonArray pools) {
                    continue;
                }
                foreach (var pool in pools) {
                    var candidate = pool?["name"]?.ToString();

                    if ((candidate == name) || (candidate == mapped)) {
                        return true;
                    }
                }
            }
            return false;
        }
        private string Map(string name) =>
            (declared.TryGetValue(
                key: name,
                value: out var prefixed
            )
                ? prefixed
                : name
            );
        private static int MatchingBracket(string text, int open) {
            var depth = 0;

            for (var index = open; (index < text.Length); index++) {
                if (text[index] == '[') {
                    depth++;
                } else if (
                    (text[index] == ']') &&
                    (--depth == 0)
                ) {
                    return index;
                }
            }

            return -1;
        }
        // A declared non-cell name (a topology, table, or generator name) may itself carry a dot — SafeName admits
        // one where CellName never does — so a name found whole in `declared` is renamed whole; only a name that
        // is NOT itself declared falls to ExpressionSpelling's typed lexical-field rule. An instance binding stays
        // local while a qualified declaration is renamed; the field half is never itself a `Declares` site.
        private string MapNameOrSplit(string name) =>
            (declared.ContainsKey(key: name)
                ? Map(name: name)
                : (ExpressionSpelling.TrySplitDottedName(
                key: out var key,
                name: name,
                row: out var row
            )
                    ? $"{(IsInstanceBinding(name: row) ? row : Map(name: row))}.{key}"
                    : Map(name: name))
            );
        private string RewriteBinding(string token) {
            if (!token.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: BindingPrefix
            )) {
                return token;
            }

            var rest = token[BindingPrefix.Length..];
            var dot = rest.IndexOf(value: '.');
            var row = ((dot < 0)
                ? rest
                : rest[..dot]
            );
            var tail = ((dot < 0)
                ? string.Empty
                : rest[dot..]
            );

            return $"{BindingPrefix}{Map(name: row)}{tail}";
        }
        // The text between a name's brackets: a bare or backquoted name is a cell key and stays; a reserved token
        // rewrites as a key; anything else is an expression.
        private string RewriteBracket(string inner) {
            if (!ExpressionSpelling.TryParseKey(
                error: out _,
                key: out var key,
                text: inner
            )) {
                return inner;
            }

            if (
                key.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.ExpressionKeyPrefix
            ) ||
                key.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.CellKeyPrefix
            )
            ) {
                return RewriteExpression(text: inner);
            }

            if (key.StartsWith(value: '$')) {
                return RewriteReserved(name: inner.Trim());
            }

            return inner;
        }
        private string RewriteExpression(string text) {
            var output = new StringBuilder(capacity: text.Length);
            var index = 0;

            while (index < text.Length) {
                var character = text[index];

                if (character == '`') {
                    var close = text.IndexOf(
                        startIndex: (index + 1),
                        value: '`'
                    );

                    if (close < 0) {
                        _ = output.Append(
                            value: text,
                            startIndex: index,
                            count: (text.Length - index)
                        );

                        break;
                    }

                    _ = output.Append(value: '`').Append(value: Map(name: text[(index + 1)..close])).Append(value: '`');
                    index = (close + 1);

                    continue;
                }

                var nameLength = ExpressionSpelling.ScanBareName(
                    start: index,
                    text: text
                );

                if (nameLength > 0) {
                    var end = (index + nameLength);
                    var name = text[index..end];

                    _ = output.Append(value: (name.StartsWith(value: '$')
                        ? RewriteReserved(name: name)
                        : MapNameOrSplit(name: name)));
                    index = end;

                    if (
                        (index < text.Length) &&
                        (text[index] == '[')
                    ) {
                        var close = MatchingBracket(
                            open: index,
                            text: text
                        );

                        if (close < 0) {
                            continue;
                        }

                        _ = output.Append(value: '[').Append(value: RewriteBracket(inner: text[(index + 1)..close])).Append(value: ']');
                        index = (close + 1);
                    }

                    continue;
                }

                if (char.IsAsciiDigit(c: character)) {
                    var end = index;

                    while (
                        (end < text.Length) &&
                        (char.IsAsciiLetterOrDigit(c: text[end]) || (text[end] == '.'))
                    ) {
                        end++;
                    }

                    _ = output.Append(
                        count: (end - index),
                        startIndex: index,
                        value: text
                    );
                    index = end;

                    continue;
                }

                _ = output.Append(value: character);
                index++;
            }

            return output.ToString();
        }
        // A literal key is local to its row and stays; a reserved spelling ($cell:, $zone:, $pair:, $expr:, $zones[)
        // or a body-reference spelling (cell:<row>:<key>, argmax:<row>) carries names in its segments.
        private string RewriteKey(string key) {
            if (key.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.ExpressionKeyPrefix
            )) {
                return $"{RuleFacts.ExpressionKeyPrefix}{RewriteExpression(text: key[RuleFacts.ExpressionKeyPrefix.Length..])}";
            }

            if (
                key.StartsWith(value: '$') ||
                key.Contains(value: ':')
            ) {
                return RewriteReserved(name: key);
            }

            return key;
        }
        // Colon segments outside brackets are names; a bracketed span is a live-zone index, a cell key in its own
        // right. A $local: read names a rule-scoped local, never a row.
        private string RewriteReserved(string name) {
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldRuleFacts.InfluencePrefix
            )) {
                var separator = name.IndexOf(
                    ':',
                    WorldRuleFacts.InfluencePrefix.Length
                );
                // Influence labels are shared semantics, even if a module happens to declare a row of that name.
                if (separator >= 0) {
                    return (name[..(separator + 1)] + RewriteReserved(name: name[(separator + 1)..]));
                }
            }
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.LocalPrefix
            )) {
                return name;
            }

            var output = new StringBuilder(capacity: name.Length);
            var segment = new StringBuilder();
            var index = 0;

            while (index < name.Length) {
                var character = name[index];

                if (character == '[') {
                    var close = MatchingBracket(
                        open: index,
                        text: name
                    );

                    if (close < 0) {
                        _ = segment.Append(
                            value: name,
                            startIndex: index,
                            count: (name.Length - index)
                        );

                        break;
                    }

                    _ = output.Append(value: Map(name: segment.ToString()));
                    _ = segment.Clear();
                    _ = output.Append(value: '[').Append(value: RewriteBracket(inner: name[(index + 1)..close])).Append(value: ']');
                    index = (close + 1);

                    continue;
                }

                if (character == ':') {
                    _ = output.Append(value: Map(name: segment.ToString())).Append(value: ':');
                    _ = segment.Clear();
                    index++;

                    continue;
                }

                _ = segment.Append(value: character);
                index++;
            }

            _ = output.Append(value: Map(name: segment.ToString()));

            return output.ToString();
        }
        private string RewriteTemplate(string template) {
            var output = new StringBuilder(capacity: template.Length);
            var index = 0;

            while (index < template.Length) {
                var character = template[index];

                if (
                    (character == '{') &&
                    ((index + 1) < template.Length) &&
                    (template[(index + 1)] == '{')
                ) {
                    _ = output.Append(value: "{{");
                    index += 2;

                    continue;
                }

                if (character == '{') {
                    var close = template.IndexOf(
                        startIndex: index,
                        value: '}'
                    );

                    if (close < 0) {
                        _ = output.Append(
                            value: template,
                            startIndex: index,
                            count: (template.Length - index)
                        );

                        break;
                    }

                    _ = output.Append(value: '{').Append(value: RewriteBinding(token: template[(index + 1)..close])).Append(value: '}');
                    index = (close + 1);

                    continue;
                }

                _ = output.Append(value: character);
                index++;
            }

            return output.ToString();
        }

        public string Rewrite(string text, WorldNameRole role) => role switch {
            WorldNameRole.Declares => Map(name: text),
            WorldNameRole.Names => (text.StartsWith(value: '$')
            ? RewriteReserved(name: text)
            : MapNameOrSplit(name: text)),
            WorldNameRole.Key => RewriteKey(key: text),
            WorldNameRole.Expression => RewriteExpression(text: text),
            WorldNameRole.Binding => RewriteBinding(token: text),
            WorldNameRole.Template => RewriteTemplate(template: text),
            _ => text,
        };
    }
}
