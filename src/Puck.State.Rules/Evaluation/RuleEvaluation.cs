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
    public static Verdict Decide(ref EvaluationFrame frame, CompiledGate gate, List<string>? trace = null) {
        ArgumentNullException.ThrowIfNull(argument: gate);

        if (gate.Length == 0) {
            return Verdict.Open;
        }

        _ = frame.Reader;

        var root = gate.Root();
        var context = ProgramContext.Start();
        var outer = frame.Trace;

        frame.Trace = trace;
        try {
            var open = root.Run(context: ref context, frame: ref frame);

            return ((context.Halt != Halt.None)
                ? Verdict.Faulted
                : ((open != 0L)
                    ? Verdict.Open
                    : Verdict.Closed));
        } finally {
            frame.Trace = outer;
        }
    }
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
            return operand.Read(frame: ref frame);
        }
        if (source.Expression is { } expression) {
            return LandProgram(
                kind: kind,
                outcome: RuleExpressions.Evaluate(
                    frame: ref frame,
                    kind: ComputedKind(kind: kind, source: in source),
                    program: expression
                )
            );
        }

        return Fact.Finite(
            kind: kind,
            value: source.RawValue
        );
    }

    // The kind a program source computes in: its carrier, else the kind it is read in, where a Bool destination's
    // program was compiled in Int, the kind a comparison leaves.
    internal static CellKind ComputedKind(in CompiledValueSource source, CellKind kind) => (source.CarrierKind ?? ((kind == CellKind.Bool)
        ? CellKind.Int
        : kind));
    // What a program source answers as a fact in the kind it is read in: a Bool lands as 0 for zero and 1 otherwise,
    // and an absence or a fault is the fact's own case.
    internal static Fact LandProgram(Outcome outcome, CellKind kind) {
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
