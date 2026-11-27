using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Diagnostics;

/// <summary>The one reading of "what did the author write here" for a message that says what a value was expected to
/// be and what it is instead: a message names the written value's kind in words (a comparison, a calculation, text),
/// never as a syntax-tree type.</summary>
public static class ExpressionDescription {
    private static readonly HashSet<string> Comparisons = new(collection: ["==", "!=", "<", "<=", ">", ">="], comparer: StringComparer.Ordinal);

    /// <summary>Describes the value an expression writes, with the source text of the part that identifies it.</summary>
    /// <param name="expression">The expression the author wrote.</param>
    /// <returns>A phrase with its article, such as <c>a comparison ('a &gt; b')</c>, <c>a calculation</c>,
    /// <c>text</c>, <c>a number</c>, <c>true or false</c> or <c>the name 'speed'</c>.</returns>
    public static string Of(ExpressionNode expression) {
        ArgumentNullException.ThrowIfNull(argument: expression);

        return expression switch {
            LiteralExpressionNode { Unit: not null } => "a number with a unit",
            LiteralExpressionNode { Value: bool } => "true or false",
            LiteralExpressionNode { Value: string } => "text",
            LiteralExpressionNode => "a number",
            IdentifierExpressionNode named => $"the name '{named.Name}'",
            CallExpressionNode call => $"a call to '{call.Name}'",
            BinaryExpressionNode binary when Comparisons.Contains(item: binary.Operator) => $"a comparison ('{binary.Operator}')",
            BinaryExpressionNode binary when (binary.Operator is "and" or "or") => $"a logical '{binary.Operator}'",
            BinaryExpressionNode binary => $"a calculation ('{binary.Operator}')",
            ObjectExpressionNode => "a block",
            ArrayExpressionNode => "a list",
            InterpolatedStringNode => "an interpolated string",
            _ => "an expression",
        };
    }
}
