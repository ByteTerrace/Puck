using System.Globalization;

using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>The allocation-free read side a compiled gate, binding and effect source share: reading one value
/// source, folding a postfix Boolean gate, and formatting a fact for the trace. Nothing here holds state of its
/// own; every number it formats is rendered with the invariant culture.</summary>
public static class RuleEvaluation {
    /// <summary>Formats a fact for a trace: <c>absent</c>, <c>forever</c>, a fixed-point value, or an integer.</summary>
    /// <param name="fact">The fact.</param>
    /// <returns>The spelling.</returns>
    public static string DescribeFact(in Fact fact) {
        fact.Outcome.TryFinite(raw: out var raw);

        return DescribeFact(
            isAbsent: (fact.Outcome.Case is FaultCase.Absent or FaultCase.Faulted),
            isForever: (fact.Outcome.Case == FaultCase.Forever),
            kind: fact.Kind,
            value: raw
        );
    }
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
    /// <summary>Evaluates a compiled postfix gate inside a fault scope of its own. A conjunct that faults leaves the
    /// gate undecided: evaluation stops ahead of any negation or grouping, the gate answers
    /// <see langword="false"/>, and <paramref name="fault"/> names the fault, which also passes on to any enclosing
    /// scope. Both sides of a comparison are read, so an absence on one side never hides a fault on the other. A gate
    /// with no tokens always holds.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="gate">The compiled postfix gate.</param>
    /// <param name="fault">The first fault any conjunct raised, or <see cref="ExpressionFault.None"/>; a caller
    /// seeing a fault decides what an undecided gate means where it sits.</param>
    /// <param name="trace">An optional per-conjunct narration sink.</param>
    /// <returns><see langword="true"/> when the gate holds and nothing faulted.</returns>
    public static bool GateHolds(IStateReader reader, GateToken[] gate, out ExpressionFault fault, List<string>? trace = null) {
        ArgumentNullException.ThrowIfNull(argument: reader);
        ArgumentNullException.ThrowIfNull(argument: gate);

        if (gate.Length == 0) {
            fault = ExpressionFault.None;

            return true;
        }

        var scratch = reader.Scratch;
        var scope = scratch.EnterFaultScope();
        bool holds;

        try {
            holds = Fold(
                gate: gate,
                reader: reader,
                scratch: scratch,
                trace: trace
            );
        } finally {
            fault = scratch.LeaveFaultScope(scope: scope);
        }

        return (holds && (fault == ExpressionFault.None));
    }
    // Folds the gate's postfix program, returning false the moment a conjunct raises a fault.
    private static bool Fold(IStateReader reader, GateToken[] gate, ArenaScratch scratch, List<string>? trace) {
        // A token pushes at most one Boolean. Bound machine-stack use independently of authored
        // program size; larger gates keep the arena's reusable storage. Both run the same fold.
        if (gate.Length <= 128) {
            Span<bool> stack = stackalloc bool[gate.Length];
            return Fold(reader: reader, gate: gate, scratch: scratch, trace: trace, stack: stack);
        }
        using var stackLease = scratch.Rent<bool>(length: gate.Length);
        return Fold(reader: reader, gate: gate, scratch: scratch, trace: trace, stack: stackLease.Span);
    }
    private static bool Fold(IStateReader reader, GateToken[] gate, ArenaScratch scratch, List<string>? trace, Span<bool> stack) {
        var top = 0;

        for (var index = 0; index < gate.Length; index++) {
            ref readonly var predicate = ref gate[index];
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
                var leftResolved = ((leftSource.Text is { } leftLiteral) ? Resolved<string>.Named(leftLiteral) : ReadText(reader, leftSource.Operand));
                var rightResolved = ((rightSource.Text is { } rightLiteral) ? Resolved<string>.Named(rightLiteral) : ReadText(reader, rightSource.Operand));
                var leftNamed = leftResolved.TryNamed(out var leftValue);
                var rightNamed = rightResolved.TryNamed(out var rightValue);
                var leftText = (leftValue ?? string.Empty);
                var rightText = (rightValue ?? string.Empty);
                var equal = string.Equals(leftText, rightText, StringComparison.Ordinal);
                var textHolds = leftNamed && rightNamed &&
                    (predicate.Comparison == ExpressionOp.Equal ? equal : !equal);
                stack[top++] = textHolds;
                trace?.Add($"{predicate.Describe}: \"{leftText}\" {predicate.Comparison.Symbol()} \"{rightText}\" -> {Spell(textHolds)}");
                if (scratch.RaisedFault != ExpressionFault.None) {
                    return false;
                }
                continue;
            }
            var leftRead = TryReadSource(
                fact: out var left,
                fault: out _,
                kind: predicate.ValueKind,
                reader: reader,
                source: in leftSource
            );
            var rightRead = TryReadSource(
                fact: out var right,
                fault: out _,
                kind: predicate.ValueKind,
                reader: reader,
                source: in rightSource
            );

            // An absent side names no cell, so no comparison holds against it — not even NotEqual: "the top card is
            // not a king" must not read as true of an empty pile. Absence converts to false at a gate.
            var absent = (
                (left.Outcome.Case is FaultCase.Absent or FaultCase.Faulted) ||
                (right.Outcome.Case is FaultCase.Absent or FaultCase.Faulted)
            );

            left.Outcome.TryFinite(raw: out var leftRaw);
            right.Outcome.TryFinite(raw: out var rightRaw);

            var holds = (
                leftRead &&
                rightRead &&
                !absent &&
                predicate.Comparison.Holds(
                expected: FixedQ4816.FromRawBits(value: rightRaw),
                expectedIsForever: (right.Outcome.Case == FaultCase.Forever),
                value: FixedQ4816.FromRawBits(value: leftRaw),
                valueIsForever: (left.Outcome.Case == FaultCase.Forever)
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

            // A faulted comparison has no truth value to negate or combine.
            if (scratch.RaisedFault != ExpressionFault.None) {
                return false;
            }
        }

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
    /// the absent fact faults <see cref="ExpressionFault.Absent"/>, and one whose address computation faulted
    /// (<see cref="FaultCase.Faulted"/>) faults <see cref="ExpressionFault.Domain"/>; one answering <c>forever</c> does not fault, since
    /// a fact comparison answers it under infinity semantics. <see cref="ExpressionFault.Forever"/> comes only from an
    /// expression, whose numeric evaluation cannot read infinity.</param>
    /// <returns><see langword="true"/> when the source answered. An operand answering the absent or forever fact
    /// still answers: the caller decides what an absence means where it sits.</returns>
    public static bool TryReadSource(IStateReader reader, in CompiledValueSource source, CellKind kind, out Fact fact, out ExpressionFault fault) {
        fault = ExpressionFault.None;

        if (source.Operand is { } operand) {
            ArgumentNullException.ThrowIfNull(argument: reader);

            fact = operand.Read(reader: reader);
            fault = (fact.Outcome.Case switch {
                FaultCase.Faulted => ExpressionFault.Domain,
                FaultCase.Absent => ExpressionFault.Absent,
                _ => ExpressionFault.None,
            });
            if (fact.Outcome.Case == FaultCase.Faulted) {
                reader.Scratch.RaiseFault(fault: ExpressionFault.Domain);
            }

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
                fact = Fact.Absent(kind: kind);

                return false;
            }

            fact = Fact.Finite(
                kind: kind,
                value: ((kind == CellKind.Bool)
                ? ((computed != 0L)
                    ? 1L
                    : 0L)
                : computed)
            );

            return true;
        }

        fact = Fact.Finite(
            kind: kind,
            value: source.RawValue
        );

        return true;
    }

    private static string Spell(bool value) => (value
        ? "true"
        : "false"
    );
    private static string Spell(in Fact fact, bool read) => (read
        ? DescribeFact(fact: in fact)
        : "refused"
    );
    /// <summary>Reads a live Text operand through the same addresses used by text-copy effects and gates.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="source">The compiled text operand.</param>
    /// <returns>The text when a Text cell was read, <see cref="FaultCase.Faulted"/> when computing its row or key
    /// faulted, and <see cref="FaultCase.Absent"/> when no Text cell answered.</returns>
    public static Resolved<string> ReadText(IStateReader reader, IRuleOperand? source) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var resolved = ReadTextCore(reader: reader, source: source);

        if (resolved.Case == FaultCase.Faulted) {
            reader.Scratch.RaiseFault(fault: ExpressionFault.Domain);
        }

        return resolved;
    }

    private static Resolved<string> ReadTextCore(IStateReader reader, IRuleOperand? source) {
        CellValue carried;

        switch (source) {
            case StateCellOperand cell: {
                    var ordinal = cell.RowOrdinal;

                    if (cell.RowFrom is { } live) {
                        var selected = live.Resolve(reader: reader);

                        if (!selected.TryNamed(value: out ordinal)) {
                            return ((selected.Case == FaultCase.Faulted)
                                ? Resolved<string>.Faulted
                                : Resolved<string>.Nothing);
                        }
                    }

                    var resolved = RuleReads.ResolveKey(keyFrom: cell.KeyFrom, literal: cell.Key, reader: reader);

                    if (resolved.Case == FaultCase.Faulted) {
                        return Resolved<string>.Faulted;
                    }
                    if (!resolved.TryNamed(value: out var key) || !key.IsValid || !reader.Arena.TryRead(key: key, rowOrdinal: ordinal, value: out carried)) {
                        return Resolved<string>.Nothing;
                    }
                    break;
                }
            case InstanceFieldOperand field: {
                    var bindings = reader.InstanceBindings;

                    if ((((uint)field.BindingSlot) >= ((uint)bindings.Length)) || (bindings[field.BindingSlot].PoolOrdinal != field.Pool.Ordinal) || !reader.Arena.TryReadLive(handle: bindings[field.BindingSlot], fieldOrdinal: field.Field.Ordinal, time: reader.Time, value: out carried)) {
                        return Resolved<string>.Nothing;
                    }
                    break;
                }
            case StaticInstanceFieldOperand field: {
                    if (!reader.Arena.TryResolvePoolSlot(poolOrdinal: field.Pool.Ordinal, slot: field.Handle.Slot, handle: out var handle) ||
                        !reader.Arena.TryReadLive(handle: handle, fieldOrdinal: field.Field.Ordinal, time: reader.Time, value: out carried)) {
                        return Resolved<string>.Nothing;
                    }
                    break;
                }
            default:
                return Resolved<string>.Nothing;
        }

        if (carried.Kind != CellKind.Text) {
            return Resolved<string>.Nothing;
        }

        return Resolved<string>.Named(value: carried.AsText);
    }
}
