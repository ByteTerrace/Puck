using System.Globalization;

using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>Why a compiled expression did not evaluate.</summary>
public enum ExpressionFault : byte {
    /// <summary>The expression evaluated.</summary>
    None,
    /// <summary>An operand read a fact with no number.</summary>
    Forever,
    /// <summary>An operand's dynamic key named no cell (an empty zone's endpoint).</summary>
    Absent,
    /// <summary>Overflow, a divide by zero, a function argument outside its domain, or an invalid stack result.</summary>
    Domain,
}
/// <summary>The allocation-free read side a compiled gate, binding and effect source share: reading one value
/// source, folding a postfix Boolean gate, and formatting a fact for the trace. Nothing here holds state of its
/// own; every number it formats is rendered with the invariant culture.</summary>
public static class RuleEvaluation {
    /// <summary>Formats a fact for a trace: <c>absent</c>, <c>forever</c>, a fixed-point value, or an integer.</summary>
    /// <param name="fact">The fact.</param>
    /// <returns>The spelling.</returns>
    public static string DescribeFact(in RuleFact fact) => DescribeFact(
        isAbsent: fact.IsAbsent,
        isForever: fact.IsForever,
        kind: fact.Kind,
        value: fact.Value
    );
    /// <summary>Formats a raw value for a trace.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="kind">Its encoding.</param>
    /// <param name="isForever">Whether the fact is positive infinity.</param>
    /// <param name="isAbsent">Whether the read named no cell.</param>
    /// <returns>The spelling.</returns>
    public static string DescribeFact(long value, CellKind kind, bool isForever, bool isAbsent = false) => (isAbsent
        ? "absent"
        : (isForever
            ? "forever"
            : ((kind == CellKind.Fixed)
                ? FixedQ4816.FromRawBits(value: value).ToString()
                : value.ToString(provider: CultureInfo.InvariantCulture)
    )));
    /// <summary>Names why an expression or a read did not answer, in the author's own vocabulary.</summary>
    /// <param name="fault">The fault.</param>
    /// <returns>The sentence.</returns>
    public static string DescribeFault(ExpressionFault fault) => fault switch {
        ExpressionFault.Forever => "the read answered a fact with no number (a forever fact)",
        ExpressionFault.Absent => "the read went through a dynamic key that named no cell (an empty zone's endpoint); answer the absence with 'isAbsent(...)' or '... ?? <fallback>'",
        _ => "the expression overflowed, divided by zero, left a function's domain, or produced an invalid stack result",
    };
    /// <summary>Evaluates a compiled postfix gate. A conjunct that cannot answer reads false and names its fault, so
    /// the caller reports it rather than letting the gate silently stop holding. A gate with no tokens always
    /// holds.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="gate">The compiled postfix gate.</param>
    /// <param name="fault">The first conjunct fault, or <see cref="ExpressionFault.None"/>.</param>
    /// <param name="trace">An optional per-conjunct narration sink.</param>
    /// <returns><see langword="true"/> when the gate holds.</returns>
    public static bool GateHolds(IStateReader reader, GateToken[] gate, out ExpressionFault fault, List<string>? trace = null) {
        ArgumentNullException.ThrowIfNull(argument: gate);

        fault = ExpressionFault.None;

        if (gate.Length == 0) {
            return true;
        }

        var raised = ExpressionFault.None;

        using var stackLease = reader.Scratch.Rent<bool>(length: gate.Length);

        var stack = stackLease.Span;
        var top = 0;

        for (var index = 0; index < gate.Length; index++) {
            var predicate = gate[index];
            if (predicate.Op == GateOp.ShortCircuit) {
                var answer = stack[top - 1];
                if (answer == predicate.SkipWhen) {
                    top -= predicate.Arity;
                    stack[top - 1] = answer;
                    index += predicate.Skip;
                }
                continue;
            }
            if (predicate.Op == GateOp.Not) {
                GateProgramEvaluator.Invert(
                    stack: stack,
                    top: top
                );
                trace?.Add(item: $"not -> {Spell(value: stack[(top - 1)])}");

                continue;
            }
            if (predicate.Op is (GateOp.All or GateOp.Any)) {
                GateProgramEvaluator.FoldGroup(
                    arity: predicate.Arity,
                    isAll: (predicate.Op == GateOp.All),
                    stack: stack,
                    top: ref top
                );
                trace?.Add(item: $"{((predicate.Op == GateOp.All)
                    ? "all"
                    : "any")} of {predicate.Arity.ToString(provider: CultureInfo.InvariantCulture)} -> {Spell(value: stack[(top - 1)])}");

                continue;
            }

            var leftSource = predicate.LeftSource;
            var rightSource = predicate.RightSource;
            if (predicate.ValueKind == CellKind.Text) {
                var leftText = leftSource.Text;
                var rightText = rightSource.Text;
                var leftAnswered = leftText is not null || TryReadText(reader, leftSource.Operand, out leftText);
                var rightAnswered = rightText is not null || TryReadText(reader, rightSource.Operand, out rightText);
                var equal = string.Equals(leftText, rightText, StringComparison.Ordinal);
                var textHolds = leftAnswered && rightAnswered &&
                    (predicate.Comparison == ExpressionOp.Equal ? equal : !equal);
                stack[top++] = textHolds;
                trace?.Add($"{predicate.Describe}: \"{leftText}\" {predicate.Comparison.Symbol()} \"{rightText}\" -> {Spell(textHolds)}");
                continue;
            }
            var right = default(RuleFact);
            var rightFault = ExpressionFault.None;
            var leftRead = TryReadSource(
                fact: out var left,
                fault: out var leftFault,
                kind: predicate.ValueKind,
                reader: reader,
                source: in leftSource
            );
            var rightRead = (leftRead && TryReadSource(
                fact: out right,
                fault: out rightFault,
                kind: predicate.ValueKind,
                reader: reader,
                source: in rightSource
            ));

            if (raised == ExpressionFault.None) {
                raised = First(
                    left: leftFault == ExpressionFault.Absent ? ExpressionFault.None : leftFault,
                    right: rightFault == ExpressionFault.Absent ? ExpressionFault.None : rightFault
                );
            }

            // An absent side names no cell, so no comparison holds against it — not even NotEqual: "the top card is
            // not a king" must not read as true of an empty pile. Absence converts to false at a gate.
            var absent = (left.IsAbsent || right.IsAbsent);

            var holds = (
                leftRead &&
                rightRead &&
                !absent &&
                predicate.Comparison.Holds(
                expected: FixedQ4816.FromRawBits(value: right.Value),
                expectedIsForever: right.IsForever,
                value: FixedQ4816.FromRawBits(value: left.Value),
                valueIsForever: left.IsForever
            )
            );

            stack[top++] = holds;
            trace?.Add(item: $"{predicate.Describe}: {Spell(
                fact: in left,
                read: leftRead
            )} {predicate.Comparison.Symbol()} {Spell(
                fact: in right,
                read: rightRead
            )} -> {Spell(value: holds)}");
        }

        fault = raised;

        return (
            (top == 1) &&
            stack[0]
        );
    }
    /// <summary>Reads one compiled value source for the evaluation in flight. A literal answers its already-converted
    /// raw value, an operand its live fact, and an expression the number its program computes.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="source">The compiled source.</param>
    /// <param name="kind">The encoding the read answers in.</param>
    /// <param name="fact">The fact, on success.</param>
    /// <param name="fault">Why the read did not answer, or <see cref="ExpressionFault.None"/>. An operand answering
    /// the absent fact faults <see cref="ExpressionFault.Absent"/>; one answering <c>forever</c> does not fault, since
    /// a fact comparison answers it under infinity semantics. <see cref="ExpressionFault.Forever"/> comes only from an
    /// expression, whose numeric evaluation cannot read infinity.</param>
    /// <returns><see langword="true"/> when the source answered. An operand answering the absent or forever fact
    /// still answers: the caller decides what an absence means where it sits.</returns>
    public static bool TryReadSource(IStateReader reader, in CompiledValueSource source, CellKind kind, out RuleFact fact, out ExpressionFault fault) {
        fault = ExpressionFault.None;

        if (source.Operand is { } operand) {
            ArgumentNullException.ThrowIfNull(argument: reader);

            fact = operand.Read(reader: reader);
            fault = (fact.IsAbsent
                ? ExpressionFault.Absent
                : ExpressionFault.None
            );

            return true;
        }
        if (source.Expression is { } expression) {
            // A Bool destination's program was compiled in Int, the kind a comparison leaves, so it evaluates in Int
            // and lands as 0 for zero and 1 for anything else.
            var computedKind = source.CarrierKind ?? ((kind == CellKind.Bool)
                ? CellKind.Int
                : kind
            );

            if (!RuleExpressions.TryEvaluate(
                fault: out fault,
                kind: computedKind,
                program: expression,
                reader: reader,
                value: out var computed
            )) {
                fact = RuleFact.Absent(kind: kind);

                return false;
            }

            fact = RuleFact.Finite(
                kind: kind,
                value: ((kind == CellKind.Bool)
                ? ((computed != 0L)
                    ? 1L
                    : 0L)
                : computed)
            );

            return true;
        }

        fact = RuleFact.Finite(
            kind: kind,
            value: source.RawValue
        );

        return true;
    }

    private static ExpressionFault First(ExpressionFault left, ExpressionFault right) => ((left is (ExpressionFault.Domain or ExpressionFault.Forever or ExpressionFault.Absent))
        ? left
        : ((right is (ExpressionFault.Domain or ExpressionFault.Forever or ExpressionFault.Absent))
            ? right
            : ExpressionFault.None
    ));
    private static string Spell(bool value) => (value
        ? "true"
        : "false"
    );
    private static string Spell(in RuleFact fact, bool read) => (read
        ? DescribeFact(fact: in fact)
        : "refused"
    );
    /// <summary>Reads a live Text operand through the same addresses used by text-copy effects and gates.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="source">The compiled text operand.</param>
    /// <param name="text">The text, or empty on a missing read.</param>
    /// <returns>Whether a Text cell was read.</returns>
    public static bool TryReadText(IStateReader reader, IRuleOperand? source, out string text) {
        text = string.Empty;
        CellValue carried;

        switch (source) {
            case StateCellOperand cell: {
                    var ordinal = cell.RowOrdinal;

                    if ((cell.RowFrom is { } live) && !live.TryResolve(reader: reader, rowOrdinal: out ordinal)) {
                        return false;
                    }
                    var key = RuleReads.ResolveKey(keyFrom: cell.KeyFrom, literal: cell.Key, reader: reader);

                    if (!key.IsValid || !reader.Arena.TryRead(key: key, rowOrdinal: ordinal, value: out carried)) {
                        return false;
                    }
                    break;
                }
            case InstanceFieldOperand field: {
                    var bindings = reader.InstanceBindings;

                    if ((((uint)field.BindingSlot) >= ((uint)bindings.Length)) || (bindings[field.BindingSlot].PoolOrdinal != field.Pool.Ordinal) || !reader.Arena.TryReadLive(handle: bindings[field.BindingSlot], fieldOrdinal: field.Field.Ordinal, time: reader.Time, value: out carried)) {
                        return false;
                    }
                    break;
                }
            case StaticInstanceFieldOperand field: {
                    if (!reader.Arena.TryResolvePoolSlot(poolOrdinal: field.Pool.Ordinal, slot: field.Handle.Slot, handle: out var handle) ||
                        !reader.Arena.TryReadLive(handle: handle, fieldOrdinal: field.Field.Ordinal, time: reader.Time, value: out carried)) {
                        return false;
                    }
                    break;
                }
            default:
                return false;
        }

        if (carried.Kind != CellKind.Text) {
            return false;
        }
        text = carried.AsText;
        return true;
    }
}
