using System.Globalization;
using System.Text;

namespace Puck.State;

public static partial class ExpressionSpelling {
    /// <summary>Prints unresolved operand syntax without interpreting names or interpolated atoms.</summary>
    /// <param name="syntax">The source tree.</param>
    /// <param name="atom">Prints an atom by its source-language index.</param>
    /// <returns>A spelling that retains the tree's grouping and literal names.</returns>
    /// <exception cref="ArgumentException">The tree exceeds the nesting bound or contains runtime-only syntax.</exception>
    public static string PrintSyntax(SyntaxNode syntax, Func<int, string> atom) {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(atom);
        var into = new StringBuilder();

        Write(depth: 0, node: syntax);
        return into.ToString();

        void Write(SyntaxNode node, int depth) {
            if (depth > MaxNesting) { throw new ArgumentException(message: "The source tree exceeds the nesting bound.", paramName: nameof(syntax)); }
            switch (node) {
                case Literal literal: into.Append(value: literal.Value.ToString(provider: CultureInfo.InvariantCulture)); break;
                case SourceName name: into.Append(value: PrintName(name: name)); break;
                case SourceAtom value: into.Append(value: atom(value.Index)); break;
                case SourceString value:
                    into.Append(value: '"').Append(value: value.Value.Replace(comparisonType: StringComparison.Ordinal, newValue: "\\\\", oldValue: "\\").Replace(comparisonType: StringComparison.Ordinal, newValue: "\\\"", oldValue: "\"")).Append(value: '"');
                    break;
                case SourceGroup group:
                    into.Append(value: '('); Write(group.Value, (depth + 1)); into.Append(value: ')'); break;
                case SourceIndex index:
                    Write(index.Target, (depth + 1)); into.Append(value: '['); Write(index.Index, (depth + 1)); into.Append(value: ']'); break;
                case SourceAccess access:
                    Write(access.Target, (depth + 1)); into.Append(value: '.').Append(value: access.Member); break;
                case Unary unary:
                    into.Append(value: unary.Operator).Append(value: '('); Write(unary.Operand, (depth + 1)); into.Append(value: ')'); break;
                case Binary binary:
                    into.Append(value: '('); Write(binary.Left, (depth + 1)); into.Append(value: ' ').Append(value: binary.Operator).Append(value: ' ');
                    Write(binary.Right, (depth + 1)); into.Append(value: ')'); break;
                case Ternary ternary:
                    into.Append(value: '('); Write(ternary.Condition, (depth + 1)); into.Append(value: " ? "); Write(ternary.WhenTrue, (depth + 1));
                    into.Append(value: " : "); Write(ternary.WhenFalse, (depth + 1)); into.Append(value: ')'); break;
                case SourceCall call:
                    into.Append(value: call.Name).Append(value: '(');
                    for (var index = 0; (index < call.Arguments.Count); index++) {
                        if (index > 0) { into.Append(value: ", "); }
                        var argument = call.Arguments[index];

                        if (argument.Name is { } name) { into.Append(value: name).Append(value: ": "); }
                        Write(argument.Value, (depth + 1));
                    }
                    into.Append(value: ')'); break;
                case SourceLambda lambda:
                    into.Append(value: lambda.Binder).Append(value: " -> "); Write(lambda.Body, (depth + 1)); break;
                default: throw new ArgumentException(message: "Only source syntax can be printed as an operand.", paramName: nameof(syntax));
            }
        }
    }
}
