using Puck.Abstractions.Machines;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Composition;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Validation;

// Symbol resolution over the LOWERED JSON, not the AST: it sees exactly what ships, template-expanded and ready to
// compose. Four reference families — state row, prototypeId, placement parent, camera/spawn-point — plus a
// best-effort `$`-prefix typo check and a shape-parent check, each Information severity except shape-parent
// (Warning). Zero false positives is the bar: a `$`-prefixed name is never checked against a declared-row set, and a
// cell key's reserved spellings are read only where they hold an expression.
//
// The state-row family is found by WorldNameRegistry, not listed here: WorldModuleNamespace.Visit walks the tree
// type-directed and hands back every registered site, the same sites an aliased import rewrites, so a member
// registered there is resolved here without a second edit. A site is read by its role — a name or a list of
// them, an expression as IR or as infix text, a `state.<row>` binding, a template's placeholders. A `state`,
// `comparandState` or `fromState` field the registry excludes sits at body scope and names a per-body slot; the
// generic walk resolves those against the same catalog, which holds every lane's names.
//
// A document naming a `basis` or `imports` composes its whole graph through PuckDocumentComposer — the same
// composition the game boot path and `compile --validate` run — so a name only a basis or import supplies resolves
// correctly instead of reading as unresolved. That composed tree is used ONLY to build the name catalog: every
// candidate reference site is still found by walking THIS document's own tree (never the composed one, whose
// merged array order no longer lines up with `sourceMap`), so a reported finding's JSON pointer always traces back
// to this document's own source. Whether an unresolvable name is REPORTED turns on
// WorldSemanticValidator.IsRootDocument instead: a module is a fragment some other, unknown root may import, so a
// name it does not declare may be one that root supplies, which a standalone pass over the module cannot see. The
// shape-parent check is the one exception on both sides: it resolves a shape's `parent` against sibling names in
// the SAME shapes array, a purely local scope no basis or importer could ever change, so it always runs and always
// reports.
public static partial class PuckLinter {
    private const string BindingPrefix = "state.";

    // Read from RuleFacts' own constants, trimmed of their trailing ':'/'[' separator, so a channel added there is
    // known here without a second edit.
    private static readonly string[] KnownChannelPrefixes = [
        .. new[] {
            RuleFacts.MatchPrefix, RuleFacts.HistoryPrefix, RuleFacts.CellKeyPrefix, RuleFacts.ZoneKeyPrefix,
            RuleFacts.LiveZonePrefix, RuleFacts.ForEachZones, RuleFacts.ExpressionKeyPrefix, RuleFacts.LocalPrefix,
            RuleFacts.TablePrefix, RuleFacts.ReducePrefix, RuleFacts.SymmetryPrefix, RuleFacts.Tick,
        }.Select(selector: static prefix => prefix.TrimEnd(
            ':',
            '['
        )).Distinct(comparer: StringComparer.Ordinal),
    ];

    /// <summary>Lints a canonical lowered <c>puck.world.definition.v1</c> document for unresolved symbolic references.
    /// A document naming a <c>basis</c> or <c>imports</c> composes that whole graph (through
    /// <see cref="PuckDocumentComposer"/>, rooted beside <paramref name="sourcePath"/> exactly like
    /// <see cref="WorldSemanticValidator.ValidateComposedWorld"/>) to build the name catalog references resolve
    /// against, but always walks <paramref name="document"/>'s own tree — never the composed one — to find
    /// candidate reference sites, so every reported finding's JSON pointer already belongs to this document's own
    /// <paramref name="sourceMap"/>. A document <see cref="WorldSemanticValidator.IsRootDocument"/> calls a module
    /// never reports an unresolvable name (it may be supplied by whichever root imports it), except a shape's
    /// <c>parent</c>, which is scoped to sibling shapes in the same array and never depends on an outside
    /// catalog.</summary>
    /// <param name="document">The lowered world document — this document's own tree, not a composed one.</param>
    /// <param name="sourceMap">The JSON-pointer-to-span map the same lowering pass populated, for diagnostic spans.</param>
    /// <param name="diagnostics">The bag to record findings into.</param>
    /// <param name="sourcePath">This document's own resolved path — a declared <c>basis</c>/<c>imports</c> entry
    /// resolves relative to its directory. Required even for a document with no basis, for parity with
    /// <see cref="WorldSemanticValidator.ValidateComposedWorld"/>'s contract; unused when there is nothing to
    /// compose.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for the selected host catalog.</param>
    /// <param name="machines">The selected host machine catalog used for provider composition.</param>
    public static void LintReferences(JsonObject document, SourceMap? sourceMap, DiagnosticBag diagnostics, string sourcePath, string catalogFingerprint = "", IMachineValidationCatalog? machines = null) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var isRoot = WorldSemanticValidator.IsRootDocument(loweredJson: document);
        var catalogSource = document;

        if (
            (document["basis"] is not null) ||
            (document["imports"] is not null)
        ) {
            var rootBytes = Encoding.UTF8.GetBytes(s: document.ToJsonString());

            if (PuckDocumentComposer.TryComposeWorldDocument(
                catalog: machines,
                catalogFingerprint: catalogFingerprint,
                chainBytes: out _,
                composed: out var composed,
                reason: out var reason,
                rootBytes: rootBytes,
                rootResolvedPath: sourcePath
            )) {
                catalogSource = (composed ?? document);
            } else {
                var span = (((sourceMap is not null) && sourceMap.TryGetSpan(
                    jsonPointer: "/basis",
                    span: out var basisSpan
                ))
                    ? basisSpan
                    : SourceSpan.None
                );

                diagnostics.ReportError(
                    code: PuckDiagnosticCodes.CompositionRefused,
                    message: $"Basis/import composition refused: {reason}",
                    span: span
                );
                return;
            }
        }

        var references = new ReferenceCatalog(
            State: CollectRowNames(stateSection: (catalogSource["state"] as JsonObject)),
            Prototypes: CollectFieldValues(
                array: (catalogSource["prototypes"] as JsonArray),
                field: "id"
            ),
            Placements: CollectFieldValues(
                array: ((catalogSource["placements"] as JsonObject)?["rows"] as JsonArray),
                field: "id"
            ),
            Cameras: CollectFieldValues(
                array: (catalogSource["cameras"] as JsonArray),
                field: "name"
            ),
            SpawnPoints: CollectFieldValues(
                array: (catalogSource["spawnPoints"] as JsonArray),
                field: "id"
            )
        );

        var registered = new HashSet<(JsonObject Holder, string Member)>();

        WorldModuleNamespace.Visit(
            node: document,
            type: typeof(WorldDefinition),
            visitor: (holder, member, value, field, memberType) => {
                _ = registered.Add(item: (holder, member));
                CheckRegisteredSite(
                    catalog: references,
                    diagnostics: diagnostics,
                    field: field,
                    holder: holder,
                    member: member,
                    resolveGlobalReferences: isRoot,
                    sourceMap: sourceMap,
                    value: WorldChannelNodes.Spelled(
                        memberType: memberType,
                        value: value
                    )
                );
            }
        );
        WalkForReferences(
            document,
            string.Empty,
            references,
            sourceMap,
            diagnostics,
            registered,
            resolveGlobalReferences: isRoot
        );
    }

    private readonly record struct ReferenceCatalog(
        HashSet<string> State,
        HashSet<string> Prototypes,
        HashSet<string> Placements,
        HashSet<string> Cameras,
        HashSet<string> SpawnPoints
    );

    private static HashSet<string> CollectRowNames(JsonObject? stateSection) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (stateSection is null) {
            return names;
        }
        foreach (var (_, section) in stateSection) {
            if (section is not JsonArray rows) {
                continue;
            }
            foreach (var row in rows) {
                if (
                    (row is JsonObject rowObj) &&
                    TryGetString(
                    field: "name",
                    obj: rowObj,
                    value: out var name
                )
                ) {
                    names.Add(item: name);
                }
            }
        }
        return names;
    }
    private static HashSet<string> CollectFieldValues(JsonArray? array, string field) {
        var values = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (array is null) {
            return values;
        }
        foreach (var element in array) {
            if (
                (element is JsonObject obj) &&
                TryGetString(
                field: field,
                obj: obj,
                value: out var value
            )
            ) {
                values.Add(item: value);
            }
        }
        return values;
    }
    private static bool TryGetString(JsonObject obj, string field, out string value) {
        if (
            (obj[field] is JsonValue jsonValue) &&
            jsonValue.TryGetValue(value: out string? text) &&
            (text is not null)
        ) {
            value = text;
            return true;
        }
        value = string.Empty;
        return false;
    }
    private static void WalkForReferences(JsonNode? node, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, HashSet<(JsonObject Holder, string Member)> registered, bool resolveGlobalReferences) {
        switch (node) {
            case JsonObject obj:
                CheckObjectReferences(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    obj: obj,
                    pointer: pointer,
                    registered: registered,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap
                );
                foreach (var (key, value) in obj) {
                    WalkForReferences(
                        catalog: catalog,
                        diagnostics: diagnostics,
                        node: value,
                        pointer: $"{pointer}/{key}",
                        registered: registered,
                        resolveGlobalReferences: resolveGlobalReferences,
                        sourceMap: sourceMap
                    );
                }
                break;

            case JsonArray arr:
                // A `shapes` array (a creation document's own row collection) scopes its own `parent` resolution to
                // sibling shape names in the SAME array — never a global name, and never gated by module/root mode.
                if (pointer.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "/shapes"
                )) {
                    CheckShapeParents(
                        diagnostics: diagnostics,
                        pointer: pointer,
                        shapes: arr,
                        sourceMap: sourceMap
                    );
                }
                for (var index = 0; (index < arr.Count); index++) {
                    WalkForReferences(
                        arr[index],
                        $"{pointer}/{index}",
                        catalog,
                        sourceMap,
                        diagnostics,
                        registered,
                        resolveGlobalReferences
                    );
                }
                break;
        }
    }
    private static void CheckShapeParents(JsonArray shapes, string pointer, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        var siblingNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var element in shapes) {
            if (
                (element is JsonObject shape) &&
                TryGetString(
                field: "name",
                obj: shape,
                value: out var name
            )
            ) {
                siblingNames.Add(item: name);
            }
        }
        for (var index = 0; (index < shapes.Count); index++) {
            if (
                (shapes[index] is JsonObject shapeObj) &&
                TryGetString(
                field: "parent",
                obj: shapeObj,
                value: out var parentName
            ) &&
                !siblingNames.Contains(item: parentName)
            ) {
                Report(
                    code: PuckDiagnosticCodes.UnresolvedParent,
                    diagnostics: diagnostics,
                    message: $"Shape parent '{parentName}' does not resolve to a sibling shape name in this collection.",
                    pointer: $"{pointer}/{index}/parent",
                    severity: DiagnosticSeverity.Warning,
                    sourceMap: sourceMap
                );
            }
        }
    }
    private static void CheckObjectReferences(JsonObject obj, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, HashSet<(JsonObject Holder, string Member)> registered, bool resolveGlobalReferences) {
        // A placement row (identified by its own required prototypeId) — parent names a sibling placement id.
        if (
            resolveGlobalReferences &&
            TryGetString(
            field: "prototypeId",
            obj: obj,
            value: out var prototypeId
        )
        ) {
            if (!catalog.Prototypes.Contains(item: prototypeId)) {
                Report(
                    code: PuckDiagnosticCodes.LintUnresolvedPrototype,
                    diagnostics: diagnostics,
                    message: $"Unresolved prototypeId '{prototypeId}'.",
                    pointer: $"{pointer}/prototypeId",
                    severity: DiagnosticSeverity.Information,
                    sourceMap: sourceMap
                );
            }
            if (
                TryGetString(
                field: "parent",
                obj: obj,
                value: out var placementParent
            ) &&
                !catalog.Placements.Contains(item: placementParent)
            ) {
                Report(
                    code: PuckDiagnosticCodes.LintUnresolvedPlacementParent,
                    diagnostics: diagnostics,
                    message: $"Unresolved placement parent '{placementParent}'.",
                    pointer: $"{pointer}/parent",
                    severity: DiagnosticSeverity.Information,
                    sourceMap: sourceMap
                );
            }
        }

        CheckStateField(
            catalog: catalog,
            diagnostics: diagnostics,
            field: "state",
            obj: obj,
            pointer: pointer,
            registered: registered,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );
        CheckStateField(
            catalog: catalog,
            diagnostics: diagnostics,
            field: "comparandState",
            obj: obj,
            pointer: pointer,
            registered: registered,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );
        CheckStateField(
            catalog: catalog,
            diagnostics: diagnostics,
            field: "fromState",
            obj: obj,
            pointer: pointer,
            registered: registered,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );

        if (resolveGlobalReferences) {
            if (
                TryGetString(
                field: "camera",
                obj: obj,
                value: out var cameraName
            ) &&
                (cameraName.Length > 0) &&
                !catalog.Cameras.Contains(item: cameraName)
            ) {
                Report(
                    code: PuckDiagnosticCodes.LintUnresolvedView,
                    diagnostics: diagnostics,
                    message: $"Unresolved camera reference '{cameraName}'.",
                    pointer: $"{pointer}/camera",
                    severity: DiagnosticSeverity.Information,
                    sourceMap: sourceMap
                );
            }
            if (
                TryGetString(
                field: "spawnPoint",
                obj: obj,
                value: out var spawnPointId
            ) &&
                !catalog.SpawnPoints.Contains(item: spawnPointId)
            ) {
                Report(
                    code: PuckDiagnosticCodes.LintUnresolvedView,
                    diagnostics: diagnostics,
                    message: $"Unresolved spawn point reference '{spawnPointId}'.",
                    pointer: $"{pointer}/spawnPoint",
                    severity: DiagnosticSeverity.Information,
                    sourceMap: sourceMap
                );
            }
        }
    }
    // A field the registry walk already read is not read twice; what is left names a per-body slot.
    private static void CheckStateField(JsonObject obj, string field, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, HashSet<(JsonObject Holder, string Member)> registered, bool resolveGlobalReferences) {
        if (
            !resolveGlobalReferences ||
            registered.Contains(item: (obj, field)) ||
            !TryGetString(
            field: field,
            obj: obj,
            value: out var name
        ) ||
            IsSkippableName(name: name) ||
            catalog.State.Contains(item: name)
        ) {
            return;
        }
        Report(
            code: PuckDiagnosticCodes.LintUnresolvedState,
            diagnostics: diagnostics,
            message: $"Unresolved state row '{name}'.",
            pointer: $"{pointer}/{field}",
            severity: DiagnosticSeverity.Information,
            sourceMap: sourceMap
        );
    }
    // One registered site, read by its role. Only a state row's namespace is resolved here: a zone is a state row,
    // and every other kind is refused by the engine's own validation with a better message than a lint could give.
    private static void CheckRegisteredSite(JsonObject holder, string member, JsonNode value, WorldNameField field, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (
            (field.Role == WorldNameRole.Declares) ||
            (field.Kind is not (WorldNameKind.State or WorldNameKind.Zone))
        ) {
            return;
        }

        // A region interaction's right side is the placement carrying the region, never a row.
        if (
            (field.Owner == typeof(WorldInteraction)) &&
            string.Equals(
                a: field.Member,
                b: nameof(WorldInteraction.Right),
                comparisonType: StringComparison.Ordinal
            ) &&
            TryGetString(
                field: "coOccurrence",
                obj: holder,
                value: out var coOccurrence
            ) &&
            string.Equals(
                a: coOccurrence,
                b: nameof(WorldInteractionCoOccurrence.Region),
                comparisonType: StringComparison.Ordinal
            )
        ) {
            if (
                resolveGlobalReferences &&
                TryGetString(
                    field: member,
                    obj: holder,
                    value: out var placementId
                ) &&
                !catalog.Placements.Contains(item: placementId)
            ) {
                Report(
                    code: PuckDiagnosticCodes.LintUnresolvedPlacementParent,
                    diagnostics: diagnostics,
                    message: $"Unresolved region placement '{placementId}'.",
                    pointer: $"{PointerOf(node: holder)}/{member}",
                    severity: DiagnosticSeverity.Information,
                    sourceMap: sourceMap
                );
            }

            return;
        }
        if (value is JsonArray list) {
            if (field.Role != WorldNameRole.Names) {
                return;
            }
            for (var index = 0; (index < list.Count); index++) {
                if (
                    (list[index] is JsonValue element) &&
                    element.TryGetValue(value: out string? item) &&
                    (item is not null)
                ) {
                    CheckRowName(
                        catalog: catalog,
                        diagnostics: diagnostics,
                        name: item,
                        pointer: () => $"{PointerOf(node: holder)}/{member}/{index}",
                        resolveGlobalReferences: resolveGlobalReferences,
                        sourceMap: sourceMap
                    );
                }
            }

            return;
        }
        if (
            (value is not JsonValue leaf) ||
            !leaf.TryGetValue(value: out string? text) ||
            (text is null)
        ) {
            // An expression held as IR is walked instruction by instruction, each one a site of its own.
            return;
        }

        string Pointer() => $"{PointerOf(node: holder)}/{member}";

        switch (field.Role) {
            case WorldNameRole.Names:
                CheckRowName(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    name: text,
                    pointer: Pointer,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap
                );
                break;
            case WorldNameRole.Expression:
                CheckExpressionText(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    pointer: Pointer,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap,
                    text: text
                );
                break;
            case WorldNameRole.Key when text.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.ExpressionKeyPrefix
            ):
                CheckExpressionText(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    pointer: Pointer,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap,
                    text: text[RuleFacts.ExpressionKeyPrefix.Length..]
                );
                break;
            case WorldNameRole.Binding:
                CheckBinding(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    pointer: Pointer,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap,
                    token: text
                );
                break;
            case WorldNameRole.Template:
                for (var index = 0; (index < text.Length); index++) {
                    if (text[index] != '{') {
                        continue;
                    }
                    if (((index + 1) < text.Length) && (text[(index + 1)] == '{')) {
                        index++;
                        continue;
                    }

                    var close = text.IndexOf(
                        startIndex: index,
                        value: '}'
                    );

                    if (close < 0) {
                        break;
                    }
                    CheckBinding(
                        catalog: catalog,
                        diagnostics: diagnostics,
                        pointer: Pointer,
                        resolveGlobalReferences: resolveGlobalReferences,
                        sourceMap: sourceMap,
                        token: text[(index + 1)..close]
                    );
                    index = close;
                }
                break;
        }
    }
    // A `state.<row>[.<key>]` token; anything else a binding admits (a literal, a host channel) names no row.
    private static void CheckBinding(string token, Func<string> pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (!token.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: BindingPrefix
        )) {
            return;
        }

        var rest = token[BindingPrefix.Length..];
        var dot = rest.IndexOf(value: '.');

        CheckRowName(
            catalog: catalog,
            diagnostics: diagnostics,
            name: ((dot < 0)
                ? rest
                : rest[..dot]
            ),
            pointer: pointer,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );
    }
    // Infix text is parsed by the grammar that will run it, so a function or a constant is never mistaken for a
    // row; text the grammar refuses is the engine's to report.
    private static void CheckExpressionText(string text, Func<string> pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (ExpressionSpelling.TryParseVector(
            error: out _,
            text: text,
            token: out var operand
        )) {
            if (operand is VectorOperand.Cell cell) {
                CheckRowName(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    name: cell.Name.Spelling,
                    pointer: pointer,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap
                );
            }

            return;
        }
        if (!ExpressionSpelling.TryParse(
            error: out _,
            program: out var program,
            text: text
        )) {
            return;
        }
        foreach (var instructions in program.Subprograms.Select(selector: static subprogram => subprogram.Instructions).Prepend(element: program.Instructions)) {
            foreach (var instruction in instructions) {
                if ((instruction.Payload switch {
                    InstructionPayload.State state => state.Name.Spelling,
                    InstructionPayload.Fold fold => fold.Family,
                    _ => null,
                }) is { } name) {
                    CheckRowName(
                        catalog: catalog,
                        diagnostics: diagnostics,
                        name: name,
                        pointer: pointer,
                        resolveGlobalReferences: resolveGlobalReferences,
                        sourceMap: sourceMap
                    );
                }
            }
        }
    }
    private static void CheckRowName(string name, Func<string> pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (IsSkippableName(name: name)) {
            // The channel-prefix typo check is purely local (a fixed known-prefix list, never the document's own
            // catalog), so it runs whether or not this document declares a basis.
            CheckChannelPrefixTypo(
                name,
                pointer,
                sourceMap,
                diagnostics
            );

            return;
        }
        if (
            resolveGlobalReferences &&
            !catalog.State.Contains(item: name)
        ) {
            Report(
                code: PuckDiagnosticCodes.LintUnresolvedState,
                diagnostics: diagnostics,
                message: $"Unresolved state row '{name}'.",
                pointer: pointer(),
                severity: DiagnosticSeverity.Information,
                sourceMap: sourceMap
            );
        }
    }
    // The JSON pointer of a node in the tree being linted, from the path System.Text.Json renders for it.
    private static string PointerOf(JsonNode node) {
        var path = node.GetPath();
        var pointer = new StringBuilder(capacity: path.Length);
        var index = 1;

        while (index < path.Length) {
            if (path[index] == '.') {
                var end = path.IndexOfAny(
                    anyOf: ['.', '['],
                    startIndex: (index + 1)
                );

                end = ((end < 0)
                    ? path.Length
                    : end
                );
                _ = pointer.Append(value: '/').Append(
                    count: (end - index - 1),
                    startIndex: (index + 1),
                    value: path
                );
                index = end;

                continue;
            }

            var close = path.IndexOf(
                startIndex: index,
                value: ']'
            );
            var inner = path[(index + 1)..close];

            if (inner.StartsWith(value: '\'')) {
                close = path.IndexOf(
                    comparisonType: StringComparison.Ordinal,
                    startIndex: index,
                    value: "']"
                );
                inner = path[(index + 2)..close];
                close++;
            }
            _ = pointer.Append(value: '/').Append(value: inner);
            index = (close + 1);
        }

        return pointer.ToString();
    }
    // Reserved ($-prefixed) names are outside this pass's declared-row universe. A dotted "row.key" read already
    // resolves to its undotted row name by the time it reaches here — ExpressionSpelling splits it at parse time —
    // so it is checked like any other read, never skipped.
    private static bool IsSkippableName(string name) => ((name.Length == 0) || (name[0] == '$'));
    private static void CheckChannelPrefixTypo(string name, Func<string> atPointer, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        if (
            (name.Length == 0) ||
            (name[0] != '$')
        ) {
            return;
        }
        var colon = name.IndexOf(value: ':');
        var prefix = ((colon >= 0)
            ? name[..colon]
            : name
        );

        if (Array.IndexOf(
            array: KnownChannelPrefixes,
            value: prefix
        ) >= 0) {
            return;
        }
        foreach (var known in KnownChannelPrefixes) {
            if (IsOneEditApart(
                a: prefix,
                b: known
            )) {
                Report(
                    code: PuckDiagnosticCodes.LintUnknownChannelPrefix,
                    diagnostics: diagnostics,
                    message: $"'{prefix}' does not match any known reserved-channel prefix — did you mean '{known}'?",
                    pointer: atPointer(),
                    severity: DiagnosticSeverity.Information,
                    sourceMap: sourceMap
                );
                return;
            }
        }
    }
    // Whether `a` and `b` differ by at most one character insertion, deletion, or substitution — deliberately
    // narrow (never the full corpus's own extension prefixes, e.g. $physics/$board/$upright, which sit far from
    // every RuleFacts prefix) so this stays a typo check, not a guess at what an extension might register.
    private static bool IsOneEditApart(string a, string b) {
        if (a == b) {
            return true;
        }
        if (Math.Abs(value: (a.Length - b.Length)) > 1) {
            return false;
        }
        if (a.Length < b.Length) {
            (a, b) = (b, a);
        }
        var sameLength = (a.Length == b.Length);
        var i = 0;
        var j = 0;
        var usedEdit = false;

        while (
            (i < a.Length) &&
            (j < b.Length)
        ) {
            if (a[i] == b[j]) {
                i++;
                j++;
                continue;
            }
            if (usedEdit) {
                return false;
            }
            usedEdit = true;
            i++;
            if (sameLength) {
                j++;
            }
        }
        return true;
    }
    // The map registers the nodes the emitter lowered, which are rarely the leaf field a finding names, so the
    // pointer is walked back up to the nearest enclosing node that does carry a span.
    private static SourceSpan SpanOf(SourceMap? sourceMap, string pointer) {
        if (sourceMap is null) {
            return SourceSpan.None;
        }

        var candidate = pointer;

        while (candidate.Length > 1) {
            if (sourceMap.TryGetSpan(
                jsonPointer: candidate,
                span: out var span
            )) {
                return span;
            }

            var lastSegment = candidate.LastIndexOf(value: '/');

            if (lastSegment <= 0) {
                break;
            }
            candidate = candidate[..lastSegment];
        }

        return SourceSpan.None;
    }
    private static void Report(SourceMap? sourceMap, DiagnosticBag diagnostics, string pointer, string code, DiagnosticSeverity severity, string message) {
        var span = SpanOf(
            pointer: pointer,
            sourceMap: sourceMap
        );

        if (severity == DiagnosticSeverity.Warning) {
            diagnostics.ReportWarning(
                code: code,
                message: message,
                span: span
            );
        } else {
            diagnostics.ReportInformation(
                code: code,
                message: message,
                span: span
            );
        }
    }
}
