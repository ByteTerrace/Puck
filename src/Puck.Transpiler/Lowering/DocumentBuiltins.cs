using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Lowering;

/// <summary>The array builtins, evaluated while lowering rather than at run time: a document carries the array they
/// produced, not the call that produced it.</summary>
/// <remarks>Reserved in VALUE position only. A statement-position call of the same name is its vocabulary's own
/// (a cartridge's <c>map(row:, column:, tile:)</c> rule step is untouched).</remarks>
public static class DocumentBuiltins {
    /// <summary>The names this evaluates, so a caller can tell a builtin from a vocabulary's call form.</summary>
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal) {
        "concat", "filter", "length", "map", "range", "reduce",
    };

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

        if (!Names.Contains(call.Name)) {
            return false;
        }

        var args = call.Arguments;

        switch (call.Name) {
            case "range":
                result = Range(call: call, args: args, scope: scope);

                break;

            case "length":
                result = Length(call: call, args: args, scope: scope, fieldKey: fieldKey);

                break;

            case "concat":
                result = Concat(args: args, scope: scope, fieldKey: fieldKey);

                break;

            case "map":
                result = Map(call: call, args: args, scope: scope, fieldKey: fieldKey);

                break;

            case "filter":
                result = Filter(call: call, args: args, scope: scope, fieldKey: fieldKey);

                break;

            case "reduce":
                result = Reduce(call: call, args: args, scope: scope, fieldKey: fieldKey);

                break;
        }

        return true;
    }

    private static JsonNode? Range(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope) {
        if (args.Count != 2) {
            return Refuse(scope: scope, call: call, message: "range(start, count) takes two arguments");
        }

        if (!TryWholeNumber(node: DocumentLowering.LowerValue(expr: args[0].Value, scope: scope), value: out var start) ||
            !TryWholeNumber(node: DocumentLowering.LowerValue(expr: args[1].Value, scope: scope), value: out var count)) {
            return Refuse(scope: scope, call: call, message: "range(start, count) takes two whole numbers known at compile time");
        }

        if (count < 0) {
            return Refuse(scope: scope, call: call, message: "range count is never negative");
        }

        var arr = new JsonArray();

        for (var index = 0L; (index < count); ++index) {
            arr.AppendNode(item: JsonValue.Create(value: (start + index)));
        }

        return arr;
    }

    private static JsonNode? Length(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 1) {
            return Refuse(scope: scope, call: call, message: "length(value) takes one argument");
        }

        return DocumentLowering.LowerValue(expr: args[0].Value, scope: scope, fieldKey: fieldKey) switch {
            JsonArray arr => JsonValue.Create(value: (long)arr.Count),
            JsonObject obj => JsonValue.Create(value: (long)obj.Count),
            JsonValue value when value.TryGetValue<string>(value: out var text) => JsonValue.Create(value: (long)text.Length),
            _ => Refuse(scope: scope, call: call, message: "length(value) reads an array, an object or a string"),
        };
    }

    private static JsonNode Concat(IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        var arr = new JsonArray();

        foreach (var arg in args) {
            var lowered = DocumentLowering.LowerValue(expr: arg.Value, scope: scope, fieldKey: fieldKey);

            if (lowered is JsonArray nested) {
                foreach (var item in nested.ToList()) {
                    arr.AppendNode(item: item?.DeepClone());
                }

                continue;
            }

            arr.AppendNode(item: lowered);
        }

        return arr;
    }

    private static JsonNode? Map(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadSource(call: call, args: args, expected: 2, scope: scope, fieldKey: fieldKey, source: out var source, lambda: out var lambda)) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(scope: scope, call: call, message: "map's lambda takes the item, or the item and its index");
        }

        var arr = new JsonArray();

        for (var index = 0; (index < source!.Count); ++index) {
            arr.AppendNode(item: Apply(lambda: lambda, scope: scope, fieldKey: fieldKey, first: source[index], second: JsonValue.Create(value: (long)index)));
        }

        return arr;
    }

    private static JsonNode? Filter(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadSource(call: call, args: args, expected: 2, scope: scope, fieldKey: fieldKey, source: out var source, lambda: out var lambda)) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(scope: scope, call: call, message: "filter's lambda takes the item, or the item and its index");
        }

        var arr = new JsonArray();

        for (var index = 0; (index < source!.Count); ++index) {
            var kept = Apply(lambda: lambda, scope: scope, fieldKey: fieldKey, first: source[index], second: JsonValue.Create(value: (long)index));

            if (IsTruthy(node: kept)) {
                arr.AppendNode(item: source[index]?.DeepClone());
            }
        }

        return arr;
    }

    private static JsonNode? Reduce(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 3) {
            return Refuse(scope: scope, call: call, message: "reduce(array, seed, lambda) takes three arguments");
        }

        if (DocumentLowering.LowerValue(expr: args[0].Value, scope: scope, fieldKey: fieldKey) is not JsonArray source) {
            return Refuse(scope: scope, call: call, message: "reduce's first argument is an array");
        }

        if (args[2].Value is not LambdaExpressionNode lambda) {
            return Refuse(scope: scope, call: call, message: "reduce's third argument is a lambda");
        }

        if (lambda.Parameters.Count != 2) {
            return Refuse(scope: scope, call: call, message: "reduce's lambda takes the running value and the item");
        }

        var accumulated = DocumentLowering.LowerValue(expr: args[1].Value, scope: scope, fieldKey: fieldKey);

        foreach (var item in source.ToList()) {
            accumulated = Apply(lambda: lambda, scope: scope, fieldKey: fieldKey, first: accumulated, second: item);
        }

        return accumulated;
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
            Refuse(scope: scope, call: call, message: $"{call.Name}(array, lambda) takes two arguments");

            return false;
        }

        if (DocumentLowering.LowerValue(expr: args[0].Value, scope: scope, fieldKey: fieldKey) is not JsonArray arr) {
            Refuse(scope: scope, call: call, message: $"{call.Name}'s first argument is an array");

            return false;
        }

        if (args[1].Value is not LambdaExpressionNode written) {
            Refuse(scope: scope, call: call, message: $"{call.Name}'s second argument is a lambda");

            return false;
        }

        source = arr;
        lambda = written;

        return true;
    }

    // The lambda's parameters are bound as locals rather than as constants: a bound value is an already-lowered
    // JSON node, not an expression that could be lowered again.
    private static JsonNode? Apply(LambdaExpressionNode lambda, DocumentScope scope, string? fieldKey, JsonNode? first, JsonNode? second) {
        var locals = new Dictionary<string, JsonNode?>(dictionary: scope.Locals, comparer: StringComparer.Ordinal) {
            [lambda.Parameters[0]] = first?.DeepClone(),
        };

        if (lambda.Parameters.Count > 1) {
            locals[lambda.Parameters[1]] = second?.DeepClone();
        }

        return DocumentLowering.LowerValue(expr: lambda.Body, scope: scope.WithLocals(lambdaLocals: locals), fieldKey: fieldKey);
    }

    private static bool IsTruthy(JsonNode? node) {
        if (node is not JsonValue value) {
            return (node is not null);
        }

        if (value.TryGetValue<bool>(value: out var flag)) {
            return flag;
        }

        if (DocumentLowering.TryReadNumber(node: node, number: out var number)) {
            return (number != 0);
        }

        return value.TryGetValue<string>(value: out var text) && (text.Length > 0);
    }

    private static bool TryWholeNumber(JsonNode? node, out long value) {
        value = 0;

        if (!DocumentLowering.TryReadNumber(node: node, number: out var number) || (number != Math.Truncate(d: number))) {
            return false;
        }

        value = (long)number;

        return true;
    }

    private static JsonNode? Refuse(DocumentScope scope, CallExpressionNode call, string message) {
        scope.Diagnostics.ReportError(PuckDiagnosticCodes.BuiltinRefused, message, call.Span);

        return null;
    }
}
