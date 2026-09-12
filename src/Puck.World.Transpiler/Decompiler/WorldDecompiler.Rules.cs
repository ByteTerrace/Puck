using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

// `rule "name" { }` inverse (§1-§3): gate/bind/decision/option/interrupt/onNoChoice, and every effect statement.
// Every `ValueExpression`-typed field (`compareValue.left`/`right`, `setState`/`addState`/`push.expression`,
// `bindings[].expression`, `options[].score`) is printed VERBATIM — never reprinted through `ExpressionSpelling`,
// since `ValueExpressionJsonConverter.Write` stored the author's own source text on the way in. Anything the
// dedicated sugar below does not cover (every `Puck.World.Schema` extension arm, every `StateTransform` sub-arm)
// falls through to `FormatCallForm` — the same escape hatch `FormatValue` uses everywhere else in the document.
public static partial class WorldDecompiler {
    // Whether every row in a `rules` array is a rule the `rule "name" { }` grammar can carry. A row with no `name`
    // is a WorldDocumentBasis merge directive, not a rule: it has no name to quote and no effects to author, and
    // routing it through the sugar emits a rule the compiler then refuses. When any row is one of those, the whole
    // section prints through the generic value path, which carries every row unchanged.
    private static bool CanSugarRules(JsonArray rules) {
        foreach (var item in rules) {
            if (item is not JsonObject rule || rule["name"] is not JsonValue nameVal || !nameVal.TryGetValue<string>(out _)) {
                return false;
            }
        }
        return true;
    }

    private static void DecompileRulesBlock(StringBuilder sb, JsonArray rules, int indentLevel) {
        var first = true;
        foreach (var item in rules) {
            if (item is not JsonObject rule) {
                continue;
            }
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            AppendRuleBlock(sb, rule, indentLevel);
        }
    }

    private static readonly HashSet<string> s_ruleKnownKeys = new(StringComparer.Ordinal) {
        "name", "gate", "bindings", "mode", "forEach", "zones", "decision", "effects",
    };

    private static void AppendRuleBlock(StringBuilder sb, JsonObject rule, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = new string(' ', (indentLevel + 1) * 4);
        var name = rule["name"]?.ToString() ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}rule \"{EscapeString(name)}\" {{");

        if (rule["gate"] is JsonObject gate) {
            AppendGateProperty(sb, gate, indentLevel + 1);
        }
        if (rule["bindings"] is JsonArray bindings) {
            if (CanSugarBindings(bindings)) {
                foreach (var b in bindings) {
                    if (b is JsonObject bindObj) {
                        AppendBindStatement(sb, bindObj, indentLevel + 1);
                    }
                }
            } else {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}bindings: {FormatValue(bindings, indentLevel + 1)}");
            }
        }
        if (rule["mode"] is { } mode) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}mode: {FormatValue(mode, indentLevel + 1)}");
        }
        if (rule["forEach"] is { } forEach) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}forEach: {FormatValue(forEach, indentLevel + 1)}");
        }
        if (rule["zones"] is { } zones) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}zones: {FormatValue(zones, indentLevel + 1)}");
        }
        if (rule["decision"] is JsonObject decision) {
            AppendDecisionBlock(sb, decision, indentLevel + 1);
        }
        if (rule["effects"] is JsonArray effects) {
            foreach (var e in effects) {
                AppendEffectStatement(sb, e, indentLevel + 1);
            }
        }

        foreach (var (k, v) in rule) {
            if (s_ruleKnownKeys.Contains(k) || v is null) {
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}`{k}`: {FormatValue(v, indentLevel + 1)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    // `RuleBinding.Kind` is a required positional parameter with no default, so a row omitting it — or spelling it
    // as a kind the `bind` grammar does not admit — is off-contract; inventing one here would author a binding the
    // source never asked for, so the whole array falls back to a plain `bindings:` property instead.
    private static bool CanSugarBindings(JsonArray bindings) {
        foreach (var item in bindings) {
            if (item is not JsonObject bind
                || !PuckDslVocabulary.TryParseComparisonKind(bind["kind"]?.ToString(), out _)
                || bind["name"] is null
                || bind["expression"] is null) {
                return false;
            }
        }
        return true;
    }

    private static void AppendBindStatement(StringBuilder sb, JsonObject bind, int indentLevel) {
        var inner = new string(' ', indentLevel * 4);
        var name = bind["name"]?.ToString() ?? "";
        var kind = bind["kind"]?.ToString() ?? "";
        var expression = bind["expression"]?.ToString() ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}bind {name} : {kind} = {expression}");
    }

    // ---- Decision block inverse (§3.1/§3.2) -----------------------------------------------------------------

    private static void AppendDecisionBlock(StringBuilder sb, JsonObject decision, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = new string(' ', (indentLevel + 1) * 4);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}decision {{");

        if (decision["periodSeconds"] is { } period) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}periodSeconds: {FormatValue(period, 0)}s");
        }
        if (decision["mode"] is { } mode) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}mode: {FormatValue(mode, indentLevel + 1)}");
        }
        if (decision["scoreKind"] is { } scoreKind) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}scoreKind: {FormatValue(scoreKind, indentLevel + 1)}");
        }
        if (decision["commitmentSeconds"] is { } commitment) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}commitmentSeconds: {FormatValue(commitment, 0)}s");
        }
        if (decision["incumbentBonus"] is { } bonus) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}incumbentBonus: {FormatValue(bonus, indentLevel + 1)}");
        }
        if (decision["seed"] is { } seed) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}seed: {FormatValue(seed, indentLevel + 1)}");
        }
        if (decision["interrupt"] is JsonObject interrupt) {
            if (IsPredicateSafeForGateSugar(interrupt)) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}interrupt {FormatPredicate(interrupt)}");
            } else {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}interrupt: {FormatValue(interrupt, indentLevel + 1)}");
            }
        }
        if (decision["onNoChoice"] is JsonArray onNoChoice) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}onNoChoice {{");
            foreach (var e in onNoChoice) {
                AppendEffectStatement(sb, e, indentLevel + 2);
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}}}");
        }
        if (decision["options"] is JsonArray options) {
            foreach (var opt in options) {
                if (opt is JsonObject optObj) {
                    AppendOptionBlock(sb, optObj, indentLevel + 1);
                }
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    private static void AppendOptionBlock(StringBuilder sb, JsonObject option, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = new string(' ', (indentLevel + 1) * 4);
        var name = option["name"]?.ToString() ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}option \"{EscapeString(name)}\" {{");

        if (option["gate"] is JsonObject gate) {
            AppendGateProperty(sb, gate, indentLevel + 1);
        }
        if (option["score"] is { } score) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}score: {score}");
        }
        if (option["effects"] is JsonArray effects) {
            foreach (var e in effects) {
                AppendEffectStatement(sb, e, indentLevel + 1);
            }
        }
        if (option["neighbors"] is { } neighbors) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}neighbors: {FormatValue(neighbors, indentLevel + 1)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    // ---- Gate (predicate) inverse (§1.4) ---------------------------------------------------------------------

    // A rule's `gate` is an ordinary rule-body property (§2.2's generic-property fallback), so — unlike `when` on
    // its own — it has a safe escape hatch: print it as a plain `gate: <value>` property, letting `FormatValue`'s
    // call-form printer carry every `$type` node, whenever `when <comparison>` sugar could not parse back to the
    // same tree. This is required, not cosmetic (A11): `ParseAtom` special-cases a LEADING '(' as a parenthesized
    // sub-gate before a `compareValue` predicate's own (opaque, verbatim) `left` text ever reaches the operand
    // scanner — a `left` that itself begins with a parenthesized clause (a `&`/`|`-chain of grouped comparisons,
    // real in this corpus's own board-legality binds) is truncated at the first ')' no matter where in an
    // `and`/`or` chain it sits, so it can only be represented by NOT going through the gate grammar at all.
    private static void AppendGateProperty(StringBuilder sb, JsonObject gate, int indentLevel) {
        var inner = new string(' ', indentLevel * 4);
        if (IsPredicateSafeForGateSugar(gate)) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}when {FormatPredicate(gate)}");
        } else {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}gate: {FormatValue(gate, indentLevel)}");
        }
    }

    private static bool IsPredicateSafeForGateSugar(JsonObject node) => node["$type"]?.ToString() switch {
        // `comparison` and `kind` must be spelled exactly as their own enum members: the engine's converter accepts
        // any casing, but the sugar can only reproduce the canonical spelling, and printing a different one back
        // changes the document. `value` and `comparandState` together contradict `CompareState`'s own
        // exactly-one-of contract and the sugar carries only one of them; a key present with an explicit JSON null
        // is also unrepresentable, since the comparand text the sugar prints always recompiles to a real value.
        "compareState" => PuckDslVocabulary.TryParseCanonicalComparisonName(node["comparison"]?.ToString(), out _)
            && ((node["value"] is not null) != (node["comparandState"] is not null))
            && !(node.ContainsKey("value") && node["value"] is null)
            && !(node.ContainsKey("comparandState") && node["comparandState"] is null)
            && RowRefRoundTrips(node["state"]?.ToString() ?? "", node["key"]?.ToString())
            && (node["comparandState"] is not { } comparandState
                || RowRefRoundTrips(comparandState.ToString() ?? "", node["comparandKey"]?.ToString())),
        // A `compareValue` whose two operands are simple enough to re-lower as `compareState` can only keep its
        // `$type` when the printed text carries a kind annotation forcing it back (`LowerComparison` treats any
        // annotation as that force), so a node with no `kind` at all in that position has no sugar spelling.
        "compareValue" => PuckDslVocabulary.TryParseCanonicalComparisonName(node["comparison"]?.ToString(), out _)
            && (node["kind"] is null || PuckDslVocabulary.TryParseComparisonKind(node["kind"]!.ToString(), out _))
            && (node["kind"] is not null || !CompareValueNeedsKindAnnotation(node))
            && !(node["left"]?.ToString() ?? "").TrimStart().StartsWith('('),
        // A single-child (or empty) `all`/`any` has no bare-sugar spelling at all: `AndGate`/`OrGate` only ever
        // build the `All`/`Any` wrapper when MORE than one operand was matched at the same keyword — one operand
        // (or zero) collapses to that operand directly (or nothing), so the wrapper itself is unrecoverable from
        // `and`/`or` text no matter where it sits in the tree.
        "all" or "any" => node["predicates"] is JsonArray predicates
            && predicates.Count >= 2
            && predicates.All(static p => p is JsonObject o && IsPredicateSafeForGateSugar(o)),
        "not" => node["predicate"] is JsonObject inner && IsPredicateSafeForGateSugar(inner),
        _ => false,
    };

    // Whether printing (name, key) through `FormatRowRef` and reparsing it through `ExpressionSpelling.TryParse`
    // reproduces the SAME pair. Most keys round-trip; the one confirmed exception is a "$expr:" key whose own
    // expression happens to have exactly the "row[cell]" shape (a live cell read used as a dynamic key) — printing
    // strips the "$expr:" prefix (`ExpressionSpelling`'s own `Print`), but reparsing then reads "row[cell]" as a
    // `$cell:` indirection instead, a different key spelling for the same key text. Testing the round trip directly,
    // rather than re-deriving `ExpressionSpelling`'s own key grammar, is what rule 8 asks for here.
    private static bool RowRefRoundTrips(string name, string? key) {
        var printed = FormatRowRef(name, key);
        return ExpressionSpelling.TryParse(printed, out var tokens, out _)
            && tokens.Count == 1
            && tokens[0] is ValueToken.State state
            && string.Equals(state.Name, name, StringComparison.Ordinal)
            && string.Equals(state.Key, key, StringComparison.Ordinal);
    }

    private static string FormatPredicate(JsonObject node) => node["$type"]?.ToString() switch {
        "compareState" => FormatCompareState(node),
        "compareValue" => FormatCompareValue(node),
        "all" => FormatConjunction(node, "and"),
        "any" => FormatConjunction(node, "or"),
        "not" => FormatNotPredicate(node),
        _ => FormatCallForm(node, 0),
    };

    private static bool PredicateNeedsParens(JsonObject node) => node["$type"]?.ToString() is "all" or "any";

    private static string FormatConjunction(JsonObject node, string keyword) {
        if (node["predicates"] is not JsonArray items) {
            return FormatCallForm(node, 0);
        }
        var parts = new List<string>();
        foreach (var item in items) {
            if (item is not JsonObject child) {
                continue;
            }
            var text = FormatPredicate(child);
            parts.Add(PredicateNeedsParens(child) ? $"({text})" : text);
        }
        return string.Join($" {keyword} ", parts);
    }

    private static string FormatNotPredicate(JsonObject node) {
        if (node["predicate"] is not JsonObject child) {
            return FormatCallForm(node, 0);
        }
        var text = FormatPredicate(child);
        return $"not {(PredicateNeedsParens(child) ? $"({text})" : text)}";
    }

    private static string FormatCompareState(JsonObject node) {
        var state = node["state"]?.ToString() ?? "";
        var key = node["key"]?.ToString();
        var left = FormatRowRef(state, key);
        var symbol = ComparisonToSymbol(node["comparison"]?.ToString());

        // `IsPredicateSafeForGateSugar` admits exactly one non-null comparand, so both arms below are total.
        var right = (node["comparandState"] is { } comparandState)
            ? FormatRowRef(comparandState.ToString() ?? "", node["comparandKey"]?.ToString())
            : (node["value"] is { } value)
                ? FormatValue(value, 0)
                : throw new InvalidOperationException("compareState carries neither a value nor a comparandState");

        return $"{left} {symbol} {right}";
    }

    // Whether the operands are simple enough that `LowerComparison` would read the bare `left cmp right` text back
    // as a `compareState` node — the one case where a `Fixed` kind must still be printed, since the annotation is
    // what keeps the node a `compareValue` on recompile.
    private static bool CompareValueNeedsKindAnnotation(JsonObject node) =>
        WorldDocumentEmitter.ClassifyBareComparison(node["left"]?.ToString() ?? "", node["right"]?.ToString() ?? "", out _, out _)
            != WorldDocumentEmitter.BareComparisonShape.CompareValue;

    // A kind annotation is wrapped in parens around the WHOLE comparison it names, `(left cmp right : Int)`,
    // unconditionally of where the comparison sits: printed bare, the suffix would trail whatever came textually
    // last in an `and`/`or` chain (`FormatConjunction` joins operands with no delimiter of its own), leaving a
    // reader unable to tell whether it annotates that one comparison or the entire chain. Wrapping reuses the
    // parenthesized-subgate grammar the language already has (`when (Gate)` — `ParseAtom`'s leading-`(` case), so
    // nothing in `Parsing/` changes. `Fixed` is the default and stays elided wherever the bare text re-lowers to a
    // `compareValue` on its own.
    private static string FormatCompareValue(JsonObject node) {
        var left = node["left"]?.ToString() ?? "";
        var right = node["right"]?.ToString() ?? "";
        var symbol = ComparisonToSymbol(node["comparison"]?.ToString());
        var comparison = $"{left} {symbol} {right}";
        if (!PuckDslVocabulary.TryParseComparisonKind(node["kind"]?.ToString(), out var kind)) {
            return comparison;
        }
        return ((kind == CellKind.Int) || CompareValueNeedsKindAnnotation(node))
            ? $"({comparison} : {Enum.GetName(kind)})"
            : comparison;
    }

    // Only ever reached for a node `IsPredicateSafeForGateSugar` already accepted, which is what guarantees the wire
    // spelling names an enum member exactly.
    private static string ComparisonToSymbol(string? comparison) =>
        PuckDslVocabulary.TryParseCanonicalComparisonName(comparison, out var parsed)
            ? PuckDslVocabulary.SymbolFor(parsed)
            : throw new InvalidOperationException($"'{comparison}' does not name an ActionStateComparison member");

    // ---- Effect statement inverse (§2) ---------------------------------------------------------------------

    private static void AppendEffectStatement(StringBuilder sb, JsonNode? effectNode, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        if (effectNode is not JsonObject obj || obj["$type"] is not JsonValue typeVal) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatValue(effectNode, indentLevel)}");
            return;
        }

        switch (typeVal.ToString()) {
            case "setState":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatSetOrAddEffect("setState", obj)}");
                break;
            case "addState":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatSetOrAddEffect("addState", obj)}");
                break;
            case "pushState":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatPushEffect(obj)}");
                break;
            case "countdownState":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatRowOnlyEffect("countdown", obj)}");
                break;
            case "removeStateCell":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatRowOnlyEffect("remove", obj)}");
                break;
            case "scheduleState":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatScheduleEffect(obj)}");
                break;
            case "transformState":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatTransformEffect(obj)}");
                break;
            case "transaction":
                AppendTransactionStatement(sb, obj, indentLevel);
                break;
            default:
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatCallForm(obj, indentLevel)}");
                break;
        }
    }

    // Row+key target text shared by every effect arm — `state` or `state[key]`, matching `RowRef`'s own spelling.
    private static string FormatRowRefTarget(JsonObject obj) => FormatRowRef(obj["state"]?.ToString() ?? "", obj["key"]?.ToString());

    // Prints a state/key pair through `ExpressionSpelling.Print` rather than hand-built bracket concatenation
    // (rule 8): a `key` carrying a `$expr:`/`$cell:` indirection prefix has its OWN print spelling (the prefix is
    // stripped and the underlying expression reprinted, parenthesized when it is a bare name) that a literal
    // `state[key]` splice would get wrong.
    private static string FormatRowRef(string name, string? key) =>
        ExpressionSpelling.Print([new ValueToken.State(Name: name, Key: key)]);

    // Whether an effect's own `state`/`key` target is safe to spell as `RowRef` sugar (§2.2) at all — see
    // `RowRefRoundTrips`.
    private static bool IsEffectTargetSafe(JsonObject obj) => RowRefRoundTrips(obj["state"]?.ToString() ?? "", obj["key"]?.ToString());

    // The RHS classification table (§2.3), shared by setState/addState/push: `text` (setState only), `valueSeconds`,
    // `expression` (printed verbatim, but only when it round-trips as one — see
    // `ExpressionRoundTripsThroughOperandRhs`), `value`, then an unkeyed `fromState`. `null` means no `=` sugar
    // applies — either nothing classifiable is present, `fromState`+`fromKey` are BOTH present, or `expression`
    // holds a shape the compiler's own operand classifier would reclassify as `value`/`fromState` on recompile
    // (that combination has no `=` sugar inverse; it always prints as call-form).
    private static (string Text, string Key)? FormatEffectRhs(JsonObject obj, bool allowText) {
        if (allowText && obj["text"] is { } textVal) {
            return (FormatValue(textVal, 0), "text");
        }
        if (obj["valueSeconds"] is { } secondsVal) {
            return ($"{FormatValue(secondsVal, 0)}s", "valueSeconds");
        }
        if (obj["expression"] is { } exprVal) {
            var text = exprVal.ToString() ?? "";
            return ExpressionRoundTripsThroughOperandRhs(text) ? (text, "expression") : null;
        }
        if (obj["value"] is { } val) {
            return (FormatValue(val, 0), "value");
        }
        if (obj["fromState"] is { } fromStateVal && obj["fromKey"] is null) {
            return (fromStateVal.ToString() ?? "", "fromState");
        }
        return null;
    }

    // Whether `obj` carries nothing beyond the keys a sugar spelling reprints. Any other key — including one
    // present with an explicit JSON null, which the `is { }` reads above pass over — would be silently dropped, so
    // the caller falls back to call form, whose printer carries every key.
    private static bool CarriesOnly(JsonObject obj, params ReadOnlySpan<string> reprinted) {
        foreach (var (key, _) in obj) {
            if (!reprinted.Contains(key)) {
                return false;
            }
        }
        return true;
    }

    // The compiler's own operand classifier (`WorldDocumentEmitter.ApplyOperandRhs`) reclassifies a bare RHS operand
    // that parses to exactly one `Constant` token as `value`, and one unkeyed `State` token as `fromState` — never
    // leaving either under `expression`. An `expression` field already carrying one of those two shapes could only
    // have been authored through call-form (`setState(..., expression: "...")`) to begin with, so `=` sugar has no
    // inverse for it; anything else (a genuinely multi-token expression, or a single KEYED `State` token) always
    // stays `expression` on recompile and prints verbatim.
    private static bool ExpressionRoundTripsThroughOperandRhs(string text) {
        if (!ExpressionSpelling.TryParse(text, out var tokens, out _)) {
            return false;
        }
        if (tokens.Count != 1) {
            return true;
        }
        return tokens[0] switch {
            ValueToken.Constant => false,
            ValueToken.State { Key: null } => false,
            _ => true,
        };
    }

    // The `=` sugar emits only `$type`, `state`, `key` and the one right-hand-side key it consumed (LowerCellEffect
    // fills nothing else), so an object carrying anything more — an explicit `"target": "Self"` the author wrote,
    // or a second RHS key left behind — has no sugar spelling and prints as call form.
    private static string FormatSetOrAddEffect(string discriminator, JsonObject obj) {
        var rhs = IsEffectTargetSafe(obj) ? FormatEffectRhs(obj, allowText: discriminator == "setState") : null;
        if (rhs is not { } entry || !CarriesOnly(obj, "$type", "state", "key", entry.Key)) {
            return FormatCallForm(obj, 0);
        }
        var op = (discriminator == "addState") ? "+=" : "=";
        return $"{FormatRowRefTarget(obj)} {op} {entry.Text}";
    }

    private static string FormatPushEffect(JsonObject obj) {
        var rhs = FormatEffectRhs(obj, allowText: false);
        if (rhs is not { } entry || !CarriesOnly(obj, "$type", "state", entry.Key)) {
            return FormatCallForm(obj, 0);
        }
        return $"push {obj["state"]} = {entry.Text}";
    }

    private static string FormatRowOnlyEffect(string keyword, JsonObject obj) =>
        (IsEffectTargetSafe(obj) && CarriesOnly(obj, "$type", "state", "key"))
            ? $"{keyword} {FormatRowRefTarget(obj)}"
            : FormatCallForm(obj, 0);

    private static string FormatScheduleEffect(JsonObject obj) {
        if (obj["delaySeconds"] is not { } delay || !IsEffectTargetSafe(obj) || !CarriesOnly(obj, "$type", "state", "key", "delaySeconds")) {
            return FormatCallForm(obj, 0);
        }
        return $"schedule {FormatRowRefTarget(obj)} in {FormatValue(delay, 0)}s";
    }

    private static string FormatTransformEffect(JsonObject obj) {
        if (obj["transform"] is not JsonObject inner) {
            return FormatCallForm(obj, 0);
        }
        // The local name carries no wire representation (ActionEffect.TransformState has no `state`/name field at
        // all) — any placeholder round-trips identically, since the emitter discards it.
        return $"transform t = {FormatCallForm(inner, 0)}";
    }

    private static void AppendTransactionStatement(StringBuilder sb, JsonObject obj, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}transaction {{");
        if (obj["effects"] is JsonArray effects) {
            foreach (var e in effects) {
                AppendEffectStatement(sb, e, indentLevel + 1);
            }
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");

        if (obj["onFailure"] is JsonArray onFailure) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}onFailure {{");
            foreach (var e in onFailure) {
                AppendEffectStatement(sb, e, indentLevel + 1);
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        }
    }
}
