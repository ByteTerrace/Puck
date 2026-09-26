using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Puck.Abstractions.Machines;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

/// <summary>
/// Composes an imported fragment under an alias (<see cref="WorldImport.As"/>): every name the fragment declares —
/// each <see cref="WorldNameRole.Declares"/> site the <see cref="WorldNameRegistry"/> lists — becomes the generated
/// name <c>&lt;alias&gt;$&lt;name&gt;</c> (<see cref="GeneratedName.Qualify"/>), which no author-written name can
/// spell, and every other registered site that spells one of those names in the same namespace is rewritten to
/// match: a bare name position, the name positions of a reserved <c>$</c> channel's colon segments (read by the
/// channel's grammar, <see cref="DescribesChannel"/>), a cell key's <c>$cell:</c>/<c>cell:</c> spelling, an infix
/// expression's state reads and topology arguments, a postfix token's row, and a <c>state.&lt;row&gt;</c> binding.
/// A placement and a prototype each live in their own namespace, apart from every state-side name. The engine's own
/// words are never renamed: a function an expression calls, and a channel's operation, facet, or body-reference kind.
/// A name the fragment does not declare is left as written, so a fragment may still address its host's rows by
/// name. Runs on the fragment's composed raw JSON tree before the strict parse, in the same pass that strips
/// <c>basis</c>/<c>imports</c>.
/// </summary>
public static class WorldModuleNamespace {
    // The model's shape never changes. Resolve each type's property registrations once, without retaining document
    // nodes: every walk still observes the current values and lets its visitor rewrite them.
    private static readonly ConditionalWeakTable<WorldModelType, VisitMemberPlan[]> VisitMembers = new();

    private readonly record struct VisitMemberPlan(string Name, Type Type, WorldNameField? Field);

    // A state row is converter-backed, so the serializer lists no members for it; its converter reads the record's
    // own writable, unignored properties by camel-cased name.
    private static VisitMemberPlan[] BuildVisitMembers(WorldModelType shape) {
        var members = new List<VisitMemberPlan>();

        if (typeof(StateRow).IsAssignableFrom(c: shape.Type)) {
            foreach (var property in shape.Properties) {
                if (((property.Access & WorldModelAccess.Write) == 0) || ((property.Access & WorldModelAccess.Ignored) != 0)) {
                    continue;
                }
                _ = WorldNameRegistry.TryResolve(declaringType: property.DeclaringType, field: out var field, member: property.Member, propertyType: property.Type);
                members.Add(item: new(Field: field, Name: property.Name, Type: property.Type));
            }
        } else {
            foreach (var property in shape.Members) {
                if ((property.Access & (WorldModelAccess.ExtensionData | WorldModelAccess.Read | WorldModelAccess.Write)) != (WorldModelAccess.Read | WorldModelAccess.Write)) {
                    continue;
                }
                _ = WorldNameRegistry.TryResolve(declaringType: property.DeclaringType, member: property.Member, propertyType: property.Type, field: out var field);
                members.Add(item: new(Name: property.Name, Type: property.Type, Field: field));
            }
        }
        return [.. members];
    }
    private static DeclaredNames CollectDeclaredNames(JsonObject module, string alias) {
        var declared = new DeclaredNames();

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
                    declared.Declare(
                        kind: field.Kind,
                        name: textValue,
                        qualified: GeneratedName.Qualify(
                            head: alias,
                            name: textValue
                        )
                    );
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
    private static bool TryRewriteMachineMetadata(JsonObject module, string alias, IMachineValidationCatalog catalog,
        string sourceDocumentPath, string targetDocumentPath, DeclaredNames declared, out string reason) {
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
                        value: GeneratedName.Qualify(
                            head: alias,
                            name: name
                        )
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
                            (declared.TryMapExact(
                        kind: ProviderReferenceKind(role: site.Field.Role),
                        mapped: out var worldName,
                        name: textValue
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

        return new Rewriter(names: new KindlessNames(names: declared)) { Scope = scope }.Rewrite(
            kind: WorldNameKind.Any,
            role: role,
            text: text
        );
    }
    /// <summary>Returns whether the rewrite knows a reserved channel: every channel it knows is a row of its channel
    /// table, which reads the channel's arguments by their positions in its grammar, so that only a position naming a
    /// declaration is renamed and the engine's own words there (an operation, a facet, a body-reference kind) never
    /// are. A spelling the table does not hold is no channel, and its arguments are left as written.</summary>
    /// <param name="channel">The channel's name, without the reserved prefix: <c>reduce</c>, <c>distance</c>.</param>
    /// <returns><see langword="true"/> when the channel's grammar is described.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="channel"/> is <see langword="null"/>.</exception>
    public static bool DescribesChannel(string channel) {
        ArgumentNullException.ThrowIfNull(argument: channel);

        return Channels.ContainsKey(key: channel);
    }
    /// <summary>Qualifies every name <paramref name="module"/> declares by <paramref name="alias"/> and rewrites the
    /// module's references to match, in place.</summary>
    /// <param name="module">The fragment's composed tree; mutated.</param>
    /// <param name="alias">The alias, admissible under <see cref="WorldImport.TryValidateAlias"/>.</param>
    /// <param name="reason">The one-line refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the alias was admissible and the rewrite applied.</returns>
    public static bool TryApply(JsonObject module, string alias, out string reason) => TryApply(
        alias: alias,
        module: module,
        qualified: out _,
        reason: out reason
    );
    /// <summary>Qualifies every name <paramref name="module"/> declares by <paramref name="alias"/> and rewrites the
    /// module's references to match, in place, returning the qualified names it minted.</summary>
    /// <param name="module">The fragment's composed tree; mutated.</param>
    /// <param name="alias">The alias, admissible under <see cref="WorldImport.TryValidateAlias"/>.</param>
    /// <param name="qualified">Every declaration's qualified name, <c>&lt;alias&gt;$&lt;name&gt;</c>; empty on
    /// refusal.</param>
    /// <param name="reason">The one-line refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the alias was admissible and the rewrite applied.</returns>
    public static bool TryApply(JsonObject module, string alias, out IReadOnlyCollection<string> qualified, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: module);
        qualified = [];

        if (!WorldImport.TryValidateAlias(
            alias: alias,
            reason: out reason
        )) {
            return false;
        }

        var declared = CollectDeclaredNames(
            alias: alias,
            module: module
        );

        qualified = [.. declared.Qualified];

        if (declared.Count == 0) {
            return true;
        }

        var rewriter = new Rewriter(names: declared);

        Visit(
            node: module,
            type: typeof(WorldDefinition),
            visitor: (parent, name, value, field, memberType) => {
                rewriter.Scope = parent;
                rewriter.RewriteSite(
                    field: field,
                    memberType: memberType,
                    name: name,
                    parent: parent,
                    value: value
                );
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

        var rewriter = new Rewriter(names: new KindlessNames(names: replacements));

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
                                list[index] = rewriter.Rewrite(item, field.Role, field.Kind);
                            }
                        }
                        break;
                    case JsonValue leaf when leaf.TryGetValue<string>(value: out var text):
                        var rewritten = rewriter.Rewrite(text, field.Role, field.Kind);
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

                    site.Value = JsonValue.Create(WorldDocumentPaths.Relocate(
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

    // A creation document rides its own serializer, so the walk reads it by hand: every value in it a state cell may
    // stand in for — a shape's spatial value, a palette color, an identifier — binds a row by its `state.<row>` token.
    private static readonly WorldNameField CreationBinding = new(
        Facet: WorldExportFacet.Binding,
        Kind: WorldNameKind.State,
        Member: "state binding",
        Owner: typeof(Puck.World.Authoring.CreationDocument),
        Role: WorldNameRole.Binding
    );

    private static void VisitCreationBindings(JsonNode node, WorldNameVisitor visitor) {
        switch (node) {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToArray()) {
                    if (
                        (value is JsonValue leaf) &&
                        leaf.TryGetValue<string>(value: out var text) &&
                        text.StartsWith(
                        comparisonType: StringComparison.Ordinal,
                        value: "state."
                    )
                    ) {
                        visitor(
                            obj,
                            name,
                            value,
                            CreationBinding,
                            typeof(string)
                        );
                    } else if (value is not null) {
                        VisitCreationBindings(
                            node: value,
                            visitor: visitor
                        );
                    }
                }

                break;
            case JsonArray list:
                foreach (var element in list.ToArray()) {
                    if (element is not null) {
                        VisitCreationBindings(
                            node: element,
                            visitor: visitor
                        );
                    }
                }

                break;
        }
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
    /// <remarks>Member registrations are cached against the model's shape of each type (<see cref="WorldModelShape"/>).
    /// Document values, collection contents, and discriminator choices are read afresh on every walk.</remarks>
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

        if (type == typeof(Puck.World.Authoring.CreationDocument)) {
            VisitCreationBindings(
                node: node,
                visitor: visitor
            );

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

        // The walk reads the model's shape, never serializes, so it reads the generated description of the type.
        if (WorldModelShape.Of(type: type) is not { Described: true } shape) {
            return;
        }

        if (
            typeof(StateRow).IsAssignableFrom(c: type) &&
            (node is JsonObject rowObject)
        ) {
            foreach (var member in VisitMembers.GetValue(createValueCallback: BuildVisitMembers, key: shape)) {
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

        switch (shape.Kind) {
            case JsonTypeInfoKind.Object when (node is JsonObject obj):
                if (
                    (shape.Arms.Count > 0) &&
                    obj.TryGetPropertyValue(
                    jsonNode: out var discriminatorNode,
                    propertyName: "$type"
                ) &&
                    (discriminatorNode is JsonValue discriminatorValue) &&
                    discriminatorValue.TryGetValue<string>(value: out var discriminator)
                ) {
                    foreach (var derived in shape.Arms) {
                        if (
                            (derived.Discriminator is string arm) &&
                            string.Equals(
                            a: arm,
                            b: discriminator,
                            comparisonType: StringComparison.Ordinal
                        ) &&
                            (derived.Type != type)
                        ) {
                            Visit(
                                node: node,
                                type: derived.Type,
                                visitor: visitor
                            );

                            return;
                        }
                    }
                }

                foreach (var member in VisitMembers.GetValue(createValueCallback: BuildVisitMembers, key: shape)) {
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
                        type: shape.ElementType!,
                        visitor: visitor
                    );
                }

                break;
            case JsonTypeInfoKind.Dictionary when (node is JsonObject entries):
                foreach (var entry in entries) {
                    Visit(
                        node: entry.Value,
                        type: shape.ElementType!,
                        visitor: visitor
                    );
                }

                break;
        }
    }

    // Where a declared name lives: a placement and a prototype each keep their own namespace, and every other kind
    // shares the document's, so a module's placement 'gate' never renames a host row 'gate' the module reads.
    private enum NameSpace : byte {
        Document,
        Placement,
        Prototype,
    }

    private static NameSpace SpaceOf(WorldNameKind kind) => kind switch {
        WorldNameKind.Placement => NameSpace.Placement,
        WorldNameKind.Prototype => NameSpace.Prototype,
        _ => NameSpace.Document,
    };

    // What the rewrite asks at each name position: the position's kind, or null for a position naming what no module
    // declaration renames (an input channel, an adjacency, a music row, a screen), which only a module argument's
    // restoration reaches.
    private interface INameMap {
        bool TryMap(WorldNameKind? kind, string name, [NotNullWhen(returnValue: true)] out string? mapped);
    }
    // A map that answers by spelling alone, whatever the position: a module argument's placeholders, and a caller's
    // own dictionary.
    private sealed class KindlessNames(IReadOnlyDictionary<string, string> names) : INameMap {
        public bool TryMap(WorldNameKind? kind, string name, [NotNullWhen(returnValue: true)] out string? mapped) => names.TryGetValue(
            key: name,
            value: out mapped
        );
    }
    // Every name a fragment declares, by its kind and by the namespace it lives in, paired with its qualified spelling.
    private sealed class DeclaredNames : INameMap {
        private readonly Dictionary<(WorldNameKind Kind, string Name), string> m_exact = [];
        private readonly Dictionary<(NameSpace Space, string Name), string> m_spaced = [];

        public int Count => m_spaced.Count;
        public IReadOnlyCollection<string> Qualified => m_spaced.Values;

        public void Declare(WorldNameKind kind, string name, string qualified) {
            m_exact[(kind, name)] = qualified;
            m_spaced[(SpaceOf(kind: kind), name)] = qualified;
        }
        public bool TryMap(WorldNameKind? kind, string name, [NotNullWhen(returnValue: true)] out string? mapped) {
            mapped = null;

            if (kind is not { } known) {
                return false;
            }

            if (known != WorldNameKind.Any) {
                return m_spaced.TryGetValue(
                    key: (SpaceOf(kind: known), name),
                    value: out mapped
                );
            }

            foreach (var space in ((ReadOnlySpan<NameSpace>)[NameSpace.Document, NameSpace.Placement, NameSpace.Prototype])) {
                if (m_spaced.TryGetValue(
                    key: (space, name),
                    value: out mapped
                )) {
                    return true;
                }
            }

            return false;
        }
        public bool TryMapExact(WorldNameKind kind, string name, [NotNullWhen(returnValue: true)] out string? mapped) => m_exact.TryGetValue(
            key: (kind, name),
            value: out mapped
        );
    }
    // One reserved channel's arguments, read by the channel's grammar into the rewritten arguments: a position holding
    // a name maps under that name's kind, and a position the engine reads as its own word never does.
    private sealed class ChannelReader(Rewriter rewriter, List<string> arguments, List<string> output) {
        public void Name(int index, WorldNameKind? kind) => output.Add(item: rewriter.RewriteSegment(
            kind: kind,
            map: true,
            segment: arguments[index]
        ));
        public void NameAt(int index, WorldNameKind? kind) {
            if (index < arguments.Count) {
                Name(
                    index: index,
                    kind: kind
                );
            }
        }
        public void Word(int index) => output.Add(item: rewriter.RewriteSegment(
            kind: null,
            map: false,
            segment: arguments[index]
        ));
        public void Words(int from) {
            for (var index = from; (index < arguments.Count); index++) {
                Word(index: index);
            }
        }
        public void Names(int from, WorldNameKind? kind) {
            for (var index = from; (index < arguments.Count); index++) {
                Name(
                    index: index,
                    kind: kind
                );
            }
        }
        public void Leading(int count, WorldNameKind? kind) {
            for (var index = 0; ((index < count) && (index < arguments.Count)); index++) {
                Name(
                    index: index,
                    kind: kind
                );
            }
        }
        public void KeyTail(int from) => Tail(
            from: from,
            rewrite: rewriter.RewriteKey
        );
        public void ExpressionTail(int from) => Tail(
            from: from,
            rewrite: rewriter.RewriteExpression
        );
        // Words until an argument opens with '$', which starts a key spelling that runs to the end.
        public void WordsThenKey(int from) {
            for (var index = from; (index < arguments.Count); index++) {
                if (arguments[index].StartsWith(value: '$')) {
                    KeyTail(from: index);

                    return;
                }

                Word(index: index);
            }
        }
        // reduce:<op>:<row>, argmax:<row>, then any where:<filterRow> or between:<lower>:<upper>.
        public void Aggregate(bool operation) {
            var index = 0;

            if (operation && (arguments.Count > 0)) {
                Word(index: index++);
            }

            if (index < arguments.Count) {
                Name(
                    index: index++,
                    kind: WorldNameKind.State
                );
            }

            while (index < arguments.Count) {
                var isWhere = (arguments[index] == "where");

                Word(index: index++);

                if (isWhere && (index < arguments.Count)) {
                    Name(
                        index: index++,
                        kind: WorldNameKind.State
                    );
                }
            }
        }
        // symmetry:<function>[:<argument>]:<row>, where an argument cell:<row>[.<key>] reads a second row.
        public void Symmetry() {
            for (var index = 0; (index < arguments.Count); index++) {
                if (index == (arguments.Count - 1)) {
                    Name(
                        index: index,
                        kind: WorldNameKind.State
                    );
                } else if ((arguments[index] == "cell") && ((index + 1) < (arguments.Count - 1))) {
                    Word(index: index);
                    output.Add(item: rewriter.MapNameOrSplit(
                        kind: WorldNameKind.State,
                        name: arguments[++index]
                    ));
                } else {
                    Word(index: index);
                }
            }
        }
        // board:<operation>:<row>:<arguments>; cellOf's argument is a body reference.
        public void Board() {
            if (arguments.Count > 0) {
                Word(index: 0);
            }

            if (arguments.Count > 1) {
                Name(
                    index: 1,
                    kind: WorldNameKind.State
                );
            }

            if ((arguments.Count > 0) && (arguments[0] == "cellOf")) {
                BodyReferences(start: 2);
            } else {
                WordsThenKey(from: 2);
            }
        }
        // Successive body references from a position to the end.
        public void BodyReferences(int start) {
            while (start < arguments.Count) {
                start += BodyReference(start: start);
            }
        }
        // One body reference, then the channel's own facet words, or, for nearest, the rows it filters by.
        public void BodyReferenceThen(WorldNameKind? rows) {
            var width = BodyReference(start: 0);

            if (rows is { } kind) {
                Names(
                    from: width,
                    kind: kind
                );
            } else {
                Words(from: width);
            }
        }

        // One body reference: cell:<row>:<key> and argmax:<row>/argmin:<row> name a row, placement:<id> a placement,
        // and body:<n> and a bound each/left/right name nothing. Returns how many arguments it spanned.
        private int BodyReference(int start) {
            if (start >= arguments.Count) {
                return 0;
            }

            var width = Math.Min(
                val1: WorldFactsCompileContext.BodyRefTokenWidth(start: start, tokens: [.. arguments]),
                val2: (arguments.Count - start)
            );
            var kind = arguments[start];
            var named = kind switch {
                "cell" or "argmax" or "argmin" => WorldNameKind.State,
                "placement" => WorldNameKind.Placement,
                _ => ((WorldNameKind?)null),
            };

            output.Add(item: kind);

            for (var index = (start + 1); (index < (start + width)); index++) {
                if ((index == (start + 1)) && (named is { } nameKind)) {
                    Name(
                        index: index,
                        kind: nameKind
                    );
                } else if ((index == (start + 2)) && (kind == "cell")) {
                    output.Add(item: rewriter.RewriteKey(key: arguments[index]));
                } else {
                    Word(index: index);
                }
            }

            return Math.Max(
                val1: width,
                val2: 1
            );
        }
        private void Tail(int from, Func<string, string> rewrite) {
            if (from < arguments.Count) {
                output.Add(item: rewrite(arg: string.Join(separator: ':', values: arguments.Skip(count: from))));
            }
        }
    }

    // The channel's name, as a reserved spelling opens it: '$reduce:' names 'reduce'.
    private static string ChannelName(string spelling) => spelling[1..spelling.IndexOf(value: ':')];

    // Every reserved channel, each with the grammar its arguments are read by. The grammars mirror the channel
    // compilers (RuleCompiler's operands and WorldFactsVocabulary's) and the spellings RuleFacts and WorldRuleFacts
    // document; a spelling this table does not hold is no channel, and its arguments are left as written.
    private static readonly FrozenDictionary<string, Action<ChannelReader>> Channels = new (string Name, Action<ChannelReader> Grammar)[] {
        (ChannelName(spelling: RuleFacts.CellKeyPrefix), static read => {
            read.Leading(count: 1, kind: WorldNameKind.State);
            read.KeyTail(from: 1);
        }),
        (ChannelName(spelling: RuleFacts.ExpressionKeyPrefix), static read => read.ExpressionTail(from: 0)),
        (ChannelName(spelling: RuleFacts.HistoryPrefix), static read => {
            read.Leading(count: 1, kind: WorldNameKind.State);
            read.ExpressionTail(from: 1);
        }),
        (ChannelName(spelling: RuleFacts.MatchPrefix), static read => {
            read.Leading(count: 1, kind: WorldNameKind.Pattern);
            read.NameAt(index: 1, kind: WorldNameKind.Zone);
            read.Words(from: 2);
        }),
        (ChannelName(spelling: RuleFacts.ZoneKeyPrefix), static read => {
            read.Leading(count: 1, kind: WorldNameKind.Zone);
            read.Words(from: 1);
        }),
        ("phase", static read => {
            read.Leading(count: 1, kind: WorldNameKind.State);
            read.Words(from: 1);
        }),
        (ChannelName(spelling: RuleFacts.TablePrefix), static read => {
            read.Leading(count: 1, kind: WorldNameKind.Table);
            read.WordsThenKey(from: 1);
        }),
        (ChannelName(spelling: RuleFacts.ReducePrefix), static read => read.Aggregate(operation: true)),
        (ChannelName(spelling: WorldRuleFacts.ArgMaxPrefix), static read => read.Aggregate(operation: false)),
        (ChannelName(spelling: WorldRuleFacts.ArgMinPrefix), static read => read.Aggregate(operation: false)),
        (ChannelName(spelling: RuleFacts.SymmetryPrefix), static read => read.Symmetry()),
        ("board", static read => read.Board()),
        (ChannelName(spelling: WorldRuleFacts.DistancePrefix), static read => read.BodyReferences(start: 0)),
        (ChannelName(spelling: WorldRuleFacts.LineOfSightPrefix), static read => read.BodyReferences(start: 0)),
        (ChannelName(spelling: WorldRuleFacts.PairKeyPrefix), static read => read.BodyReferences(start: 0)),
        (ChannelName(spelling: WorldRuleFacts.FactPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.IdentityPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.NavigationPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.ParkedPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.UprightPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.NearestPrefix), static read => read.BodyReferenceThen(rows: WorldNameKind.State)),
        // A seat, then the input channel it reads.
        (ChannelName(spelling: WorldRuleFacts.ChannelPrefix), static read => {
            read.Word(index: 0);
            read.Names(from: 1, kind: null);
        }),
        // A seat, a screen index and the facet it reads: nothing a module renames.
        (ChannelName(spelling: WorldRuleFacts.PointerPrefix), static read => read.Words(from: 0)),
        // An influence label every module shares, then the placement it reads.
        (ChannelName(spelling: WorldRuleFacts.InfluencePrefix), static read => {
            read.Word(index: 0);
            read.Names(from: 1, kind: WorldNameKind.Placement);
        }),
        // The music row or screen, then the channel's facet or byte address.
        (ChannelName(spelling: WorldRuleFacts.ClockPrefix), static read => {
            read.Leading(count: 1, kind: null);
            read.Words(from: 1);
        }),
        (ChannelName(spelling: WorldRuleFacts.MachinePrefix), static read => {
            read.Leading(count: 1, kind: null);
            read.Words(from: 1);
        }),
        (ChannelName(spelling: WorldRuleFacts.LinkPrefix), static read => read.Names(from: 0, kind: null)),
        (ChannelName(spelling: WorldRuleFacts.RegionPrefix), static read => read.Names(from: 0, kind: WorldNameKind.Placement)),
        (ChannelName(spelling: WorldRuleFacts.PhysicsQuiescent), static read => read.Words(from: 0)),
        (ChannelName(spelling: RuleFacts.SearchPly), static read => read.Words(from: 0)),
    }.ToFrozenDictionary(
        comparer: StringComparer.Ordinal,
        elementSelector: static channel => channel.Grammar,
        keySelector: static channel => channel.Name
    );

    private sealed class Rewriter(INameMap names) {
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
            var mapped = Map(
                kind: WorldNameKind.Pool,
                name: name
            );

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
        private string Map(string name, WorldNameKind? kind) => (names.TryMap(
            kind: kind,
            mapped: out var mapped,
            name: name
        )
            ? mapped
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
        // one where CellName never does — so a name found whole in the map is renamed whole; only a name that is NOT
        // itself declared falls to ExpressionSpelling's typed lexical-field rule. An instance binding stays local
        // while a qualified declaration is renamed; the field half is never itself a `Declares` site.
        public string MapNameOrSplit(string name, WorldNameKind? kind) {
            if (names.TryMap(
                kind: kind,
                mapped: out var mapped,
                name: name
            )) {
                return mapped;
            }

            return (ExpressionSpelling.TrySplitDottedName(
                key: out var key,
                name: name,
                row: out var row
            )
                ? $"{(IsInstanceBinding(name: row) ? row : Map(kind: kind, name: row))}.{key}"
                : name
            );
        }

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

            return $"{BindingPrefix}{Map(kind: WorldNameKind.State, name: row)}{tail}";
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

        // Rewrites one registered site in place: a list of names, or one name-bearing value. A member that holds a
        // call node holds one again, its row arguments renamed.
        public void RewriteSite(JsonObject parent, string name, JsonNode value, WorldNameField field, Type memberType) {
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
                            list[index: index] = Rewrite(
                                kind: field.Kind,
                                role: field.Role,
                                text: item
                            );
                        }
                    }

                    break;
                case JsonObject map when ((field.Role == WorldNameRole.Names) && (memberType == typeof(IReadOnlyDictionary<string, string>))):
                    // A map whose values are names: a deal's variant prototypes.
                    foreach (var (key, entry) in map.ToArray()) {
                        if ((entry is JsonValue mapped) && mapped.TryGetValue<string>(value: out var item)) {
                            map[propertyName: key] = Rewrite(
                                kind: field.Kind,
                                role: field.Role,
                                text: item
                            );
                        }
                    }

                    break;
                case JsonValue leaf when leaf.TryGetValue<string>(value: out var text):
                    var rewritten = Rewrite(
                        kind: field.Kind,
                        role: field.Role,
                        text: text
                    );

                    parent[propertyName: name] = ((value is JsonObject)
                        ? WorldChannelNodes.Node(spelling: rewritten)
                        : rewritten
                    );

                    break;
            }
        }
        // Infix text is read by the expression grammar itself, never lexically: it parses to the IR, the IR's
        // registered sites are rewritten exactly as a document's own IR is, and the program prints back. So a channel
        // call's arguments are read by the channel's grammar, a reduction's options and a fold's binder stay the
        // engine's, and only a position that names a declaration is renamed. Text that does not parse is left as
        // written; it is refused where it compiles.
        public string RewriteExpression(string text) {
            if (!ExpressionSpelling.TryParse(
                error: out _,
                program: out var program,
                text: text
            )) {
                return text;
            }

            var node = ExpressionProgramJsonConverter.ToNode(program: program);

            VisitExpressionProgram(
                node: node,
                visitor: (parent, name, value, field, memberType) => RewriteSite(
                    field: field,
                    memberType: memberType,
                    name: name,
                    parent: parent,
                    value: value
                )
            );

            return (ExpressionSpelling.TryPrint(
                program: ExpressionProgramJsonConverter.FromNode(node: node),
                text: out var printed
            )
                ? printed
                : text
            );
        }
        // A literal key is local to its row and stays; a reserved spelling ($cell:, $zone:, $pair:, $expr:, $zones[)
        // or a body-reference spelling (cell:<row>:<key>, argmax:<row>) carries names in its segments.
        public string RewriteKey(string key) {
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

        // A reserved spelling's colon segments, each read by the position it holds in its channel's grammar
        // (Channels): only a position that names a declaration maps, so a keyword the engine reads there (an
        // operation, a facet, a body-reference kind) keeps its meaning whatever a module declares. A body-reference
        // key (cell:<row>:<key>, argmax:<row>, placement:<id>) reads the same way. A $local: read names a rule-scoped
        // local, never a row, and a channel the table does not hold is left as written.
        private string RewriteReserved(string name) {
            if (name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.LocalPrefix
            )) {
                return name;
            }

            var segments = SplitSegments(text: name);
            var output = new List<string>(capacity: segments.Count);

            if (!segments[0].StartsWith(value: '$')) {
                new ChannelReader(arguments: segments, output: output, rewriter: this).BodyReferences(start: 0);
            } else {
                output.Add(item: RewriteSegment(
                    kind: null,
                    map: false,
                    segment: segments[0]
                ));

                if (segments.Count > 1) {
                    var arguments = segments.GetRange(
                        count: (segments.Count - 1),
                        index: 1
                    );

                    if (Channels.TryGetValue(
                        key: segments[0][1..],
                        value: out var grammar
                    )) {
                        grammar(obj: new ChannelReader(arguments: arguments, output: output, rewriter: this));
                    } else {
                        output.AddRange(collection: arguments);
                    }
                }
            }

            return string.Join(
                separator: ':',
                values: output
            );
        }

        // One colon segment: its text outside brackets mapped under the kind when it holds a name, and each
        // bracketed span — a live zone's index, a cell key in its own right — rewritten as a key.
        public string RewriteSegment(string segment, bool map, WorldNameKind? kind) {
            var output = new StringBuilder(capacity: segment.Length);
            var text = new StringBuilder();
            var index = 0;

            while (index < segment.Length) {
                var character = segment[index];

                if (character == '[') {
                    var close = MatchingBracket(
                        open: index,
                        text: segment
                    );

                    if (close < 0) {
                        _ = text.Append(
                            count: (segment.Length - index),
                            startIndex: index,
                            value: segment
                        );

                        break;
                    }

                    _ = output.Append(value: (map ? Map(kind: kind, name: text.ToString()) : text.ToString()));
                    _ = text.Clear();
                    _ = output.Append(value: '[').Append(value: RewriteBracket(inner: segment[(index + 1)..close])).Append(value: ']');
                    index = (close + 1);

                    continue;
                }

                _ = text.Append(value: character);
                index++;
            }

            _ = output.Append(value: (map ? Map(kind: kind, name: text.ToString()) : text.ToString()));

            return output.ToString();
        }

        // Splits a spelling on the colons outside any bracket or parenthesis.
        private static List<string> SplitSegments(string text) {
            var segments = new List<string>();
            var depth = 0;
            var start = 0;

            for (var index = 0; (index < text.Length); index++) {
                switch (text[index]) {
                    case '[' or '(':
                        depth++;

                        break;
                    case ']' or ')':
                        depth--;

                        break;
                    case ':' when (depth == 0):
                        segments.Add(item: text[start..index]);
                        start = (index + 1);

                        break;
                }
            }

            segments.Add(item: text[start..]);

            return segments;
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

        public string Rewrite(string text, WorldNameRole role, WorldNameKind kind) => role switch {
            WorldNameRole.Declares => Map(kind: kind, name: text),
            WorldNameRole.Names => (text.StartsWith(value: '$')
            ? RewriteReserved(name: text)
            : MapNameOrSplit(kind: kind, name: text)),
            WorldNameRole.Key => RewriteKey(key: text),
            WorldNameRole.Expression => RewriteExpression(text: text),
            WorldNameRole.Binding => RewriteBinding(token: text),
            WorldNameRole.Template => RewriteTemplate(template: text),
            _ => text,
        };
    }
}
