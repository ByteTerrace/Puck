using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Lowering;

/// <summary>Lowers the parts of a parsed document that mean the same thing in every schema: values, constants,
/// units, arithmetic, template invocation, and canonical key order. What a SECTION means is a vocabulary's own
/// emitter; what <c>1.5m</c>, <c>-spread</c> or <c>2 * 3</c> mean is here.</summary>
public static class DocumentLowering {
    /// <summary>Recursively canonicalizes a JSON node by sorting every object's properties ordinally.</summary>
    /// <param name="node">The node to canonicalize.</param>
    /// <returns>A new canonicalized node, or <see langword="null"/> when the input was null.</returns>
    public static JsonNode? Canonicalize(JsonNode? node) {
        if (node is null) {
            return null;
        }

        if (node is JsonObject obj) {
            var sorted = new JsonObject();

            foreach (var entry in obj.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal).ToList()) {
                sorted[entry.Key] = Canonicalize(node: entry.Value);
            }

            return sorted;
        }

        if (node is JsonArray arr) {
            var canonicalArr = new JsonArray();

            foreach (var item in arr) {
                canonicalArr.AppendNode(item: Canonicalize(node: item));
            }

            return canonicalArr;
        }

        return node.DeepClone();
    }

    /// <summary>Lowers one expression to JSON.</summary>
    /// <param name="expr">The expression to lower.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The enclosing JSON key this expression fills — a property name, or a call argument's
    /// resolved <c>call.argument</c> key — threaded down so a unit-suffixed literal reachable from it (directly, or
    /// through an array, object, range, arithmetic or <c>let</c> indirection) validates against the field it
    /// actually lands on. <see langword="null"/> when no such key applies.</param>
    /// <returns>The lowered node, or <see langword="null"/> for a null literal or an unrecognized expression.</returns>
    public static JsonNode? LowerValue(ExpressionNode expr, DocumentScope scope, string? fieldKey = null) {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(scope);

        switch (expr) {
            case LiteralExpressionNode lit:
                return LowerLiteral(lit: lit, fieldKey: fieldKey, scope: scope);

            case ColorExpressionNode color:
                return JsonValue.Create(value: color.Hex);

            case IdentifierExpressionNode ident:
                // A lambda parameter shadows a constant of the same name, for the length of one application.
                if (scope.Locals.TryGetValue(key: ident.Name, value: out var local)) {
                    return local?.DeepClone();
                }
                if (scope.Constants.TryGetValue(key: ident.Name, value: out var constExpr)) {
                    return LowerValue(expr: constExpr, scope: scope, fieldKey: fieldKey);
                }
                if (string.Equals(a: ident.Name, b: "null", comparisonType: StringComparison.Ordinal)) {
                    return null;
                }
                if (string.Equals(a: ident.Name, b: "true", comparisonType: StringComparison.Ordinal)) {
                    return JsonValue.Create(value: true);
                }
                if (string.Equals(a: ident.Name, b: "false", comparisonType: StringComparison.Ordinal)) {
                    return JsonValue.Create(value: false);
                }
                if (string.Equals(a: ident.Name, b: "auto", comparisonType: StringComparison.Ordinal)) {
                    return JsonValue.Create(value: "auto");
                }

                return JsonValue.Create(value: ident.Name);

            case ArrayExpressionNode arr: {
                var jsonArr = new JsonArray();

                foreach (var elem in arr.Elements) {
                    jsonArr.AppendNode(item: LowerValue(expr: elem, scope: scope, fieldKey: fieldKey));
                }

                return jsonArr;
            }

            case ObjectExpressionNode obj: {
                var jsonObj = new JsonObject();

                foreach (var prop in obj.Properties) {
                    jsonObj[prop.Name] = LowerValue(expr: prop.Value, scope: scope, fieldKey: prop.Name);
                }

                return jsonObj;
            }

            case CallExpressionNode call: {
                if (DocumentBuiltins.TryEvaluate(call: call, scope: scope, fieldKey: fieldKey, result: out var builtin)) {
                    return builtin;
                }

                var jsonObj = new JsonObject {
                    ["$type"] = call.Name,
                };
                var positionalIndex = 0;

                foreach (var arg in call.Arguments) {
                    var key = (arg.Name ?? scope.Vocabulary.NameCallArgument(callName: call.Name, positionalIndex: positionalIndex) ?? $"arg{positionalIndex}");

                    // Classified by the qualified `call.argument` key, so a unit reads against the argument's own
                    // dimension rather than whatever a same-named block property elsewhere means.
                    jsonObj[key] = LowerValue(expr: arg.Value, scope: scope, fieldKey: $"{call.Name}.{key}");
                    positionalIndex++;
                }

                return jsonObj;
            }

            case BinaryExpressionNode bin:
                return EvaluateBinary(bin: bin, scope: scope, fieldKey: fieldKey);

            case UnaryExpressionNode un:
                return EvaluateUnary(un: un, scope: scope, fieldKey: fieldKey);

            case RangeExpressionNode range: {
                var rangeArr = new JsonArray();

                rangeArr.AppendNode(item: LowerValue(expr: range.Start, scope: scope, fieldKey: fieldKey));
                rangeArr.AppendNode(item: LowerValue(expr: range.End, scope: scope, fieldKey: fieldKey));

                return rangeArr;
            }

            case LambdaExpressionNode lambda:
                scope.Diagnostics.ReportError(
                    PuckDiagnosticCodes.LambdaOutsideBuiltin,
                    $"a lambda is an argument to {string.Join("/", DocumentBuiltins.Names)} and nothing else",
                    lambda.Span
                );

                return null;

            default:
                return null;
        }
    }

    /// <summary>Expands a template invocation, binding its arguments and feeding each statement of its body to
    /// <paramref name="sink"/> under a scope carrying those bindings as constants.</summary>
    /// <param name="call">The invocation as written.</param>
    /// <param name="target">The object the expanded statements land in.</param>
    /// <param name="scope">The invoking scope.</param>
    /// <param name="sink">The vocabulary's own statement processor.</param>
    public static void ExpandTemplate(CallExpressionNode call, JsonObject target, DocumentScope scope, Action<StatementNode, JsonObject, DocumentScope> sink) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);

        if (!scope.Templates.TryGetValue(key: call.Name, value: out var template)) {
            return;
        }

        var localConstants = new Dictionary<string, ExpressionNode>(dictionary: scope.Constants);

        for (var index = 0; (index < template.Parameters.Count); ++index) {
            var param = template.Parameters[index];
            ExpressionNode? boundValue = null;

            foreach (var arg in call.Arguments) {
                if (string.Equals(a: arg.Name, b: param.Name, comparisonType: StringComparison.Ordinal)) {
                    boundValue = arg.Value;

                    break;
                }
            }

            if ((boundValue is null) && (index < call.Arguments.Count) && (call.Arguments[index].Name is null)) {
                boundValue = call.Arguments[index].Value;
            }

            boundValue ??= param.DefaultValue;

            if (boundValue is not null) {
                localConstants[param.Name] = boundValue;
            }
        }

        var invocationScope = scope.WithConstants(invocationConstants: localConstants);

        foreach (var stmt in template.Body.Statements) {
            var expandedStmt = stmt;

            // A block whose NAME is a template parameter takes the bound value as its name, so one template can
            // declare a differently named row per invocation.
            if ((stmt is BlockNode blockStmt) && (blockStmt.Name is not null) && invocationScope.Constants.TryGetValue(key: blockStmt.Name, value: out var nameExpr)) {
                expandedStmt = (blockStmt with { Name = (LowerValue(expr: nameExpr, scope: invocationScope)?.ToString() ?? blockStmt.Name) });
            }

            sink(expandedStmt, target, invocationScope);
        }
    }

    /// <summary>Reads a lowered number whatever JSON numeric kind it landed as.</summary>
    /// <param name="node">The node to read.</param>
    /// <param name="number">The value read, or zero.</param>
    /// <returns><see langword="true"/> when the node held a number.</returns>
    /// <remarks>A lowered number reaches a caller as a long whenever it came out integral, so a reader that asks
    /// only for a double refuses every whole number.</remarks>
    public static bool TryReadNumber(JsonNode? node, out double number) {
        number = 0;

        if (node is not JsonValue value) {
            return false;
        }

        if (value.TryGetValue<double>(value: out var asDouble)) {
            number = asDouble;

            return true;
        }

        if (value.TryGetValue<long>(value: out var asLong)) {
            number = asLong;

            return true;
        }

        return false;
    }

    // An integral result narrows back to a long so arithmetic and a written literal reach the document as the same
    // JSON kind.
    private static JsonNode NumberNode(double value) {
        if (Math.Abs(value: (value % 1)) < double.Epsilon) {
            return JsonValue.Create(value: (long)value);
        }

        return JsonValue.Create(value: value);
    }

    private static JsonNode? LowerLiteral(LiteralExpressionNode lit, string? fieldKey, DocumentScope scope) {
        if (lit.Value is null) {
            return null;
        }

        if (lit.Value is bool b) {
            return JsonValue.Create(value: b);
        }

        if (lit.Value is string s) {
            return JsonValue.Create(value: s);
        }

        var numVal = lit.Value switch {
            long l => l,
            double d => d,
            int i => i,
            _ => Convert.ToDouble(value: lit.Value, provider: CultureInfo.InvariantCulture),
        };

        if (lit.Unit is not null) {
            return LowerUnitLiteral(numVal: numVal, unit: lit.Unit, fieldKey: fieldKey, span: lit.Span, scope: scope);
        }

        if (lit.Value is long longVal) {
            return JsonValue.Create(value: longVal);
        }

        return JsonValue.Create(value: numVal);
    }

    // Every unit is checked against the vocabulary's field-dimension table, `%`/`pct` included: a field the table
    // does not cover is PUCK024, a unit the field's own dimension does not accept is PUCK025. No unit converts
    // outside the table, so a suffix can never silently change a value the table says nothing about.
    private static JsonNode LowerUnitLiteral(double numVal, string unit, string? fieldKey, SourceSpan span, DocumentScope scope) {
        var dimension = ((fieldKey is null) ? UnitDimension.None : scope.Vocabulary.ClassifyField(fieldKey: fieldKey));

        if (UnitConversion.TryConvert(dimension: dimension, numericValue: numVal, unit: unit, converted: out var converted)) {
            return NumberNode(value: converted);
        }

        if (dimension == UnitDimension.None) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.UnitOnUnknownField, $"'{fieldKey ?? "this field"}' admits no unit — remove the '{unit}' suffix", span);
        } else {
            var accepted = string.Join(separator: "/", values: UnitConversion.AcceptedUnits(dimension: dimension));

            scope.Diagnostics.ReportError(PuckDiagnosticCodes.UnitNotAdmitted, $"'{fieldKey}' accepts {accepted}, not '{unit}'", span);
        }

        return JsonValue.Create(value: numVal);
    }

    // The operand is lowered under the SAME fieldKey, so a unit inside it converts against the field the sign is
    // written for; the sign is then applied to the converted number.
    private static JsonNode? EvaluateUnary(UnaryExpressionNode un, DocumentScope scope, string? fieldKey) {
        if (!TryReadNumber(node: LowerValue(expr: un.Operand, scope: scope, fieldKey: fieldKey), number: out var number)) {
            return null;
        }

        return NumberNode(value: ((un.Operator == "-") ? -number : number));
    }

    private static JsonNode? EvaluateBinary(BinaryExpressionNode bin, DocumentScope scope, string? fieldKey) {
        var leftNode = LowerValue(expr: bin.Left, scope: scope, fieldKey: fieldKey);
        var rightNode = LowerValue(expr: bin.Right, scope: scope, fieldKey: fieldKey);

        if (!TryReadNumber(node: leftNode, number: out var lNum) || !TryReadNumber(node: rightNode, number: out var rNum)) {
            return null;
        }

        switch (bin.Operator) {
            case "==":
                return JsonValue.Create(value: (lNum == rNum));

            case "!=":
                return JsonValue.Create(value: (lNum != rNum));

            case "<":
                return JsonValue.Create(value: (lNum < rNum));

            case "<=":
                return JsonValue.Create(value: (lNum <= rNum));

            case ">":
                return JsonValue.Create(value: (lNum > rNum));

            case ">=":
                return JsonValue.Create(value: (lNum >= rNum));
        }

        return NumberNode(value: bin.Operator switch {
            "+" => (lNum + rNum),
            "-" => (lNum - rNum),
            "*" => (lNum * rNum),
            "/" => ((rNum != 0) ? (lNum / rNum) : 0),
            // A zero divisor yields zero rather than failing, matching division.
            "%" => ((rNum != 0) ? (lNum % rNum) : 0),
            _ => 0,
        });
    }
}
