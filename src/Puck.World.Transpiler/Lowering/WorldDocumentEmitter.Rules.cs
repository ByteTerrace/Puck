using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler;

namespace Puck.World.Transpiler.Lowering;

// `rule "name" { }` (§2, §3): gate/local/decision/option/interrupt/onNoChoice, and every effect statement
// (set/add/push/countdown/remove/schedule/transform/transaction, plus a bare call for `generate(...)` and any
// Puck.World.Schema extension arm). Every operand-bearing node here (ComparisonPredicateNode.LeftText/RightText,
// LocalStatementNode.ExpressionText, RhsOperandNode.Text, ScoreStatementNode.Text) is raw source text the parser
// already ran through ExpressionSpelling.TryParse for PUCK002 validation; this stage is the only one that inspects
// the resulting instruction list to classify compareState/compareValue and value/fromState+fromKey/expression, and
// it writes that text back VERBATIM (never reprinted through ExpressionSpelling.Print) so a ExpressionProgram-typed
// field's wire spelling round-trips exactly through ExpressionProgramJsonConverter's own verbatim-Text convention.
public static partial class WorldDocumentEmitter {
    private static void LowerRuleBlock(RuleBlockNode rule, JsonObject parent, DocumentScope scope, RuleScopeContext? scopeContext = null) {
        if (parent["rules"] is not JsonArray rulesArr) {
            rulesArr = [];
            parent["rules"] = rulesArr;
        }

        var ruleIdx = rulesArr.Count;
        var rulePointer = $"{scope.CurrentPointer}/rules/{ruleIdx}";

        scope.SourceMap?.Register(
            jsonPointer: rulePointer,
            span: rule.Span
        );
        var oldPointer = scope.CurrentPointer;

        scope.CurrentPointer = rulePointer;

        var written = (DocumentLowering.ResolveRuleName(
            rule: rule,
            scope: scope
        ) ?? string.Empty);

        // A bare identifier in the name position that is also a bound template parameter never reached Rename, so
        // the rule would take the parameter's own spelling and every instantiation would mint the same name.
        if (
            (rule.NameExpression is null) &&
            IsBoundTemplateParameter(
            name: written,
            scope: scope
        )
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.RuleNameNotLiteral,
                message: $"Rule name '{written}' is template parameter '{written}', so every instantiation would mint the same name — write an interpolated name such as rule $\"{written}-{{{written}}}\" {{ }}, or move the rule to the template's top level where the parameter substitutes.",
                span: rule.Span
            );
        }

        var ruleName = (string.IsNullOrEmpty(value: scopeContext?.Prefix)
            ? written
            : (string.IsNullOrEmpty(value: written) ? scopeContext.Prefix : $"{scopeContext.Prefix}_{written}"));

        var obj = new JsonObject { ["name"] = ruleName };
        var prevRule = (scope.Annotations.TryGetValue(key: "CurrentRule", value: out var pr) ? pr : null);

        scope.Annotations["CurrentRule"] = obj;

        var effects = new JsonArray();
        JsonArray? locals = null;

        if (scopeContext?.Locals is { Count: > 0 } parentLocals) {
            locals = [];
            foreach (var l in parentLocals) {
                locals.AppendNode(item: l.DeepClone());
            }
        }

        foreach (var stmt in rule.Statements) {
            switch (stmt) {
                case WhenStatementNode when1:
                    var ruleGate = LowerPredicate(
                        node: when1.Predicate,
                        ruleObj: obj,
                        scope: scope
                    );

                    scope.SourceMap?.Register(
                        jsonPointer: $"{rulePointer}/gate",
                        span: when1.Span
                    );
                    obj["gate"] = CombineGates(existing: (obj["gate"] as JsonObject), newGate: ruleGate);
                    break;
                case LocalStatementNode local:
                    locals ??= [];
                    scope.SourceMap?.Register(
                        jsonPointer: $"{rulePointer}/locals/{locals.Count}",
                        span: local.Span
                    );
                    locals.AppendNode(item: LowerLocal(local: local));
                    break;
                case DecisionBlockNode decision:
                    scope.SourceMap?.Register(
                        jsonPointer: $"{rulePointer}/decision",
                        span: decision.Span
                    );
                    obj["decision"] = LowerDecisionBlock(
                        decision: decision,
                        scope: scope
                    );
                    break;
                case PropertyNode prop:
                    var loweredProp = LowerExpression(
                        prop.Value,
                        scope,
                        prop.Name
                    );
                    DocumentLowering.AssignOrExtend(
                        obj,
                        prop.Name,
                        loweredProp
                    );
                    break;
                case EffectStatementNode or ExpressionStatementNode:
                    if (LowerEffectStatement(
                        pointer: $"{rulePointer}/effects/{effects.Count}",
                        ruleObj: obj,
                        scope: scope,
                        stmt: stmt
                    ) is { } effect) {
                        scope.SourceMap?.Register(
                            jsonPointer: $"{rulePointer}/effects/{effects.Count}",
                            span: stmt.Span
                        );
                        effects.AppendNode(item: effect);
                    }
                    break;
            }
        }

        if (scopeContext?.Gate is not null) {
            obj["gate"] = CombineGates(existing: ((JsonObject)scopeContext.Gate.DeepClone()), newGate: (obj["gate"] as JsonObject));
        }

        if (scopeContext?.Properties is { Count: > 0 } parentProps) {
            foreach (var (k, v) in parentProps) {
                if (!obj.ContainsKey(propertyName: k)) {
                    obj[k] = v?.DeepClone();
                } else if ((k == "zones") && (v is JsonArray scopeZones) && (obj["zones"] is JsonArray ruleZones)) {
                    var existingSet = new HashSet<string>(comparer: StringComparer.Ordinal);

                    foreach (var item in ruleZones) {
                        if (item?.ToString() is { } s) {
                            existingSet.Add(item: s);
                        }
                    }
                    foreach (var item in scopeZones) {
                        if ((item?.ToString() is { } s) && existingSet.Add(item: s)) {
                            ruleZones.AppendNode(item: item.DeepClone());
                        }
                    }
                }
            }
        }

        // An authored `effects:`/`locals:` property is the whole array; the statement-built one only fills in when
        // no property named it, so neither spelling silently erases the other.
        if (
            (effects.Count > 0) ||
            !obj.ContainsKey(propertyName: "effects")
        ) {
            obj["effects"] = effects;
        }
        if (locals is not null) {
            obj["locals"] = locals;
        }

        scope.Annotations["CurrentRule"] = prevRule;
        scope.CurrentPointer = oldPointer;
        rulesArr.AppendNode(item: obj);
    }
    private static JsonObject LowerLocal(LocalStatementNode local) => new() {
        ["name"] = local.Name,
        ["kind"] = local.Kind,
        ["expression"] = local.ExpressionText,
    };
    private static JsonObject LowerDecisionBlock(DecisionBlockNode decision, DocumentScope scope) {
        var decisionPointer = $"{scope.CurrentPointer}/decision";
        var obj = new JsonObject();
        var options = new JsonArray();
        decimal? periodSeconds = null;
        var mode = "HighestScore";
        var sawMode = false;
        var scoreKind = "Fixed";
        var sawScoreKind = false;
        var commitmentSeconds = 0M;
        var sawCommitmentSeconds = false;
        var incumbentBonus = 0M;
        var sawIncumbentBonus = false;
        var seed = 0L;
        var sawSeed = false;

        foreach (var stmt in decision.Statements) {
            switch (stmt) {
                case PropertyNode { Name: "periodSeconds" } p:
                    periodSeconds = ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    ));
                    break;
                case PropertyNode { Name: "mode" } p:
                    mode = (LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    )?.ToString() ?? mode);
                    sawMode = true;
                    break;
                case PropertyNode { Name: "scoreKind" } p:
                    scoreKind = (LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    )?.ToString() ?? scoreKind);
                    sawScoreKind = true;
                    break;
                case PropertyNode { Name: "commitmentSeconds" } p:
                    commitmentSeconds = ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    ));
                    sawCommitmentSeconds = true;
                    break;
                case PropertyNode { Name: "incumbentBonus" } p:
                    incumbentBonus = ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    ));
                    sawIncumbentBonus = true;
                    break;
                case PropertyNode { Name: "seed" } p:
                    seed = ((long)ToDecimalNode(node: LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    )));
                    sawSeed = true;
                    break;
                case InterruptStatementNode interrupt:
                    scope.SourceMap?.Register(
                        jsonPointer: $"{decisionPointer}/interrupt",
                        span: interrupt.Span
                    );
                    obj["interrupt"] = LowerPredicate(
                        node: interrupt.Predicate,
                        scope: scope
                    );
                    break;
                case PropertyNode { Name: "interrupt" } p:
                    obj["interrupt"] = LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    );
                    break;
                case OnNoChoiceBlockNode onNoChoice: {
                        var arr = new JsonArray();

                        scope.SourceMap?.Register(
                            jsonPointer: $"{decisionPointer}/onNoChoice",
                            span: onNoChoice.Span
                        );

                        foreach (var effect in onNoChoice.Effects) {
                            if (LowerEffectStatement(
                                pointer: $"{decisionPointer}/onNoChoice/{arr.Count}",
                                scope: scope,
                                stmt: effect
                            ) is { } lowered) {
                                scope.SourceMap?.Register(
                                    jsonPointer: $"{decisionPointer}/onNoChoice/{arr.Count}",
                                    span: effect.Span
                                );
                                arr.AppendNode(item: lowered);
                            }
                        }
                        obj["onNoChoice"] = arr;
                        break;
                    }
                case OptionBlockNode option:
                    scope.SourceMap?.Register(
                        jsonPointer: $"{decisionPointer}/options/{options.Count}",
                        span: option.Span
                    );
                    options.AppendNode(item: LowerOptionBlock(
                        option: option,
                        pointer: $"{decisionPointer}/options/{options.Count}",
                        scope: scope
                    ));
                    break;
            }
        }

        if (periodSeconds is { } periodValue) {
            obj["periodSeconds"] = periodValue;
        }
        if (
            sawMode ||
            !string.Equals(
            a: mode,
            b: "HighestScore",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            obj["mode"] = mode;
        }
        if (
            sawScoreKind ||
            !string.Equals(
            a: scoreKind,
            b: "Fixed",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            obj["scoreKind"] = scoreKind;
        }
        if (
            sawCommitmentSeconds ||
            (commitmentSeconds != 0)
        ) {
            obj["commitmentSeconds"] = commitmentSeconds;
        }
        if (
            sawIncumbentBonus ||
            (incumbentBonus != 0)
        ) {
            obj["incumbentBonus"] = incumbentBonus;
        }
        if (
            sawSeed ||
            (seed != 0)
        ) {
            obj["seed"] = seed;
        }
        obj["options"] = options;
        return obj;
    }
    private static JsonObject LowerOptionBlock(OptionBlockNode option, DocumentScope scope, string pointer) {
        var obj = new JsonObject { ["name"] = option.Name };
        var effects = new JsonArray();

        foreach (var stmt in option.Statements) {
            switch (stmt) {
                case WhenStatementNode when1:
                    obj["gate"] = LowerPredicate(
                        node: when1.Predicate,
                        scope: scope
                    );
                    break;
                case ScoreStatementNode score:
                    obj["score"] = score.Text;
                    break;
                case PropertyNode p:
                    obj[p.Name] = LowerExpression(
                        p.Value,
                        scope,
                        p.Name
                    );
                    break;
                case EffectStatementNode or ExpressionStatementNode:
                    if (LowerEffectStatement(
                        pointer: $"{pointer}/effects/{effects.Count}",
                        scope: scope,
                        stmt: stmt
                    ) is { } effect) {
                        scope.SourceMap?.Register(
                            jsonPointer: $"{pointer}/effects/{effects.Count}",
                            span: stmt.Span
                        );
                        effects.AppendNode(item: effect);
                    }
                    break;
            }
        }

        obj["effects"] = effects;
        return obj;
    }
    private static decimal ToDecimalNode(JsonNode? node) => node switch {
        JsonValue v when v.TryGetValue<decimal>(value: out var d) => d,
        JsonValue v when v.TryGetValue<long>(value: out var l) => l,
        JsonValue v when v.TryGetValue<double>(value: out var db) => ((decimal)db),
        _ => 0m,
    };
    // ---- Gate lowering (§1) --------------------------------------------------------------------------------

    private static JsonObject LowerPredicate(PredicateNode node, DocumentScope scope, JsonObject? ruleObj = null) => node switch {
        ComparisonPredicateNode cmp => LowerComparison(
            cmp: cmp,
            ruleObj: ruleObj,
            scope: scope
        ),
        AndPredicateNode and => LowerPredicateList(
            "all",
            "predicates",
            and.Operands,
            scope,
            ruleObj
        ),
        OrPredicateNode or => LowerPredicateList(
            "any",
            "predicates",
            or.Operands,
            scope,
            ruleObj
        ),
        NotPredicateNode not => new JsonObject {
            ["$type"] = "not",
            ["predicate"] = LowerPredicate(
                node: not.Operand,
                ruleObj: ruleObj,
                scope: scope
            ),
        },
        CallPredicateNode call => RefuseCallGate(
            call: call,
            scope: scope
        ),
        _ => throw new InvalidOperationException(message: $"unrecognized predicate node '{node.GetType()}'"),
    };
    // A world gate is a comparison, or and/or/not over comparisons - there is no arm for a named test. The vacuous
    // `all` keeps the shape well-formed for whatever else the emitter is midway through building; the reported error
    // is what stops the compile.
    private static JsonObject RefuseCallGate(CallPredicateNode call, DocumentScope scope) {
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnsupportedCallGate,
            message: $"'{call.Call.Name}(...)' is not a puck.world.definition.v1 gate - a world gate compares two operands",
            span: call.Span
        );

        return new JsonObject { ["$type"] = "all", ["predicates"] = new JsonArray() };
    }
    private static JsonObject LowerPredicateList(string discriminator, string propertyName, IReadOnlyList<PredicateNode> operands, DocumentScope scope, JsonObject? ruleObj = null) {
        var arr = new JsonArray();

        foreach (var operand in operands) {
            arr.AppendNode(item: LowerPredicate(
                node: operand,
                ruleObj: ruleObj,
                scope: scope
            ));
        }
        return new JsonObject { ["$type"] = discriminator, [propertyName] = arr };
    }
    // Substitutes any bare identifier naming a `let` binding or a loop local with the literal it stands for. A
    // token carrying a state-read sigil ($, a backtick name, a `.key` or `[index]` tail) is left alone: those are
    // reads of live state, never compile-time values.
    private static string ResolveOperandConstants(string text, DocumentScope scope) {
        if (
            string.IsNullOrEmpty(value: text) ||
            ((scope.Constants.Count == 0) && (scope.Locals.Count == 0))
        ) {
            return text;
        }

        return BareIdentifier.Replace(
            input: text,
            evaluator: match => {
                var name = match.Value;

                if (scope.TryLowerBinding(
                    name,
                    out var bound
                )) {
                    return (DocumentLowering.KeyText(node: bound) ?? name);
                }

                return name;
            }
        );
    }

    private static readonly System.Text.RegularExpressions.Regex BareIdentifier =
        new(
        options: System.Text.RegularExpressions.RegexOptions.Compiled,
        pattern: @"(?<![\w$`.\[])[A-Za-z_][A-Za-z0-9_]*(?![\w(\[:`])"
    );

    private static JsonObject LowerComparison(ComparisonPredicateNode cmp, DocumentScope scope, JsonObject? ruleObj = null) {
        // A gate operand is resolved against the `let` bindings and loop locals in scope BEFORE it is classified.
        // Without this a bare name can only ever read as a state row, so a bound named `wellFloor` silently became
        // a read of a row by that name and the author had to write the number out with a comment naming what it
        // meant. A binding shadows a state row of the same name, which is the same precedence the cartridge
        // vocabulary's operands already use.
        var leftText = cmp.LeftText;
        var rightText = cmp.RightText;

        leftText = ResolveDerivedStateInText(text: leftText, scope: scope);
        rightText = ResolveDerivedStateInText(text: rightText, scope: scope);

        leftText = ResolveCollectionOperationsInText(scope: scope, text: leftText);
        rightText = ResolveCollectionOperationsInText(scope: scope, text: rightText);

        leftText = ResolveRecordFieldAccessesInText(scope: scope, text: leftText);
        rightText = ResolveRecordFieldAccessesInText(scope: scope, text: rightText);

        leftText = ResolveEnumsInText(scope: scope, text: leftText);
        rightText = ResolveEnumsInText(scope: scope, text: rightText);

        leftText = ResolveOperandConstants(scope: scope, text: leftText);
        rightText = ResolveOperandConstants(scope: scope, text: rightText);

        leftText = ResolveEmbeddedLiteralsInText(text: leftText, expectedSpace: null, scope: scope, span: cmp.Span);
        rightText = ResolveEmbeddedLiteralsInText(text: rightText, expectedSpace: null, scope: scope, span: cmp.Span);

        leftText = ResolveFamilyReferencesInText(scope: scope, text: leftText);
        rightText = ResolveFamilyReferencesInText(scope: scope, text: rightText);

        if (!PuckDslVocabulary.TryParseComparator(
            cmp.Comparator,
            out var parsedComparison
        )) {
            throw new InvalidOperationException(message: $"'{cmp.Comparator}' is not a DSL comparison operator");
        }
        var comparison = PuckDslVocabulary.NameOf(comparison: parsedComparison);

        // An explicit `: Kind`/`as Kind` suffix always forces compareValue, even for two simple single-token
        // operands — CompareState carries no Kind field at all, so an authored kind annotation on it is meaningless;
        // forcing compareValue is the only reading that keeps the annotation.
        if (cmp.Kind is not null) {
            return new JsonObject {
                ["$type"] = "compareValue",
                ["comparison"] = comparison,
                ["kind"] = cmp.Kind,
                ["left"] = leftText,
                ["right"] = rightText,
            };
        }

        switch (ClassifyBareComparison(
            leftText: leftText,
            leftToken: out var leftToken,
            rightText: rightText,
            rightToken: out var rightToken
        )) {
            case BareComparisonShape.StateAgainstConstant:
                return NewCompareState(
                    (((InstructionPayload.State)leftToken!.Payload!)).Name,
                    (((InstructionPayload.State)leftToken!.Payload!)).Key,
                    comparison,
                    value: (((InstructionPayload.Constant)rightToken!.Payload!)).Value
                );
            case BareComparisonShape.StateAgainstState:
                return NewCompareState(
                    (((InstructionPayload.State)leftToken!.Payload!)).Name,
                    (((InstructionPayload.State)leftToken!.Payload!)).Key,
                    comparison,
                    comparandState: (((InstructionPayload.State)rightToken!.Payload!)).Name,
                    comparandKey: (((InstructionPayload.State)rightToken!.Payload!)).Key
                );
            case BareComparisonShape.ConstantAgainstState:
                // Operands swapped so the live State read is always the CompareState subject — the decompiler never
                // emits this constant-first spelling, only the compiler tolerates it.
                return NewCompareState(
                    (((InstructionPayload.State)rightToken!.Payload!)).Name,
                    (((InstructionPayload.State)rightToken!.Payload!)).Key,
                    PuckDslVocabulary.NameOf(comparison: PuckDslVocabulary.Flip(comparison: parsedComparison)),
                    value: (((InstructionPayload.Constant)leftToken!.Payload!)).Value
                );
            default:
                return new JsonObject {
                    ["$type"] = "compareValue",
                    ["comparison"] = comparison,
                    ["kind"] = "Fixed",
                    ["left"] = leftText,
                    ["right"] = rightText,
                };
        }
    }

    /// <summary>Which node an unannotated <c>left cmp right</c> comparison lowers to.</summary>
    internal enum BareComparisonShape {
        /// <summary>A <c>compareValue</c> node carrying both operand texts verbatim.</summary>
        CompareValue,

        /// <summary>A <c>compareState</c> node reading the left operand's row against a literal.</summary>
        StateAgainstConstant,

        /// <summary>A <c>compareState</c> node reading the left operand's row against the right operand's row.</summary>
        StateAgainstState,

        /// <summary>A <c>compareState</c> node built from the FLIPPED comparison, the right operand's row as
        /// subject and the left operand's literal as comparand.</summary>
        ConstantAgainstState,
    }

    /// <summary>Classifies what a comparison with no <c>: Kind</c> annotation lowers to.</summary>
    /// <remarks>The decompiler's <c>FormatCompareValue</c> calls this to decide whether a <c>compareValue</c>
    /// node's bare <c>left cmp right</c> text would re-lower to <c>compareState</c>, which is when it must print
    /// the kind annotation even for the default kind.</remarks>
    /// <param name="leftText">The left operand's verbatim source text.</param>
    /// <param name="rightText">The right operand's verbatim source text.</param>
    /// <param name="leftToken">The left operand's single token, when it has exactly one; otherwise
    /// <see langword="null"/>.</param>
    /// <param name="rightToken">The right operand's single token, when it has exactly one; otherwise
    /// <see langword="null"/>.</param>
    /// <returns>The shape the comparison lowers to.</returns>
    internal static BareComparisonShape ClassifyBareComparison(string leftText, string rightText, out Instruction? leftToken, out Instruction? rightToken) {
        ExpressionSpelling.TryParse(
            error: out _,
            program: out var leftProgram,
            text: leftText
        );
        ExpressionSpelling.TryParse(
            error: out _,
            program: out var rightProgram,
            text: rightText
        );
        leftToken = ((leftProgram.Instructions.Count == 1)
            ? leftProgram.Instructions[0]
            : null
        );
        rightToken = ((rightProgram.Instructions.Count == 1)
            ? rightProgram.Instructions[0]
            : null
        );

        return (leftToken?.Payload, rightToken?.Payload) switch {
            (InstructionPayload.State, InstructionPayload.Constant) => BareComparisonShape.StateAgainstConstant,
            (InstructionPayload.State, InstructionPayload.State) => BareComparisonShape.StateAgainstState,
            (InstructionPayload.Constant, InstructionPayload.State) => BareComparisonShape.ConstantAgainstState,
            _ => BareComparisonShape.CompareValue,
        };
    }

    private static JsonObject NewCompareState(string state, string? key, string comparison, decimal? value = null, string? comparandState = null, string? comparandKey = null) {
        var obj = new JsonObject { ["$type"] = "compareState", ["state"] = state, ["comparison"] = comparison };

        if (key is not null) {
            obj["key"] = key;
        }
        if (value is { } v) {
            obj["value"] = v;
        }
        if (comparandState is not null) {
            obj["comparandState"] = comparandState;
        }
        if (comparandKey is not null) {
            obj["comparandKey"] = comparandKey;
        }
        return obj;
    }
    // ---- Effect statement lowering (§2) ---------------------------------------------------------------------

    // Null means "refused, and the refusal is already reported": a caller drops it rather than writing a null into
    // an effects array.
    private static JsonNode? LowerEffectStatement(StatementNode stmt, DocumentScope scope, string pointer, JsonObject? ruleObj = null) => stmt switch {
        SetCellStatementNode s => LowerCellEffect(
            discriminator: "setState",
            target: s.Target,
            rhs: s.Rhs,
            allowText: true,
            ruleObj: ruleObj,
            scope: scope
        ),
        AddCellStatementNode a => LowerCellEffect(
            discriminator: "addState",
            target: a.Target,
            rhs: a.Rhs,
            allowText: false,
            ruleObj: ruleObj,
            scope: scope
        ),
        PushStatementNode push => LowerPush(push: push, ruleObj: ruleObj, scope: scope),
        CountdownStatementNode countdown => LowerRowOnlyEffect(
            discriminator: "countdownState",
            ruleObj: ruleObj,
            scope: scope,
            target: countdown.Target
        ),
        RemoveCellStatementNode remove => LowerRowOnlyEffect(
            discriminator: "removeStateCell",
            ruleObj: ruleObj,
            scope: scope,
            target: remove.Target
        ),
        ScheduleStatementNode schedule => LowerSchedule(ruleObj: ruleObj, schedule: schedule, scope: scope),
        TransformStatementNode transform => LowerTransformStatement(scope: scope, transform: transform),
        DrawStatementNode draw => new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = new JsonObject {
                ["$type"] = "transfer",
                ["from"] = ResolveFamilyReferencesInText(text: draw.From, scope: scope),
                ["to"] = ResolveFamilyReferencesInText(text: draw.To, scope: scope),
                ["selector"] = "First",
            },
        },
        DealStatementNode deal => new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = new JsonObject {
                ["$type"] = "transfer",
                ["from"] = ResolveFamilyReferencesInText(text: deal.From, scope: scope),
                ["to"] = ResolveFamilyReferencesInText(text: deal.To, scope: scope),
                ["selector"] = "First",
                ["count"] = deal.Count,
            },
        },
        ShuffleStatementNode shuffle => new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = new JsonObject {
                ["$type"] = "shuffle",
                ["row"] = ResolveFamilyReferencesInText(text: shuffle.Row, scope: scope),
                ["draw"] = ((shuffle.Draw is not null) ? ResolveFamilyReferencesInText(text: shuffle.Draw, scope: scope) : null),
            },
        },
        TransactionStatementNode transaction => LowerTransaction(
            pointer: pointer,
            ruleObj: ruleObj,
            scope: scope,
            transaction: transaction
        ),
        ExpressionStatementNode { Expression: CallExpressionNode call } => LowerExpression(
            call,
            scope
        )!,
        CompoundAssignStatementNode compound => RefuseCompoundAssignment(
            compound: compound,
            scope: scope
        ),
        IfStatementNode ifStmt => LowerIf(
            ifStmt: ifStmt,
            pointer: pointer,
            ruleObj: ruleObj,
            scope: scope
        ),
        RepeatStatementNode => RefuseControlFlow(
            alternative: "a rule already runs once per matching subject - use 'forEach'",
            keyword: "repeat",
            scope: scope,
            stmt: stmt
        ),
        BreakStatementNode => RefuseControlFlow(
            alternative: "there is no loop in a puck.world.definition.v1 rule to leave",
            keyword: "break",
            scope: scope,
            stmt: stmt
        ),
        _ => throw new InvalidOperationException(message: $"unrecognized effect statement '{stmt.GetType()}'"),
    };
    // puck.world.definition.v1 carries two assignment effects, setState and addState; every other operator belongs in the
    // expression on the right, where the state engine evaluates it.
    private static JsonNode? RefuseCompoundAssignment(CompoundAssignStatementNode compound, DocumentScope scope) {
        var target = ((compound.Target.Key is null)
            ? compound.Target.Name
            : $"{compound.Target.Name}[{compound.Target.Key}]"
        );

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnsupportedAssignmentOperator,
            message: $"puck.world.definition.v1 has no '{compound.Operator}=' effect - write it as '{target} = {target} {compound.Operator} ...'",
            span: compound.Span
        );

        return null;
    }
    // `if Gate { ... } [else { ... }]` lowers to ActionEffect.If, reusing the same predicate lowering `when` uses
    // for its own condition. An `else if` chain is already one nested IfStatementNode per level (the parser's own
    // shape), so recursing through LowerEffectStatement nests it the same way with no separate case. `repeat` and
    // `break` still have nothing to lower onto in a straight-line rule body.
    private static JsonObject LowerIf(IfStatementNode ifStmt, DocumentScope scope, string pointer, JsonObject? ruleObj = null) {
        var thenArr = new JsonArray();

        foreach (var thenStmt in ifStmt.Then) {
            if (LowerEffectStatement(
                pointer: $"{pointer}/then/{thenArr.Count}",
                ruleObj: ruleObj,
                scope: scope,
                stmt: thenStmt
            ) is { } lowered) {
                scope.SourceMap?.Register(
                    jsonPointer: $"{pointer}/then/{thenArr.Count}",
                    span: thenStmt.Span
                );
                thenArr.AppendNode(item: lowered);
            }
        }

        var obj = new JsonObject {
            ["$type"] = "if",
            ["condition"] = LowerPredicate(
                node: ifStmt.Condition,
                ruleObj: ruleObj,
                scope: scope
            ),
            ["then"] = thenArr,
        };

        if (ifStmt.Else is { } elseStatements) {
            var elseArr = new JsonArray();

            foreach (var elseStmt in elseStatements) {
                if (LowerEffectStatement(
                    pointer: $"{pointer}/else/{elseArr.Count}",
                    ruleObj: ruleObj,
                    scope: scope,
                    stmt: elseStmt
                ) is { } lowered) {
                    scope.SourceMap?.Register(
                        jsonPointer: $"{pointer}/else/{elseArr.Count}",
                        span: elseStmt.Span
                    );
                    elseArr.AppendNode(item: lowered);
                }
            }
            obj["else"] = elseArr;
        }
        return obj;
    }
    // puck.world.definition.v1 rule effects are a straight line: the rule's own gate decides whether the whole body runs,
    // and there is no branch or loop for one to lower onto. The language still parses control flow, because another
    // document vocabulary (a cartridge's rules) carries it natively.
    private static JsonNode? RefuseControlFlow(StatementNode stmt, string keyword, string alternative, DocumentScope scope) {
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnsupportedControlFlow,
            message: $"a puck.world.definition.v1 rule body is straight-line, so '{keyword}' has nothing to lower onto - {alternative}",
            span: stmt.Span
        );

        return null;
    }
    private static JsonObject LowerCellEffect(string discriminator, RowRefNode target, RhsNode rhs, bool allowText, DocumentScope scope, JsonObject? ruleObj = null) {
        var targetName = target.Name;
        var targetKey = target.Key;

        var fullTarget = ((targetKey is null) ? targetName : $"{targetName}[{targetKey}]");
        var originalTarget = fullTarget;

        fullTarget = ResolveDerivedStateInText(text: fullTarget, scope: scope);
        fullTarget = ResolveRecordFieldAccessesInText(scope: scope, text: fullTarget);
        var resolved = ResolveFamilyReferencesInText(scope: scope, text: fullTarget);

        // A live row position stays one spelling, whatever it resolved from: the rule compiler selects the row from
        // it and the remainder, if any, is the cell key inside that row.
        if (TryLiveRowHead(head: out var liveHead, key: out var liveKey, scope: scope, text: resolved)) {
            targetName = liveHead;
            targetKey = liveKey;
        } else if (resolved != originalTarget) {
            var bracketIdx = resolved.IndexOf(value: '[');

            if (bracketIdx < 0) {
                targetName = resolved;
                targetKey = null;
            } else {
                targetName = resolved[..bracketIdx];

                var closeIdx = resolved.LastIndexOf(value: ']');

                targetKey = ((closeIdx > 0) ? resolved[(bracketIdx + 1)..closeIdx] : resolved[(bracketIdx + 1)..]);
            }
        }

        var obj = new JsonObject { ["$type"] = discriminator, ["state"] = targetName };

        if (targetKey is not null) {
            obj["key"] = targetKey;
        }

        var rootObj = ((scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rObj) && (rObj is JsonObject ro)) ? ro : null);
        var targetRowSpace = FindRowSpace(rootObj: rootObj, rowName: targetName);
        var isVectorRow = (targetRowSpace is not null);

        if (isVectorRow && (discriminator != "setState")) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationModifierNotAdmitted,
                message: $"Vector row '{targetName}' admits only assignment ('=' / setState), not '{discriminator}'.",
                span: target.Span
            );
        }

        ApplyRhs(
            allowText: allowText,
            isVectorRow: isVectorRow,
            obj: obj,
            rhs: rhs,
            ruleObj: ruleObj,
            scope: scope,
            targetRowSpace: targetRowSpace
        );
        return obj;
    }
    // `allowText` gates whether an RhsTextNode's Text lands on the object — AddState carries no Text field at all;
    // a string RHS reaching it is already PUCK009 from the parser, and best-effort lowering here emits nothing for
    // the field the destination cannot carry rather than shaping JSON that violates the target's own record.
    private static void ApplyRhs(JsonObject obj, RhsNode rhs, bool allowText, bool isVectorRow, string? targetRowSpace, DocumentScope scope, JsonObject? ruleObj = null) {
        switch (rhs) {
            case RhsTextNode text:
                if (isVectorRow) {
                    if (TryResolveEmbeddedText(text.Text, targetRowSpace, scope, text.Span, out var b64)) {
                        obj["vector"] = b64;
                    }
                } else if (allowText) {
                    obj["text"] = text.Text;
                }
                break;
            case RhsSecondsNode seconds:
                if (isVectorRow) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.VectorLiteralMisplaced,
                        message: "Seconds literal is not admitted on Vector row.",
                        span: seconds.Span
                    );
                } else {
                    obj["valueSeconds"] = seconds.Seconds;
                }
                break;
            case RhsOperandNode operand:
                ApplyOperandRhs(
                    isVectorRow: isVectorRow,
                    obj: obj,
                    ruleObj: ruleObj,
                    scope: scope,
                    span: operand.Span,
                    targetRowSpace: targetRowSpace,
                    text: operand.Text
                );
                break;
        }
    }
    // A single unkeyed state read (`row`, no brackets) lowers to a bare `fromState`; a single KEYED read
    // (`row[key]`) always lowers to a verbatim `expression`, never decomposed into `fromState`+`fromKey`, even
    // though both spellings are runtime-equivalent — confirmed against `first-witness` (puck.world.json), which
    // ships `{"$type":"setState","expression":"houndIdentity[$each]",...}` for exactly this RHS shape (§2.3).
    // `fromState`+`fromKey` together stay reachable through call-form for an author who wants that exact wire
    // shape.
    private static void ApplyOperandRhs(JsonObject obj, string text, bool isVectorRow = false, string? targetRowSpace = null, DocumentScope? scope = null, SourceSpan span = default, JsonObject? ruleObj = null) {
        if (scope is not null) {
            text = ResolveDerivedStateInText(text: text, scope: scope);
            text = ResolveCollectionOperationsInText(scope: scope, text: text);
            text = ResolveRecordFieldAccessesInText(scope: scope, text: text);
            text = ResolveEnumsInText(scope: scope, text: text);
            text = ResolveOperandConstants(scope: scope, text: text);
            text = ResolveFamilyReferencesInText(scope: scope, text: text);
        }

        if (isVectorRow && (scope is not null)) {
            if (ExpressionSpelling.TryParseVector(error: out var parseErr, text: text, token: out var vecOp)) {
                if (vecOp is VectorOperand.Embed emb) {
                    var sp = (emb.Space ?? targetRowSpace);

                    if (TryResolveEmbeddedText(emb.Text, sp, scope, span, out var b64)) {
                        obj["vector"] = b64;
                        return;
                    }
                    return;
                }
                if (vecOp is VectorOperand.Literal lit) {
                    var rootObj = ((scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rObj) && (rObj is JsonObject ro)) ? ro : null);
                    var spaceInfo = FindSpaceInfo(parent: rootObj, spaceName: targetRowSpace);

                    if (spaceInfo is not null) {
                        if (!StateVector.TryParseBase64Url(lit.Value, spaceInfo.Value.Dimensions, out _, out var vErr)) {
                            scope.Diagnostics.ReportError(
                                code: PuckDiagnosticCodes.VectorLiteralInvalid,
                                message: $"vector(...) literal is invalid: {vErr}",
                                span: span
                            );
                            return;
                        }
                    } else if (!StateVector.TryParseBase64Url(lit.Value, out _, out var vErr)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.VectorLiteralInvalid,
                            message: $"vector(...) literal is invalid: {vErr}",
                            span: span
                        );
                        return;
                    }
                    obj["vector"] = lit.Value;
                    return;
                }
            } else if (text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "vector(") || text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "embed(")) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorLiteralInvalid,
                    message: $"vector or embed literal is invalid: {parseErr}",
                    span: span
                );
                return;
            }
        } else if (!isVectorRow && (scope is not null)) {
            if (text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "embed(") || text.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "vector(")) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorLiteralMisplaced,
                    message: "embed or vector literal appears where no vector operand or value is admitted.",
                    span: span
                );
                return;
            }
        }

        ExpressionSpelling.TryParse(
            error: out _,
            program: out var parsed,
            text: text
        );
        if (
            (parsed.Instructions.Count == 1) &&
            (parsed.Instructions[0] is { Payload: InstructionPayload.Constant constant })
        ) {
            if (isVectorRow && (scope is not null)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorLiteralMisplaced,
                    message: "Constant value is not admitted on Vector row.",
                    span: span
                );
            } else {
                obj["value"] = constant.Value;
            }
        } else if (
            (parsed.Instructions.Count == 1) &&
            (parsed.Instructions[0] is { Payload: InstructionPayload.State { Key: null } state })
        ) {
            obj["fromState"] = state.Name;
        } else {
            obj["expression"] = text;
        }
    }
    private static JsonObject LowerPush(PushStatementNode push, DocumentScope scope, JsonObject? ruleObj = null) {
        var resolvedName = ResolveFamilyReferencesInText(text: push.RowName, scope: scope);
        var obj = new JsonObject { ["$type"] = "pushState", ["state"] = resolvedName };
        // PushState carries no Text/Key/ValueSeconds field; ApplyRhs's text/seconds arms would shape JSON it
        // cannot carry, so only the classified-operand arm applies here (the parser already refuses the others).
        if (push.Rhs is RhsOperandNode operand) {
            ApplyOperandRhs(
                obj: obj,
                ruleObj: ruleObj,
                scope: scope,
                text: operand.Text
            );
        }
        return obj;
    }
    private static JsonObject LowerRowOnlyEffect(string discriminator, RowRefNode target, DocumentScope scope, JsonObject? ruleObj = null) {
        var fullTarget = ((target.Key is null) ? target.Name : $"{target.Name}[{target.Key}]");
        var resolved = ResolveFamilyReferencesInText(scope: scope, text: fullTarget);
        var targetName = resolved;
        string? targetKey = null;

        if (TryLiveRowHead(head: out var liveHead, key: out var liveKey, scope: scope, text: resolved)) {
            targetName = liveHead;
            targetKey = liveKey;
        } else if (resolved.IndexOf(value: '[') is var bracketIdx and >= 0) {
            targetName = resolved[..bracketIdx];

            var closeIdx = resolved.IndexOf(startIndex: (bracketIdx + 1), value: ']');

            targetKey = ((closeIdx > 0) ? resolved[(bracketIdx + 1)..closeIdx] : resolved[(bracketIdx + 1)..]);
        }

        var obj = new JsonObject { ["$type"] = discriminator, ["state"] = targetName };

        if (targetKey is not null) {
            obj["key"] = targetKey;
        }
        return obj;
    }
    private static JsonObject LowerSchedule(ScheduleStatementNode schedule, DocumentScope scope, JsonObject? ruleObj = null) {
        var fullTarget = ((schedule.Target.Key is null) ? schedule.Target.Name : $"{schedule.Target.Name}[{schedule.Target.Key}]");
        var resolved = ResolveFamilyReferencesInText(scope: scope, text: fullTarget);
        var targetName = resolved;
        string? targetKey = null;

        if (TryLiveRowHead(head: out var liveHead, key: out var liveKey, scope: scope, text: resolved)) {
            targetName = liveHead;
            targetKey = liveKey;
        } else if (resolved.IndexOf(value: '[') is var bracketIdx and >= 0) {
            targetName = resolved[..bracketIdx];

            var closeIdx = resolved.IndexOf(startIndex: (bracketIdx + 1), value: ']');

            targetKey = ((closeIdx > 0) ? resolved[(bracketIdx + 1)..closeIdx] : resolved[(bracketIdx + 1)..]);
        }

        var obj = new JsonObject {
            ["$type"] = "scheduleState",
            ["state"] = targetName,
            ["delaySeconds"] = schedule.DelaySeconds,
        };

        if (targetKey is not null) {
            obj["key"] = targetKey;
        }
        return obj;
    }
    private static JsonObject LowerTransaction(TransactionStatementNode transaction, DocumentScope scope, string pointer, JsonObject? ruleObj = null) {
        var mainEffects = new JsonArray();

        foreach (var stmt in transaction.MainEffects) {
            if (LowerEffectStatement(
                pointer: $"{pointer}/effects/{mainEffects.Count}",
                ruleObj: ruleObj,
                scope: scope,
                stmt: stmt
            ) is { } lowered) {
                scope.SourceMap?.Register(
                    jsonPointer: $"{pointer}/effects/{mainEffects.Count}",
                    span: stmt.Span
                );
                mainEffects.AppendNode(item: lowered);
            }
        }

        var obj = new JsonObject { ["$type"] = "transaction", ["effects"] = mainEffects };

        if (transaction.OnFailureEffects is { } onFailure) {
            var onFailureArr = new JsonArray();

            // The `onFailure` block's own node is the array: its refusals name the array, never one element.
            scope.SourceMap?.Register(
                jsonPointer: $"{pointer}/onFailure",
                span: (transaction.OnFailureSpan ?? transaction.Span)
            );

            foreach (var stmt in onFailure) {
                if (LowerEffectStatement(
                    pointer: $"{pointer}/onFailure/{onFailureArr.Count}",
                    ruleObj: ruleObj,
                    scope: scope,
                    stmt: stmt
                ) is { } lowered) {
                    scope.SourceMap?.Register(
                        jsonPointer: $"{pointer}/onFailure/{onFailureArr.Count}",
                        span: stmt.Span
                    );
                    onFailureArr.AppendNode(item: lowered);
                }
            }
            obj["onFailure"] = onFailureArr;
        }
        return obj;
    }
    // A name is a template parameter's own identifier only where that parameter is live: a document may bind the
    // same spelling as an ordinary `let`, and a rule named after one of those is the author's literal choice.
    private static bool IsBoundTemplateParameter(string name, DocumentScope scope) {
        if (
            string.IsNullOrEmpty(value: name) ||
            !scope.Constants.ContainsKey(key: name)
        ) {
            return false;
        }

        foreach (var (_, template) in scope.Templates) {
            foreach (var parameter in template.Parameters) {
                if (string.Equals(
                    parameter.Name,
                    name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return true;
                }
            }
        }
        return false;
    }
}
