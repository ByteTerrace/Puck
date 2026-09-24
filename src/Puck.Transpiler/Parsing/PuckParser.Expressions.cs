using Parlot.Fluent;
using Puck.State;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Parsing;

// A document value's expression layer: the lambda, the conditional, the range, the infix operators read from the
// rule language's operator table, and the prefix operators, each over the postfix and primary forms the main file
// reads.
public static partial class PuckParser {
    private static ExpressionNode ParseExpression(ParseContext context) {
        if (TryParseLambda(context: context) is { } lambda) {
            return lambda;
        }

        return ParseConditionalExpression(context: context);
    }

    /// <summary>The level a conditional binds at: looser than every infix operator, as the rule language reads
    /// <c>condition ? whenTrue : whenFalse</c>.</summary>
    internal const int ConditionalLevel = 1;

    // `condition ? whenTrue : whenFalse`, associating to the right so `a ? x : b ? y : z` reads as a chain. A `??`
    // never reaches here: the infix reader has already taken it as the coalesce operator.
    private static ExpressionNode ParseConditionalExpression(ParseContext context, int depth = 0) {
        if (depth >= ExpressionSpelling.MaxNesting) { throw CreateException(context: context, message: $"Conditionals nest at most {ExpressionSpelling.MaxNesting} levels"); }
        var condition = ParseInfixExpression(
            context: context,
            minimum: 0
        );
        var cursor = context.Scanner.Cursor;

        SkipWhiteSpace(context: context);

        if (cursor.Current != '?') {
            return condition;
        }

        cursor.Advance();

        var whenTrue = ParseConditionalExpression(context: context, depth: (depth + 1));

        SkipWhiteSpace(context: context);

        if (!TryConsume(c: ':', context: context)) {
            throw CreateException(context: context, message: "A conditional reads 'condition ? whenTrue : whenFalse'; expected ':'");
        }

        var whenFalse = ParseConditionalExpression(context: context, depth: (depth + 1));

        return new ConditionalExpressionNode(Condition: condition, WhenTrue: whenTrue, WhenFalse: whenFalse, Offset: condition.Offset, Length: (cursor.Offset - condition.Offset), Line: condition.Line, Column: condition.Column);
    }

    /// <summary>The level a primary, and a unary operator over one, binds at: tighter than every infix
    /// operator.</summary>
    internal const int PrimaryLevel = int.MaxValue;

    /// <summary>The level a range binds at: looser than a shift, so its ends take shifts and arithmetic whole, and
    /// tighter than a relational comparison, so a comparison takes the whole range as an operand.</summary>
    internal static readonly int RangeLevel = (InfixLevel(symbol: "<<") - 1);

    /// <summary>Returns the level an infix operator binds at in a document value: its operator table binding
    /// (<see cref="ExpressionOperator.Binding"/>), spread so the range, which the rule language does not spell, has a
    /// level of its own between two of them.</summary>
    /// <param name="symbol">The operator's symbol.</param>
    /// <returns>The level; <see cref="PrimaryLevel"/> for a symbol the table does not spell.</returns>
    internal static int InfixLevel(string symbol) => (ExpressionOperators.TryFindSymbol(
        descriptor: out var row,
        symbol: symbol
    )
        ? (row!.Binding * 2)
        : PrimaryLevel
    );

    // Precedence climbing over the rule language's own operator table, so a value and a rule operand group the same
    // text the same way. Every operator associates to the left, and a comparison yields 1 or 0 like any operator.
    private static ExpressionNode ParseInfixExpression(ParseContext context, int minimum) {
        var left = ((minimum <= RangeLevel)
            ? ParseRangeExpression(context: context)
            : ParseUnaryExpression(context: context)
        );

        while (true) {
            SkipWhiteSpace(context: context);

            if (MatchInfix(context: context) is not { } symbol) {
                return left;
            }

            var level = InfixLevel(symbol: symbol);

            if (level < minimum) {
                return left;
            }

            context.Scanner.Cursor.Advance(count: symbol.Length);

            var right = ParseInfixExpression(
                context: context,
                minimum: (level + 1)
            );

            left = new BinaryExpressionNode(Left: left, Operator: symbol, Right: right, Offset: left.Offset, Length: (context.Scanner.Cursor.Offset - left.Offset), Line: left.Line, Column: left.Column);
        }
    }
    private static string? MatchInfix(ParseContext context) {
        var buffer = context.Scanner.Buffer;
        var offset = context.Scanner.Cursor.Offset;

        foreach (var row in ExpressionOperators.Infix) {
            if (string.CompareOrdinal(
                indexA: offset,
                indexB: 0,
                length: row.Symbol!.Length,
                strA: buffer,
                strB: row.Symbol
            ) == 0) {
                return row.Symbol;
            }
        }

        return null;
    }
    // `item => body` and `(running, item) => body`. Tried before anything else an expression could be, and rewound
    // when the arrow is absent, because the parameter list is indistinguishable from an ordinary operand until it
    // arrives: `(a, b)` alone is a parenthesized expression and `item` alone is an identifier.
    private static ExpressionNode? TryParseLambda(ParseContext context) {
        var cursor = context.Scanner.Cursor;
        var savedPosition = cursor.Position;

        SkipWhiteSpace(context: context);

        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);
        var parameters = new List<string>();

        if (cursor.Current == '(') {
            cursor.Advance();
            SkipWhiteSpace(context: context);

            while (!cursor.Eof && (cursor.Current != ')')) {
                if (!TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var parameter)) {
                    cursor.ResetPosition(position: savedPosition);

                    return null;
                }

                parameters.Add(item: parameter);
                SkipWhiteSpace(context: context);

                if (cursor.Current == ',') {
                    cursor.Advance();
                    SkipWhiteSpace(context: context);
                }
            }

            if (!TryConsume(c: ')', context: context)) {
                cursor.ResetPosition(position: savedPosition);

                return null;
            }
        } else if (TryReadName(admitted: NameForms.Identifier, context: context, spelling: out _, text: out var single)) {
            parameters.Add(item: single);
        } else {
            cursor.ResetPosition(position: savedPosition);

            return null;
        }

        SkipWhiteSpace(context: context);

        if ((cursor.Current != '=') || (cursor.PeekNext() != '>')) {
            cursor.ResetPosition(position: savedPosition);

            return null;
        }

        cursor.Advance(count: 2);

        var body = ParseExpression(context: context);

        return new LambdaExpressionNode(Parameters: parameters, Body: body, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col);
    }
    private static ExpressionNode ParseRangeExpression(ParseContext context) {
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;

        if ((cursor.Current == '.') && (cursor.PeekNext() == '.')) {
            var startOffset = cursor.Offset;

            var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);

            cursor.Advance(count: 2);
            SkipWhiteSpace(context: context);
            var right = ((cursor.Current is ')' or ',')
                ? null
                : ParseRangeEnd(context: context)
            );

            return new RangeExpressionNode(Start: null, End: right, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col);
        }

        var left = ParseRangeEnd(context: context);

        SkipWhiteSpace(context: context);

        if ((context.Scanner.Cursor.Current == '.') && (context.Scanner.Cursor.PeekNext() == '.')) {
            context.Scanner.Cursor.Advance(count: 2);
            SkipWhiteSpace(context: context);
            var right = ((context.Scanner.Cursor.Current is ')' or ',')
                ? null
                : ParseRangeEnd(context: context)
            );
            var len = (context.Scanner.Cursor.Offset - left.Offset);

            return new RangeExpressionNode(Start: left, End: right, Offset: left.Offset, Length: len, Line: left.Line, Column: left.Column);
        }

        return left;
    }
    private static ExpressionNode ParseRangeEnd(ParseContext context) => ParseInfixExpression(
        context: context,
        minimum: (RangeLevel + 1)
    );
    // The rule language's two prefix operators. A minus in front of anything that is not a number: `-spread`,
    // `-scale(2)`, `-(a + b)`. A number keeps its own signed-literal reader (ParsePrimaryExpression), because folding
    // the sign into the literal is what lets a unit suffix read against the value it signs; intercepting `-1.5m` here
    // would sign a unit-converted quantity instead. A bitwise complement `~` has no literal form, so it is read here in
    // front of anything.
    private static ExpressionNode ParseUnaryExpression(ParseContext context, int depth = 0) {
        if (depth >= 64) { throw CreateException(context: context, message: "Unary expressions nest at most 64 levels"); }
        SkipWhiteSpace(context: context);
        var cursor = context.Scanner.Cursor;

        if (cursor.Current == '+') {
            throw CreateException(context: context, message: "'+' is not a prefix operator; a value is written without a sign");
        }
        if (cursor.Current is not ('-' or '~')) {
            return ParsePostfixExpression(context: context);
        }

        var next = cursor.PeekNext();

        if ((cursor.Current != '~') && (char.IsDigit(c: next) || (next == '.'))) {
            return ParsePostfixExpression(context: context);
        }

        var startOffset = cursor.Offset;

        var (line, col) = GetLineAndColumn(buffer: context.Scanner.Buffer, offset: startOffset);
        var op = cursor.Current.ToString();

        cursor.Advance();

        var operand = ParseUnaryExpression(context: context, depth: (depth + 1));

        return new UnaryExpressionNode(Operator: op, Operand: operand, Offset: startOffset, Length: (cursor.Offset - startOffset), Line: line, Column: col);
    }
}
