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
// (Warning). Zero false positives is the bar: a `$`-prefixed name is never checked against a declared-row set. A
// dotted "row.key" read is checked on its row half like any bracketed read — ExpressionSpelling already split it
// by the time a token reaches this pass. The state-row family reads a `StateTransform` arm's own row-naming fields
// too, per arm, since an effect's own node carries only the arm.
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
    // The pointer segment a `StateTransform` arm always sits under, which is what makes the arm table below safe to
    // key on a `$type` alone: no other union's arm is the value of a `transform` key.
    private const string TransformSegment = "/transform";

    // The state rows each `StateTransform` arm names, keyed by the arm's own discriminator. A field whose value is
    // a cell key, a slot or cell spelling, a literal, or a row of another vocabulary is absent: resolving one
    // against the state catalog would report a name that is not a row name.
    private static readonly Dictionary<string, string[]> TransformRowFields = new(comparer: StringComparer.Ordinal) {
        ["arrange"] = ["row", "from"],
        ["boardCombine"] = ["row", "left", "right"],
        ["clearEnclosed"] = ["row"],
        ["mean"] = ["from", "where"],
        ["nearest"] = ["from", "where"],
        ["observe"] = ["row"],
        ["push"] = ["row"],
        ["remember"] = ["into"],
        ["setRay"] = ["row"],
        ["shuffle"] = ["row", "draw"],
        ["sortKeyed"] = ["row"],
        ["sortZone"] = ["row"],
        ["transfer"] = ["from", "to", "draw"],
        ["writeSet"] = ["row", "set"],
    };
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

        WalkForReferences(
            document,
            string.Empty,
            references,
            sourceMap,
            diagnostics,
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
    private static void WalkForReferences(JsonNode? node, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        switch (node) {
            case JsonObject obj:
                CheckObjectReferences(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    obj: obj,
                    pointer: pointer,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap
                );
                foreach (var (key, value) in obj) {
                    WalkForReferences(
                        catalog: catalog,
                        diagnostics: diagnostics,
                        node: value,
                        pointer: $"{pointer}/{key}",
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
    private static void CheckObjectReferences(JsonObject obj, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
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
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );
        CheckStateField(
            catalog: catalog,
            diagnostics: diagnostics,
            field: "comparandState",
            obj: obj,
            pointer: pointer,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );
        CheckStateField(
            catalog: catalog,
            diagnostics: diagnostics,
            field: "fromState",
            obj: obj,
            pointer: pointer,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );

        // ExpressionProgram-typed fields carry the IR — every state read inside gets the same check.
        // "left"/"right" are programs ONLY on a compareValue predicate (WorldNameRegistry's
        // one Expression-role entry for that pair) — every other left/right pair in the document model (e.g. an
        // interaction row's property/placement-id pair) is a different name kind entirely and must not be walked
        // as an expression.
        var isArm = TryGetString(
            field: "$type",
            obj: obj,
            value: out var discriminator
        );

        if (isArm && (discriminator == "compareValue")) {
            CheckOperandField(
                catalog: catalog,
                diagnostics: diagnostics,
                field: "left",
                obj: obj,
                pointer: pointer,
                resolveGlobalReferences: resolveGlobalReferences,
                sourceMap: sourceMap
            );
            CheckOperandField(
                catalog: catalog,
                diagnostics: diagnostics,
                field: "right",
                obj: obj,
                pointer: pointer,
                resolveGlobalReferences: resolveGlobalReferences,
                sourceMap: sourceMap
            );
        }
        if (
            isArm &&
            pointer.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: TransformSegment
        ) &&
            TransformRowFields.TryGetValue(
            key: discriminator,
            value: out var armFields
        )
        ) {
            foreach (var field in armFields) {
                CheckStateField(
                    catalog: catalog,
                    diagnostics: diagnostics,
                    field: field,
                    obj: obj,
                    pointer: pointer,
                    resolveGlobalReferences: resolveGlobalReferences,
                    sourceMap: sourceMap
                );
            }
        }
        CheckOperandField(
            catalog: catalog,
            diagnostics: diagnostics,
            field: "expression",
            obj: obj,
            pointer: pointer,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );
        CheckOperandField(
            catalog: catalog,
            diagnostics: diagnostics,
            field: "score",
            obj: obj,
            pointer: pointer,
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
    private static void CheckStateField(JsonObject obj, string field, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (
            !resolveGlobalReferences ||
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
    private static void CheckOperandField(JsonObject obj, string field, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (obj[field] is not JsonObject program) {
            return;
        }

        ExpressionProgram parsed;

        try {
            parsed = ExpressionProgramJsonConverter.FromNode(node: program);
        } catch (System.Text.Json.JsonException) {
            return;
        }
        var fieldPointer = $"{pointer}/{field}";

        CheckInstructions(
            catalog: catalog,
            diagnostics: diagnostics,
            fieldPointer: fieldPointer,
            instructions: parsed.Instructions,
            resolveGlobalReferences: resolveGlobalReferences,
            sourceMap: sourceMap
        );
        // A fold body and a shared function body read rows of their own, so the subprogram table is checked on the
        // same terms as the top-level instruction list.
        foreach (var subprogram in parsed.Subprograms) {
            CheckInstructions(
                catalog: catalog,
                diagnostics: diagnostics,
                fieldPointer: fieldPointer,
                instructions: subprogram.Instructions,
                resolveGlobalReferences: resolveGlobalReferences,
                sourceMap: sourceMap
            );
        }
    }
    private static void CheckInstructions(IReadOnlyList<Instruction> instructions, string fieldPointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        foreach (var token in instructions) {
            var name = (token.Payload switch {
                InstructionPayload.State state => state.Name,
                InstructionPayload.Fold fold => fold.Family,
                _ => null,
            });

            if (name is null) {
                continue;
            }
            if (IsSkippableName(name: name)) {
                // The channel-prefix typo check is purely local (a fixed known-prefix list, never the document's
                // own catalog), so it runs whether or not this document declares a basis.
                CheckChannelPrefixTypo(
                    name,
                    fieldPointer,
                    sourceMap,
                    diagnostics
                );
                continue;
            }
            if (
                resolveGlobalReferences &&
                !catalog.State.Contains(item: name)
            ) {
                Report(
                    sourceMap,
                    diagnostics,
                    fieldPointer,
                    PuckDiagnosticCodes.LintUnresolvedState,
                    DiagnosticSeverity.Information,
                    $"Unresolved state row '{name}'."
                );
            }
        }
    }
    // Reserved ($-prefixed) names are outside this pass's declared-row universe. A dotted "row.key" read already
    // resolves to its undotted row name by the time it reaches here — ExpressionSpelling splits it at parse time —
    // so it is checked like any other read, never skipped.
    private static bool IsSkippableName(string name) => ((name.Length == 0) || (name[0] == '$'));
    private static void CheckChannelPrefixTypo(string name, string atPointer, SourceMap? sourceMap, DiagnosticBag diagnostics) {
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
                    pointer: atPointer,
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
