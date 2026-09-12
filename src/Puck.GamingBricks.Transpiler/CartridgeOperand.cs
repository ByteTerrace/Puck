using System.Globalization;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;

namespace Puck.GamingBricks.Transpiler;

/// <summary>Reads and writes a <c>CartridgeValue</c>: exactly one of a literal byte, a named variable, or an
/// element of a named array.</summary>
public static class CartridgeOperand {
    /// <summary>Converts a parsed row reference into an operand.</summary>
    /// <param name="rowRef">The reference, whose key — when present — is the array index.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="reason">Why the conversion failed, or <see langword="null"/>.</param>
    /// <returns>The operand, or <see langword="null"/> when <paramref name="reason"/> says why not.</returns>
    public static JsonObject? FromRowRef(RowRefNode rowRef, DocumentScope scope, out string? reason) {
        ArgumentNullException.ThrowIfNull(rowRef);

        reason = null;

        if (rowRef.Key is null) {
            return new JsonObject { ["variable"] = rowRef.Name };
        }

        var index = FromText(text: rowRef.Key, scope: scope, reason: out reason);

        if (index is null) {
            return null;
        }

        return new JsonObject {
            ["array"] = rowRef.Name,
            ["index"] = index,
        };
    }

    /// <summary>Converts one operand's source text into an operand.</summary>
    /// <param name="text">The text as written: a literal, a name, or <c>name[index]</c>.</param>
    /// <param name="scope">The lowering scope, whose <c>let</c> bindings a bare name is resolved against first.</param>
    /// <param name="reason">Why the conversion failed, or <see langword="null"/>.</param>
    /// <returns>The operand, or <see langword="null"/> when <paramref name="reason"/> says why not.</returns>
    /// <remarks>Parsed through <see cref="ExpressionSpelling"/> rather than by hand, so the DSL and a compiled rule
    /// can never disagree about what a name or a number looks like. A cartridge operand is one token: the hardware
    /// evaluates no stack.</remarks>
    public static JsonObject? FromText(string text, DocumentScope scope, out string? reason) {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(text);

        if (!ExpressionSpelling.TryParse(text, out var tokens, out var error)) {
            reason = error;

            return null;
        }

        if (tokens.Count != 1) {
            reason = $"'{text}' is an expression; a cartridge operand is one literal, variable or array element";

            return null;
        }

        reason = null;

        switch (tokens[0]) {
            case ValueToken.Constant constant: {
                if ((constant.Value != decimal.Truncate(d: constant.Value)) || (constant.Value < 0) || (constant.Value > 255)) {
                    reason = $"'{text}' is not a whole number in 0..255";

                    return null;
                }

                return new JsonObject { ["constant"] = (int)constant.Value };
            }

            case ValueToken.State state: {
                if (state.Key is null) {
                    // A `let` name is not a machine variable: it stands for the value it was bound to, resolved at
                    // compile time exactly as it would be anywhere else in the document.
                    if (scope.Constants.TryGetValue(key: state.Name, value: out var constant)) {
                        return FromLoweredValue(node: DocumentLowering.LowerValue(expr: constant, scope: scope), scope: scope, reason: out reason);
                    }

                    return new JsonObject { ["variable"] = state.Name };
                }

                var index = FromText(text: state.Key, scope: scope, reason: out reason);

                if (index is null) {
                    return null;
                }

                return new JsonObject {
                    ["array"] = state.Name,
                    ["index"] = index,
                };
            }

            default:
                reason = $"'{text}' is not a literal, variable or array element";

                return null;
        }
    }

    /// <summary>Converts an already-lowered JSON value into an operand, for a call argument the generic value
    /// lowering has already turned into a number or a name.</summary>
    /// <param name="node">The lowered node.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="reason">Why the conversion failed, or <see langword="null"/>.</param>
    /// <returns>The operand, or <see langword="null"/> when <paramref name="reason"/> says why not.</returns>
    public static JsonObject? FromLoweredValue(JsonNode? node, DocumentScope scope, out string? reason) {
        ArgumentNullException.ThrowIfNull(scope);

        reason = null;

        if (node is JsonObject alreadyOperand) {
            // A `field[k]` argument reaches here through the DSL's own indexing, already in operand shape.
            if (alreadyOperand.ContainsKey(propertyName: "variable") || alreadyOperand.ContainsKey(propertyName: "constant") || alreadyOperand.ContainsKey(propertyName: "array")) {
                return alreadyOperand;
            }
        }

        if (node is not JsonValue value) {
            reason = "expected a literal, variable or array element";

            return null;
        }

        if (value.TryGetValue<string>(value: out var name)) {
            return FromText(text: name, scope: scope, reason: out reason);
        }

        if (value.TryGetValue<long>(value: out var number)) {
            if ((number < 0) || (number > 255)) {
                reason = $"{number.ToString(provider: CultureInfo.InvariantCulture)} is not in 0..255";

                return null;
            }

            return new JsonObject { ["constant"] = (int)number };
        }

        reason = "expected a literal, variable or array element";

        return null;
    }

    /// <summary>Writes an operand back as the source text that reads it.</summary>
    /// <param name="node">The operand node.</param>
    /// <returns>The source spelling.</returns>
    public static string ToSource(JsonNode? node) {
        if (node is not JsonObject obj) {
            return "0";
        }

        if (obj["variable"] is { } variable) {
            return (variable.GetValue<string>());
        }

        if (obj["array"] is { } array) {
            return $"{array.GetValue<string>()}[{ToSource(node: obj["index"])}]";
        }

        if (obj["constant"] is { } constant) {
            return constant.GetValue<int>().ToString(provider: CultureInfo.InvariantCulture);
        }

        return "0";
    }
}
