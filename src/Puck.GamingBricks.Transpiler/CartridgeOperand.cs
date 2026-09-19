using System.Globalization;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;

using Puck.GamingBricks.Forge;

namespace Puck.GamingBricks.Transpiler;

/// <summary>Reads and writes a cartridge operand: a <c>ExpressionProgram</c> for a read, and a state-and-key pair for a
/// write.</summary>
/// <remarks>An expression is carried as its canonical infix spelling, which is the same text an author wrote, so a
/// compiled cartridge reads as arithmetic rather than as nested operand objects. The one thing this does beyond
/// parsing is substitute the document's compile-time bindings: a <c>let</c> or <c>for</c> name is not machine state,
/// so it is replaced by the value it stands for wherever it appears in the expression, at any depth.</remarks>
public static class CartridgeOperand {
    private static decimal? Number(JsonValue value) {
        if (value.TryGetValue<decimal>(value: out var exact)) { return exact; }
        if (value.TryGetValue<long>(value: out var integral)) { return integral; }
        if (value.TryGetValue<int>(value: out var narrow)) { return narrow; }
        if (
            value.TryGetValue<double>(value: out var real) &&
            double.IsFinite(d: real) &&
            (real >= 0) &&
            (real <= CartridgeLimits.WideMaximum)
        ) {
            return DecimalValues.FromDouble(value: real);
        }

        return null;
    }
    private static List<Instruction>? Resolve(IReadOnlyList<Instruction> tokens, DocumentScope scope, out string? reason) {
        using var evaluation = scope.Budget.Enter(span: default);

        reason = null;

        var result = new List<Instruction>(capacity: tokens.Count);

        foreach (var token in tokens) {
            switch (token) {
                case { Payload: InstructionPayload.Constant constant }:
                    // The widest slot a document may declare. Whether a literal fits the slot it is actually paired
                    // with is the forge validator's question, since only it knows that slot's declared ceiling.
                    if (
                        (constant.Value != decimal.Truncate(d: constant.Value)) ||
                        (constant.Value < 0) ||
                        (constant.Value > CartridgeLimits.WideMaximum)
                    ) {
                        reason = $"'{constant.Value}' is not a whole number in 0..{CartridgeLimits.WideMaximum}";

                        return null;
                    }

                    result.Add(item: token);

                    break;
                case { Payload: InstructionPayload.State { Key: null } state }: {
                        // A name bound by a `for` is not machine state either, and it shadows a constant of the same
                        // name: the loop already lowered its value, so it needs no second pass.
                        if (!scope.TryLowerBinding(
                            state.Name,
                            out var bound
                        )) {
                            result.Add(item: token);

                            break;
                        }

                        var substituted = Substitute(
                            node: bound,
                            reason: out reason,
                            scope: scope
                        );

                        if (substituted is null) {
                            return null;
                        }

                        result.AddRange(collection: substituted);

                        break;
                    }
                case { Payload: InstructionPayload.State state }: {
                        ExpressionProgram? index;

                        try {
                            index = CartridgeExpressions.Index(key: state.Key);
                        } catch (FormatException error) {
                            reason = $"the index of '{state.Name}' does not parse: {error.Message}";

                            return null;
                        }

                        if (index is null) {
                            reason = $"'{state.Name}' is indexed by nothing";

                            return null;
                        }

                        var inner = Resolve(
                            tokens: index.Instructions,
                            scope: scope,
                            reason: out reason
                        );

                        if (inner is null) {
                            return null;
                        }

                        result.Add(item: Instruction.Operand(
                            key: CartridgeExpressions.Key(index: new ExpressionProgram(Instructions: inner)),
                            name: state.Name
                        ));

                        break;
                    }
                default:
                    result.Add(item: token);

                    break;
            }
        }

        return result;
    }
    private static List<Instruction>? Substitute(JsonNode? node, DocumentScope scope, out string? reason) {
        reason = null;

        if (node is not JsonValue value) {
            reason = "a bound name stands for a number or an expression";

            return null;
        }

        if (value.TryGetValue<string>(value: out var text)) {
            if (!ExpressionSpelling.TryParse(
                error: out var error,
                text: text,
                program: out var parsed
            )) {
                reason = error;

                return null;
            }

            return Resolve(
                reason: out reason,
                scope: scope,
                tokens: parsed.Instructions
            );
        }

        // A lowered number arrives in whichever numeric backing the value lowering produced, so each is asked for
        // rather than assuming one.
        if (Number(value: value) is { } number) {
            if (
                (number != decimal.Truncate(d: number)) ||
                (number < 0) ||
                (number > CartridgeLimits.WideMaximum)
            ) {
                reason = $"'{number}' is not a whole number in 0..{CartridgeLimits.WideMaximum}";

                return null;
            }

            return [Instruction.Constant(value: number)];
        }

        reason = "a bound name stands for a number or an expression";

        return null;
    }

    /// <summary>Parses one operand's source text into an expression with every compile-time binding substituted.</summary>
    /// <param name="text">The text as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="reason">Why the conversion failed, or <see langword="null"/>.</param>
    /// <returns>The expression, or <see langword="null"/> when <paramref name="reason"/> says why not.</returns>
    public static ExpressionProgram? Expression(string text, DocumentScope scope, out string? reason) {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(text);

        if (!ExpressionSpelling.TryParse(
            error: out var error,
            text: text,
            program: out var parsed
        )) {
            reason = error;

            return null;
        }

        var resolved = Resolve(
            reason: out reason,
            scope: scope,
            tokens: parsed.Instructions
        );

        return ((resolved is null)
            ? null
            : new ExpressionProgram(Instructions: resolved)
        );
    }
    /// <summary>Lowers an authored expression without folding machine state as compile-time data.</summary>
    /// <param name="expression">The parsed expression.</param>
    /// <param name="scope">The compile-time bindings.</param>
    /// <param name="reason">The refusal, if any.</param>
    /// <returns>The canonical runtime operand.</returns>
    public static JsonNode? FromExpression(ExpressionNode expression, DocumentScope scope, out string? reason) {
        using var evaluation = scope.Budget.Enter(span: expression.Span);

        if (!Runtime(node: expression)) {
            return FromLoweredValue(
                DocumentLowering.LowerValue(
                    expression,
                    scope
                ),
                scope,
                out reason
            );
        }
        return FromText(
            Spell(node: expression),
            scope,
            out reason
        );

        bool Runtime(ExpressionNode node) {
            using var checking = scope.Budget.Enter(span: node.Span);

            return node switch {
                IdentifierExpressionNode name => (!scope.Constants.ContainsKey(key: name.Name) && !scope.Locals.ContainsKey(key: name.Name)),
                BinaryExpressionNode binary => (Runtime(node: binary.Left) || Runtime(node: binary.Right)),
                UnaryExpressionNode unary => Runtime(node: unary.Operand),
                IndexExpressionNode index => (Runtime(node: index.Target) || Runtime(node: index.Index)),
                CallExpressionNode call => call.Arguments.Any(predicate: argument => Runtime(node: argument.Value)),
                _ => false,
            };
        }
        string Spell(ExpressionNode node) {
            using var spelling = scope.Budget.Enter(span: node.Span);

            if (!Runtime(node: node)) {
                var lowered = FromLoweredValue(
                    DocumentLowering.LowerValue(
                        node,
                        scope
                    ),
                    scope,
                    out var error
                );

                if (error is not null) {
                    throw new DocumentEvaluationException(
                    error,
                    node.Span,
                    Puck.Transpiler.Diagnostics.PuckDiagnosticCodes.InvalidValue
                );
                }
                return lowered!.GetValue<string>();
            }
            return node switch {
                IdentifierExpressionNode name => $"`{name.Name}`",
                BinaryExpressionNode binary => $"({Spell(node: binary.Left)} {binary.Operator} {Spell(node: binary.Right)})",
                UnaryExpressionNode unary => $"({unary.Operator}{Spell(node: unary.Operand)})",
                IndexExpressionNode index => $"{Spell(node: index.Target)}[{Spell(node: index.Index)}]",
                CallExpressionNode call => $"{call.Name}({string.Join(
                separator: ", ",
                values: call.Arguments.Select(selector: argument => Spell(node: argument.Value))
            )})",
                _ => throw new DocumentEvaluationException(
                "This value is not a cartridge expression.",
                node.Span
            ),
            };
        }
    }
    /// <summary>Converts an already-lowered JSON value into an expression node, for a call argument the generic value
    /// lowering has already turned into a number or a name.</summary>
    /// <param name="node">The lowered node.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="reason">Why the conversion failed, or <see langword="null"/>.</param>
    /// <returns>The operand, or <see langword="null"/> when <paramref name="reason"/> says why not.</returns>
    public static JsonNode? FromLoweredValue(JsonNode? node, DocumentScope scope, out string? reason) {
        ArgumentNullException.ThrowIfNull(scope);

        reason = null;

        if (node is not JsonValue value) {
            reason = "expected a number, a state name or an expression";

            return null;
        }

        if (value.TryGetValue<string>(value: out var name)) {
            return FromText(
                reason: out reason,
                scope: scope,
                text: name
            );
        }

        if (Number(value: value) is { } number) {
            if (
                (number != decimal.Truncate(d: number)) ||
                (number < 0) ||
                (number > CartridgeLimits.WideMaximum)
            ) {
                reason = $"{number.ToString(provider: CultureInfo.InvariantCulture)} is not a whole number in 0..{CartridgeLimits.WideMaximum}";

                return null;
            }

            return JsonValue.Create(value: number.ToString(provider: CultureInfo.InvariantCulture));
        }

        reason = "expected a number, a state name or an expression";

        return null;
    }
    /// <summary>Converts a parsed row reference into a write target.</summary>
    /// <param name="rowRef">The reference, whose key — when present — is the array index.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="reason">Why the conversion failed, or <see langword="null"/>.</param>
    /// <returns>The target, or <see langword="null"/> when <paramref name="reason"/> says why not.</returns>
    public static JsonObject? FromRowRef(RowRefNode rowRef, DocumentScope scope, out string? reason) {
        ArgumentNullException.ThrowIfNull(rowRef);

        reason = null;

        if (rowRef.Key is null) {
            return new JsonObject { ["state"] = rowRef.Name };
        }

        var index = Expression(
            text: rowRef.Key,
            scope: scope,
            reason: out reason
        );

        if (index is null) {
            return null;
        }

        return new JsonObject {
            ["state"] = rowRef.Name,
            ["key"] = CartridgeExpressions.Key(index: index),
        };
    }
    /// <summary>Converts one operand's source text into an expression node.</summary>
    /// <param name="text">The text as written.</param>
    /// <param name="scope">The lowering scope, whose <c>let</c> and <c>for</c> bindings resolve first.</param>
    /// <param name="reason">Why the conversion failed, or <see langword="null"/>.</param>
    /// <returns>The operand, or <see langword="null"/> when <paramref name="reason"/> says why not.</returns>
    public static JsonNode? FromText(string text, DocumentScope scope, out string? reason) {
        var expression = Expression(
            reason: out reason,
            scope: scope,
            text: text
        );

        if (expression is null) {
            return null;
        }

        return JsonValue.Create(value: ExpressionSpelling.Print(instructions: expression.Instructions));
    }
    /// <summary>Writes a write target back as the source text that addresses it.</summary>
    /// <param name="node">The target node.</param>
    /// <returns>The source spelling.</returns>
    public static string TargetToSource(JsonNode? node) {
        if (node is not JsonObject target) {
            return "0";
        }

        var state = (target["state"]?.GetValue<string>() ?? "0");

        if (target["key"]?.GetValue<string>() is not { } key) {
            return state;
        }

        return $"{state}[{ExpressionSpelling.Print(instructions: (CartridgeExpressions.Index(key: key)?.Instructions ?? []))}]";
    }
    /// <summary>Writes an operand back as the source text that reads it.</summary>
    /// <param name="node">The operand node.</param>
    /// <returns>The source spelling.</returns>
    /// <exception cref="FormatException">The operand carries the postfix token spelling, which a cartridge document
    /// never writes: every expression a cartridge carries serializes as its infix spelling.</exception>
    public static string ToSource(JsonNode? node) => (node switch {
        JsonObject => throw new FormatException(message: "a cartridge operand is read from its infix spelling, not from a token list"),
        JsonValue value when value.TryGetValue<string>(value: out var text) => text,
        JsonValue value when value.TryGetValue<decimal>(value: out var number) => number.ToString(provider: CultureInfo.InvariantCulture),
        _ => "0",
    });
}
