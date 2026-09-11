using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Ast;

namespace Puck.World.Transpiler.Lowering;

// `rule "name" { }` (§2, §3): gate/bind/decision/option/interrupt/onNoChoice, and every effect statement
// (set/add/push/countdown/remove/schedule/transform/transaction, plus a bare call for `generate(...)` and any
// Puck.World.Schema extension arm). Every operand-bearing node here (ComparisonPredicateNode.LeftText/RightText,
// BindStatementNode.ExpressionText, RhsOperandNode.Text, ScoreStatementNode.Text) is raw source text the parser
// already ran through ExpressionSpelling.TryParse for PUCK002 validation; this stage is the only one that inspects
// the resulting ValueToken list to classify compareState/compareValue and value/fromState+fromKey/expression, and
// it writes that text back VERBATIM (never reprinted through ExpressionSpelling.Print) so a ValueExpression-typed
// field's wire spelling round-trips exactly through ValueExpressionJsonConverter's own verbatim-Text convention.
public static partial class WorldDocumentEmitter {
    private static void LowerRuleBlock(RuleBlockNode rule, JsonObject parent, EvaluationScope scope) {
        if (parent["rules"] is not JsonArray rulesArr) {
            rulesArr = [];
            parent["rules"] = rulesArr;
        }

        var ruleIdx = rulesArr.Count;
        var rulePointer = $"{scope.CurrentPointer}/rules/{ruleIdx}";
        scope.SourceMap?.Register(rulePointer, rule.Span);
        var oldPointer = scope.CurrentPointer;
        scope.CurrentPointer = rulePointer;

        var obj = new JsonObject { ["name"] = rule.Name };
        var effects = new JsonArray();
        JsonArray? bindings = null;

        foreach (var stmt in rule.Statements) {
            switch (stmt) {
                case WhenStatementNode when1:
                    obj["gate"] = LowerPredicate(when1.Predicate);
                    break;
                case BindStatementNode bind:
                    bindings ??= [];
                    bindings.AppendNode(LowerBind(bind));
                    break;
                case DecisionBlockNode decision:
                    obj["decision"] = LowerDecisionBlock(decision, scope);
                    break;
                case PropertyNode prop:
                    obj[prop.Name] = LowerExpression(prop.Value, scope, prop.Name);
                    break;
                case EffectStatementNode or ExpressionStatementNode:
                    effects.AppendNode(LowerEffectStatement(stmt, scope));
                    break;
            }
        }

        obj["effects"] = effects;
        if (bindings is not null) {
            obj["bindings"] = bindings;
        }

        scope.CurrentPointer = oldPointer;
        rulesArr.AppendNode(obj);
    }

    private static JsonObject LowerBind(BindStatementNode bind) => new() {
        ["name"] = bind.Name,
        ["kind"] = bind.Kind,
        ["expression"] = bind.ExpressionText,
    };

    private static JsonObject LowerDecisionBlock(DecisionBlockNode decision, EvaluationScope scope) {
        var obj = new JsonObject();
        var options = new JsonArray();
        decimal? periodSeconds = null;
        var mode = "HighestScore";
        var sawMode = false;
        var scoreKind = "Fixed";
        var sawScoreKind = false;
        decimal commitmentSeconds = 0;
        var sawCommitmentSeconds = false;
        decimal incumbentBonus = 0;
        var sawIncumbentBonus = false;
        var seed = 0L;
        var sawSeed = false;

        foreach (var stmt in decision.Statements) {
            switch (stmt) {
                case PropertyNode { Name: "periodSeconds" } p:
                    periodSeconds = ToDecimalNode(LowerExpression(p.Value, scope, p.Name));
                    break;
                case PropertyNode { Name: "mode" } p:
                    mode = LowerExpression(p.Value, scope, p.Name)?.ToString() ?? mode;
                    sawMode = true;
                    break;
                case PropertyNode { Name: "scoreKind" } p:
                    scoreKind = LowerExpression(p.Value, scope, p.Name)?.ToString() ?? scoreKind;
                    sawScoreKind = true;
                    break;
                case PropertyNode { Name: "commitmentSeconds" } p:
                    commitmentSeconds = ToDecimalNode(LowerExpression(p.Value, scope, p.Name));
                    sawCommitmentSeconds = true;
                    break;
                case PropertyNode { Name: "incumbentBonus" } p:
                    incumbentBonus = ToDecimalNode(LowerExpression(p.Value, scope, p.Name));
                    sawIncumbentBonus = true;
                    break;
                case PropertyNode { Name: "seed" } p:
                    seed = (long)ToDecimalNode(LowerExpression(p.Value, scope, p.Name));
                    sawSeed = true;
                    break;
                case InterruptStatementNode interrupt:
                    obj["interrupt"] = LowerPredicate(interrupt.Predicate);
                    break;
                case OnNoChoiceBlockNode onNoChoice: {
                    var arr = new JsonArray();
                    foreach (var effect in onNoChoice.Effects) {
                        arr.AppendNode(LowerEffectStatement(effect, scope));
                    }
                    obj["onNoChoice"] = arr;
                    break;
                }
                case OptionBlockNode option:
                    options.AppendNode(LowerOptionBlock(option, scope));
                    break;
            }
        }

        if (periodSeconds is { } periodValue) {
            obj["periodSeconds"] = periodValue;
        }
        if (sawMode || !string.Equals(mode, "HighestScore", StringComparison.Ordinal)) {
            obj["mode"] = mode;
        }
        if (sawScoreKind || !string.Equals(scoreKind, "Fixed", StringComparison.Ordinal)) {
            obj["scoreKind"] = scoreKind;
        }
        if (sawCommitmentSeconds || commitmentSeconds != 0) {
            obj["commitmentSeconds"] = commitmentSeconds;
        }
        if (sawIncumbentBonus || incumbentBonus != 0) {
            obj["incumbentBonus"] = incumbentBonus;
        }
        if (sawSeed || seed != 0) {
            obj["seed"] = seed;
        }
        obj["options"] = options;
        return obj;
    }

    private static JsonObject LowerOptionBlock(OptionBlockNode option, EvaluationScope scope) {
        var obj = new JsonObject { ["name"] = option.Name };
        var effects = new JsonArray();

        foreach (var stmt in option.Statements) {
            switch (stmt) {
                case WhenStatementNode when1:
                    obj["gate"] = LowerPredicate(when1.Predicate);
                    break;
                case ScoreStatementNode score:
                    obj["score"] = score.Text;
                    break;
                case PropertyNode { Name: "neighbors" } p:
                    obj["neighbors"] = LowerExpression(p.Value, scope, p.Name);
                    break;
                case EffectStatementNode or ExpressionStatementNode:
                    effects.AppendNode(LowerEffectStatement(stmt, scope));
                    break;
            }
        }

        obj["effects"] = effects;
        return obj;
    }

    private static decimal ToDecimalNode(JsonNode? node) => node switch {
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var db) => (decimal)db,
        _ => 0m,
    };

    // ---- Gate lowering (§1) --------------------------------------------------------------------------------

    private static JsonObject LowerPredicate(PredicateNode node) => node switch {
        ComparisonPredicateNode cmp => LowerComparison(cmp),
        AndPredicateNode and => LowerPredicateList("all", "predicates", and.Operands),
        OrPredicateNode or => LowerPredicateList("any", "predicates", or.Operands),
        NotPredicateNode not => new JsonObject { ["$type"] = "not", ["predicate"] = LowerPredicate(not.Operand) },
        _ => throw new InvalidOperationException($"unrecognized predicate node '{node.GetType()}'"),
    };

    private static JsonObject LowerPredicateList(string discriminator, string propertyName, IReadOnlyList<PredicateNode> operands) {
        var arr = new JsonArray();
        foreach (var operand in operands) {
            arr.AppendNode(LowerPredicate(operand));
        }
        return new JsonObject { ["$type"] = discriminator, [propertyName] = arr };
    }

    private static JsonObject LowerComparison(ComparisonPredicateNode cmp) {
        var comparison = ComparatorToComparisonName(cmp.Comparator);

        // An explicit `: Kind`/`as Kind` suffix always forces compareValue, even for two simple single-token
        // operands — CompareState carries no Kind field at all, so an authored kind annotation on it is meaningless;
        // forcing compareValue is the only reading that keeps the annotation.
        if (cmp.Kind is not null) {
            return new JsonObject {
                ["$type"] = "compareValue",
                ["comparison"] = comparison,
                ["kind"] = cmp.Kind,
                ["left"] = cmp.LeftText,
                ["right"] = cmp.RightText,
            };
        }

        ExpressionSpelling.TryParse(cmp.LeftText, out var leftTokens, out _);
        ExpressionSpelling.TryParse(cmp.RightText, out var rightTokens, out _);

        if (leftTokens.Count == 1 && leftTokens[0] is ValueToken.State leftState) {
            if (rightTokens.Count == 1 && rightTokens[0] is ValueToken.Constant rightConstant) {
                return NewCompareState(leftState.Name, leftState.Key, comparison, value: rightConstant.Value);
            }
            if (rightTokens.Count == 1 && rightTokens[0] is ValueToken.State rightState) {
                return NewCompareState(leftState.Name, leftState.Key, comparison, comparandState: rightState.Name, comparandKey: rightState.Key);
            }
        } else if (leftTokens.Count == 1 && leftTokens[0] is ValueToken.Constant leftConstant
            && rightTokens.Count == 1 && rightTokens[0] is ValueToken.State rightState2) {
            // Operands swapped so the live State read is always the CompareState subject — the decompiler never
            // emits this constant-first spelling, only the compiler tolerates it.
            return NewCompareState(rightState2.Name, rightState2.Key, FlipComparison(comparison), value: leftConstant.Value);
        }

        return new JsonObject {
            ["$type"] = "compareValue",
            ["comparison"] = comparison,
            ["kind"] = "Fixed",
            ["left"] = cmp.LeftText,
            ["right"] = cmp.RightText,
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

    private static string ComparatorToComparisonName(string comparator) => comparator switch {
        "==" => "Equal",
        "!=" => "NotEqual",
        "<" => "Less",
        "<=" => "LessOrEqual",
        ">" => "Greater",
        ">=" => "GreaterOrEqual",
        _ => throw new InvalidOperationException($"unknown comparator '{comparator}'"),
    };

    // Swaps comparison direction (not equality) — `a < b` read as `b > a` is still "greater", not "less".
    private static string FlipComparison(string comparison) => comparison switch {
        "Less" => "Greater",
        "LessOrEqual" => "GreaterOrEqual",
        "Greater" => "Less",
        "GreaterOrEqual" => "LessOrEqual",
        _ => comparison,
    };

    // ---- Effect statement lowering (§2) ---------------------------------------------------------------------

    private static JsonNode LowerEffectStatement(StatementNode stmt, EvaluationScope scope) => stmt switch {
        SetCellStatementNode s => LowerCellEffect("setState", s.Target, s.Rhs, allowText: true),
        AddCellStatementNode a => LowerCellEffect("addState", a.Target, a.Rhs, allowText: false),
        PushStatementNode push => LowerPush(push),
        CountdownStatementNode countdown => LowerRowOnlyEffect("countdownState", countdown.Target),
        RemoveCellStatementNode remove => LowerRowOnlyEffect("removeStateCell", remove.Target),
        ScheduleStatementNode schedule => LowerSchedule(schedule),
        TransformStatementNode transform => new JsonObject { ["$type"] = "transformState", ["transform"] = LowerExpression(transform.Transform, scope) },
        TransactionStatementNode transaction => LowerTransaction(transaction, scope),
        ExpressionStatementNode { Expression: CallExpressionNode call } => LowerExpression(call, scope)!,
        _ => throw new InvalidOperationException($"unrecognized effect statement '{stmt.GetType()}'"),
    };

    private static JsonObject LowerCellEffect(string discriminator, RowRefNode target, RhsNode rhs, bool allowText) {
        var obj = new JsonObject { ["$type"] = discriminator, ["state"] = target.Name };
        if (target.Key is not null) {
            obj["key"] = target.Key;
        }
        ApplyRhs(obj, rhs, allowText);
        return obj;
    }

    // `allowText` gates whether an RhsTextNode's Text lands on the object — AddState carries no Text field at all;
    // a string RHS reaching it is already PUCK009 from the parser, and best-effort lowering here emits nothing for
    // the field the destination cannot carry rather than shaping JSON that violates the target's own record.
    private static void ApplyRhs(JsonObject obj, RhsNode rhs, bool allowText) {
        switch (rhs) {
            case RhsTextNode text:
                if (allowText) {
                    obj["text"] = text.Text;
                }
                break;
            case RhsSecondsNode seconds:
                obj["valueSeconds"] = seconds.Seconds;
                break;
            case RhsOperandNode operand:
                ApplyOperandRhs(obj, operand.Text);
                break;
        }
    }

    // A single unkeyed state read (`row`, no brackets) lowers to a bare `fromState`; a single KEYED read
    // (`row[key]`) always lowers to a verbatim `expression`, never decomposed into `fromState`+`fromKey`, even
    // though both spellings are runtime-equivalent — confirmed against `first-witness` (puck.world.json), which
    // ships `{"$type":"setState","expression":"houndIdentity[$each]",...}` for exactly this RHS shape (§2.3).
    // `fromState`+`fromKey` together stay reachable through call-form for an author who wants that exact wire
    // shape.
    private static void ApplyOperandRhs(JsonObject obj, string text) {
        ExpressionSpelling.TryParse(text, out var tokens, out _);
        if (tokens.Count == 1 && tokens[0] is ValueToken.Constant constant) {
            obj["value"] = constant.Value;
        } else if (tokens.Count == 1 && tokens[0] is ValueToken.State { Key: null } state) {
            obj["fromState"] = state.Name;
        } else {
            obj["expression"] = text;
        }
    }

    private static JsonObject LowerPush(PushStatementNode push) {
        var obj = new JsonObject { ["$type"] = "pushState", ["state"] = push.RowName };
        // PushState carries no Text/Key/ValueSeconds field; ApplyRhs's text/seconds arms would shape JSON it
        // cannot carry, so only the classified-operand arm applies here (the parser already refuses the others).
        if (push.Rhs is RhsOperandNode operand) {
            ApplyOperandRhs(obj, operand.Text);
        }
        return obj;
    }

    private static JsonObject LowerRowOnlyEffect(string discriminator, RowRefNode target) {
        var obj = new JsonObject { ["$type"] = discriminator, ["state"] = target.Name };
        if (target.Key is not null) {
            obj["key"] = target.Key;
        }
        return obj;
    }

    private static JsonObject LowerSchedule(ScheduleStatementNode schedule) {
        var obj = new JsonObject {
            ["$type"] = "scheduleState",
            ["state"] = schedule.Target.Name,
            ["delaySeconds"] = schedule.DelaySeconds,
        };
        if (schedule.Target.Key is not null) {
            obj["key"] = schedule.Target.Key;
        }
        return obj;
    }

    private static JsonObject LowerTransaction(TransactionStatementNode transaction, EvaluationScope scope) {
        var mainEffects = new JsonArray();
        foreach (var stmt in transaction.MainEffects) {
            mainEffects.AppendNode(LowerEffectStatement(stmt, scope));
        }

        var obj = new JsonObject { ["$type"] = "transaction", ["effects"] = mainEffects };
        if (transaction.OnFailureEffects is { } onFailure) {
            var onFailureArr = new JsonArray();
            foreach (var stmt in onFailure) {
                onFailureArr.AppendNode(LowerEffectStatement(stmt, scope));
            }
            obj["onFailure"] = onFailureArr;
        }
        return obj;
    }
}
