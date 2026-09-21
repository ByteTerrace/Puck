using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler;

namespace Puck.World.Transpiler.Decompiler;

// `rule "name" { }` inverse (§1-§3): gate/local/decision/option/interrupt/onNoChoice, and every effect statement.
// Every `ExpressionProgram`-typed field (`compareValue.left`/`right`, `setState`/`addState`/`push.expression`,
// `locals[].expression`, `options[].score`) holds the IR and prints back through
// `WorldExpressionJson`/`ExpressionSpelling`, so print-then-parse is the identity. Anything the dedicated sugar
// below does not cover (every `Puck.World.Schema` extension arm, every `StateTransform` sub-arm) falls through to
// `FormatCallForm` — the same escape hatch `FormatValue` uses everywhere else in the document.
public static partial class WorldDecompiler {
    private static string FormatStateReference(JsonNode? node) => (((node is JsonValue value) && value.TryGetValue<string>(value: out var spelling))
        ? StateChannelRef.Parse(spelling: spelling).Spelling
        : StateChannelRefJsonConverter.FromNode(node: node).Spelling
    );
    private static bool IsPoolFieldReference(JsonNode? node) => (
        (node is not JsonValue) &&
        (StateChannelRefJsonConverter.FromNode(node: node).PoolField is not null)
    );
    // Whether every row in a `rules` array is a rule the `rule "name" { }` grammar can carry. A row with no `name`
    // is a WorldDocumentBasis merge directive, not a rule: it has no name to quote and no effects to author, and
    // routing it through the sugar emits a rule the compiler then refuses. When any row is one of those, the whole
    // section prints through the generic value path, which carries every row unchanged.
    private static bool CanSugarRules(JsonArray rules) {
        foreach (var item in rules) {
            if (
                (item is not JsonObject rule) ||
                (rule["name"] is not JsonValue nameVal) ||
                !nameVal.TryGetValue<string>(value: out _)
            ) {
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
            AppendRuleBlock(
                indentLevel: indentLevel,
                rule: rule,
                sb: sb
            );
        }
    }

    private static readonly HashSet<string> RuleKnownKeys = new(comparer: StringComparer.Ordinal) {
        "name", "gate", "locals", "mode", "forEach", "poolForEach", "zones", "decision", "effects",
    };

    // Respelled under the rule's own locals (see ExpressionSpelling.WithLocals): a plain row read one of them
    // shadows backquotes here, at the one place that knows both the row and the local carry the same name.
    private static void AppendRuleBlock(StringBuilder sb, JsonObject rule, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var name = (rule["name"]?.ToString() ?? "");
        var block = new StringBuilder();

        using (ExpressionSpelling.WithLocals(locals: RuleLocalNames(rule: rule))) {
            var poolHeader = (((rule["poolForEach"] is JsonObject iteration) &&
                (iteration["pool"] is { } pool) && (iteration["binding"] is { } binding) &&
                CarriesOnly(iteration, "pool", "binding"))
                ? $" for each {binding} in {pool}"
                : "");

            block.AppendLine(CultureInfo.InvariantCulture, $"{indent}rule \"{EscapeString(s: name)}\"{poolHeader} {{");
            AppendRuleBody(
                indentLevel: (indentLevel + 1),
                rule: rule,
                sb: block
            );
            block.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}}}"
            );

            sb.Append(value: Respell(text: block.ToString()));
        }
    }
    // The bare names a rule's own `locals` bind — what ExpressionSpelling.WithLocals needs to backquote a plain row
    // read one of them shadows, whether or not the row and the local ever actually collide.
    private static IReadOnlySet<string> RuleLocalNames(JsonObject rule) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (rule["locals"] is JsonArray locals) {
            foreach (var item in locals) {
                if (
                    (item is JsonObject local) &&
                    (local["name"] is JsonValue nameValue) &&
                    nameValue.TryGetValue<string>(value: out var name)
                ) {
                    names.Add(item: name);
                }
            }
        }

        return names;
    }
    // A rule's body without its `rule "name" { }` wrapper, so a workflow step -- whose body IS one rule's body --
    // prints through the same writer the standalone spelling uses.
    private static void AppendRuleBody(StringBuilder sb, JsonObject rule, int indentLevel) {
        var inner = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        if (rule["gate"] is JsonObject gate) {
            AppendGateProperty(
                gate: gate,
                indentLevel: indentLevel,
                sb: sb
            );
        }
        if (rule["locals"] is JsonArray locals) {
            if (CanSugarLocals(locals: locals)) {
                foreach (var l in locals) {
                    if (l is JsonObject localObj) {
                        AppendLocalStatement(
                            indentLevel: indentLevel,
                            local: localObj,
                            sb: sb
                        );
                    }
                }
            } else {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{inner}locals{FieldSeparator(value: locals)}{FormatModelField(holder: typeof(WorldRule), indentLevel: indentLevel, key: "locals", node: locals)}"
                );
            }
        }
        if (rule["mode"] is { } mode) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}mode: {FormatModelField(holder: typeof(WorldRule), indentLevel: indentLevel, key: "mode", node: mode)}"
            );
        }
        if (rule["forEach"] is { } forEach) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}forEach: {FormatModelField(holder: typeof(WorldRule), indentLevel: indentLevel, key: "forEach", node: forEach)}"
            );
        }
        if ((rule["poolForEach"] is { }) && !((rule["poolForEach"] is JsonObject iteration) && CarriesOnly(iteration, "pool", "binding"))) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}poolForEach{FieldSeparator(value: rule["poolForEach"])}{FormatValue(rule["poolForEach"], indentLevel)}");
        }
        if (rule["zones"] is { } zones) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}zones{FieldSeparator(value: zones)}{FormatModelField(holder: typeof(WorldRule), indentLevel: indentLevel, key: "zones", node: zones)}"
            );
        }
        if (rule["decision"] is JsonObject decision) {
            AppendDecisionBlock(
                decision: decision,
                indentLevel: indentLevel,
                sb: sb
            );
        }
        if (rule["effects"] is JsonArray effects) {
            foreach (var e in effects) {
                AppendEffectStatement(
                    effectNode: e,
                    indentLevel: indentLevel,
                    sb: sb
                );
            }
        }

        foreach (var (k, v) in rule) {
            if (
                RuleKnownKeys.Contains(item: k) ||
                (v is null)
            ) {
                continue;
            }
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}`{k}`{FieldSeparator(value: v)}{FormatModelField(holder: typeof(WorldRule), indentLevel: indentLevel, key: k, node: v)}"
            );
        }
    }
    // A source local statement omits kind. Only use it where this expression provides enough evidence that
    // inference preserves the declared kind; otherwise retain the explicit `locals:` property.
    private static bool KindReadsBack(JsonObject local) => (local["kind"]?.ToString() switch {
        null => true,
        "Int" => IntExpressionReadsBack(local: local),
        "Fixed" => (
            ExpressionSpelling.TryParse(
                error: out _,
                program: out var program,
                text: WorldExpressionJson.Text(node: local["expression"])
            ) &&
            program.Instructions.Concat(second: program.Subprograms.SelectMany(selector: static subprogram => subprogram.Instructions)).Any(predicate: static instruction => (instruction.Payload is InstructionPayload.State))
        ),
        _ => false,
    });
    private static bool IntExpressionReadsBack(JsonObject local) {
        if (!ExpressionSpelling.TryParse(
            error: out _,
            program: out var program,
            text: WorldExpressionJson.Text(node: local["expression"])
        )) {
            return false;
        }

        var instructions = program.Instructions
            .Concat(second: program.Subprograms.SelectMany(selector: static subprogram => subprogram.Instructions))
            .ToArray();

        // A source local has no kind annotation. If it reads state, the row's kind is required to prove that the
        // inferred result remains Int; retain the explicit JSON form when the decompiler cannot carry that proof.
        if (instructions.Any(predicate: static instruction => (instruction.Payload is InstructionPayload.State))) {
            return false;
        }

        return instructions.All(predicate: static instruction => (
            (instruction.Operation != ExpressionOp.Divide) &&
            (
                (instruction.Payload is not InstructionPayload.Constant constant) ||
                (decimal.Truncate(d: constant.Value) == constant.Value)
            )
        ));
    }
    private static bool CanSugarLocals(JsonArray locals) {
        foreach (var item in locals) {
            if (
                (item is not JsonObject local) ||
                !KindReadsBack(local: local) ||
                (local["name"] is null) ||
                (local["expression"] is null)
            ) {
                return false;
            }
        }
        return true;
    }
    private static void AppendLocalStatement(StringBuilder sb, JsonObject local, int indentLevel) {
        var inner = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var name = (local["name"]?.ToString() ?? "");
        var expression = WorldExpressionJson.SourceText(node: local["expression"]);

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{inner}local {name} = {expression}"
        );
    }
    // ---- Decision block inverse (§3.1/§3.2) -----------------------------------------------------------------

    private static void AppendDecisionBlock(StringBuilder sb, JsonObject decision, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var inner = new string(
            c: ' ',
            count: ((indentLevel + 1) * 4)
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}decision {{"
        );

        if (decision["periodSeconds"] is { } period) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}periodSeconds: {FormatValue(
                    indentLevel: 0,
                    node: period
                )}s"
            );
        }
        if (decision["mode"] is { } mode) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}mode: {FormatModelField(holder: DecisionModel, indentLevel: (indentLevel + 1), key: "mode", node: mode)}"
            );
        }
        if (decision["scoreKind"] is { } scoreKind) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}scoreKind: {FormatModelField(holder: DecisionModel, indentLevel: (indentLevel + 1), key: "scoreKind", node: scoreKind)}"
            );
        }
        if (decision["commitmentSeconds"] is { } commitment) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}commitmentSeconds: {FormatValue(
                    indentLevel: 0,
                    node: commitment
                )}s"
            );
        }
        if (decision["incumbentBonus"] is { } bonus) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}incumbentBonus: {FormatValue(
                    indentLevel: (indentLevel + 1),
                    node: bonus
                )}"
            );
        }
        if (decision["seed"] is { } seed) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}seed: {FormatValue(
                    indentLevel: (indentLevel + 1),
                    node: seed
                )}"
            );
        }
        if (decision["interrupt"] is JsonObject interrupt) {
            if (IsPredicateSafeForGateSugar(node: interrupt)) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{inner}interrupt {FormatPredicate(node: interrupt)}"
                );
            } else {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{inner}interrupt{FieldSeparator(value: interrupt)}{FormatModelField(holder: DecisionModel, indentLevel: (indentLevel + 1), key: "interrupt", node: interrupt)}"
                );
            }
        }
        if (decision["onNoChoice"] is JsonArray onNoChoice) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}onNoChoice {{"
            );
            foreach (var e in onNoChoice) {
                AppendEffectStatement(
                    effectNode: e,
                    indentLevel: (indentLevel + 2),
                    sb: sb
                );
            }
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}}}"
            );
        }
        if (decision["options"] is JsonArray options) {
            foreach (var opt in options) {
                if (opt is JsonObject optObj) {
                    AppendOptionBlock(
                        indentLevel: (indentLevel + 1),
                        option: optObj,
                        sb: sb
                    );
                }
            }
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static void AppendOptionBlock(StringBuilder sb, JsonObject option, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var inner = new string(
            c: ' ',
            count: ((indentLevel + 1) * 4)
        );
        var name = (option["name"]?.ToString() ?? "");

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}option \"{EscapeString(s: name)}\" {{"
        );

        if (option["gate"] is JsonObject gate) {
            AppendGateProperty(
                gate: gate,
                indentLevel: (indentLevel + 1),
                sb: sb
            );
        }
        if (option["score"] is { } score) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}score: {WorldExpressionJson.SourceText(node: score)}"
            );
        }
        if (option["effects"] is JsonArray effects) {
            foreach (var e in effects) {
                AppendEffectStatement(
                    effectNode: e,
                    indentLevel: (indentLevel + 1),
                    sb: sb
                );
            }
        }
        if (option["neighbors"] is { } neighbors) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}neighbors{FieldSeparator(value: neighbors)}{FormatModelField(holder: OptionModel, indentLevel: (indentLevel + 1), key: "neighbors", node: neighbors)}"
            );
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    // ---- Gate (predicate) inverse (§1.4) ---------------------------------------------------------------------

    // A rule's `gate` is an ordinary rule-body property (§2.2's generic-property fallback), so — unlike `when` on
    // its own — it has a safe escape hatch: print it as a plain `gate: <value>` property, letting `FormatValue`'s
    // call-form printer carry every `$type` node, whenever `when <comparison>` sugar could not parse back to the
    // same tree. This is required, not cosmetic (A11): `ParseAtom` special-cases a LEADING '(' as a parenthesized
    // sub-gate before a `compareValue` predicate's own `left` reaches the operand parser — a `left` that
    // itself begins with a parenthesized clause (a `&`/`|`-chain of grouped comparisons,
    // real in this corpus's own board-legality binds) is truncated at the first ')' no matter where in an
    // `and`/`or` chain it sits, so it can only be represented by NOT going through the gate grammar at all.
    private static void AppendGateProperty(StringBuilder sb, JsonObject gate, int indentLevel) {
        var inner = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        if (IsPredicateSafeForGateSugar(node: gate)) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}when {FormatPredicate(node: gate)}"
            );
        } else {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}gate{FieldSeparator(value: gate)}{FormatValue(
                    indentLevel: indentLevel,
                    node: gate
                )}"
            );
        }
    }

    private static readonly Type? DecisionModel = WorldCallArguments.MemberType(member: "decision", owner: typeof(WorldRule));
    private static readonly Type? OptionModel = ((DecisionModel is null) ? null : WorldCallArguments.MemberType(member: "options", owner: DecisionModel));

    // One property line of a rule, a decision or an option, in the spelling the model gives its member.
    private static string FormatModelField(Type? holder, string key, JsonNode? node, int indentLevel) => FormatArgument(
        context: ((holder is null) ? null : WorldCallArguments.MemberType(member: key, owner: holder)),
        form: ((holder is null) ? WorldArgumentForm.Unclassified : WorldCallArguments.Classify(member: key, owner: holder)),
        indentLevel: indentLevel,
        node: node
    );
    private static bool IsPredicateSafeForGateSugar(JsonObject node) => node["$type"]?.ToString() switch {
        // `comparison` and `kind` must be spelled exactly as their own enum members: the engine's converter accepts
        // any casing, but the sugar can only reproduce the canonical spelling, and printing a different one back
        // changes the document. `value` and `comparandState` together contradict `CompareState`'s own
        // exactly-one-of contract and the sugar carries only one of them; a key present with an explicit JSON null
        // is also unrepresentable, since the comparand text the sugar prints always recompiles to a real value.
        "compareState" => (PuckDslVocabulary.TryParseCanonicalComparisonName(
        node["comparison"]?.ToString(),
        out _
    )
            && ((node["value"] is not null) != (node["comparandState"] is not null))
            && !(node.ContainsKey(propertyName: "value") && (node["value"] is null))
            && !(node.ContainsKey(propertyName: "comparandState") && (node["comparandState"] is null))
            && RowRefRoundTrips(
        (node["state"]?.ToString() ?? ""),
        node["key"]?.ToString()
    )
            && ((node["comparandState"] is not { } comparandState)
                || RowRefRoundTrips(
        (comparandState.ToString() ?? ""),
        node["comparandKey"]?.ToString()
    ))),
        // A `compareValue` whose two operands are simple enough to re-lower as `compareState` can only keep its
        // `$type` when the printed text carries a kind annotation forcing it back (`LowerComparison` treats any
        // annotation as that force), so a node with no `kind` at all in that position has no sugar spelling.
        "compareValue" => (PuckDslVocabulary.TryParseCanonicalComparisonName(
        node["comparison"]?.ToString(),
        out _
    )
            && ((node["kind"] is null) || PuckDslVocabulary.TryParseComparisonKind(
        node["kind"]!.ToString(),
        out _
    ))
            && ((node["kind"] is not null) || !CompareValueNeedsKindAnnotation(node: node))),
        // A single-child (or empty) `all`/`any` has no bare-sugar spelling at all: `AndGate`/`OrGate` only ever
        // build the `All`/`Any` wrapper when MORE than one operand was matched at the same keyword — one operand
        // (or zero) collapses to that operand directly (or nothing), so the wrapper itself is unrecoverable from
        // `and`/`or` text no matter where it sits in the tree.
        "all" or "any" => ((node["predicates"] is JsonArray predicates)
            && (predicates.Count >= 2)
            && predicates.All(predicate: static p => ((p is JsonObject o) && IsPredicateSafeForGateSugar(node: o)))),
        "not" => ((node["predicate"] is JsonObject inner) && IsPredicateSafeForGateSugar(node: inner)),
        _ => false,
    };
    // Whether printing (name, key) through `FormatRowRef` and reparsing it through `ExpressionSpelling.TryParse`
    // reproduces the SAME pair. Most keys round-trip; the one confirmed exception is a "$expr:" key whose own
    // expression happens to have exactly the "row[cell]" shape (a live cell read used as a dynamic key) — printing
    // strips the "$expr:" prefix (`ExpressionSpelling`'s own `Print`), but reparsing then reads "row[cell]" as a
    // `$cell:` indirection instead, a different key spelling for the same key text. Testing the round trip directly,
    // rather than re-deriving `ExpressionSpelling`'s own key grammar, is what rule 8 asks for here.
    private static bool RowRefRoundTrips(string name, string? key) {
        var printed = FormatRowRef(
            key: key,
            name: name
        );

        return (
            ExpressionSpelling.TryParse(
            error: out _,
            program: out var parsed,
            text: printed
        ) &&
            (parsed.Instructions.Count == 1) &&
            (parsed.Instructions[0] is { Payload: InstructionPayload.State state }) &&
            string.Equals(
            a: state.Name.Spelling,
            b: name,
            comparisonType: StringComparison.Ordinal
        ) &&
            string.Equals(
            a: state.Key?.Spelling,
            b: key,
            comparisonType: StringComparison.Ordinal
        )
        );
    }
    private static string FormatPredicate(JsonObject node) => node["$type"]?.ToString() switch {
        "compareState" => FormatCompareState(node: node),
        "compareValue" => FormatCompareValue(node: node),
        "all" => FormatConjunction(
        keyword: "and",
        node: node
    ),
        "any" => FormatConjunction(
        keyword: "or",
        node: node
    ),
        "not" => FormatNotPredicate(node: node),
        _ => FormatCallForm(
        indentLevel: 0,
        obj: node
    ),
    };
    private static bool PredicateNeedsParens(JsonObject node) => (node["$type"]?.ToString() is "all" or "any");
    private static string FormatConjunction(JsonObject node, string keyword) {
        if (node["predicates"] is not JsonArray items) {
            return FormatCallForm(
                indentLevel: 0,
                obj: node
            );
        }
        var parts = new List<string>();

        foreach (var item in items) {
            if (item is not JsonObject child) {
                continue;
            }
            var text = FormatPredicate(node: child);

            parts.Add(item: (PredicateNeedsParens(node: child)
                ? $"({text})"
                : text));
        }
        return string.Join(
            separator: $" {keyword} ",
            values: parts
        );
    }
    private static string FormatNotPredicate(JsonObject node) {
        if (node["predicate"] is not JsonObject child) {
            return FormatCallForm(
                indentLevel: 0,
                obj: node
            );
        }
        var text = FormatPredicate(node: child);

        return $"not {(PredicateNeedsParens(node: child)
            ? $"({text})"
            : text)}";
    }
    private static string FormatCompareState(JsonObject node) {
        var state = (node["state"]?.ToString() ?? "");
        var key = node["key"]?.ToString();
        var left = FormatRowRef(
            key: key,
            name: state
        );
        var symbol = ComparisonToSymbol(comparison: node["comparison"]?.ToString());

        // `IsPredicateSafeForGateSugar` admits exactly one non-null comparand, so both arms below are total.
        var right = ((node["comparandState"] is { } comparandState)
            ? FormatRowRef(
                (comparandState.ToString() ?? ""),
                node["comparandKey"]?.ToString()
            )
            : ((node["value"] is { } value)
                ? FormatValue(
                    indentLevel: 0,
                    node: value
                )
                : throw new InvalidOperationException(message: "compareState carries neither a value nor a comparandState")
        ));

        return $"{left} {symbol} {right}";
    }
    // Whether the operands are simple enough that `LowerComparison` would read the bare `left cmp right` text back
    // as a `compareState` node — the one case where a `Fixed` kind must still be printed, since the annotation is
    // what keeps the node a `compareValue` on recompile.
    private static bool CompareValueNeedsKindAnnotation(JsonObject node) =>
        (WorldDocumentEmitter.ClassifyBareComparison(
            WorldExpressionJson.SourceText(node: node["left"]),
            WorldExpressionJson.SourceText(node: node["right"]),
            out _,
            out _
        )
            != WorldDocumentEmitter.BareComparisonShape.CompareValue);
    // A kind annotation is wrapped in parens around the WHOLE comparison it names, `(left cmp right : Int)`,
    // unconditionally of where the comparison sits: printed bare, the suffix would trail whatever came textually
    // last in an `and`/`or` chain (`FormatConjunction` joins operands with no delimiter of its own), leaving a
    // reader unable to tell whether it annotates that one comparison or the entire chain. Wrapping reuses the
    // parenthesized-subgate grammar the language already has (`when (Gate)` — `ParseAtom`'s leading-`(` case), so
    // nothing in `Parsing/` changes. `Fixed` is the default and stays elided wherever the bare text re-lowers to a
    // `compareValue` on its own.
    private static string FormatCompareValue(JsonObject node) {
        // A composed operand is parenthesized: the comparison joins the two printed operands with its own symbol,
        // and an operand whose own top-level operator binds looser than a comparison would otherwise re-parse as a
        // chain. A bare read or constant stays bare, which is what keeps `ClassifyBareComparison` reading it back.
        var left = WorldExpressionJson.SourceComparisonOperand(node: node["left"]);
        var right = WorldExpressionJson.SourceComparisonOperand(node: node["right"]);
        var symbol = ComparisonToSymbol(comparison: node["comparison"]?.ToString());
        var comparison = $"{left} {symbol} {right}";

        if (!PuckDslVocabulary.TryParseComparisonKind(
            node["kind"]?.ToString(),
            out var kind
        )) {
            return comparison;
        }
        return (((kind == CellKind.Int) || CompareValueNeedsKindAnnotation(node: node))
            ? $"({comparison} : {Enum.GetName(value: kind)})"
            : comparison
        );
    }
    // Only ever reached for a node `IsPredicateSafeForGateSugar` already accepted, which is what guarantees the wire
    // spelling names an enum member exactly.
    private static string ComparisonToSymbol(string? comparison) =>
        (PuckDslVocabulary.TryParseCanonicalComparisonName(
            comparison: out var parsed,
            name: comparison
        )
            ? PuckDslVocabulary.SymbolFor(comparison: parsed)
            : throw new InvalidOperationException(message: $"'{comparison}' does not name an ActionStateComparison member")
        );
    // ---- Effect statement inverse (§2) ---------------------------------------------------------------------

    private static void AppendEffectStatement(StringBuilder sb, JsonNode? effectNode, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        if (
            (effectNode is not JsonObject obj) ||
            (obj["$type"] is not JsonValue typeVal)
        ) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}{FormatValue(
                    indentLevel: indentLevel,
                    node: effectNode
                )}"
            );
            return;
        }

        switch (typeVal.ToString()) {
            case "setState":
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}{FormatSetOrAddEffect(
                        discriminator: "setState",
                        obj: obj
                    )}"
                );
                break;
            case "addState":
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}{FormatSetOrAddEffect(
                        discriminator: "addState",
                        obj: obj
                    )}"
                );
                break;
            case "pushState":
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}{FormatPushEffect(obj: obj)}"
                );
                break;
            case "removeStateCell":
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}{FormatRowOnlyEffect(
                        keyword: "remove",
                        obj: obj
                    )}"
                );
                break;
            case "scheduleState":
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}{FormatScheduleEffect(obj: obj)}"
                );
                break;
            case "transformState":
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}{FormatTransformEffect(obj: obj)}"
                );
                break;
            case "transaction":
                AppendTransactionStatement(
                    indentLevel: indentLevel,
                    obj: obj,
                    sb: sb
                );
                break;
            case "if":
                AppendIfStatement(
                    indentLevel: indentLevel,
                    keyword: "if",
                    obj: obj,
                    sb: sb
                );
                break;
            case "claim":
                if (CarriesOnly(obj, "$type", "pool", "binding", "effects")) {
                    AppendPoolBodyStatement(indentLevel: indentLevel, keyword: "claim", obj: obj, sb: sb);
                } else {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatCallForm(indentLevel: indentLevel, obj: obj)}");
                }
                break;
            case "claimPair":
                if (CarriesOnly(obj, "$type", "pool", "left", "right", "binding", "effects")) {
                    AppendPairPoolClaimStatement(indentLevel: indentLevel, obj: obj, sb: sb);
                } else {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatCallForm(indentLevel: indentLevel, obj: obj)}");
                }
                break;
            case "forEachPool":
                if (CarriesOnly(obj, "$type", "pool", "binding", "effects")) {
                    AppendPoolBodyStatement(indentLevel: indentLevel, keyword: "forEach", obj: obj, sb: sb);
                } else {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{FormatCallForm(indentLevel: indentLevel, obj: obj)}");
                }
                break;
            case "release":
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{(CarriesOnly(obj, "$type", "binding")
                    ? $"release {(obj["binding"]?.ToString() ?? "")}"
                    : FormatCallForm(indentLevel: indentLevel, obj: obj))}");
                break;
            default:
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{indent}{FormatCallForm(
                        indentLevel: indentLevel,
                        obj: obj
                    )}"
                );
                break;
        }
    }
    private static void AppendPoolBodyStatement(string keyword, int indentLevel, JsonObject obj, StringBuilder sb) {
        var indent = new string(c: ' ', count: (indentLevel * 4));
        var pool = (obj["pool"]?.ToString() ?? "");
        var binding = (obj["binding"]?.ToString() ?? "");
        var header = ((keyword == "claim") ? $"claim {pool} as {binding}" : $"for each {binding} in {pool}");

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{header} {{");
        if (obj["effects"] is JsonArray effects) {
            foreach (var effect in effects) {
                AppendEffectStatement(effectNode: effect, indentLevel: (indentLevel + 1), sb: sb);
            }
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }
    private static void AppendPairPoolClaimStatement(int indentLevel, JsonObject obj, StringBuilder sb) {
        var indent = new string(c: ' ', count: (indentLevel * 4));
        var pool = (obj["pool"]?.ToString() ?? "");
        var left = (obj["left"]?.ToString() ?? "");
        var right = (obj["right"]?.ToString() ?? "");
        var binding = (obj["binding"]?.ToString() ?? "");

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}claim pair {pool} between {left}, {right} as {binding} {{");
        if (obj["effects"] is JsonArray effects) {
            foreach (var effect in effects) {
                AppendEffectStatement(effectNode: effect, indentLevel: (indentLevel + 1), sb: sb);
            }
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }
    // Row+key target text shared by every effect arm — `state` or `state[key]`, matching `RowRef`'s own spelling.
    private static string FormatRowRefTarget(JsonObject obj) => (IsPoolFieldReference(node: obj["state"])
        ? FormatStateReference(node: obj["state"])
        : FormatRowRef(FormatStateReference(node: obj["state"]), obj["key"]?.ToString())
    );
    // Prints a state/key pair through `ExpressionSpelling.TryPrintSource` rather than hand-built bracket
    // concatenation (rule 8): a `key` carrying a `$expr:`/`$cell:` indirection prefix has its OWN print spelling
    // (the prefix is stripped and the underlying expression reprinted, parenthesized when it is a bare name) that a
    // literal `state[key]` splice would get wrong, and the source dialect backquotes a keyless plain name one of
    // the caller's `ExpressionSpelling.WithLocals` locals shadows.
    private static string FormatRowRef(string name, string? key) {
        var dottedParts = name.Split('.');

        if ((dottedParts.Length == 2) && dottedParts.All(predicate: IsDeclarationIdentifier)) {
            if (key is null) {
                return name;
            }

            if (ExpressionSpelling.TryPrintSource(
                program: new ExpressionProgram(Instructions: [Instruction.Operand(
                    key: key,
                    name: name
                )]),
                text: out var dottedText
            )) {
                var keyStart = dottedText.LastIndexOf(value: '[');

                if (keyStart >= 0) {
                    return $"{dottedParts[0]}{dottedText[keyStart..]}.{dottedParts[1]}";
                }
            }
        }

        return (ExpressionSpelling.TryPrintSource(
            program: new ExpressionProgram(Instructions: [Instruction.Operand(
                key: key,
                name: name
            )]),
            text: out var text
        )
            ? text
            : throw new InvalidOperationException(message: $"a state operand '{name}' does not print"));
    }
    // Whether an effect's own `state`/`key` target is safe to spell as `RowRef` sugar (§2.2) at all — see
    // `RowRefRoundTrips`.
    private static bool IsEffectTargetSafe(JsonObject obj) => (
        (IsPoolFieldReference(node: obj["state"]) && (obj["key"] is null)) ||
        RowRefRoundTrips(FormatStateReference(node: obj["state"]), obj["key"]?.ToString())
    );
    // The RHS classification table (§2.3), shared by setState/addState/push: `text` (setState only), `valueSeconds`,
    // `expression` (printed verbatim, but only when it round-trips as one — see
    // `ExpressionRoundTripsThroughOperandRhs`), `value`, then an unkeyed `fromState`. `null` means no `=` sugar
    // applies — either nothing classifiable is present, `fromState`+`fromKey` are BOTH present, or `expression`
    // holds a shape the compiler's own operand classifier would reclassify as `value`/`fromState` on recompile
    // (that combination has no `=` sugar inverse; it always prints as call-form).
    private static (string Text, string Key)? FormatEffectRhs(JsonObject obj, bool allowText) {
        if (
            allowText &&
            (obj["text"] is { } textVal)
        ) {
            return (FormatValue(
                indentLevel: 0,
                node: textVal
            ), "text");
        }
        if (obj["valueSeconds"] is { } secondsVal) {
            return ($"{FormatValue(
                indentLevel: 0,
                node: secondsVal
            )}s", "valueSeconds");
        }
        if (obj["expression"] is { } exprVal) {
            var text = WorldExpressionJson.SourceText(node: exprVal);

            return (ExpressionRoundTripsThroughOperandRhs(text: text)
                ? (text, "expression")
                : null
            );
        }
        if (obj["value"] is { } val) {
            return (FormatValue(
                indentLevel: 0,
                node: val
            ), "value");
        }
        if (
            (obj["fromState"] is { } fromStateVal) &&
            (obj["fromKey"] is null)
        ) {
            return (FormatRowRef(
                FormatStateReference(node: fromStateVal),
                null
            ), "fromState");
        }
        return null;
    }
    // Whether `obj` carries nothing beyond the keys a sugar spelling reprints. Any other key — including one
    // present with an explicit JSON null, which the `is { }` reads above pass over — would be silently dropped, so
    // the caller falls back to call form, whose printer carries every key.
    private static bool CarriesOnly(JsonObject obj, params ReadOnlySpan<string> reprinted) {
        foreach (var (key, _) in obj) {
            if (!reprinted.Contains(value: key)) {
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
        if (!ExpressionSpelling.TryParse(
            error: out _,
            program: out var parsed,
            text: text
        )) {
            return false;
        }
        if (parsed.Instructions.Count != 1) {
            return true;
        }
        return parsed.Instructions[0] switch {
            { Payload: InstructionPayload.Constant } => false,
            { Payload: InstructionPayload.State { Key: null } } => false,
            _ => true,
        };
    }
    // The `=` sugar emits only `$type`, `state`, `key` and the one right-hand-side key it consumed (LowerCellEffect
    // fills nothing else), so an object carrying anything more — an explicit `"target": "Self"` the author wrote,
    // or a second RHS key left behind — has no sugar spelling and prints as call form.
    private static string FormatSetOrAddEffect(string discriminator, JsonObject obj) {
        var rhs = (IsEffectTargetSafe(obj: obj)
            ? FormatEffectRhs(
                obj,
                allowText: (discriminator == "setState")
            )
            : null
        );

        if (
            (rhs is not { } entry) ||
            !CarriesOnly(
            obj,
            "$type",
            "state",
            "key",
            entry.Key
        )
        ) {
            return FormatCallForm(
                indentLevel: 0,
                obj: obj
            );
        }
        var op = ((discriminator == "addState")
            ? "+="
            : "="
        );

        return $"{FormatRowRefTarget(obj: obj)} {op} {entry.Text}";
    }
    private static string FormatPushEffect(JsonObject obj) {
        var rhs = FormatEffectRhs(
            obj,
            allowText: false
        );

        if (
            (rhs is not { } entry) ||
            !CarriesOnly(
            obj,
            "$type",
            "state",
            entry.Key
        )
        ) {
            return FormatCallForm(
                indentLevel: 0,
                obj: obj
            );
        }
        return $"push {FormatRowRef(
            FormatStateReference(node: obj["state"]),
            null
        )} = {entry.Text}";
    }
    private static string FormatRowOnlyEffect(string keyword, JsonObject obj) =>
        ((IsEffectTargetSafe(obj: obj) && CarriesOnly(
            obj,
            "$type",
            "state",
            "key"
        ))
            ? $"{keyword} {FormatRowRefTarget(obj: obj)}"
            : FormatCallForm(
                indentLevel: 0,
                obj: obj
            )
        );
    private static string FormatScheduleEffect(JsonObject obj) {
        if (
            (obj["delaySeconds"] is not { } delay) ||
            !IsEffectTargetSafe(obj: obj) ||
            !CarriesOnly(
            obj,
            "$type",
            "state",
            "key",
            "delaySeconds"
        )
        ) {
            return FormatCallForm(
                indentLevel: 0,
                obj: obj
            );
        }
        return $"schedule {FormatRowRefTarget(obj: obj)} in {FormatValue(
            indentLevel: 0,
            node: delay
        )}s";
    }
    private static string FormatTransformEffect(JsonObject obj) {
        if (obj["transform"] is not JsonObject inner) {
            return FormatCallForm(
                indentLevel: 0,
                obj: obj
            );
        }
        var type = inner["$type"]?.ToString();

        if (string.Equals(a: type, b: "transfer", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            var from = inner["from"]?.ToString();
            var to = inner["to"]?.ToString();
            var selector = inner["selector"]?.ToString();
            var count = (((inner["count"] is JsonNode c) && int.TryParse(c.ToString(), out var cv)) ? cv : 1);
            var key = inner["key"]?.ToString();
            var insertFirst = ((inner["insertFirst"] is JsonNode inf) && bool.TryParse(inf.ToString(), out var ib) && ib);

            if (string.Equals(a: selector, b: "First", comparisonType: StringComparison.OrdinalIgnoreCase) && (key is null) && !insertFirst) {
                if (inner.ContainsKey(propertyName: "count")) {
                    return $"deal {count} from {from} to {to}";
                }
                return $"draw {from} to {to}";
            }
        } else if (string.Equals(a: type, b: "shuffle", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            var row = inner["row"]?.ToString();
            var draw = inner["draw"]?.ToString();

            if (!string.IsNullOrEmpty(value: row) && !string.IsNullOrEmpty(value: draw)) {
                return $"shuffle {row} with {draw}";
            }
        }
        return $"transform {FormatCallForm(
            indentLevel: 0,
            obj: inner
        )}";
    }
    private static void AppendTransactionStatement(StringBuilder sb, JsonObject obj, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}transaction {{"
        );
        if (obj["effects"] is JsonArray effects) {
            foreach (var e in effects) {
                AppendEffectStatement(
                    effectNode: e,
                    indentLevel: (indentLevel + 1),
                    sb: sb
                );
            }
        }
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );

        if (obj["onFailure"] is JsonArray onFailure) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}onFailure {{"
            );
            foreach (var e in onFailure) {
                AppendEffectStatement(
                    effectNode: e,
                    indentLevel: (indentLevel + 1),
                    sb: sb
                );
            }
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}}}"
            );
        }
    }
    // `if Gate { }` mandates a Gate-grammar condition with no property-style escape hatch the way a rule's own
    // `gate` has: `AppendGateProperty` falls back to a raw `gate: <value>` property (parsed generically, never
    // re-entering Gate grammar) when the sugar cannot safely reproduce a predicate, but `if` has nowhere to fall
    // back to — the core parser reads `if(...)` at statement position unconditionally as this same if-statement
    // grammar, never as a generic call the way every other effect verb is, so no text this method could emit both
    // preserves an unsafe predicate's exact shape and reparses at all. Refuse to decompile rather than emit text
    // that silently recompiles to a different predicate tree (a `compareValue` whose `left` itself starts with '(',
    // misread by `ParseAtom`'s leading-'(' sub-gate case) or fails to reparse at all (an unparseable comparison
    // name, a single-operand `all`/`any`).
    private static string FormatSafeIfCondition(JsonObject cond, string keyword) {
        if (!IsPredicateSafeForGateSugar(node: cond)) {
            throw new InvalidOperationException(message: $"cannot decompile this '{keyword}' effect to .puck source: its condition (\"{cond["$type"]}\") has no DSL spelling that reparses back to the same predicate — author or edit this rule as JSON instead");
        }

        return FormatPredicate(node: cond);
    }
    // An `else` branch holding exactly one nested `if` prints as `else if`, the same shape the parser stores for
    // both an authored `else if` and an authored `else { if ... }` — either recompiles to the identical JSON, so
    // recursing here rather than always opening a fresh `else { }` block is purely a readability choice.
    private static void AppendIfStatement(StringBuilder sb, JsonObject obj, int indentLevel, string keyword) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var condition = ((obj["condition"] is JsonObject cond)
            ? FormatSafeIfCondition(
                cond: cond,
                keyword: keyword
            )
            : ""
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}{keyword} {condition} {{"
        );
        if (obj["then"] is JsonArray thenArr) {
            foreach (var e in thenArr) {
                AppendEffectStatement(
                    effectNode: e,
                    indentLevel: (indentLevel + 1),
                    sb: sb
                );
            }
        }
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );

        if (obj["else"] is not JsonArray elseArr) {
            return;
        }
        if (
            (elseArr.Count == 1) &&
            (elseArr[0] is JsonObject nestedIf) &&
            (nestedIf["$type"]?.ToString() == "if")
        ) {
            AppendIfStatement(
                indentLevel: indentLevel,
                keyword: "else if",
                obj: nestedIf,
                sb: sb
            );
            return;
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}else {{"
        );
        foreach (var e in elseArr) {
            AppendEffectStatement(
                effectNode: e,
                indentLevel: (indentLevel + 1),
                sb: sb
            );
        }
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
}
