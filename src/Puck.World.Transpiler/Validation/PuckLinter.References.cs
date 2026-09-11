using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Validation;

// Symbol resolution over the LOWERED JSON, not the AST: it sees exactly what ships, template-expanded and
// basis-independent, and needs no re-implementation of the emitter's own resolution. Four reference families —
// state row, prototypeId, placement parent, camera/spawn-point — plus a best-effort `$`-prefix typo check, all
// Information severity except the shape-parent check (Warning). Zero false positives is the bar: a `$`-prefixed or
// dotted (import-alias) name is never checked against a declared-row set, and a document declaring `basis` skips
// every family outright, since its own rows may live in a basis chain this pass cannot see.
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

    /// <summary>Lints a canonical lowered <c>puck.world.def.v1</c> document for unresolved symbolic references.</summary>
    /// <param name="document">The lowered world document.</param>
    /// <param name="sourceMap">The JSON-pointer-to-span map the same lowering pass populated, for diagnostic spans.</param>
    /// <param name="diagnostics">The bag to record findings into.</param>
    public static void LintReferences(JsonObject document, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (document["basis"] is not null) {
            diagnostics.ReportInformation(
                PuckDiagnosticCodes.LintUnresolvedState,
                "Symbol resolution skipped: this document declares a 'basis' — rows may be inherited from the basis chain, which this pass cannot see.",
                SourceSpan.None
            );
            return;
        }

        var references = new ReferenceCatalog(
            State: CollectRowNames(document["state"] as JsonObject),
            Prototypes: CollectFieldValues(document["prototypes"] as JsonArray, "id"),
            Placements: CollectFieldValues((document["placements"] as JsonObject)?["rows"] as JsonArray, "id"),
            Cameras: CollectFieldValues(document["cameras"] as JsonArray, "name"),
            SpawnPoints: CollectFieldValues(document["spawnPoints"] as JsonArray, "id")
        );

        WalkForReferences(document, string.Empty, references, sourceMap, diagnostics);
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

    private static void WalkForReferences(JsonNode? node, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        switch (node) {
            case JsonObject obj:
                CheckObjectReferences(obj, pointer, catalog, sourceMap, diagnostics);
                foreach (var (key, value) in obj) {
                    WalkForReferences(value, $"{pointer}/{key}", catalog, sourceMap, diagnostics);
                }
                break;

            case JsonArray arr:
                // A `shapes` array (a creation document's own row collection) scopes its own `parent` resolution to
                // sibling shape names in the SAME array — never a global name.
                if (pointer.EndsWith("/shapes", StringComparison.Ordinal)) {
                    CheckShapeParents(arr, pointer, sourceMap, diagnostics);
                }
                for (var index = 0; index < arr.Count; index++) {
                    WalkForReferences(arr[index], $"{pointer}/{index}", catalog, sourceMap, diagnostics);
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

    private static void CheckObjectReferences(JsonObject obj, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        // A placement row (identified by its own required prototypeId) — parent names a sibling placement id.
        if (TryGetString(obj, "prototypeId", out var prototypeId)) {
            if (!catalog.Prototypes.Contains(prototypeId)) {
                Report(sourceMap, diagnostics, $"{pointer}/prototypeId", PuckDiagnosticCodes.LintUnresolvedPrototype, DiagnosticSeverity.Information,
                    $"Unresolved prototypeId '{prototypeId}'.");
            }
            if (TryGetString(obj, "parent", out var placementParent) && !catalog.Placements.Contains(placementParent)) {
                Report(sourceMap, diagnostics, $"{pointer}/parent", PuckDiagnosticCodes.LintUnresolvedPlacementParent, DiagnosticSeverity.Information,
                    $"Unresolved placement parent '{placementParent}'.");
            }
        }

        CheckStateField(obj, "state", pointer, catalog, sourceMap, diagnostics);
        CheckStateField(obj, "comparandState", pointer, catalog, sourceMap, diagnostics);
        CheckStateField(obj, "fromState", pointer, catalog, sourceMap, diagnostics);

        // ValueExpression-typed fields carry their author's verbatim infix text — every State token inside gets the same check.
        CheckOperandField(obj, "left", pointer, catalog, sourceMap, diagnostics);
        CheckOperandField(obj, "right", pointer, catalog, sourceMap, diagnostics);
        CheckOperandField(obj, "expression", pointer, catalog, sourceMap, diagnostics);
        CheckOperandField(obj, "score", pointer, catalog, sourceMap, diagnostics);

        if (TryGetString(obj, "camera", out var cameraName) && cameraName.Length > 0 && !catalog.Cameras.Contains(cameraName)) {
            Report(sourceMap, diagnostics, $"{pointer}/camera", PuckDiagnosticCodes.LintUnresolvedView, DiagnosticSeverity.Information,
                $"Unresolved camera reference '{cameraName}'.");
        }
        if (TryGetString(obj, "spawnPoint", out var spawnPointId) && !catalog.SpawnPoints.Contains(spawnPointId)) {
            Report(sourceMap, diagnostics, $"{pointer}/spawnPoint", PuckDiagnosticCodes.LintUnresolvedView, DiagnosticSeverity.Information,
                $"Unresolved spawn point reference '{spawnPointId}'.");
        }
    }

    private static void CheckStateField(JsonObject obj, string field, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        if (!TryGetString(obj, field, out var name) || IsSkippableName(name) || catalog.State.Contains(name)) {
            return;
        }
        Report(sourceMap, diagnostics, $"{pointer}/{field}", PuckDiagnosticCodes.LintUnresolvedState, DiagnosticSeverity.Information,
            $"Unresolved state row '{name}'.");
    }

    private static void CheckOperandField(JsonObject obj, string field, string pointer, ReferenceCatalog catalog, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        if (!TryGetString(obj, field, out var text) || !ExpressionSpelling.TryParse(text, out var tokens, out _)) {
            return;
        }
        var fieldPointer = $"{pointer}/{field}";
        foreach (var token in tokens) {
            if (token is not ValueToken.State state) {
                continue;
            }
            if (IsSkippableName(state.Name)) {
                CheckChannelPrefixTypo(state.Name, fieldPointer, sourceMap, diagnostics);
                continue;
            }
            if (!catalog.State.Contains(state.Name)) {
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
