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
    public static JsonNode? LowerValue(ExpressionNode expr, DocumentScope scope, string? fieldKey = null) =>
        scope.Budget.Copy(EvaluateValue(expr, scope, fieldKey), expr.Span);

    // Internal evaluation returns borrowed, read-only values. Clone when constructing an output container, never
    // merely to inspect a value or select one element of a cached array.
    internal static JsonNode? EvaluateValue(ExpressionNode expr, DocumentScope scope, string? fieldKey = null) {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(scope);
        using var evaluation = scope.Budget.Enter(expr.Span);
        if (scope.Vocabulary.TryLowerValue(expr, scope, fieldKey, out var specialized)) { return specialized; }

        switch (expr) {
            case LiteralExpressionNode lit:
                return LowerLiteral(lit: lit, fieldKey: fieldKey, scope: scope);

            case InterpolatedStringNode interpolated: {
                var built = new System.Text.StringBuilder();

                foreach (var segment in interpolated.Segments) {
                    switch (segment) {
                        case InterpolationSegment.Literal text:
                            built.Append(text.Text);

                            break;

                        case InterpolationSegment.Hole hole:
                            built.Append(FormatHole(node: LowerValue(expr: hole.Expression, scope: scope)));

                            break;
                    }
                    if (built.Length > DocumentEvaluationBudget.TextLimit) {
                        throw new DocumentEvaluationException("The interpolated string exceeds the text limit.", expr.Span);
                    }
                }
                return JsonValue.Create(value: built.ToString());
            }

            case ColorExpressionNode color:
                return JsonValue.Create(value: color.Hex);

            case IdentifierExpressionNode ident:
                if (scope.TryEvaluateBinding(ident.Name, fieldKey, out var bound)) {
                    return bound;
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
                scope.Budget.Collection(arr.Elements.Count, arr.Span);
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

            case IndexExpressionNode indexed:
                return EvaluateIndex(indexed: indexed, scope: scope, fieldKey: fieldKey);

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
        using var expansion = scope.Budget.Enter(call.Span);

        if (!scope.Templates.TryGetValue(key: call.Name, value: out var template)) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"Unknown template '{call.Name}'.", call.Span);
            return;
        }

        var definitionScope = scope.TemplateDefinitionScope(call.Name);
        var localConstants = new Dictionary<string, ExpressionNode>(dictionary: definitionScope.Constants);
        var invocationScope = definitionScope.WithConstants(invocationConstants: localConstants);
        var consumed = new HashSet<int>();

        for (var index = 0; (index < template.Parameters.Count); ++index) {
            var param = template.Parameters[index];
            ExpressionNode? boundValue = null;

            for (var argumentIndex = 0; argumentIndex < call.Arguments.Count; ++argumentIndex) {
                var arg = call.Arguments[argumentIndex];
                if (string.Equals(a: arg.Name, b: param.Name, comparisonType: StringComparison.Ordinal)) {
                    if (boundValue is not null) {
                        scope.Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"Argument '{param.Name}' is supplied more than once.", arg.Span);
                        return;
                    }
                    boundValue = arg.Value;
                    consumed.Add(argumentIndex);
                }
            }

            if ((boundValue is null) && (index < call.Arguments.Count) && (call.Arguments[index].Name is null)) {
                boundValue = call.Arguments[index].Value;
                consumed.Add(index);
            }

            var isDefault = boundValue is null;
            boundValue ??= param.DefaultValue;

            if (boundValue is not null) {
                invocationScope.BindArgument(param.Name, boundValue, isDefault ? invocationScope : scope);
            } else {
                scope.Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"Template '{call.Name}' requires argument '{param.Name}'.", call.Span);
                return;
            }
        }

        if (consumed.Count != call.Arguments.Count) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"Template '{call.Name}' received an unknown or duplicate argument.", call.Span);
            return;
        }

        invocationScope.IndexDeclarations(template.Body.Statements);

        foreach (var stmt in template.Body.Statements) {
            scope.Budget.Spend(1, stmt.Span);
            sink(Rename(statement: stmt, scope: invocationScope), target, invocationScope);
        }
    }

    /// <summary>Flattens a template invocation into the statements it produces, each paired with the scope its
    /// argument bindings are live in, for a section whose rows a dedicated dispatcher reads.</summary>
    /// <param name="call">The invocation as written.</param>
    /// <param name="scope">The invoking scope.</param>
    /// <returns>Each produced statement and the scope to lower it under; nothing when no template bears the name.</returns>
    public static IEnumerable<(StatementNode Statement, DocumentScope Scope)> ExpandTemplateStatements(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        var captured = new List<(StatementNode, DocumentScope)>();

        ExpandTemplate(
            call: call,
            target: new JsonObject(),
            scope: scope,
            sink: (statement, _, invocationScope) => captured.Add(item: (statement, invocationScope)));

        return captured;
    }

    // A block whose NAME is a template parameter takes the bound value as its name, so one template can declare a
    // differently named row per invocation.
    private static StatementNode Rename(StatementNode statement, DocumentScope scope) {
        if ((statement is not BlockNode block) || (block.Name is null) ||
            !scope.TryLowerBinding(block.Name, out var nameValue)) {
            return statement;
        }

        return (block with { Name = (nameValue?.ToString() ?? block.Name) });
    }

    /// <summary>Expands a <c>for</c> block, emitting its body once per element of the sequence.</summary>
    /// <param name="loop">The loop as written.</param>
    /// <param name="target">The object the body's statements land in.</param>
    /// <param name="scope">The enclosing scope.</param>
    /// <param name="sink">The vocabulary's own statement processor.</param>
    /// <remarks>Compile-time: the document carries the statements produced, never the loop. The bound names are
    /// locals, so they shadow a constant of the same name for the length of one iteration and are gone after.</remarks>
    public static void ExpandFor(ForStatementNode loop, JsonObject target, DocumentScope scope, Action<StatementNode, JsonObject, DocumentScope> sink) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);

        foreach (var (statement, iteration) in ExpandForStatements(loop: loop, scope: scope)) {
            sink(statement, target, iteration);
        }
    }

    /// <summary>Refuses a <c>for</c> body that assigns a field instead of emitting a row.</summary>
    /// <param name="loop">The loop as written.</param>
    /// <param name="scope">The scope refusals are reported into.</param>
    /// <returns><see langword="true"/> when every body statement is admissible.</returns>
    /// <remarks>
    /// <para>A <c>for</c> EMITS ROWS. Building a value out of a sequence is <c>map</c>'s job, in value position, and
    /// the two are not interchangeable: a row goes through its section's own sugar, defaults and refusals, while a
    /// value is the JSON it lowers to and nothing else.</para>
    /// <para>Assigning a field from a loop body had no honest reading. A scalar (<c>scalars: p</c>) kept the last
    /// iteration and silently dropped every earlier one. An array (<c>curve [p, 0]</c>) went through
    /// <see cref="AssignOrExtend"/>, whose extend exists so <c>shapes:</c> can merge with the array the row sugar
    /// already built — so each iteration's elements were spliced into the field rather than appended to it,
    /// flattening <c>[[1,0],[2,0]]</c> to <c>[1,0,2,0]</c> and corrupting any array the document had authored above
    /// it. Both were silent. Refusing is what keeps one syntax from meaning append-element here and merge-lists
    /// there, which is a distinction no reader could recover from the source.</para>
    /// </remarks>
    public static bool ValidateForBody(ForStatementNode loop, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scope);

        var admissible = true;

        foreach (var statement in loop.Body) {
            if (statement is not PropertyNode property) {
                continue;
            }

            scope.Diagnostics.ReportError(
                PuckDiagnosticCodes.ForAssignsAField,
                $"a 'for' emits rows, and '{property.Name}' is a field assignment — every iteration would write the "
                    + $"same field. Build the value with map(...) in value position instead.",
                property.Span
            );

            admissible = false;
        }

        return admissible;
    }

    /// <summary>Flattens a <c>for</c> into the statements it produces, each paired with the scope its bindings are
    /// live in, for a section whose rows a dedicated dispatcher reads.</summary>
    /// <param name="loop">The loop as written.</param>
    /// <param name="scope">The enclosing scope.</param>
    /// <returns>Each produced statement and the scope to lower it under.</returns>
    public static IEnumerable<(StatementNode Statement, DocumentScope Scope)> ExpandForStatements(ForStatementNode loop, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scope);
        using var expansion = scope.Budget.Enter(loop.Span);

        if (!ValidateForBody(loop: loop, scope: scope)) {
            yield break;
        }

        if (EvaluateValue(expr: loop.Sequence, scope: scope) is not JsonArray sequence) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.ForSequenceRefused, "a 'for' walks an array known at compile time", loop.Sequence.Span);

            yield break;
        }

        for (var index = 0; (index < sequence.Count); ++index) {
            var locals = new Dictionary<string, JsonNode?>(dictionary: scope.Locals, comparer: StringComparer.Ordinal) {
                [loop.Item] = sequence[index],
            };

            if (loop.Index is { } ordinal) {
                locals[ordinal] = JsonValue.Create(value: (long)index);
            }

            var iteration = scope.WithLocals(lambdaLocals: locals);

            foreach (var statement in loop.Body) {
                scope.Budget.Spend(1, statement.Span);
                yield return (statement, iteration);
            }
        }
    }

    /// <summary>Returns a block's name, resolving an interpolated header.</summary>
    /// <param name="block">The block.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The resolved name, or <see langword="null"/> when the block carries none.</returns>
    public static string? ResolveBlockName(BlockNode block, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(scope);

        if (block.NameExpression is null) {
            return block.Name;
        }

        return (LowerValue(expr: block.NameExpression, scope: scope) is JsonValue value && value.TryGetValue<string>(value: out var text))
            ? text
            : block.Name;
    }

    /// <summary>Writes a lowered value into a section, extending rather than replacing when both the value and
    /// what is already there are arrays.</summary>
    /// <param name="target">The object the section belongs to.</param>
    /// <param name="key">The section name.</param>
    /// <param name="value">The lowered value.</param>
    /// <remarks>A section's array is built in statement order: a sugar block appends one row and a property
    /// carrying an array appends its rows, so the two compose instead of the later one erasing the earlier.
    /// Assigning over a statement-built array is how rows went missing with no diagnostic.</remarks>
    public static void AssignOrExtend(JsonObject target, string key, JsonNode? value) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(key);

        if ((target[key] is JsonArray existing) && (value is JsonArray addition)) {
            var items = addition.ToList();
            addition.Clear();

            foreach (var item in items) {
                existing.AppendNode(item: item);
            }

            return;
        }

        target[key] = value;
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

        if (value.TryGetValue<int>(out var asInt)) {
            number = asInt;
            return true;
        }
        if (value.TryGetValue<decimal>(out var exact)) {
            number = (double)exact;
            return true;
        }

        return false;
    }

    // A hole's value as it reads inside a string. A whole number prints without a decimal point, so
    // `$"row-{floor(i / 4)}"` reads "row-1" rather than "row-1.0".
    private static string FormatHole(JsonNode? node) {
        if (node is not JsonValue value) {
            return (node?.ToJsonString() ?? string.Empty);
        }

        if (value.TryGetValue<string>(value: out var text)) {
            return text;
        }

        if (value.TryGetValue<bool>(value: out var flag)) {
            return (flag ? "true" : "false");
        }

        if (value.TryGetValue<long>(value: out var whole)) {
            return whole.ToString(provider: CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(value: out var real)) {
            return real.ToString(format: "R", provider: CultureInfo.InvariantCulture);
        }

        return value.ToJsonString();
    }

    /// <summary>Reads a lowered value as an object key: a string as itself, a number by its shortest round-trip
    /// spelling, and anything else as nothing.</summary>
    /// <param name="node">The lowered node.</param>
    /// <returns>The key text, or <see langword="null"/> when the value cannot key an object.</returns>
    public static string? KeyText(JsonNode? node) {
        if (node is not JsonValue value) {
            return null;
        }

        if (value.TryGetValue<string>(value: out var text)) {
            return text;
        }

        if (DocumentNumbers.TryInteger(node, out var integer)) {
            return integer.ToString(CultureInfo.InvariantCulture);
        }
        return TryReadNumber(node: node, number: out var number)
            ? ((number == Math.Truncate(d: number)) ? ((long)number).ToString(provider: System.Globalization.CultureInfo.InvariantCulture) : number.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>Reads a node as a truth value: the condition `select` branches on and `filter` keeps by. A boolean is
    /// itself; a number is true when non-zero, which is how a comparison's 1/0 reads; a string is true when non-empty;
    /// and a present container is true.</summary>
    /// <param name="node">The lowered node.</param>
    /// <returns><see langword="true"/> when the node reads as true.</returns>
    public static bool IsTruthy(JsonNode? node) {
        if (node is not JsonValue value) {
            return (node is not null);
        }

        if (value.TryGetValue<bool>(value: out var flag)) {
            return flag;
        }

        if (TryReadNumber(node: node, number: out var number)) {
            return (number != 0);
        }

        return value.TryGetValue<string>(value: out var text) && (text.Length > 0);
    }

    /// <summary>Narrows an integral result back to a long, so arithmetic and a written literal reach the document as
    /// the same JSON kind.</summary>
    /// <param name="value">The folded number.</param>
    /// <param name="span">The expression responsible for the result.</param>
    /// <returns>A long-valued node when the value is integral, a double-valued one otherwise.</returns>
    public static JsonNode NumberNode(double value, SourceSpan span = default) {
        if (!double.IsFinite(value)) {
            throw new DocumentEvaluationException("The calculation is outside the finite numeric range.", span, PuckDiagnosticCodes.InvalidValue);
        }
        if (value >= long.MinValue && value < 9223372036854775808d && Math.Truncate(value) == value) {
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

        if (!double.IsFinite(numVal)) {
            throw new DocumentEvaluationException("The literal is outside the finite numeric range.", lit.Span, PuckDiagnosticCodes.InvalidValue);
        }
        return JsonValue.Create(value: numVal);
    }

    // Every unit is checked against the vocabulary's field-dimension table, `%`/`pct` included: a field the table
    // does not cover is PUCK024, a unit the field's own dimension does not accept is PUCK025. No unit converts
    // outside the table, so a suffix can never silently change a value the table says nothing about.
    private static JsonNode LowerUnitLiteral(double numVal, string unit, string? fieldKey, SourceSpan span, DocumentScope scope) {
        var dimension = ((fieldKey is null) ? UnitDimension.None : scope.Vocabulary.ClassifyField(fieldKey: fieldKey));

        if (UnitConversion.TryConvert(dimension: dimension, numericValue: numVal, unit: unit, converted: out var converted)) {
            return NumberNode(value: converted, span: span);
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
        var operand = EvaluateValue(un.Operand, scope, fieldKey);
        if (DocumentNumbers.TryInteger(operand, out var integer)) {
            if (un.Operator == "-" && integer == long.MinValue) {
                throw new DocumentEvaluationException("Integer negation exceeds the signed 64-bit range.", un.Span, PuckDiagnosticCodes.InvalidValue);
            }
            return JsonValue.Create(un.Operator == "-" ? -integer : integer);
        }
        if (!TryReadNumber(node: operand, number: out var number)) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, "A unary numeric operator needs a number.", un.Span);
            return null;
        }

        return NumberNode(value: ((un.Operator == "-") ? -number : number), span: un.Span);
    }

    private static JsonNode? EvaluateIndex(IndexExpressionNode indexed, DocumentScope scope, string? fieldKey) {
        var target = EvaluateValue(expr: indexed.Target, scope: scope, fieldKey: fieldKey);
        var index = EvaluateValue(expr: indexed.Index, scope: scope);

        if ((target is JsonObject obj) && (index is JsonValue key) && key.TryGetValue<string>(value: out var name)) {
            obj.TryGetPropertyValue(name, out var member);
            return member;
        }

        if (target is not JsonArray arr) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.IndexRefused, "only an array or an object can be indexed", indexed.Span);

            return null;
        }

        if (!DocumentNumbers.TryInteger(index, out var ordinal)) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.IndexRefused, "an array index is a whole number known at compile time", indexed.Span);

            return null;
        }

        if ((ordinal < 0) || (ordinal >= arr.Count)) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.IndexRefused, $"index {ordinal} is outside the array's 0..{(arr.Count - 1)}", indexed.Span);

            return null;
        }

        return arr[(int)ordinal];
    }

    private static JsonNode? EvaluateBinary(BinaryExpressionNode bin, DocumentScope scope, string? fieldKey) {
        var leftNode = EvaluateValue(expr: bin.Left, scope: scope, fieldKey: fieldKey);
        var rightNode = EvaluateValue(expr: bin.Right, scope: scope, fieldKey: fieldKey);

        if (!TryReadNumber(node: leftNode, number: out var lNum) || !TryReadNumber(node: rightNode, number: out var rNum)) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.InvalidValue, $"'{bin.Operator}' needs numbers known at compile time.", bin.Span);
            return null;
        }

        // A comparison is worth 1 or 0, never a JSON boolean — the rule language has no boolean either, so this is
        // what keeps `cleared + (row > 0)` meaning the same thing on both sides of the compiler. `IsTruthy` reads a
        // non-zero number as true, so a comparison still reads as a condition wherever one is wanted.
        var order = DocumentNumbers.Compare(leftNode, rightNode);
        var comparison = bin.Operator switch {
            "==" => order == 0,
            "!=" => order != 0,
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => (bool?)null,
        };

        if (comparison is { } verdict) {
            return JsonValue.Create(value: (verdict ? 1L : 0L));
        }

        if (DocumentNumbers.TryInteger(leftNode, out var left) && DocumentNumbers.TryInteger(rightNode, out var right)) {
            try {
                long? exact = bin.Operator switch {
                    "+" => checked(left + right),
                    "-" => checked(left - right),
                    "*" => checked(left * right),
                    "%" => right is 0 or -1 ? 0 : left % right,
                    "/" when right == 0 => 0,
                    "/" when right == -1 => checked(-left),
                    "/" when left % right == 0 => left / right,
                    _ => null,
                };
                if (exact is { } result) {
                    return JsonValue.Create(result);
                }
            } catch (OverflowException) {
                throw new DocumentEvaluationException("Integer arithmetic exceeds the signed 64-bit range.", bin.Span, PuckDiagnosticCodes.InvalidValue);
            }
        }

        return NumberNode(value: bin.Operator switch {
            "+" => (lNum + rNum),
            "-" => (lNum - rNum),
            "*" => (lNum * rNum),
            "/" => ((rNum != 0) ? (lNum / rNum) : 0),
            // There is deliberately no `//` for integer division: `//` opens a line comment, so `a // b` can only
            // ever read as `a` followed by a comment. `floor(a / b)` is the spelling, and it is the rule language's
            // own `floor` — the same name, the same rounding — rather than a second one invented here.
            // A zero divisor yields zero rather than failing, matching division.
            "%" => ((rNum != 0) ? (lNum % rNum) : 0),
            _ => 0,
        }, span: bin.Span);
    }
}
