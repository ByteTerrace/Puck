using System.Runtime.CompilerServices;

namespace Puck.State.Rules;

/// <summary>The one operator dispatch for a compiled postfix program: the walk a gate, a binding, an effect source,
/// and the compiler's own constant folder all evaluate through, so a folded constant and a live evaluation of the
/// same operation agree bit for bit.</summary>
/// <remarks>Evaluation allocates nothing: the value stack and its absence marks are one block of machine stack per
/// evaluation, and a call's arguments, a fold's member body and a short-circuit body run on that same stack above the
/// values below them. A program nested deeper than the block holds leases its stack from the reader's
/// <see cref="ArenaScratch"/>, and a fold reads its family's cells straight off the arena.</remarks>
public static partial class RuleExpressions {
    /// <summary>The most arguments one shared function takes.</summary>
    public const int MaxArguments = 16;

    // The machine-stack block one evaluation's values occupy: a token pushes at most one value, so this is the deepest
    // a program at the token cap reaches, and a body nested on top of it runs on the same block while it fits.
    private const int InlineDepth = RuleCapacity.MaxExpressionTokens;
    private const int MarkBits = 64;

    // The compiler folds constants with no evaluation in flight, so it has no host to borrow from.
    [ThreadStatic]
    private static ArenaScratch? FoldScratch;

    /// <summary>Evaluates a compiled postfix program for the evaluation a frame names. The answer is the program's
    /// <see cref="Outcome"/>: a number, an absence, or a fault (<see cref="ExpressionFault.Domain"/> or
    /// <see cref="ExpressionFault.Forever"/>). The fault is in the answer and nowhere else: whoever decides what the
    /// evaluation means reads the outcome it was handed.</summary>
    /// <param name="frame">The evaluation in flight.</param>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <returns>What the program answered.</returns>
    /// <exception cref="InvalidOperationException">The frame names no reader.</exception>
    public static Outcome Evaluate(ref EvaluationFrame frame, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) {
        _ = frame.Reader;

        return Enter(
            frame: ref frame,
            kind: kind,
            program: program
        );
    }
    /// <summary>Evaluates a compiled postfix program for a caller that is not a firing and decides what a fault means
    /// where it sits: the compiler's constant folder (no reader), the search score, a flock's affinity, the console's
    /// read-back and the browser's.</summary>
    /// <param name="reader">The evaluation in flight, or <see langword="null"/> to fold constants: a live read
    /// refuses without one.</param>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <returns>What the program answered.</returns>
    public static Outcome EvaluateDetached(IStateReader? reader, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) {
        var frame = ((reader is null) ? default : new EvaluationFrame(reader: reader));

        return Enter(
            frame: ref frame,
            kind: kind,
            program: program
        );
    }

    // Every evaluation of authored tokens enters here: one block of machine stack holds the value stack and its
    // absence marks for the program and every body nested on top of it. A data-dependent overflow anywhere in the
    // walk is the evaluation's domain fault, the same answer each nested body's outcome would have carried out.
    [SkipLocalsInit]
    private static Outcome Enter(ref EvaluationFrame frame, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) {
        Span<long> values = stackalloc long[InlineDepth];
        Span<ulong> marks = stackalloc ulong[(InlineDepth / MarkBits)];

        try {
            return Run(
                arguments: [],
                bottom: 0,
                frame: ref frame,
                hasMember: false,
                kind: kind,
                marks: marks,
                member: 0L,
                program: program,
                values: values
            );
        } catch (ArithmeticException) {
            return Outcome.Faulted(why: ExpressionFault.Domain);
        }
    }

    // An operand whose dynamic key named no cell pushes a marked slot rather than failing at once, so `isAbsent` and
    // `??` can answer it. Any other operation consuming a marked slot fails as it always has. Slots index the whole
    // block, so a nested body's marks sit above its caller's.
    private static bool Marked(scoped ReadOnlySpan<ulong> marks, int slot) => ((marks[(slot / MarkBits)] & (1UL << slot)) != 0UL);
    private static void Mark(scoped Span<ulong> marks, int slot, bool absent) {
        ref var word = ref marks[(slot / MarkBits)];

        word = ((word & ~(1UL << slot)) | ((absent ? 1UL : 0UL) << slot));
    }

    // Runs one program whose values start at `bottom` of the block: the program a caller entered with, or a call's
    // subprogram, a fold's member body or a short-circuit body evaluated above the values its caller holds. A token
    // pushes at most one value, so a program's own length bounds its stack.
    [SkipLocalsInit]
    private static Outcome Run(ref EvaluationFrame frame, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind, bool hasMember, long member, scoped ReadOnlySpan<long> arguments, scoped Span<long> values, scoped Span<ulong> marks, int bottom) {
        if ((values.Length - bottom) < program.Length) {
            var scratch = (frame.IsLive ? frame.Reader.Scratch : (FoldScratch ??= new ArenaScratch()));

            using var valueLease = scratch.Rent<long>(length: program.Length);
            using var markLease = scratch.Rent<ulong>(length: (((program.Length + MarkBits) - 1) / MarkBits));

            return Run(
                arguments: arguments,
                bottom: 0,
                frame: ref frame,
                hasMember: hasMember,
                kind: kind,
                marks: markLease.Span,
                member: member,
                program: program,
                values: valueLease.Span
            );
        }

        // The values the programs computing an operand's addresses left, which the operand token consumes.
        var feedFrame = default(FeedFrame);
        Span<AddressFeed> feeds = feedFrame;
        var top = bottom;

        foreach (ref readonly var token in program) {
            switch (token.Step) {
                case ExpressionStep.Constant:
                    Mark(marks: marks, slot: top, absent: false);
                    values[top++] = token.Constant;

                    continue;
                case ExpressionStep.Argument:
                    if (((ulong)token.Constant) >= ((ulong)arguments.Length)) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    Mark(marks: marks, slot: top, absent: false);
                    values[top++] = arguments[((int)token.Constant)];

                    continue;
                case ExpressionStep.Member:
                    if (!hasMember) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    Mark(marks: marks, slot: top, absent: false);
                    values[top++] = member;

                    continue;
                case ExpressionStep.Read:
                case ExpressionStep.ReadAddressed: {
                        if (!frame.IsLive) {
                            return Outcome.Faulted(why: ExpressionFault.Domain);
                        }

                        Fact fact;

                        if (token.Step == ExpressionStep.Read) {
                            fact = token.Operand!.Read(
                                frame: ref frame,
                                keys: default
                            );
                        } else {
                            var fed = token.Consumes;

                            top -= fed;
                            for (var slot = 0; (slot < fed); slot++) {
                                feeds[slot] = new AddressFeed(
                                    Absent: Marked(marks: marks, slot: (top + slot)),
                                    Value: values[(top + slot)]
                                );
                            }

                            fact = ReadAddressed(
                                feeds: feeds[..fed],
                                frame: ref frame,
                                operand: token.Operand!
                            );
                        }
                        if (fact.Outcome.Case is FaultCase.Forever or FaultCase.Faulted) {
                            return Outcome.Faulted(why: ((fact.Outcome.Case == FaultCase.Faulted)
                                ? ExpressionFault.Domain
                                : ExpressionFault.Forever));
                        }

                        var factAbsent = (fact.Outcome.Case == FaultCase.Absent);

                        Mark(marks: marks, slot: top, absent: factAbsent);
                        values[top++] = (factAbsent
                            ? 0L
                            : fact.ToRaw(kind: (token.CarrierKind ?? kind))
                        );

                        continue;
                    }
                case ExpressionStep.IsAbsent:
                    values[(top - 1)] = (Marked(marks: marks, slot: (top - 1))
                        ? 1L
                        : 0L
                    );
                    Mark(marks: marks, slot: (top - 1), absent: false);

                    continue;
                case ExpressionStep.NotBody: {
                        var truthOutcome = Run(
                            arguments: arguments,
                            bottom: top,
                            frame: ref frame,
                            hasMember: hasMember,
                            kind: kind,
                            marks: marks,
                            member: member,
                            program: token.Call!.Body,
                            values: values
                        );

                        if (truthOutcome.Case == FaultCase.Faulted) {
                            return truthOutcome;
                        }

                        // An absent body is false.
                        values[top] = (truthOutcome.TryFinite(raw: out var truthValue)
                            ? truthValue
                            : 0L);
                        Mark(marks: marks, slot: top++, absent: false);
                        goto case ExpressionStep.Not;
                    }
                case ExpressionStep.Not:
                    values[(top - 1)] = ((Marked(marks: marks, slot: (top - 1)) || (values[(top - 1)] == 0L)) ? 1L : 0L);
                    Mark(marks: marks, slot: (top - 1), absent: false);

                    continue;
                case ExpressionStep.And:
                case ExpressionStep.Or: {
                        var truth = (!Marked(marks: marks, slot: (top - 1)) && (values[(top - 1)] != 0L));

                        if ((token.Step == ExpressionStep.And) == truth) {
                            var rightOutcome = Run(
                                arguments: arguments,
                                bottom: top,
                                frame: ref frame,
                                hasMember: hasMember,
                                kind: kind,
                                marks: marks,
                                member: member,
                                program: token.Call!.Body,
                                values: values
                            );

                            if (rightOutcome.Case == FaultCase.Faulted) {
                                return rightOutcome;
                            }

                            // An absent right side is false.
                            truth = (rightOutcome.TryFinite(raw: out var rightTruth) && (rightTruth != 0L));
                        }

                        values[(top - 1)] = (truth ? 1L : 0L);
                        Mark(marks: marks, slot: (top - 1), absent: false);

                        continue;
                    }
                case ExpressionStep.Coalesce: {
                        var fallback = values[--top];
                        var fallbackAbsent = Marked(marks: marks, slot: top);

                        if (Marked(marks: marks, slot: (top - 1))) {
                            Mark(marks: marks, slot: (top - 1), absent: fallbackAbsent);
                            values[(top - 1)] = fallback;
                        }

                        continue;
                    }
                case ExpressionStep.Fold: {
                        var foldOutcome = Fold(
                            arguments: arguments,
                            bottom: top,
                            fold: token.Fold!,
                            frame: ref frame,
                            marks: marks,
                            values: values
                        );

                        if (!foldOutcome.TryFinite(raw: out var folded)) {
                            return foldOutcome;
                        }

                        Mark(marks: marks, slot: top, absent: false);
                        values[top++] = folded;

                        continue;
                    }
                case ExpressionStep.Call: {
                        var call = token.Call!;

                        if (call.Arity > MaxArguments) {
                            return Outcome.Faulted(why: ExpressionFault.Domain);
                        }

                        top -= call.Arity;
                        for (var slot = top; (slot < (top + call.Arity)); slot++) {
                            if (Marked(marks: marks, slot: slot)) {
                                return Outcome.Absent;
                            }
                        }

                        // The arguments stay where the caller left them, and the body's own values start above them.
                        var calledOutcome = Run(
                            arguments: values.Slice(
                                length: call.Arity,
                                start: top
                            ),
                            bottom: (top + call.Arity),
                            frame: ref frame,
                            hasMember: hasMember,
                            kind: kind,
                            marks: marks,
                            member: member,
                            program: call.Body,
                            values: values
                        );

                        if (!calledOutcome.TryFinite(raw: out var called)) {
                            return calledOutcome;
                        }

                        Mark(marks: marks, slot: top, absent: false);
                        values[top++] = called;

                        continue;
                    }
            }

            // Every operator consumes its operands, and an absent one answers the program's absence.
            var arity = token.Consumes;

            for (var slot = (top - arity); (slot < top); slot++) {
                if (Marked(marks: marks, slot: slot)) {
                    return Outcome.Absent;
                }
            }

            Mark(marks: marks, slot: (top - arity), absent: false);
            switch (token.Step) {
                case ExpressionStep.Clamp: {
                        var maximum = values[--top];
                        var minimum = values[--top];
                        var input = values[--top];

                        if (!ExpressionArithmetic.TryClamp(input, minimum, maximum, out var clamped)) {
                            return Outcome.Faulted(why: ExpressionFault.Domain);
                        }

                        values[top++] = clamped;

                        continue;
                    }
                case ExpressionStep.Select: {
                        var whenFalse = values[--top];
                        var whenTrue = values[--top];
                        var condition = values[--top];

                        values[top++] = ((condition != 0L)
                            ? whenTrue
                            : whenFalse
                        );

                        continue;
                    }
                case ExpressionStep.BitField: {
                        var width = values[--top];
                        var offset = values[--top];
                        var input = values[--top];

                        if (!ExpressionArithmetic.TryBitField(
                            field: out var field,
                            offset: offset,
                            value: input,
                            width: width
                        )) {
                            return Outcome.Faulted(why: ExpressionFault.Domain);
                        }

                        values[top++] = field;

                        continue;
                    }
                case ExpressionStep.BitInsert: {
                        var width = values[--top];
                        var offset = values[--top];
                        var field = values[--top];
                        var input = values[--top];

                        if (!ExpressionArithmetic.TryBitInsert(
                            field: field,
                            inserted: out var inserted,
                            offset: offset,
                            value: input,
                            width: width
                        )) {
                            return Outcome.Faulted(why: ExpressionFault.Domain);
                        }

                        values[top++] = inserted;

                        continue;
                    }
                case ExpressionStep.BoardShift:
                    values[(top - 1)] = BoardQueries.ShiftMask(
                        ((BoardNeighbourQuery)token.Board!),
                        values[(top - 1)]
                    );

                    continue;
                case ExpressionStep.BoardRay:
                    values[(top - 1)] = BoardQueries.RayMask(
                        ((BoardNeighbourQuery)token.Board!),
                        values[(top - 1)]
                    );

                    continue;
                case ExpressionStep.BoardImage: {
                        var image = ((BoardNeighbourQuery)token.Board!);

                        values[(top - 1)] = BoardQueries.ImageOfMask(
                            image.Topology,
                            image.Direction,
                            values[(top - 1)]
                        );

                        continue;
                    }
                case ExpressionStep.Function: {
                        var carrier = (token.CarrierKind ?? kind);

                        top -= arity;
                        if (
                            !ExpressionArithmetic.FunctionAdmits(token.Operation, carrier) ||
                            !ExpressionArithmetic.TryValidatedFunction(
                            arguments: values.Slice(
                                length: arity,
                                start: top
                            ),
                            kind: carrier,
                            operation: token.Operation,
                            value: out var applied
                        )
                        ) {
                            return Outcome.Faulted(why: ExpressionFault.Domain);
                        }

                        values[top++] = applied;

                        continue;
                    }
                case ExpressionStep.Unary:
                    if (!ExpressionArithmetic.TryUnary(
                        token.Operation,
                        (token.CarrierKind ?? kind),
                        values[(top - 1)],
                        out var unary
                    )) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    values[(top - 1)] = unary;

                    continue;
            }

            var right = values[--top];
            var left = values[--top];

            // Data-dependent arithmetic refusal can occur thousands of times in a dense sample; the checked
            // semantics are preserved without allocating an exception per evaluation.
            if (!ExpressionArithmetic.TryBinary(
                token.Operation,
                (token.CarrierKind ?? kind),
                left,
                right,
                out var result
            )) {
                return Outcome.Faulted(why: ExpressionFault.Domain);
            }

            values[top++] = result;
        }

        if (top != (bottom + 1)) {
            return Outcome.Faulted(why: ExpressionFault.Domain);
        }

        return (Marked(marks: marks, slot: bottom)
            ? Outcome.Absent
            : Outcome.Finite(raw: values[bottom]));
    }
    // Every cell the family's member row holds is visited once, in the arena's own member order, so the reduction
    // is deterministic. A member's own outcome that is not a number is the fold's. Each member's body runs on the
    // block above the values its caller holds.
    private static Outcome Fold(ref EvaluationFrame frame, CompiledFold fold, scoped ReadOnlySpan<long> arguments, scoped Span<long> values, scoped Span<ulong> marks, int bottom) {
        if (
            !frame.IsLive ||
            (fold.RowOrdinal < 0)
        ) {
            return Outcome.Faulted(why: ExpressionFault.Domain);
        }

        var accumulator = ((fold.Operation == ExpressionOp.All)
            ? 1L
            : 0L
        );
        var arena = frame.Arena;
        var time = frame.Time;
        var cursor = 0;

        while (arena.TryNextLiveNumber(
            cursor: ref cursor,
            key: out _,
            rowOrdinal: fold.RowOrdinal,
            time: in time,
            value: out var member
        )) {
            var memberOutcome = Run(
                arguments: arguments,
                bottom: bottom,
                frame: ref frame,
                hasMember: true,
                kind: fold.MemberKind,
                marks: marks,
                member: member,
                program: fold.Body,
                values: values
            );

            if (!memberOutcome.TryFinite(raw: out var applied)) {
                return memberOutcome;
            }

            switch (fold.Operation) {
                case ExpressionOp.All:
                    if (applied == 0L) {
                        return Outcome.Finite(raw: 0L);
                    }

                    break;
                case ExpressionOp.Any:
                    if (applied != 0L) {
                        return Outcome.Finite(raw: 1L);
                    }

                    break;
                case ExpressionOp.Count:
                    accumulator += ((applied != 0L)
                        ? 1L
                        : 0L
                    );

                    break;
                default:
                    if (!ExpressionArithmetic.TryBinary(
                        ExpressionOp.Add,
                        fold.MemberKind,
                        accumulator,
                        applied,
                        out accumulator
                    )) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    break;
            }
        }

        return Outcome.Finite(raw: accumulator);
    }

    // The operand feeds of one address-reading token, on the machine stack beside the block.
    [InlineArray(length: MaxAddresses)]
    private struct FeedFrame {
        private AddressFeed m_first;
    }
}
