using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>The values a read or a compiled program can answer, as raw values of one cell kind: every value exactly
/// (a finite set), a closed range, or no bound at all. It is read from declarations (a row's declared range, a
/// register's envelope) and carried through a program by <see cref="RuleBounds"/>, so an analysis can prove that a
/// written value lands inside what its destination keeps.</summary>
public readonly struct ValueBounds {
    private readonly bool m_bounded;
    private readonly long[]? m_values;

    private ValueBounds(long lower, long upper, long[]? values) {
        Lower = lower;
        Upper = upper;
        m_bounded = true;
        m_values = values;
    }

    /// <summary>Gets the bound that admits every value: nothing is known.</summary>
    public static ValueBounds Unbounded => default;

    /// <summary>Gets whether every value is listed (<see cref="Values"/>).</summary>
    public bool IsExact => (m_values is not null);
    /// <summary>Gets whether nothing is known.</summary>
    public bool IsUnbounded => !m_bounded;
    /// <summary>Gets the least raw value; meaningless when <see cref="IsUnbounded"/>.</summary>
    public long Lower { get; }
    /// <summary>Gets the greatest raw value; meaningless when <see cref="IsUnbounded"/>.</summary>
    public long Upper { get; }
    /// <summary>Gets every raw value, ascending and distinct, or an empty span unless <see cref="IsExact"/>.</summary>
    public ReadOnlySpan<long> Values => m_values;

    /// <summary>Creates the closed range of raw values from <paramref name="lower"/> to <paramref name="upper"/>.</summary>
    /// <param name="lower">The least raw value.</param>
    /// <param name="upper">The greatest raw value.</param>
    /// <returns>The range, or <see cref="Unbounded"/> when <paramref name="lower"/> is above <paramref name="upper"/>.</returns>
    public static ValueBounds Between(long lower, long upper) => ((lower <= upper) ? new(lower: lower, upper: upper, values: null) : Unbounded);
    /// <summary>Creates the finite set of exactly these raw values.</summary>
    /// <param name="values">The raw values, in any order, repeats allowed.</param>
    /// <returns>The set, or <see cref="Unbounded"/> when <paramref name="values"/> is empty.</returns>
    public static ValueBounds Exactly(params ReadOnlySpan<long> values) {
        if (values.IsEmpty) {
            return Unbounded;
        }

        var sorted = values.ToArray();

        Array.Sort(array: sorted);

        var count = 1;

        for (var index = 1; (index < sorted.Length); index++) {
            if (sorted[index] != sorted[(count - 1)]) {
                sorted[count++] = sorted[index];
            }
        }

        Array.Resize(array: ref sorted, newSize: count);

        return new(lower: sorted[0], upper: sorted[^1], values: sorted);
    }

    /// <summary>Returns whether <paramref name="raw"/> is a value this bound admits.</summary>
    /// <param name="raw">The raw value.</param>
    /// <returns><see langword="true"/> when the value is listed, inside the range, or nothing is known.</returns>
    public bool Admits(long raw) => (!m_bounded || ((m_values is { } values) ? (Array.BinarySearch(array: values, value: raw) >= 0) : ((raw >= Lower) && (raw <= Upper))));
    /// <summary>Returns this bound widened to admit one more value: a set gains the value, a range stretches to it.</summary>
    /// <param name="raw">The raw value.</param>
    /// <returns>The widened bound.</returns>
    public ValueBounds With(long raw) => (!m_bounded
        ? this
        : ((m_values is { } values) ? Exactly(values: [.. values, raw]) : Between(lower: Math.Min(val1: Lower, val2: raw), upper: Math.Max(val1: Upper, val2: raw))));
    /// <summary>Returns whether every value this bound admits is one <paramref name="envelope"/> admits.</summary>
    /// <param name="envelope">The bound to lie inside.</param>
    /// <returns><see langword="true"/> when the inclusion is proven; <see langword="false"/> proves nothing.</returns>
    public bool Within(in ValueBounds envelope) {
        if (!envelope.m_bounded) {
            return true;
        }
        if (!m_bounded) {
            return false;
        }
        if (m_values is { } values) {
            foreach (var value in values) {
                if (!envelope.Admits(raw: value)) {
                    return false;
                }
            }

            return true;
        }

        return (envelope.IsExact ? ((Lower == Upper) && envelope.Admits(raw: Lower)) : ((envelope.Lower <= Lower) && (Upper <= envelope.Upper)));
    }
}
/// <summary>The bounds lowering of a compiled postfix program: the values the program can answer over the declared
/// values of what it reads (<see cref="RuleCompileContext.ReadBounds"/>). It answers every value exactly when the
/// reads' declared values are finite sets whose product is small, by running the one interpreter
/// (<see cref="RuleExpressions"/>) at each assignment of them, so a value read twice is one variable and correlated
/// terms stay correlated; otherwise it carries a closed range through the program over <see cref="FixedInterval"/>.</summary>
/// <remarks>A bound describes the values a program answers when it answers. A program that faults or answers an
/// absence for some assignment is unbounded, and so is one holding an operation the range carrier has no rule for.</remarks>
public static class RuleBounds {
    /// <summary>The most assignments of a program's reads the exact form evaluates.</summary>
    public const int MaxAssignments = 4096;

    // The whole numbers a Q48.16 value carries exactly; an integer outside them leaves the range carrier.
    private const long MaxWhole = ((1L << (FixedQ4816.IntegerBitCount - 2)) - 1L);

    /// <summary>Bounds the value a source reads.</summary>
    /// <param name="source">The literal, operand or program.</param>
    /// <param name="kind">The kind the source is read in.</param>
    /// <param name="context">The context the source compiled in.</param>
    /// <returns>The bound, in the raw encoding the read answers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static ValueBounds Of(in CompiledValueSource source, CellKind kind, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        if (source.Text is not null) {
            return ValueBounds.Unbounded;
        }
        if (source.IsLiteral) {
            return ValueBounds.Exactly(values: [source.RawValue]);
        }

        return ((source.Operand is { } operand)
            ? Of(context: context, kind: operand.ValueKind, program: [new CompiledExpressionToken(Operand: operand, Operation: ExpressionOp.Operand)])
            : Of(context: context, kind: (source.CarrierKind ?? kind), program: source.Expression!));
    }
    /// <summary>Bounds the value a compiled program answers.</summary>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind the program evaluates in.</param>
    /// <param name="context">The context the program compiled in.</param>
    /// <returns>The bound, in the raw encoding the program leaves.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static ValueBounds Of(ReadOnlySpan<CompiledExpressionToken> program, CellKind kind, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var assignments = new Assignments(context: context);

        return ((Template.Build(assignments: assignments, kind: kind, program: program) is { } template)
            ? assignments.Enumerate(template: template)
            : Range(context: context, kind: kind, program: program));
    }

    // The distinct reads of a program and the programs its locals bind, each with its declared values. Two reads the
    // context names as one variable are one, so a program that reads a value twice is evaluated with it held equal.
    private sealed class Assignments(RuleCompileContext context) {
        private readonly List<object> m_variables = [];
        private readonly List<long[]> m_domains = [];
        private long m_count = 1L;

        // The variable a read is, or -1 when its declared values are no finite set or the product outgrows the limit.
        public int Variable(IRuleOperand operand) {
            var bounds = context.ReadBounds(operand: operand, variable: out var named);

            if (!bounds.IsExact) {
                return -1;
            }

            var variable = (named ?? operand);
            var index = m_variables.IndexOf(item: variable);

            if (index >= 0) {
                return index;
            }
            if ((m_count *= bounds.Values.Length) > MaxAssignments) {
                return -1;
            }

            m_variables.Add(item: variable);
            m_domains.Add(item: bounds.Values.ToArray());

            return (m_variables.Count - 1);
        }
        // Every value the template answers, one evaluation per assignment; unbounded when any assignment answers no
        // number.
        public ValueBounds Enumerate(Template template) {
            var at = new int[m_domains.Count];
            var values = new long[m_domains.Count];
            var answers = new List<long>();

            while (true) {
                for (var variable = 0; (variable < values.Length); variable++) {
                    values[variable] = m_domains[variable][at[variable]];
                }
                if (!template.Evaluate(values: values).TryFinite(raw: out var answer)) {
                    return ValueBounds.Unbounded;
                }

                answers.Add(item: answer);

                var digit = 0;

                while ((digit < at.Length) && (++at[digit] == m_domains[digit].Length)) {
                    at[digit++] = 0;
                }
                if (digit == at.Length) {
                    return ValueBounds.Exactly(values: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: answers));
                }
            }
        }
    }
    // A program whose reads are patched to constants before each evaluation: a copy of its tokens and of every body
    // it calls, and the places a read stood. A local's read is patched to what the local's own template answers.
    private sealed class Template {
        private readonly CellKind m_kind;
        private readonly List<(CompiledExpressionToken[] Tokens, int Index, int Variable, Template? Local)> m_sites = [];
        private readonly CompiledExpressionToken[] m_tokens;

        private Template(CompiledExpressionToken[] tokens, CellKind kind) {
            m_kind = kind;
            m_tokens = tokens;
        }

        // Null when the program holds something no assignment of declared values describes: a fold over a row's
        // cells, a read through a computed address, a read with no finite declared values, or too many assignments.
        public static Template? Build(ReadOnlySpan<CompiledExpressionToken> program, CellKind kind, Assignments assignments) {
            var template = new Template(kind: kind, tokens: program.ToArray());

            return (template.Collect(assignments: assignments, tokens: template.m_tokens) ? template : null);
        }
        public Outcome Evaluate(ReadOnlySpan<long> values) {
            foreach (var (tokens, index, variable, local) in m_sites) {
                var raw = 0L;

                if (local is null) {
                    raw = values[variable];
                } else {
                    var bound = local.Evaluate(values: values);

                    if (!bound.TryFinite(raw: out raw)) {
                        return bound;
                    }
                }

                tokens[index] = new CompiledExpressionToken(Constant: raw, Operation: ExpressionOp.Constant);
            }

            return RuleExpressions.EvaluateDetached(kind: m_kind, program: new ReadOnlySpan<CompiledExpressionToken>(array: m_tokens), reader: null);
        }

        private bool Collect(CompiledExpressionToken[] tokens, Assignments assignments) {
            for (var index = 0; (index < tokens.Length); index++) {
                var token = tokens[index];

                if ((token.Fold is not null) || (token.Operation == ExpressionOp.Member)) {
                    return false;
                }
                if (token.Call is { } call) {
                    var body = call.Body.ToArray();

                    if (!Collect(assignments: assignments, tokens: body)) {
                        return false;
                    }

                    tokens[index] = token with { Call = call with { Body = new CompiledProgram(tokens: body) } };
                }
                if (token.Operation != ExpressionOp.Operand) {
                    continue;
                }

                var operand = token.Operand!;

                // A Bool read lands through a conversion a constant skips, and a fed address is values the operand
                // token consumes.
                if (((token.CarrierKind ?? m_kind) == CellKind.Bool) || (operand.ValueKind == CellKind.Bool) || (RuleExpressions.FeedCount(operand: operand) != 0)) {
                    return false;
                }
                if (operand is LocalOperand local) {
                    if ((local.Source is not { } source) || (Build(assignments: assignments, kind: source.CarrierKind, program: source.Expression) is not { } bound)) {
                        return false;
                    }

                    m_sites.Add(item: (tokens, index, -1, bound));

                    continue;
                }

                var variable = assignments.Variable(operand: operand);

                if (variable < 0) {
                    return false;
                }

                m_sites.Add(item: (tokens, index, variable, null));
            }

            return true;
        }
    }

    // The range form: one walk of the program over closed intervals of the real values its slots hold (a whole number
    // for an Int slot, the Q48.16 value for a Fixed one), so one interval algebra serves both kinds.
    private static ValueBounds Range(ReadOnlySpan<CompiledExpressionToken> program, CellKind kind, RuleCompileContext context) {
        if (program.IsEmpty) {
            return ValueBounds.Unbounded;
        }

        var stack = new (FixedInterval Value, CellKind Kind)[program.Length];
        var top = 0;
        var truth = (new FixedInterval(lower: FixedQ4816.Zero, upper: FixedQ4816.One), CellKind.Int);

        foreach (var token in program) {
            var carrier = (token.CarrierKind ?? kind);
            var arity = (token.Operation switch {
                ExpressionOp.Constant or ExpressionOp.Operand => 0,
                ExpressionOp.Not => ((token.Call is null) ? 1 : 0),
                ExpressionOp.Negate or ExpressionOp.Absolute or ExpressionOp.Sign or ExpressionOp.IsAbsent or ExpressionOp.And or ExpressionOp.Or or ExpressionOp.ToFixed or ExpressionOp.ToInt => 1,
                ExpressionOp.Clamp or ExpressionOp.Select => 3,
                ExpressionOp.Add or ExpressionOp.Subtract or ExpressionOp.Multiply or ExpressionOp.Divide or ExpressionOp.Minimum or ExpressionOp.Maximum or ExpressionOp.Coalesce or
                    ExpressionOp.Equal or ExpressionOp.NotEqual or ExpressionOp.Less or ExpressionOp.LessOrEqual or ExpressionOp.Greater or ExpressionOp.GreaterOrEqual => 2,
                _ => -1,
            });

            if ((arity < 0) || (top < arity) || (token.Fold is not null) || ((token.Call is not null) && (token.Operation is not (ExpressionOp.Not or ExpressionOp.And or ExpressionOp.Or)))) {
                return ValueBounds.Unbounded;
            }

            top -= arity;

            var (a, b, c) = (stack[top], stack[Math.Min(val1: (top + 1), val2: (stack.Length - 1))], stack[Math.Min(val1: (top + 2), val2: (stack.Length - 1))]);
            var pushed = (token.Operation switch {
                ExpressionOp.Constant => (Real(kind: carrier, lower: token.Constant, upper: token.Constant), carrier),
                ExpressionOp.Operand => Read(context: context, operand: token.Operand!),
                ExpressionOp.Add => ((a.Value + b.Value), a.Kind),
                ExpressionOp.Subtract => ((a.Value - b.Value), a.Kind),
                ExpressionOp.Multiply => ((a.Value * b.Value), a.Kind),
                // Whole-number division truncates, which the interval quotient does not bound.
                ExpressionOp.Divide => (((a.Kind == CellKind.Fixed) ? (a.Value / b.Value) : FixedInterval.Entire), a.Kind),
                ExpressionOp.Minimum => (FixedInterval.Min(first: a.Value, second: b.Value), a.Kind),
                ExpressionOp.Maximum => (FixedInterval.Max(first: a.Value, second: b.Value), a.Kind),
                ExpressionOp.Negate => (-a.Value, a.Kind),
                ExpressionOp.Absolute => (FixedInterval.Abs(value: a.Value), a.Kind),
                ExpressionOp.ToFixed => (a.Value, CellKind.Fixed),
                ExpressionOp.ToInt => (new FixedInterval(lower: FixedQ4816.Truncate(a.Value.Lower), upper: FixedQ4816.Truncate(a.Value.Upper)), CellKind.Int),
                ExpressionOp.Clamp => (FixedInterval.Min(first: FixedInterval.Max(first: a.Value, second: b.Value), second: c.Value), a.Kind),
                ExpressionOp.Select => (FixedInterval.Union(first: b.Value, second: c.Value), b.Kind),
                ExpressionOp.Coalesce => (FixedInterval.Union(first: a.Value, second: b.Value), a.Kind),
                ExpressionOp.Sign => (new FixedInterval(lower: -FixedQ4816.One, upper: FixedQ4816.One), CellKind.Int),
                _ => truth,
            });

            if (pushed.Item1.IsUnbounded) {
                return ValueBounds.Unbounded;
            }

            stack[top++] = pushed;
        }

        if (top != 1) {
            return ValueBounds.Unbounded;
        }

        var (answer, answered) = stack[0];

        // A whole-number slot's endpoints are whole: every rule above maps whole endpoints to whole endpoints.
        return ((answered == CellKind.Fixed)
            ? ValueBounds.Between(lower: answer.Lower.Value, upper: answer.Upper.Value)
            : ValueBounds.Between(lower: (answer.Lower.Value >> FixedQ4816.FractionBitCount), upper: (answer.Upper.Value >> FixedQ4816.FractionBitCount)));

        static (FixedInterval, CellKind) Read(IRuleOperand operand, RuleCompileContext context) {
            if (RuleExpressions.FeedCount(operand: operand) != 0) {
                return (FixedInterval.Entire, operand.ValueKind);
            }

            var bounds = (((operand is LocalOperand { Source: { } source })
                ? Of(context: context, kind: source.CarrierKind, program: source.Expression)
                : context.ReadBounds(operand: operand, variable: out _)));
            var read = ((operand.ValueKind == CellKind.Fixed) ? CellKind.Fixed : CellKind.Int);

            return ((bounds.IsUnbounded ? FixedInterval.Entire : Real(kind: read, lower: bounds.Lower, upper: bounds.Upper)), read);
        }
        static FixedInterval Real(long lower, long upper, CellKind kind) => ((kind == CellKind.Fixed)
            ? new FixedInterval(lower: FixedQ4816.FromRawBits(value: lower), upper: FixedQ4816.FromRawBits(value: upper))
            : (((lower < -MaxWhole) || (upper > MaxWhole))
                ? FixedInterval.Entire
                : new FixedInterval(lower: FixedQ4816.FromInteger(value: lower), upper: FixedQ4816.FromInteger(value: upper))));
    }
}
