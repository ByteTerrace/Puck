using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Lowering;

/// <summary>The builtins, evaluated while lowering rather than at run time: a document carries the array they
/// produced, not the call that produced it.</summary>
/// <remarks><para>Evaluated in compile-time value positions. A call constructing a declared document arm belongs
/// to its vocabulary even when a builtin shares the name (such as a world's <c>sort</c> transform).
/// Statement-position calls likewise belong to their vocabulary (a cartridge's <c>map</c> step is untouched).</para>
/// <para>Two families meet here. The COLLECTION builtins below are the document language's own — a rule has no
/// arrays to fold, so they exist in one language only. The SCALAR ones are not restated here at all: they come from
/// <see cref="ExpressionVocabulary"/>, the table the rule language is built from, so the two cannot drift over a
/// spelling. <see cref="DocumentScalars"/> folds them.</para></remarks>
public static class DocumentBuiltins {
    /// <summary>The collection builtins — the document language's own, with no rule-language counterpart.</summary>
    public static IReadOnlySet<string> Collections { get; } = new HashSet<string>(comparer: StringComparer.Ordinal) {
        "concat", "distinct", "filter", "groupBy", "length", "map", "range", "reduce", "sort",
    };
    /// <summary>Every name reserved in value position: the collection builtins, plus every named function of the
    /// shared expression vocabulary.</summary>
    public static IReadOnlySet<string> Names { get; } =
        new HashSet<string>(
        collection: Collections.Concat(second: ExpressionVocabulary.Functions.Keys),
        comparer: StringComparer.Ordinal
    );

    // The lambda's parameters are bound as locals rather than as constants: a bound value is an already-lowered
    // JSON node, not an expression that could be lowered again.
    private static JsonNode? Apply(LambdaExpressionNode lambda, DocumentScope scope, string? fieldKey, JsonNode? first, JsonNode? second) {
        if (
            (lambda.Parameters.Count > 1) &&
            (lambda.Parameters[0] == lambda.Parameters[1])
        ) {
            throw new DocumentEvaluationException(
                "A lambda cannot repeat a parameter name.",
                lambda.Span,
                PuckDiagnosticCodes.InvalidValue
            );
        }
        var locals = new Dictionary<string, JsonNode?>(
            dictionary: scope.Locals,
            comparer: StringComparer.Ordinal
        ) {
            [lambda.Parameters[0]] = first,
        };

        if (lambda.Parameters.Count > 1) {
            locals[lambda.Parameters[1]] = second;
        }

        return DocumentLowering.EvaluateValue(
            expr: lambda.Body,
            scope: scope.WithLocals(lambdaLocals: locals),
            fieldKey: fieldKey
        );
    }
    private static JsonNode Concat(IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        var arr = new JsonArray();

        foreach (var arg in args) {
            var lowered = DocumentLowering.EvaluateValue(
                expr: arg.Value,
                scope: scope,
                fieldKey: fieldKey
            );

            scope.Budget.Collection(
                count: (arr.Count + ((lowered is JsonArray list)
                ? list.Count
                : 1)),
                span: arg.Span
            );

            if (lowered is JsonArray nested) {
                foreach (var item in nested.ToList()) {
                    arr.AppendNode(item: scope.Budget.Copy(
                        item,
                        arg.Span
                    ));
                }

                continue;
            }

            arr.AppendNode(item: scope.Budget.Copy(
                lowered,
                arg.Span
            ));
        }

        return arr;
    }
    // `distinct(array)` keeps the first of each equal run by VALUE, not by reference: two separately-written
    // `[0, 1]` literals are one element, which is what an author generating coordinates means by it.
    private static JsonNode? Distinct(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 1) {
            return Refuse(
                call: call,
                message: "distinct(array) takes one argument",
                scope: scope
            );
        }

        if (DocumentLowering.EvaluateValue(
            expr: args[0].Value,
            scope: scope,
            fieldKey: fieldKey
        ) is not JsonArray source) {
            return Refuse(
                call: call,
                message: "distinct(array) reads an array",
                scope: scope
            );
        }

        var arr = new JsonArray();

        scope.Budget.Collection(
            count: source.Count,
            span: call.Span
        );
        var seen = new HashSet<JsonNode?>(comparer: DocumentValueEqualityComparer.Instance);

        foreach (var item in source.ToList()) {
            if (!seen.Add(item: item)) {
                continue;
            }

            arr.AppendNode(item: scope.Budget.Copy(
                item,
                call.Span
            ));
        }

        return arr;
    }
    private static JsonNode? Filter(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadSource(
            args: args,
            call: call,
            expected: 2,
            fieldKey: fieldKey,
            lambda: out var lambda,
            scope: scope,
            source: out var source
        )) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "filter's lambda takes the item, or the item and its index",
                scope: scope
            );
        }

        var arr = new JsonArray();

        for (var index = 0; (index < source!.Count); ++index) {
            var kept = Apply(
                lambda: lambda,
                scope: scope,
                fieldKey: fieldKey,
                first: source[index],
                second: JsonValue.Create(value: ((long)index))
            );

            if (DocumentLowering.IsTruthy(node: kept)) {
                arr.AppendNode(item: scope.Budget.Copy(
                    source[index],
                    call.Span
                ));
            }
        }

        return arr;
    }
    // `groupBy(array, lambda)` yields an object keyed by the lambda's result, each value the array of items that
    // produced it. Keys keep first-seen order, so the object reads the way the source does.
    private static JsonNode? GroupBy(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadSource(
            args: args,
            call: call,
            expected: 2,
            fieldKey: fieldKey,
            lambda: out var lambda,
            scope: scope,
            source: out var source
        )) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "groupBy's lambda takes the item, or the item and its index",
                scope: scope
            );
        }

        var grouped = new JsonObject();

        for (var index = 0; (index < source!.Count); ++index) {
            var key = Apply(
                lambda: lambda,
                scope: scope,
                fieldKey: fieldKey,
                first: source[index],
                second: JsonValue.Create(value: ((long)index))
            );
            var name = DocumentLowering.KeyText(node: key);

            if (name is null) {
                return Refuse(
                    call: call,
                    message: "groupBy's lambda yields a string or a number to key by",
                    scope: scope
                );
            }

            if (grouped[propertyName: name] is not JsonArray bucket) {
                bucket = [];
                grouped[propertyName: name] = bucket;
            }

            bucket.AppendNode(item: scope.Budget.Copy(
                source[index],
                call.Span
            ));
        }

        return grouped;
    }
    private static JsonNode? Length(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 1) {
            return Refuse(
                call: call,
                message: "length(value) takes one argument",
                scope: scope
            );
        }

        return DocumentLowering.EvaluateValue(
            expr: args[0].Value,
            scope: scope,
            fieldKey: fieldKey
        ) switch {
            JsonArray arr => JsonValue.Create(value: ((long)arr.Count)),
            JsonObject obj => JsonValue.Create(value: ((long)obj.Count)),
            JsonValue value when value.TryGetValue<string>(value: out var text) => JsonValue.Create(value: ((long)text.Length)),
            _ => Refuse(
            call: call,
            message: "length(value) reads an array, an object or a string",
            scope: scope
        ),
        };
    }
    private static JsonNode? Map(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadSource(
            args: args,
            call: call,
            expected: 2,
            fieldKey: fieldKey,
            lambda: out var lambda,
            scope: scope,
            source: out var source
        )) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "map's lambda takes the item, or the item and its index",
                scope: scope
            );
        }

        var arr = new JsonArray();

        for (var index = 0; (index < source!.Count); ++index) {
            arr.AppendNode(item: scope.Budget.Copy(
                Apply(
                    lambda: lambda,
                    scope: scope,
                    fieldKey: fieldKey,
                    first: source[index],
                    second: JsonValue.Create(value: ((long)index))
                ),
                call.Span
            ));
        }

        return arr;
    }
    private static JsonNode? Range(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope) {
        if (args.Count != 2) {
            return Refuse(
                call: call,
                message: "range(start, count) takes two arguments",
                scope: scope
            );
        }

        if (
            !TryWholeNumber(
            node: DocumentLowering.EvaluateValue(
                expr: args[0].Value,
                scope: scope
            ),
            value: out var start
        ) ||
            !TryWholeNumber(
            node: DocumentLowering.EvaluateValue(
                expr: args[1].Value,
                scope: scope
            ),
            value: out var count
        )
        ) {
            return Refuse(
                call: call,
                message: "range(start, count) takes two whole numbers known at compile time",
                scope: scope
            );
        }

        if (count < 0) {
            return Refuse(
                call: call,
                message: "range count is never negative",
                scope: scope
            );
        }

        scope.Budget.Collection(
            count: count,
            span: call.Span
        );
        if (
            (count > 0) &&
            (start > (long.MaxValue - (count - 1)))
        ) {
            return Refuse(
                call: call,
                message: "range exceeds the signed 64-bit integer range",
                scope: scope
            );
        }

        var arr = new JsonArray();

        for (var index = 0L; (index < count); ++index) {
            arr.AppendNode(item: JsonValue.Create(value: (start + index)));
        }

        return arr;
    }
    private static JsonNode? Reduce(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 3) {
            return Refuse(
                call: call,
                message: "reduce(array, seed, lambda) takes three arguments",
                scope: scope
            );
        }

        if (DocumentLowering.EvaluateValue(
            expr: args[0].Value,
            scope: scope,
            fieldKey: fieldKey
        ) is not JsonArray source) {
            return Refuse(
                call: call,
                message: "reduce's first argument is an array",
                scope: scope
            );
        }

        if (args[2].Value is not LambdaExpressionNode lambda) {
            return Refuse(
                call: call,
                message: "reduce's third argument is a lambda",
                scope: scope
            );
        }

        if (lambda.Parameters.Count != 2) {
            return Refuse(
                call: call,
                message: "reduce's lambda takes the running value and the item",
                scope: scope
            );
        }

        scope.Budget.Collection(
            count: source.Count,
            span: call.Span
        );
        var accumulated = DocumentLowering.EvaluateValue(
            expr: args[1].Value,
            scope: scope,
            fieldKey: fieldKey
        );

        foreach (var item in source.ToList()) {
            accumulated = Apply(
                fieldKey: fieldKey,
                first: accumulated,
                lambda: lambda,
                scope: scope,
                second: item
            );
        }

        return accumulated;
    }
    private static JsonNode? Refuse(DocumentScope scope, CallExpressionNode call, string message) {
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.BuiltinRefused,
            message: message,
            span: call.Span
        );

        return null;
    }
    // `sort(array)` and `sort(array, lambda)` — the lambda projects each item to the key it sorts by. The order is
    // STABLE and total: numbers before strings before everything else, so a mixed array has one answer rather than
    // depending on which comparison happened to run.
    private static JsonNode? Sort(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "sort(array) or sort(array, lambda) takes one or two arguments",
                scope: scope
            );
        }

        if (DocumentLowering.EvaluateValue(
            expr: args[0].Value,
            scope: scope,
            fieldKey: fieldKey
        ) is not JsonArray source) {
            return Refuse(
                call: call,
                message: "sort's first argument is an array",
                scope: scope
            );
        }

        LambdaExpressionNode? lambda = null;

        if (args.Count == 2) {
            if (args[1].Value is not LambdaExpressionNode written) {
                return Refuse(
                    call: call,
                    message: "sort's second argument is a lambda",
                    scope: scope
                );
            }

            if (written.Parameters.Count != 1) {
                return Refuse(
                    call: call,
                    message: "sort's lambda takes the item",
                    scope: scope
                );
            }

            lambda = written;
        }

        var items = source.ToList();

        scope.Budget.Collection(
            count: items.Count,
            span: call.Span
        );
        var keyed = new List<(JsonNode? Key, JsonNode? Item)>(capacity: items.Count);

        for (var index = 0; (index < items.Count); ++index) {
            keyed.Add(item: (
                Key: ((lambda is null)
                ? items[index]
                : Apply(
                    lambda: lambda,
                    scope: scope,
                    fieldKey: fieldKey,
                    first: items[index],
                    second: JsonValue.Create(value: ((long)index))
                )),
                Item: items[index]));
        }

        var arr = new JsonArray();

        foreach (var entry in keyed.OrderBy(
            keySelector: static entry => entry.Key,
            comparer: DocumentValueComparer.Instance
        )) {
            arr.AppendNode(item: scope.Budget.Copy(
                entry.Item,
                call.Span
            ));
        }

        return arr;
    }
    private static bool TryReadSource(
        CallExpressionNode call,
        IReadOnlyList<ArgumentNode> args,
        int expected,
        DocumentScope scope,
        string? fieldKey,
        out JsonArray? source,
        out LambdaExpressionNode? lambda
    ) {
        source = null;
        lambda = null;

        if (args.Count != expected) {
            Refuse(
                scope: scope,
                call: call,
                message: $"{call.Name}(array, lambda) takes two arguments"
            );

            return false;
        }

        if (DocumentLowering.EvaluateValue(
            expr: args[0].Value,
            scope: scope,
            fieldKey: fieldKey
        ) is not JsonArray arr) {
            Refuse(
                scope: scope,
                call: call,
                message: $"{call.Name}'s first argument is an array"
            );

            return false;
        }

        if (args[1].Value is not LambdaExpressionNode written) {
            Refuse(
                scope: scope,
                call: call,
                message: $"{call.Name}'s second argument is a lambda"
            );

            return false;
        }

        scope.Budget.Collection(
            count: arr.Count,
            span: call.Span
        );
        source = arr;
        lambda = written;

        return true;
    }
    private static bool TryWholeNumber(JsonNode? node, out long value) {
        return DocumentNumbers.TryInteger(
            node: node,
            number: out value
        );
    }

    /// <summary>Evaluates a builtin call.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The enclosing JSON key, threaded into each element's own lowering.</param>
    /// <param name="result">The evaluated node.</param>
    /// <returns><see langword="true"/> when <paramref name="call"/> names a builtin; refusals are reported into the
    /// scope's diagnostics and yield a null <paramref name="result"/>.</returns>
    public static bool TryEvaluate(CallExpressionNode call, DocumentScope scope, string? fieldKey, out JsonNode? result) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        result = null;

        if (!Names.Contains(item: call.Name)) {
            return false;
        }

        var args = call.Arguments;

        switch (call.Name) {
            case "range":
                result = Range(
                    args: args,
                    call: call,
                    scope: scope
                );

                break;

            case "length":
                result = Length(
                    args: args,
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            case "concat":
                result = Concat(
                    args: args,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            case "map":
                result = Map(
                    args: args,
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            case "filter":
                result = Filter(
                    args: args,
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            case "reduce":
                result = Reduce(
                    args: args,
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            case "distinct":
                result = Distinct(
                    args: args,
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            case "sort":
                result = Sort(
                    args: args,
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            case "groupBy":
                result = GroupBy(
                    args: args,
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;

            default:
                result = DocumentScalars.Evaluate(
                    call: call,
                    fieldKey: fieldKey,
                    scope: scope
                );

                break;
        }

        return true;
    }
}
