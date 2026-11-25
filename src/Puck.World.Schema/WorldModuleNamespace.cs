using Puck.Abstractions.Documents;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
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
/// A placement, a prototype, a clock, a screen material, a camera and a spawn point each live in their own namespace, apart from every
/// state-side name. The engine's own words are never renamed: a function an expression calls, and a channel's
/// operation, facet, or body-reference kind.
/// A name the fragment does not declare is left as written, so a fragment may still address its host's rows by
/// name. Runs on the fragment's composed raw JSON tree before the strict parse, in the same pass that strips
/// <c>basis</c>/<c>imports</c>.
/// </summary>
public static class WorldModuleNamespace {
    /// <summary>Returns whether a name of a kind belongs to the fragment that declares it: renamed under an alias,
    /// and named by another layer only through the fragment's exports. A camera and a spawn point are anchors of the
    /// whole composition instead, which every layer names as declared, the way a route names a place's spawn.</summary>
    /// <param name="kind">The name's kind.</param>
    /// <returns><see langword="false"/> for <see cref="WorldNameKind.Camera"/> and
    /// <see cref="WorldNameKind.SpawnPoint"/>; otherwise <see langword="true"/>.</returns>
    internal static bool IsModuleScoped(WorldNameKind kind) => (kind is not (WorldNameKind.Camera or WorldNameKind.SpawnPoint));
    private static DeclaredNames CollectDeclaredNames(JsonObject module, string alias) {
        var declared = new DeclaredNames();

        Visit(
            node: module,
            type: typeof(WorldDefinition),
            visitor: (parent, name, value, field, _) => {
                if (
                    (field.Role == WorldNameRole.Declares) && IsModuleScoped(kind: field.Kind) &&
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
    /// <summary>Returns the world name kind a provider field's value references: a state, machine or screen
    /// reference role names its kind, and every other role is <see cref="WorldNameKind.Any"/>.</summary>
    /// <param name="role">The provider field's role.</param>
    internal static WorldNameKind ProviderReferenceKind(MachineFieldRole role) => role switch {
        MachineFieldRole.StateReference => WorldNameKind.State,
        MachineFieldRole.MachineReference => WorldNameKind.Machine,
        MachineFieldRole.ScreenReference => WorldNameKind.Screen,
        _ => WorldNameKind.Any
    };
    /// <summary>One machine row of a module whose engine the catalog describes and has a configuration object, with
    /// the provider descriptor that reads that configuration.</summary>
    internal readonly record struct ModuleMachine(JsonObject Machine, JsonObject Configuration, MachineObjectDescriptor Descriptor) {
        /// <summary>Gets the machine's authored name for a refusal, or a placeholder when it has none.</summary>
        public string DisplayName => (Machine["name"]?.ToString() ?? "(unnamed)");
    }
    /// <summary>Walks the machine rows of a module the catalog describes, the one machine-configuration walk of module
    /// export collection, import rewriting and asset relocation. A row without an engine, a configuration object or a
    /// catalog entry is skipped.</summary>
    /// <param name="module">The module's document tree.</param>
    /// <param name="catalog">The host's machine vocabulary.</param>
    /// <param name="refuseInvalidDescriptor">Whether a structurally unusable provider descriptor refuses the walk.
    /// The descriptor is the catalog's, so every machine of one engine meets the same verdict: a caller that rewrites
    /// the module refuses it, because a configuration read through a broken descriptor would be rewritten blind, and
    /// a caller that only collects references skips the machine, because the rewriting path owns the refusal and a
    /// reference scan has no channel to report it.</param>
    /// <param name="visitor">Receives each machine and returns a refusal, or <see langword="null"/> to continue.</param>
    /// <param name="reason">The refusal on failure; empty otherwise.</param>
    /// <returns><see langword="true"/> when no machine was refused.</returns>
    internal static bool TryVisitMachines(JsonObject module, IMachineValidationCatalog catalog, bool refuseInvalidDescriptor, Func<ModuleMachine, string?> visitor, out string reason) {
        reason = string.Empty;

        if (module["machines"] is not JsonArray machines) {
            return true;
        }

        foreach (var node in machines) {
            if (!TryReadMachine(node: node, catalog: catalog, machine: out var visited)) {
                continue;
            }

            var descriptorErrors = new List<string>();

            if (!MachineConfigurationFields.TryValidateDescriptor(
                descriptor: visited.Descriptor,
                errors: descriptorErrors
            )) {
                if (refuseInvalidDescriptor) {
                    reason = ((("machine '" + visited.DisplayName) +
                        "' has invalid provider descriptor: ") + string.Join(
                        separator: " ",
                        values: descriptorErrors
                    ));

                    return false;
                }

                continue;
            }

            if (visitor(arg: visited) is { } refusal) {
                reason = refusal;

                return false;
            }
        }

        return true;
    }
    // Reads one machine row the catalog describes: its configuration object and its engine's provider descriptor. A
    // row without an engine, a configuration object or a catalog entry has neither.
    private static bool TryReadMachine(JsonNode? node, IMachineValidationCatalog catalog, out ModuleMachine machine) {
        if (
            (node is not JsonObject row) ||
            (row["engine"] is not JsonValue engineValue) ||
            !engineValue.TryGetValue<string>(value: out var engineId) ||
            (row["configuration"] is not JsonObject configuration) ||
            !catalog.TryDescriptor(descriptor: out var descriptor, engineId: engineId)
        ) {
            machine = default;

            return false;
        }

        machine = new ModuleMachine(Machine: row, Configuration: configuration, Descriptor: descriptor.Configuration);

        return true;
    }
    /// <summary>Reads, and respells, every name a machine row's configuration holds at a provider field whose role
    /// references a world declaration (<see cref="MachineFieldRole.StateReference"/>,
    /// <see cref="MachineFieldRole.MachineReference"/>, <see cref="MachineFieldRole.ScreenReference"/>), through the
    /// descriptor the host's catalog holds for the row's engine: the positions <see cref="Respell(JsonNode?, Type, WorldNameRespelling)"/>
    /// cannot reach, since a configuration's shape is its provider's. A row whose engine the catalog does not describe,
    /// or whose descriptor is unusable, has no such position.</summary>
    /// <param name="machine">The machine row; respelled in place.</param>
    /// <param name="catalog">The host's machine vocabulary.</param>
    /// <param name="respell">Called once per name position, with a site whose member is the configuration.</param>
    public static void RespellMachineReferences(JsonObject machine, IMachineValidationCatalog catalog, WorldNameRespelling respell) {
        ArgumentNullException.ThrowIfNull(argument: machine);
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: respell);

        if (!TryReadMachine(node: machine, catalog: catalog, machine: out var read) ||
            !MachineConfigurationFields.TryValidateDescriptor(descriptor: read.Descriptor, errors: [])) {
            return;
        }

        MachineConfigurationFields.Visit(
            configuration: read.Configuration,
            descriptor: read.Descriptor,
            visitor: site => {
                if (
                    (site.Field.Role is not (MachineFieldRole.StateReference or MachineFieldRole.MachineReference or MachineFieldRole.ScreenReference)) ||
                    (site.Value is not JsonValue value) ||
                    !value.TryGetValue<string>(value: out var name) ||
                    (name.Length == 0)
                ) {
                    return;
                }

                var kind = ProviderReferenceKind(role: site.Field.Role);

                if ((respell(new WorldNameField(Owner: typeof(WorldMachine), Member: nameof(WorldMachine.Configuration), Kind: kind, Role: WorldNameRole.Names), kind, name, read.Configuration) is { } respelled) &&
                    (respelled != name)) {
                    site.Value = JsonValue.Create(respelled);
                }
            }
        );
    }
    private static void RelocateMachinePaths(ModuleMachine machine, string sourceDocumentPath, string targetDocumentPath) =>
        MachineConfigurationFields.Visit(
            configuration: machine.Configuration,
            descriptor: machine.Descriptor,
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
    private static bool TryRewriteMachineMetadata(JsonObject module, string alias, IMachineValidationCatalog catalog,
        string sourceDocumentPath, string targetDocumentPath, DeclaredNames declared, out string reason) =>
        TryVisitMachines(
            catalog: catalog,
            module: module,
            reason: out reason,
            refuseInvalidDescriptor: true,
            visitor: machine => {
                RelocateMachinePaths(
                    machine: machine,
                    sourceDocumentPath: sourceDocumentPath,
                    targetDocumentPath: targetDocumentPath
                );

                return RewriteMachineNames(
                    alias: alias,
                    declared: declared,
                    machine: machine
                );
            }
        );
    private static string? RewriteMachineNames(string alias, DeclaredNames declared, ModuleMachine machine) {
        var configuration = machine.Configuration;
        var descriptor = machine.Descriptor;
        var local = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var localError = string.Empty;

        MachineConfigurationFields.Visit(
            configuration: configuration,
            descriptor: descriptor,
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
                    localError = (((("machine '" + machine.DisplayName) +
                        "' declares duplicate provider-local name '") + name) + "'.");
                }
            }
        );

        if (localError.Length != 0) {
            return localError;
        }

        MachineConfigurationFields.Visit(
            configuration: configuration,
            descriptor: descriptor,
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
        return null;
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
    /// <summary>Reads every name a raw tree spells at a registered site, by the same grammar the import rewrite reads
    /// (a bare name, a reserved channel's name positions, a cell key's <c>$cell:</c>/<c>$zone:</c>/<c>$expr:</c>
    /// spelling, an expression's state reads, a binding token, a HUD template's placeholders), and writes back the
    /// name <paramref name="respell"/> returns, or leaves the name as written when it returns
    /// <see langword="null"/>. The import rewrite and the write door's footprint are its two readers, so a name
    /// position the registry learns reaches both.</summary>
    /// <remarks>The tree is the caller's own: a site whose names are all left as written may still be written back
    /// in its canonical spelling.</remarks>
    /// <param name="node">The tree, or the subtree to walk.</param>
    /// <param name="type">The model type <paramref name="node"/> holds.</param>
    /// <param name="respell">Called once per name position, in document order.</param>
    public static void Respell(JsonNode? node, Type type, WorldNameRespelling respell) {
        ArgumentNullException.ThrowIfNull(argument: respell);

        var names = new RespellingNames(respell: respell);
        var rewriter = new Rewriter(names: names);

        Visit(
            node: node,
            type: type,
            visitor: (parent, name, value, field, memberType) => {
                names.Site = field;
                names.Holder = parent;
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
    }
    /// <summary>Reads every name one value of a registered site spells and returns it respelled, as
    /// <see cref="Respell(JsonNode?, Type, WorldNameRespelling)"/> does for a whole tree: the entry for a name-bearing
    /// value that is not held in a document tree (a mutation's own row or id argument).</summary>
    /// <param name="text">The value.</param>
    /// <param name="site">The registration describing how the value carries names.</param>
    /// <param name="respell">Called once per name position.</param>
    /// <returns>The respelled value.</returns>
    public static string Respell(string text, WorldNameField site, WorldNameRespelling respell) {
        ArgumentNullException.ThrowIfNull(argument: text);
        ArgumentNullException.ThrowIfNull(argument: site);
        ArgumentNullException.ThrowIfNull(argument: respell);

        return new Rewriter(names: new RespellingNames(respell: respell) { Site = site }).Rewrite(
            kind: site.Kind,
            role: site.Role,
            text: text
        );
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
                rewriter.RewriteSite(field: field, memberType: memberType, name: name, parent: parent, value: value);
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

        return TryVisitMachines(
            catalog: catalog,
            module: module,
            reason: out reason,
            refuseInvalidDescriptor: true,
            visitor: machine => {
                RelocateMachinePaths(
                    machine: machine,
                    sourceDocumentPath: sourceDocumentPath,
                    targetDocumentPath: targetDocumentPath
                );

                return null;
            }
        );
    }

    private static void VisitField(JsonObject holder, string name, JsonNode value, WorldNameField field, Type type, WorldNameVisitor visitor) {
        if (value is JsonObject map && WorldModelShape.Of(type) is { Kind: JsonTypeInfoKind.Dictionary, ElementType: { } element }) {
            // A visitor may replace the current value. Keep the dictionary's keys and document order.
            for (var i = 0; i < map.Count; i++) {
                var entry = map.GetAt(i);
                if (entry.Value is { } item) { VisitField(map, entry.Key, item, field, element, visitor); }
            }
        } else {
            visitor(holder, name, value, field, type);
        }
    }

    // A converter-backed bindable has no serializer members, but its keyed arm names a clock, and a scalar's key may hold
    // keys of its own naming another; each follows the module's declaration as a keyed section's clock does.
    private static void VisitKeyClocks(JsonObject? node, WorldNameVisitor visitor) {
        if (node is null) {
            return;
        }

        if (node["clock"] is { } clock) {
            visitor(
                node,
                "clock",
                clock,
                WorldNameRegistry.KeyClock.NameField!,
                typeof(string)
            );
        }

        if (node["keys"] is JsonArray keys) {
            foreach (var key in keys) {
                VisitKeyClocks(node: (key?["value"] as JsonObject), visitor: visitor);
            }
        }
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
            var shape = ExpressionOperators.PayloadOf(operation: operation);
            if (shape != PayloadShape.None) {
                VisitPayload(obj, WorldNameRegistry.PayloadTypes[shape], visitor);
            }
        }
    }
    private static void VisitPayload(JsonObject obj, Type type, WorldNameVisitor visitor) {
        foreach (var property in WorldModelShape.Of(type)!.Properties) {
            if (obj[property.Name] is not { } value) { continue; }
            if (property.NameField is { } field) {
                visitor(obj, property.Name, value, field, property.Type);
            } else if (property.Type == typeof(VectorOperand) && value is JsonObject operand &&
                operand["$type"] is JsonValue tag && tag.TryGetValue<string>(out var kind) &&
                WorldNameRegistry.VectorTypes.TryGetValue(kind, out var arm)) {
                VisitPayload(operand, arm, visitor);
            }
        }
    }

    // A creation document rides its own serializer, so the walk reads it by hand: every value in it a state cell may
    // stand in for binds a row by its `state.<row>` token (WorldNameRegistry.CreationBinding).
    private static void VisitCreationBindings(JsonNode node, WorldNameVisitor visitor) {
        switch (node) {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToArray()) {
                    if (
                        (value is JsonValue leaf) &&
                        leaf.TryGetValue<string>(value: out var text) &&
                        Puck.Assets.Documents.DocumentIdentifier.IsReference(value: text)
                    ) {
                        visitor(
                            obj,
                            name,
                            value,
                            WorldNameRegistry.CreationBinding,
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

        // Converter-backed bindables have no serializer members. Their keyed arm still names a clock, and
        // that reference must follow the module's declaration just as a keyed section's clock does.
        if (WorldKeyedValues.IsKeyable(type: type)) {
            VisitKeyClocks(node: (node as JsonObject), visitor: visitor);

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
                WorldModelShape.Of(type)!.Properties.First(property => property.Member == nameof(WorldLatticeScalar.Row)).NameField is { } scalarField
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

        switch (shape.Kind) {
            case JsonTypeInfoKind.None when typeof(StateRow).IsAssignableFrom(type) && node is JsonObject:
            case JsonTypeInfoKind.Object when node is JsonObject:
                var obj = (JsonObject)node;
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

                foreach (var member in WorldNameRegistry.MembersOf(shape)) {
                    if (
                        !obj.TryGetPropertyValue(
                        propertyName: member.Name,
                        jsonNode: out var value
                    ) ||
                        (value is null)
                    ) {
                        continue;
                    }

                    if (member.NameField is { } field) {
                        if (member.IdentifierBinding is { } binding && value is JsonValue literal &&
                            literal.TryGetValue<string>(out var token) && Puck.Assets.Documents.DocumentIdentifier.IsReference(token)) {
                            field = binding;
                        }
                        VisitField(obj, member.Name, value, field, member.Type, visitor);
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

    // Where a declared name lives: a placement, a prototype, a clock, a screen material, a camera and a spawn point each
    // keep their own namespace, and every other kind shares the document's, so a module's placement 'gate' never
    // renames a host row 'gate' the module reads.
    internal enum NameSpace : byte {
        Document,
        Placement,
        Prototype,
        Clock,
        ScreenMaterial,
        Camera,
        SpawnPoint,
    }

    private static readonly NameSpace[] Spaces = Enum.GetValues<NameSpace>();

    internal static NameSpace SpaceOf(WorldNameKind kind) => kind switch {
        WorldNameKind.Placement => NameSpace.Placement,
        WorldNameKind.Prototype => NameSpace.Prototype,
        WorldNameKind.Clock => NameSpace.Clock,
        WorldNameKind.ScreenMaterial => NameSpace.ScreenMaterial,
        WorldNameKind.Camera => NameSpace.Camera,
        WorldNameKind.SpawnPoint => NameSpace.SpawnPoint,
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
    // A map that asks its caller at every position, with the registration the position was read under.
    private sealed class RespellingNames(WorldNameRespelling respell) : INameMap {
        public WorldNameField? Site { get; set; }
        public JsonObject? Holder { get; set; }

        public bool TryMap(WorldNameKind? kind, string name, [NotNullWhen(returnValue: true)] out string? mapped) {
            mapped = ((name.Length == 0) ? null : respell(Site!, kind, name, Holder));
            return (mapped is not null);
        }
    }
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

            foreach (var space in Spaces) {
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
        public void Word(int index) {
            if (index < arguments.Count) {
                output.Add(item: rewriter.RewriteSegment(
                    kind: null,
                    map: false,
                    segment: arguments[index]
                ));
            }
        }
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
        // A position axis and a body-state slot are engine names, which no module renames.
        (ChannelName(spelling: WorldRuleFacts.BodyPositionPrefix), static read => read.BodyReferenceThen(rows: null)),
        // A screen index is the document's own, which no module renames.
        (ChannelName(spelling: WorldRuleFacts.EngagedPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.BodyStatePrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.IdentityPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.NavigationPrefix), static read => read.BodyReferenceThen(rows: null)),
        (ChannelName(spelling: WorldRuleFacts.ProducerPrefix), static read => read.BodyReferenceThen(rows: null)),
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
        (ChannelName(spelling: WorldRuleFacts.PhysicsQuiescent), static read => {
            read.Word(index: 0);
            read.Names(from: 1, kind: WorldNameKind.State);
        }),
        (ChannelName(spelling: RuleFacts.SearchPly), static read => read.Words(from: 0)),
    }.ToFrozenDictionary(
        comparer: StringComparer.Ordinal,
        elementSelector: static channel => channel.Grammar,
        keySelector: static channel => channel.Name
    );

    private sealed class Rewriter(INameMap names) {
        public JsonNode? Scope { get; set; }

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
            if (!Puck.Assets.Documents.DocumentIdentifier.IsReference(value: token)) {
                return token;
            }

            var rest = token[Puck.Assets.Documents.DocumentIdentifier.ReferencePrefix.Length..];
            var dot = rest.IndexOf(value: '.');
            var row = ((dot < 0)
                ? rest
                : rest[..dot]
            );
            var tail = ((dot < 0)
                ? string.Empty
                : rest[dot..]
            );

            return $"{Puck.Assets.Documents.DocumentIdentifier.ReferencePrefix}{Map(kind: WorldNameKind.State, name: row)}{tail}";
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

        // Rewrites one registered site in place: a list holding one value of the site's role per element (names,
        // binding tokens such as a list of bindable scalars or colors, templates, keys), or one name-bearing value. A
        // member that holds a call node holds one again, its row arguments renamed.
        public void RewriteSite(JsonObject parent, string name, JsonNode value, WorldNameField field, Type memberType) {
            switch (WorldChannelNodes.Spelled(
                memberType: memberType,
                value: value
            )) {
                case JsonArray list when (field.Role is WorldNameRole.Names or WorldNameRole.Binding or WorldNameRole.Template or WorldNameRole.Key):
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
            WorldNameRole.BodyReference => text.Contains(':') ? RewriteReserved(name: text) : text,
            WorldNameRole.Expression => RewriteExpression(text: text),
            WorldNameRole.Binding => RewriteBinding(token: text),
            WorldNameRole.Template => RewriteTemplate(template: text),
            _ => text,
        };
    }
}
