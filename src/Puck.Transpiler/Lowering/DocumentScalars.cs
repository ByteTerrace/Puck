using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Lowering;

/// <summary>
/// The named scalar functions of the document language, drawn from <see cref="ExpressionVocabulary"/> rather than
/// restated here, so a spelling never means one thing in a rule and another in a document — or exists in one and
/// not the other.
/// <para>The two languages share the vocabulary and keep separate evaluators, deliberately. A rule runs in the
/// simulation over <c>FixedQ4816</c> cells, where determinism is the contract; a document folds at compile time
/// over the numbers it carries, where authored precision is. Folding an authored 0.7071068 through Q48.16 to agree
/// with the rule evaluator bit-for-bit would quantize the art to 1/65536 and buy nothing, since the folded value is
/// document DATA and never simulation state.</para>
/// <para>So: an <see cref="ExpressionDomain.Integer"/> function is evaluated by the engine's own
/// <see cref="ExpressionArithmetic"/> — integers are exact in both, so delegating makes disagreement impossible.
/// Everything else folds in double, and agrees with the rule evaluator exactly on whole numbers.</para>
/// </summary>
public static class DocumentScalars {
    /// <summary>The names folded in double, because their domain includes fractions. Every
    /// <see cref="ExpressionDomain.Integer"/> name is folded too, by delegation — see <see cref="Delegated"/> — so
    /// the refused set is what is left: the fixed-point-only and specialized-lowering entries, refused BY NAME.</summary>
    private static readonly HashSet<string> InDouble = new(StringComparer.Ordinal) {
        "absolute", "ceiling", "clamp", "cosine", "floor", "maximum", "minimum", "round", "sign", "sine",
        "squareRoot",
    };

    /// <summary>Returns a value indicating whether a name is a scalar function of the expression language.</summary>
    /// <param name="name">The call name.</param>
    /// <returns><see langword="true"/> when the shared vocabulary names it.</returns>
    public static bool IsScalar(string name) => ExpressionVocabulary.Functions.ContainsKey(name);

    /// <summary>Folds a scalar call to the value it denotes.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The enclosing JSON key, threaded into each argument's own lowering.</param>
    /// <returns>The folded node, or <see langword="null"/> when the call was refused.</returns>
    public static JsonNode? Evaluate(CallExpressionNode call, DocumentScope scope, string? fieldKey) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        var function = ExpressionVocabulary.Functions[call.Name];

        if (!InDouble.Contains(call.Name) && (function.Domain is not (ExpressionDomain.Integer or ExpressionDomain.Select))) {
            return Refuse(
                scope: scope,
                call: call,
                message: $"the rule language evaluates '{call.Name}'; the document language does not fold it");
        }

        if (call.Arguments.Count != function.Arity) {
            return Refuse(scope: scope, call: call, message: $"{call.Name} takes {function.Arity} argument(s)");
        }

        // `select` reads its condition as a truth value, not as a number, so it is folded before the numeric read
        // that every other function needs.
        if (function.Domain == ExpressionDomain.Select) {
            var condition = DocumentLowering.LowerValue(expr: call.Arguments[0].Value, scope: scope, fieldKey: fieldKey);

            return DocumentLowering.LowerValue(
                expr: call.Arguments[DocumentLowering.IsTruthy(node: condition) ? 1 : 2].Value,
                scope: scope,
                fieldKey: fieldKey);
        }

        var numbers = new double[function.Arity];

        for (var index = 0; (index < function.Arity); ++index) {
            var lowered = DocumentLowering.LowerValue(expr: call.Arguments[index].Value, scope: scope, fieldKey: fieldKey);

            if (!DocumentLowering.TryReadNumber(node: lowered, number: out numbers[index])) {
                return Refuse(scope: scope, call: call, message: $"{call.Name} reads numbers known at compile time");
            }
        }

        if (function.Domain == ExpressionDomain.Integer) {
            return Delegated(call: call, function: function, numbers: numbers, scope: scope);
        }

        return DocumentLowering.NumberNode(value: call.Name switch {
            "absolute" => Math.Abs(value: numbers[0]),
            "ceiling" => Math.Ceiling(a: numbers[0]),
            "clamp" => Math.Clamp(value: numbers[0], min: numbers[1], max: numbers[2]),
            "cosine" => Math.Cos(d: numbers[0]),
            "floor" => Math.Floor(d: numbers[0]),
            "maximum" => Math.Max(val1: numbers[0], val2: numbers[1]),
            "minimum" => Math.Min(val1: numbers[0], val2: numbers[1]),
            "round" => Math.Round(value: numbers[0], mode: MidpointRounding.ToEven),
            "sign" => Math.Sign(value: numbers[0]),
            "sine" => Math.Sin(a: numbers[0]),
            "squareRoot" => Math.Sqrt(d: numbers[0]),
            _ => 0,
        });
    }

    // An integer-domain function is the engine's to evaluate: integers are exact in both languages, so calling the
    // rule evaluator is what makes a numeric disagreement impossible rather than merely unlikely.
    private static JsonNode? Delegated(CallExpressionNode call, ExpressionFunction function, double[] numbers, DocumentScope scope) {
        var arguments = new long[function.Arity];

        for (var index = 0; (index < numbers.Length); ++index) {
            if (numbers[index] != Math.Truncate(d: numbers[index])) {
                return Refuse(scope: scope, call: call, message: $"{call.Name} reads whole numbers");
            }

            arguments[index] = (long)numbers[index];
        }

        if (!ExpressionArithmetic.TryFunction(operation: function.Operation, kind: CellKind.Int, arguments: arguments, value: out var value)) {
            return Refuse(scope: scope, call: call, message: $"{call.Name} is not defined for those arguments");
        }

        return JsonValue.Create(value: value);
    }

    private static JsonNode? Refuse(DocumentScope scope, CallExpressionNode call, string message) {
        scope.Diagnostics.ReportError(PuckDiagnosticCodes.BuiltinRefused, message, call.Span);

        return null;
    }
}
