using Puck.State;

namespace Puck.GamingBricks.Forge;

/// <summary>
/// The subset of <see cref="ValueExpression"/> a cartridge evaluates, and the walk both backends share to emit one.
/// </summary>
/// <remarks>
/// <para>The rule language evaluates well over a hundred token kinds — hex lattices, Hilbert indices, primes,
/// combinatorial ranks. A cartridge evaluates what an eight-bit machine can spend a handful of instructions on, so
/// an operation outside <see cref="Reads"/> is refused BY NAME at the spelling the author wrote rather than reported
/// as unknown. This is the same posture <see cref="CartridgeOperations"/> takes for a write's combining operation.</para>
/// <para>Evaluation is in the operand width, which is what a single combining step already does: a byte expression
/// wraps modulo 256 at every step, and a sub-expression does not silently widen because it is nested. Widening the
/// evaluator is one change in one place once both machines carry sixteen-bit multiply, divide and modulo.</para>
/// <para>KEEP IN SYNC with both backends' token switches: an operation admitted here that a backend has no arm for
/// fails a compile rather than a validation, which is the wrong end to learn it from.</para>
/// </remarks>
public static class CartridgeExpressions {
    /// <summary>The reserved operand prefix a held or edged button reads through: <c>$key:&lt;button&gt;:&lt;mode&gt;</c>,
    /// yielding 1 while the button satisfies the mode and 0 otherwise.</summary>
    public const string KeyPrefix = "$key:";

    /// <summary>The deepest value stack an expression may need. A machine spends its own stack on the operands in
    /// flight, so this bounds what one expression can hold rather than how many tokens it carries.</summary>
    public const int MaxDepth = 8;

    /// <summary>The most tokens one expression may carry.</summary>
    public const int MaxTokens = 64;

    /// <summary>The operations a cartridge evaluates. Everything else is refused by its own spelling.</summary>
    public static IReadOnlySet<ExpressionOp> Reads { get; } = new HashSet<ExpressionOp> {
        ExpressionOp.Add,
        ExpressionOp.Subtract,
        ExpressionOp.Multiply,
        ExpressionOp.Divide,
        ExpressionOp.Modulo,
        ExpressionOp.BitAnd,
        ExpressionOp.BitOr,
        ExpressionOp.BitXor,
        ExpressionOp.BitNot,
        ExpressionOp.ShiftLeft,
        ExpressionOp.ShiftRight,
        ExpressionOp.Equal,
        ExpressionOp.NotEqual,
        ExpressionOp.Less,
        ExpressionOp.LessOrEqual,
        ExpressionOp.Greater,
        ExpressionOp.GreaterOrEqual,
        ExpressionOp.Minimum,
        ExpressionOp.Maximum,
        ExpressionOp.Clamp,
        ExpressionOp.Select,
        ExpressionOp.Negate,
        ExpressionOp.Sign,
    };

    /// <summary>Creates an expression reading one literal.</summary>
    /// <param name="constant">The literal.</param>
    /// <returns>The expression.</returns>
    public static ValueExpression Of(int constant) => new(Tokens: [new ValueToken.Constant(Value: constant)]) { Text = constant.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>Creates an expression reading one state slot, or one array element.</summary>
    /// <param name="state">The declared state or array name.</param>
    /// <param name="key">The element index for an array, or null for a slot.</param>
    /// <returns>The expression.</returns>
    public static ValueExpression Of(string state, string? key = null) {
        var tokens = new ValueToken[] { new ValueToken.State(Name: state, Key: key) };

        return new ValueExpression(Tokens: tokens) { Text = ExpressionSpelling.Print(tokens: tokens) };
    }

    /// <summary>Creates an expression reading one button, yielding 1 while it satisfies the mode and 0 otherwise.</summary>
    /// <param name="button">a, b, start, select, up, down, left or right.</param>
    /// <param name="mode">held, pressed or released.</param>
    /// <returns>The expression.</returns>
    public static ValueExpression Button(string button, string mode) => Of(state: $"{KeyPrefix}{button}:{mode}");

    /// <summary>Creates a gate comparing two expressions in the whole-number domain.</summary>
    /// <param name="left">The left expression.</param>
    /// <param name="comparison">The comparison.</param>
    /// <param name="right">The right expression.</param>
    /// <returns>The predicate.</returns>
    /// <remarks>A cartridge carries no fixed-point domain, so the comparison's kind is never anything else; this is the
    /// spelling that cannot get it wrong.</remarks>
    public static ActionPredicate Gate(ValueExpression left, ActionStateComparison comparison, ValueExpression right) =>
        new ActionPredicate.CompareValue(Left: left, Comparison: comparison, Right: right, Kind: CellKind.Int);

    /// <summary>Creates a gate that holds while a button satisfies a mode.</summary>
    /// <param name="button">a, b, start, select, up, down, left or right.</param>
    /// <param name="mode">held, pressed or released.</param>
    /// <returns>The predicate.</returns>
    public static ActionPredicate Pressing(string button, string mode) =>
        Gate(left: Button(button: button, mode: mode), comparison: ActionStateComparison.Equal, right: Of(constant: 1));

    /// <summary>Creates a key addressing an array element by a computed index.</summary>
    /// <param name="index">The index expression.</param>
    /// <returns>The key.</returns>
    public static string Key(ValueExpression index) {
        ArgumentNullException.ThrowIfNull(argument: index);

        // The expression language reads a key in three shapes, and printing back the one it was written in is what
        // keeps a decompiled source recompiling to the same bytes: a bare number, a bare cell read, and � for anything
        // computed � the canonical infix behind the expression prefix.
        if (Whole(expression: index) is { } literal) {
            return literal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        }

        if (index.Tokens is [ValueToken.State state]) {
            return ((state.Key is null) ? state.Name : $"{RuleFacts.CellKeyPrefix}{state.Name}:{state.Key}");
        }

        return $"{RuleFacts.ExpressionKeyPrefix}{ExpressionSpelling.Print(tokens: index.Tokens)}";
    }

    /// <summary>Returns the whole number a one-token literal expression carries.</summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The literal, or <see langword="null"/> when the expression is anything else.</returns>
    public static int? Whole(ValueExpression? expression) =>
        (((expression?.Tokens is [ValueToken.Constant constant]) && (decimal.Truncate(d: constant.Value) == constant.Value))
            ? (int)constant.Value
            : null);

    /// <summary>Returns a value indicating whether a cartridge evaluates an operation.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns><see langword="true"/> when both backends have an arm for it.</returns>
    public static bool Admits(ExpressionOp operation) => Reads.Contains(item: operation);

    /// <summary>Splits a reserved button operand into its button and mode.</summary>
    /// <param name="name">The operand name.</param>
    /// <param name="button">The button on success.</param>
    /// <param name="mode">The mode on success — held, pressed or released.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a well-formed button read.</returns>
    public static bool TryKey(string name, out string button, out string mode) {
        button = "";
        mode = "";
        if ((name is null) || !name.StartsWith(value: KeyPrefix, comparisonType: StringComparison.Ordinal)) {
            return false;
        }

        var parts = name[KeyPrefix.Length..].Split(separator: ':');

        if (parts.Length != 2) {
            return false;
        }

        button = parts[0];
        mode = parts[1];

        return true;
    }

    /// <summary>Returns the index expression a keyed operand carries, or null when the key addresses nothing
    /// computed.</summary>
    /// <param name="key">The authored cell key.</param>
    /// <returns>The index expression, or <see langword="null"/> when <paramref name="key"/> is absent.</returns>
    /// <exception cref="FormatException">The key carries a spelling that does not parse as an expression.</exception>
    /// <remarks>A cartridge addresses an array element by a computed index, which the expression language spells as
    /// a key: a bare number is a constant index, and anything else arrives as the canonical infix spelling behind
    /// <see cref="RuleFacts.ExpressionKeyPrefix"/>. Both are one index expression here, so a backend compiles the
    /// index the same way it compiles any other operand.</remarks>
    public static ValueExpression? Index(string? key) {
        if (key is null) {
            return null;
        }
        if (key.StartsWith(value: RuleFacts.ExpressionKeyPrefix, comparisonType: StringComparison.Ordinal)) {
            return ValueExpression.Parse(text: key[RuleFacts.ExpressionKeyPrefix.Length..]);
        }
        // One element read through another: the expression language's own spelling for an index taken from a cell.
        if (key.StartsWith(value: RuleFacts.CellKeyPrefix, comparisonType: StringComparison.Ordinal)) {
            var rest = key[RuleFacts.CellKeyPrefix.Length..];
            var split = rest.IndexOf(value: ':');

            if (split < 0) {
                throw new FormatException(message: $"'{key}' names a row with no cell to read the index from.");
            }

            return Of(state: rest[..split], key: rest[(split + 1)..]);
        }
        if (int.TryParse(s: key, out var literal)) {
            return new ValueExpression(Tokens: [new ValueToken.Constant(Value: literal)]);
        }

        return ValueExpression.Parse(text: key);
    }

    /// <summary>Returns the deepest value stack <paramref name="expression"/> needs.</summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The maximum number of values in flight at once.</returns>
    /// <exception cref="ArgumentException">The token list is not a well-formed postfix expression.</exception>
    public static int Depth(ValueExpression expression) {
        ArgumentNullException.ThrowIfNull(argument: expression);

        var depth = 0;
        var most = 0;

        foreach (var token in expression.Tokens) {
            var arity = Arity(token: token);

            if (depth < arity) {
                throw new ArgumentException(message: $"'{Spell(token: token)}' takes {arity} values but only {depth} are in flight.", paramName: nameof(expression));
            }

            depth = ((depth - arity) + 1);
            most = Math.Max(val1: most, val2: depth);
        }

        if (depth != 1) {
            throw new ArgumentException(message: $"An expression must leave exactly one value; this one leaves {depth}.", paramName: nameof(expression));
        }

        return most;
    }

    /// <summary>Returns how many values a token consumes.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The count; zero for a constant or a state read.</returns>
    public static int Arity(ValueToken token) =>
        ((ExpressionVocabulary.Operation(token: token) is { } operation) ? ExpressionVocabulary.Arity(operation: operation) : 0);

    /// <summary>Returns the spelling a token is named by, for a refusal that quotes the author.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The function name, operator symbol, or payload description.</returns>
    public static string Spell(ValueToken token) => (token switch {
        ValueToken.Constant constant => constant.Value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
        ValueToken.State state => ((state.Key is { } key) ? $"{state.Name}[{key}]" : state.Name),
        _ => ((ExpressionVocabulary.Operation(token: token) is { } operation) ? ExpressionVocabulary.Spelling(operation: operation) : token.GetType().Name),
    });
}
