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
        return DescribeFact(
            isAbsent: (fact.Outcome.Case is FaultCase.Absent or FaultCase.Faulted),
            isForever: (fact.Outcome.Case == FaultCase.Forever),
            kind: fact.Kind,
            value: (fact.Outcome.TryFinite(raw: out var raw)
                ? raw
                : 0L)
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

    /// <summary>Why a gate that faulted decided nothing, for a conjunct whose fault is not named.</summary>
    public const string GateFaultReason = "a conjunct overflowed, divided by zero, left a function's domain, or read a fact with no number";

    /// <summary>Names why an evaluation did not answer a number, in the author's own vocabulary.</summary>
    /// <param name="outcome">The answer: an absence or a fault.</param>
    /// <returns>The sentence.</returns>
    public static string DescribeOutcome(Outcome outcome) => DescribeFault(fault: ((outcome.Case == FaultCase.Absent)
        ? ExpressionFault.Absent
        : outcome.Fault));
    /// <summary>Decides a compiled postfix gate. A conjunct that faults leaves the gate undecided: evaluation stops
    /// ahead of any negation or grouping and the verdict is <see cref="Verdict.Faulted"/>. Both sides of a comparison are read,
    /// so an absence on one side never hides a fault on the other. A gate with no tokens is always open.</summary>
    /// <param name="frame">The evaluation in flight.</param>
    /// <param name="gate">The compiled postfix gate.</param>
    /// <param name="trace">An optional per-conjunct narration sink.</param>
    /// <returns>What the gate decided; a caller reading <see cref="Verdict.Faulted"/> decides what an undecided gate
    /// means where it sits.</returns>
    public static Verdict Decide(ref EvaluationFrame frame, GateToken[] gate, List<string>? trace = null) {
        ArgumentNullException.ThrowIfNull(argument: gate);

        return ((gate.Length == 0)
            ? Verdict.Open
            : Fold(
                frame: ref frame,
                gate: gate,
                scratch: frame.Reader.Scratch,
                trace: trace
            ));
    }

    // Folds the gate's postfix program, answering Faulted the moment a conjunct faults.
    private static Verdict Fold(scoped ref EvaluationFrame frame, GateToken[] gate, ArenaScratch scratch, List<string>? trace) {
        // A token pushes at most one Boolean. Bound machine-stack use independently of authored
        // program size; larger gates keep the arena's reusable storage. Both run the same fold.
        if (gate.Length <= 128) {
            Span<bool> stack = stackalloc bool[gate.Length];

            return Fold(frame: ref frame, gate: gate, stack: stack, trace: trace);
        }
        using var stackLease = scratch.Rent<bool>(length: gate.Length);

        return Fold(frame: ref frame, gate: gate, trace: trace, stack: stackLease.Span);
    }
    private static Verdict Fold(scoped ref EvaluationFrame frame, GateToken[] gate, List<string>? trace, scoped Span<bool> stack) {
        var reader = frame.Reader;
        var top = 0;

        for (var index = 0; (index < gate.Length); index++) {
            ref readonly var predicate = ref gate[index];

            if (predicate.Op == GateOp.ShortCircuit) {
                var answer = stack[(top - 1)];

                if (answer == predicate.SkipWhen) {
                    top -= predicate.Arity;
                    stack[(top - 1)] = answer;
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
                var leftResolved = ((leftSource.Text is { } leftLiteral) ? Resolved<string>.Named(value: leftLiteral) : ReadText(reader: reader, source: leftSource.Operand));
                var rightResolved = ((rightSource.Text is { } rightLiteral) ? Resolved<string>.Named(value: rightLiteral) : ReadText(reader: reader, source: rightSource.Operand));
                var leftNamed = leftResolved.TryNamed(value: out var leftValue);
                var rightNamed = rightResolved.TryNamed(value: out var rightValue);
                var leftText = (leftValue ?? string.Empty);
                var rightText = (rightValue ?? string.Empty);
                var equal = string.Equals(a: leftText, b: rightText, comparisonType: StringComparison.Ordinal);
                var textHolds = (leftNamed && rightNamed &&
                    ((predicate.Comparison == ExpressionOp.Equal) ? equal : !equal));

                stack[top++] = textHolds;
                trace?.Add(item: $"{predicate.Describe}: \"{leftText}\" {predicate.Comparison.Symbol()} \"{rightText}\" -> {Spell(value: textHolds)}");
                if ((leftResolved.Case == FaultCase.Faulted) || (rightResolved.Case == FaultCase.Faulted)) {
                    return Verdict.Faulted;
                }
                continue;
            }

            var left = ReadSource(
                frame: ref frame,
                kind: predicate.ValueKind,
                source: in leftSource
            );
            var right = ReadSource(
                frame: ref frame,
                kind: predicate.ValueKind,
                source: in rightSource
            );

            // An absent side names no cell, so no comparison holds against it — not even NotEqual: "the top card is
            // not a king" must not read as true of an empty pile. Absence converts to false at a gate.
            var absent = (
                (left.Outcome.Case is FaultCase.Absent or FaultCase.Faulted) ||
                (right.Outcome.Case is FaultCase.Absent or FaultCase.Faulted)
            );

            // A case with no number (forever, or the absence the comparison already refused) compares as zero beside its flag.
            var leftRaw = (left.Outcome.TryFinite(raw: out var leftFinite)
                ? leftFinite
                : 0L);
            var rightRaw = (right.Outcome.TryFinite(raw: out var rightFinite)
                ? rightFinite
                : 0L);

            var holds = (
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
                read: Read(
                    fact: in left,
                    source: in leftSource
                )
            )} {predicate.Comparison.Symbol()} {Spell(
                fact: in right,
                read: Read(
                    fact: in right,
                    source: in rightSource
                )
            )} -> {Spell(value: holds)}");

            // A faulted comparison has no truth value to negate or combine.
            if ((left.Outcome.Case == FaultCase.Faulted) || (right.Outcome.Case == FaultCase.Faulted)) {
                return Verdict.Faulted;
            }
        }

        return (((top == 1) && stack[0])
            ? Verdict.Open
            : Verdict.Closed);
    }
    // Whether a source answered: an operand always does (an absence is its answer), a program only when it left a number.
    private static bool Read(in Fact fact, in CompiledValueSource source) => (!source.IsExpression || (fact.Outcome.Case == FaultCase.Finite));

    /// <summary>Reads one compiled value source for the evaluation in flight. A literal answers its already-converted
    /// raw value, an operand its live fact, and an expression the number its program computes.</summary>
    /// <param name="frame">The evaluation in flight.</param>
    /// <param name="source">The compiled source.</param>
    /// <param name="kind">The encoding the read answers in.</param>
    /// <returns>The fact. An operand answering the absent or forever fact still answers: the caller decides what an
    /// absence means where it sits. An expression that left no number answers its absence or its fault; a fact comparison answers <see cref="FaultCase.Forever"/>
    /// under infinity semantics, and only an expression's numeric evaluation faults with
    /// <see cref="ExpressionFault.Forever"/>.</returns>
    public static Fact ReadSource(ref EvaluationFrame frame, in CompiledValueSource source, CellKind kind) {
        if (source.Operand is { } operand) {

            return operand.Read(reader: frame.Reader);
        }
        if (source.Expression is { } expression) {
            // A Bool destination's program was compiled in Int, the kind a comparison leaves, so it evaluates in Int
            // and lands as 0 for zero and 1 for anything else.
            var computedKind = (source.CarrierKind ?? ((kind == CellKind.Bool)
                ? CellKind.Int
                : kind
            ));
            var outcome = RuleExpressions.Evaluate(
                frame: ref frame,
                kind: computedKind,
                program: expression
            );

            if (!outcome.TryFinite(raw: out var computed)) {
                return new Fact(
                    kind: kind,
                    outcome: outcome
                );
            }

            return Fact.Finite(
                kind: kind,
                value: ((kind == CellKind.Bool)
                ? ((computed != 0L)
                    ? 1L
                    : 0L)
                : computed)
            );
        }

        return Fact.Finite(
            kind: kind,
            value: source.RawValue
        );
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
