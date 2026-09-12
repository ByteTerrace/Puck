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
// (Warning). Zero false positives is the bar: a `$`-prefixed or dotted (import-alias) name is never checked against
// a declared-row set.
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
    // Read from RuleFacts' own constants, trimmed of their trailing ':'/'[' separator, so a channel added there is
    // known here without a second edit.
    private static readonly string[] KnownChannelPrefixes = [
        .. new[] {
            RuleFacts.MatchPrefix, RuleFacts.HistoryPrefix, RuleFacts.CellKeyPrefix, RuleFacts.ZoneKeyPrefix,
            RuleFacts.LiveZonePrefix, RuleFacts.ForEachZones, RuleFacts.ExpressionKeyPrefix, RuleFacts.BindPrefix,
            RuleFacts.TablePrefix, RuleFacts.ReducePrefix, RuleFacts.SymmetryPrefix, RuleFacts.Tick,
        }.Select(static prefix => prefix.TrimEnd(':', '[')).Distinct(StringComparer.Ordinal),
    ];

    /// <summary>Lints a canonical lowered <c>puck.world.def.v1</c> document for unresolved symbolic references.
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

        var isRoot = WorldSemanticValidator.IsRootDocument(document);
        var catalogSource = document;

        if (document["basis"] is not null || document["imports"] is not null) {
            var rootBytes = Encoding.UTF8.GetBytes(document.ToJsonString());

            if (PuckDocumentComposer.TryComposeWorldDocument(sourcePath, rootBytes, out var composed, out _, out var reason, catalogFingerprint, machines)) {
                catalogSource = composed ?? document;
            } else {
                var span = (sourceMap is not null && sourceMap.TryGetSpan("/basis", out var basisSpan)) ? basisSpan : SourceSpan.None;
                diagnostics.ReportError(PuckDiagnosticCodes.CompositionRefused, $"Basis/import composition refused: {reason}", span);
                return;
            }
        }

        var references = new ReferenceCatalog(
            State: CollectRowNames(catalogSource["state"] as JsonObject),
            Prototypes: CollectFieldValues(catalogSource["prototypes"] as JsonArray, "id"),
            Placements: CollectFieldValues((catalogSource["placements"] as JsonObject)?["rows"] as JsonArray, "id"),
            Cameras: CollectFieldValues(catalogSource["cameras"] as JsonArray, "name"),
            SpawnPoints: CollectFieldValues(catalogSource["spawnPoints"] as JsonArray, "id")
        );

        WalkForReferences(document, string.Empty, references, sourceMap, diagnostics, resolveGlobalReferences: isRoot);
    }

    private readonly record struct ReferenceCatalog(
        HashSet<string> State,
        HashSet<string> Prototypes,
        HashSet<string> Placements,
        HashSet<string> Cameras,
        HashSet<string> SpawnPoints
    );

    private static HashSet<string> CollectRowNames(JsonObject? stateSection) {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (stateSection is null) {
            return names;
        }
        foreach (var (_, section) in stateSection) {
            if (section is not JsonArray rows) {
                continue;
            }
            foreach (var row in rows) {
                if (row is JsonObject rowObj && TryGetString(rowObj, "name", out var name)) {
                    names.Add(name);
                }
            }
        }
        return names;
    }

    private static HashSet<string> CollectFieldValues(JsonArray? array, string field) {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (array is null) {
            return values;
        }
        foreach (var element in array) {
            if (element is JsonObject obj && TryGetString(obj, field, out var value)) {
                values.Add(value);
            }
        }
        return values;
    }

    private static bool TryGetString(JsonObject obj, string field, out string value) {
        if (obj[field] is JsonValue jsonValue && jsonValue.TryGetValue(out string? text) && text is not null) {
            value = text;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static void WalkForReferences(JsonNode? node, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        switch (node) {
            case JsonObject obj:
                CheckObjectReferences(obj, pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);
                foreach (var (key, value) in obj) {
                    WalkForReferences(value, $"{pointer}/{key}", catalog, sourceMap, diagnostics, resolveGlobalReferences);
                }
                break;

            case JsonArray arr:
                // A `shapes` array (a creation document's own row collection) scopes its own `parent` resolution to
                // sibling shape names in the SAME array — never a global name, and never gated by module/root mode.
                if (pointer.EndsWith("/shapes", StringComparison.Ordinal)) {
                    CheckShapeParents(arr, pointer, sourceMap, diagnostics);
                }
                for (var index = 0; index < arr.Count; index++) {
                    WalkForReferences(arr[index], $"{pointer}/{index}", catalog, sourceMap, diagnostics, resolveGlobalReferences);
                }
                break;
        }
    }

    private static void CheckShapeParents(JsonArray shapes, string pointer, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        var siblingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in shapes) {
            if (element is JsonObject shape && TryGetString(shape, "name", out var name)) {
                siblingNames.Add(name);
            }
        }
        for (var index = 0; index < shapes.Count; index++) {
            if (shapes[index] is JsonObject shapeObj
                && TryGetString(shapeObj, "parent", out var parentName)
                && !siblingNames.Contains(parentName)) {
                Report(sourceMap, diagnostics, $"{pointer}/{index}/parent", PuckDiagnosticCodes.UnresolvedParent, DiagnosticSeverity.Warning,
                    $"Shape parent '{parentName}' does not resolve to a sibling shape name in this collection.");
            }
        }
    }

    private static void CheckObjectReferences(JsonObject obj, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        // A placement row (identified by its own required prototypeId) — parent names a sibling placement id.
        if (resolveGlobalReferences && TryGetString(obj, "prototypeId", out var prototypeId)) {
            if (!catalog.Prototypes.Contains(prototypeId)) {
                Report(sourceMap, diagnostics, $"{pointer}/prototypeId", PuckDiagnosticCodes.LintUnresolvedPrototype, DiagnosticSeverity.Information,
                    $"Unresolved prototypeId '{prototypeId}'.");
            }
            if (TryGetString(obj, "parent", out var placementParent) && !catalog.Placements.Contains(placementParent)) {
                Report(sourceMap, diagnostics, $"{pointer}/parent", PuckDiagnosticCodes.LintUnresolvedPlacementParent, DiagnosticSeverity.Information,
                    $"Unresolved placement parent '{placementParent}'.");
            }
        }

        CheckStateField(obj, "state", pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);
        CheckStateField(obj, "comparandState", pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);
        CheckStateField(obj, "fromState", pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);

        // ValueExpression-typed fields carry their author's verbatim infix text — every State token inside gets the
        // same check. "left"/"right" are ValueExpression text ONLY on a compareValue predicate (WorldNameRegistry's
        // one Expression-role entry for that pair) — every other left/right pair in the document model (e.g. an
        // interaction row's property/placement-id pair) is a different name kind entirely and must not be walked
        // as an expression.
        if (TryGetString(obj, "$type", out var discriminator) && (discriminator == "compareValue")) {
            CheckOperandField(obj, "left", pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);
            CheckOperandField(obj, "right", pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);
        }
        CheckOperandField(obj, "expression", pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);
        CheckOperandField(obj, "score", pointer, catalog, sourceMap, diagnostics, resolveGlobalReferences);

        if (resolveGlobalReferences) {
            if (TryGetString(obj, "camera", out var cameraName) && cameraName.Length > 0 && !catalog.Cameras.Contains(cameraName)) {
                Report(sourceMap, diagnostics, $"{pointer}/camera", PuckDiagnosticCodes.LintUnresolvedView, DiagnosticSeverity.Information,
                    $"Unresolved camera reference '{cameraName}'.");
            }
            if (TryGetString(obj, "spawnPoint", out var spawnPointId) && !catalog.SpawnPoints.Contains(spawnPointId)) {
                Report(sourceMap, diagnostics, $"{pointer}/spawnPoint", PuckDiagnosticCodes.LintUnresolvedView, DiagnosticSeverity.Information,
                    $"Unresolved spawn point reference '{spawnPointId}'.");
            }
        }
    }

    private static void CheckStateField(JsonObject obj, string field, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (!resolveGlobalReferences || !TryGetString(obj, field, out var name) || IsSkippableName(name) || catalog.State.Contains(name)) {
            return;
        }
        Report(sourceMap, diagnostics, $"{pointer}/{field}", PuckDiagnosticCodes.LintUnresolvedState, DiagnosticSeverity.Information,
            $"Unresolved state row '{name}'.");
    }

    private static void CheckOperandField(JsonObject obj, string field, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics, bool resolveGlobalReferences) {
        if (!TryGetString(obj, field, out var text) || !ExpressionSpelling.TryParse(text, out var tokens, out _)) {
            return;
        }
        var fieldPointer = $"{pointer}/{field}";
        foreach (var token in tokens) {
            if (token is not ValueToken.State state) {
                continue;
            }
            if (IsSkippableName(state.Name)) {
                // The channel-prefix typo check is purely local (a fixed known-prefix list, never the document's
                // own catalog), so it runs whether or not this document declares a basis.
                CheckChannelPrefixTypo(state.Name, fieldPointer, sourceMap, diagnostics);
                continue;
            }
            if (resolveGlobalReferences && !catalog.State.Contains(state.Name)) {
                Report(sourceMap, diagnostics, fieldPointer, PuckDiagnosticCodes.LintUnresolvedState, DiagnosticSeverity.Information,
                    $"Unresolved state row '{state.Name}'.");
            }
        }
    }

    // Reserved ($-prefixed) and dotted (import-alias) names are outside this pass's declared-row universe.
    private static bool IsSkippableName(string name) => (name.Length == 0 || name[0] == '$' || name.Contains('.'));

    private static void CheckChannelPrefixTypo(string name, string atPointer, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        if (name.Length == 0 || name[0] != '$') {
            return;
        }
        var colon = name.IndexOf(':');
        var prefix = (colon >= 0) ? name[..colon] : name;
        if (Array.IndexOf(KnownChannelPrefixes, prefix) >= 0) {
            return;
        }
        foreach (var known in KnownChannelPrefixes) {
            if (IsOneEditApart(prefix, known)) {
                Report(sourceMap, diagnostics, atPointer, PuckDiagnosticCodes.LintUnknownChannelPrefix, DiagnosticSeverity.Information,
                    $"'{prefix}' does not match any known reserved-channel prefix — did you mean '{known}'?");
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
        if (Math.Abs(a.Length - b.Length) > 1) {
            return false;
        }
        if (a.Length < b.Length) {
            (a, b) = (b, a);
        }
        var sameLength = (a.Length == b.Length);
        var i = 0;
        var j = 0;
        var usedEdit = false;
        while (i < a.Length && j < b.Length) {
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

    private static void Report(SourceMap? sourceMap, DiagnosticBag diagnostics, string pointer, string code, DiagnosticSeverity severity, string message) {
        var span = SourceSpan.None;
        sourceMap?.TryGetSpan(pointer, out span);
        if (severity == DiagnosticSeverity.Warning) {
            diagnostics.ReportWarning(code, message, span);
        } else {
            diagnostics.ReportInformation(code, message, span);
        }
    }
}
