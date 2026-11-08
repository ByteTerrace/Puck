namespace Puck.State.Rules;

/// <summary>The one operator dispatch for a compiled postfix program: the walk a gate, a binding, an effect source,
/// and the compiler's own constant folder all evaluate through, so a folded constant and a live evaluation of the
/// same operation agree bit for bit.</summary>
/// <remarks>Evaluation allocates nothing once it has run: the value stack and the absence marks are leased from
/// the reader's <see cref="ArenaScratch"/> at the program's own length, a call's argument frame is a fixed few
/// words of machine stack, and a fold reads its family's cells straight off the arena.</remarks>
public static partial class RuleExpressions {
    /// <summary>The most arguments one shared function takes: a call's argument frame is this many eight-byte
    /// words of machine stack in every evaluator frame.</summary>
    public const int MaxArguments = 16;

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
    public static Outcome Evaluate(ref EvaluationFrame frame, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) => Run(
        arguments: [],
        kind: kind,
        member: null,
        program: program,
        reader: frame.Reader
    );
    /// <summary>Evaluates a compiled postfix program for a caller that is not a firing and decides what a fault means
    /// where it sits: the compiler's constant folder (no reader), the search score, a flock's affinity, the console's
    /// read-back and the browser's.</summary>
    /// <param name="reader">The evaluation in flight, or <see langword="null"/> to fold constants: a live read
    /// refuses without one.</param>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <returns>What the program answered.</returns>
    public static Outcome EvaluateDetached(IStateReader? reader, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind) => Run(
        arguments: [],
        kind: kind,
        member: null,
        program: program,
        reader: reader
    );

    // Every evaluation of authored tokens runs here, and answers an Outcome: a nested call, fold member or short-circuit
    // body is a local the caller combines, so no variable is shared between a program and the programs it calls.
    private static Outcome Run(IStateReader? reader, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind, long? member, ReadOnlySpan<long> arguments) {
        // A token pushes at most one value, so a program's own length bounds its stack: the lease is as deep as
        // this program can reach, and a nested call or fold takes the next one down rather than more machine stack.
        var scratch = (reader?.Scratch ?? (FoldScratch ??= new ArenaScratch()));

        using var stackLease = scratch.Rent<long>(length: program.Length);
        // An operand whose dynamic key named no cell pushes a marked slot rather than failing at once, so
        // `isAbsent` and `??` can answer it. Any other operation consuming a marked slot fails as it always has.
        using var absentLease = scratch.Rent<bool>(length: program.Length);

        var stack = stackLease.Span;
        var absent = absentLease.Span;
        // One frame serves every call site: a call evaluates its body before the next call site is reached.
        Span<long> frame = stackalloc long[MaxArguments];
        // The values the programs computing an operand's addresses left, which the operand token consumes.
        Span<AddressFeed> feeds = stackalloc AddressFeed[MaxAddresses];
        var top = 0;

        try {
            foreach (var token in program) {
                if (token.Operation == ExpressionOp.Constant) {
                    absent[top] = false;
                    stack[top++] = token.Constant;

                    continue;
                }
                if (token.Operation == ExpressionOp.Argument) {
                    if (((ulong)token.Constant) >= ((ulong)arguments.Length)) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    absent[top] = false;
                    stack[top++] = arguments[((int)token.Constant)];

                    continue;
                }
                if (token.Operation == ExpressionOp.Member) {
                    if (member is not { } bound) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    absent[top] = false;
                    stack[top++] = bound;

                    continue;
                }
                if (token.Operation == ExpressionOp.Operand) {
                    if (reader is null) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    var operand = token.Operand!;
                    var fed = FeedCount(operand: operand);

                    top -= fed;
                    for (var slot = 0; (slot < fed); slot++) {
                        feeds[slot] = new AddressFeed(
                            Absent: absent[(top + slot)],
                            Value: stack[(top + slot)]
                        );
                    }

                    var fact = ReadOperand(
                        feeds: feeds[..fed],
                        operand: operand,
                        reader: reader
                    );

                    if (fact.Outcome.Case is FaultCase.Forever or FaultCase.Faulted) {
                        return Outcome.Faulted(why: ((fact.Outcome.Case == FaultCase.Faulted)
                            ? ExpressionFault.Domain
                            : ExpressionFault.Forever));
                    }

                    var factAbsent = (fact.Outcome.Case == FaultCase.Absent);

                    absent[top] = factAbsent;
                    stack[top++] = (factAbsent
                        ? 0L
                        : fact.ToRaw(kind: (token.CarrierKind ?? kind))
                    );

                    continue;
                }
                if (token.Operation == ExpressionOp.IsAbsent) {
                    stack[(top - 1)] = (absent[(top - 1)]
                        ? 1L
                        : 0L
                    );
                    absent[(top - 1)] = false;

                    continue;
                }
                if (token.Operation == ExpressionOp.Not) {
                    if (token.Call is { } truthBody) {
                        var truthOutcome = Run(reader, truthBody.Body, kind, member, arguments);

                        if (truthOutcome.Case == FaultCase.Faulted) {
                            return truthOutcome;
                        }

                        // An absent body is false.
                        stack[top] = (truthOutcome.TryFinite(raw: out var truthValue)
                            ? truthValue
                            : 0L);
                        absent[top++] = false;
                    }
                    stack[(top - 1)] = ((absent[(top - 1)] || (stack[(top - 1)] == 0L)) ? 1L : 0L);
                    absent[(top - 1)] = false;
                    continue;
                }
                if (token.Operation is ExpressionOp.And or ExpressionOp.Or) {
                    var truth = (!absent[(top - 1)] && (stack[(top - 1)] != 0L));

                    if (((token.Operation == ExpressionOp.And) && truth) || ((token.Operation == ExpressionOp.Or) && !truth)) {
                        var rightOutcome = Run(reader, token.Call!.Body, kind, member, arguments);

                        if (rightOutcome.Case == FaultCase.Faulted) {
                            return rightOutcome;
                        }

                        // An absent right side is false.
                        truth = (rightOutcome.TryFinite(raw: out var rightTruth) && (rightTruth != 0L));
                    }
                    stack[(top - 1)] = (truth ? 1L : 0L);
                    absent[(top - 1)] = false;
                    continue;
                }
                if (token.Operation == ExpressionOp.Coalesce) {
                    var fallback = stack[--top];
                    var fallbackAbsent = absent[top];

                    if (absent[(top - 1)]) {
                        absent[(top - 1)] = fallbackAbsent;
                        stack[(top - 1)] = fallback;
                    }

                    continue;
                }
                if (token.Fold is { } fold) {
                    var foldOutcome = Fold(
                        arguments: arguments,
                        fold: fold,
                        reader: reader
                    );

                    if (!foldOutcome.TryFinite(raw: out var folded)) {
                        return foldOutcome;
                    }

                    absent[top] = false;
                    stack[top++] = folded;

                    continue;
                }
                if (token.Call is { } call) {
                    if (call.Arity > MaxArguments) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    top -= call.Arity;
                    for (var slot = 0; (slot < call.Arity); slot++) {
                        if (absent[(top + slot)]) {
                            return Outcome.Absent;
                        }

                        frame[slot] = stack[(top + slot)];
                    }
                    var calledOutcome = Run(
                        arguments: frame[..call.Arity],
                        kind: kind,
                        member: member,
                        program: call.Body,
                        reader: reader
                    );

                    if (!calledOutcome.TryFinite(raw: out var called)) {
                        return calledOutcome;
                    }

                    absent[top] = false;
                    stack[top++] = called;

                    continue;
                }

                var arity = ExpressionOperators.Arity(operation: token.Operation);

                for (var slot = (top - arity); (slot < top); slot++) {
                    if (absent[slot]) {
                        return Outcome.Absent;
                    }
                }

                absent[(top - arity)] = false;
                if (token.Operation == ExpressionOp.Clamp) {
                    var maximum = stack[--top];
                    var minimum = stack[--top];
                    var input = stack[--top];

                    if (minimum > maximum) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    stack[top++] = Math.Clamp(
                        max: maximum,
                        min: minimum,
                        value: input
                    );

                    continue;
                }
                if (token.Operation == ExpressionOp.Select) {
                    var whenFalse = stack[--top];
                    var whenTrue = stack[--top];
                    var condition = stack[--top];

                    stack[top++] = ((condition != 0L)
                        ? whenTrue
                        : whenFalse
                    );

                    continue;
                }
                if (token.Operation == ExpressionOp.BitField) {
                    var width = stack[--top];
                    var offset = stack[--top];
                    var input = stack[--top];

                    if (!ExpressionArithmetic.TryBitField(
                        field: out var field,
                        offset: offset,
                        value: input,
                        width: width
                    )) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    stack[top++] = field;

                    continue;
                }
                if (token.Operation == ExpressionOp.BitInsert) {
                    var width = stack[--top];
                    var offset = stack[--top];
                    var field = stack[--top];
                    var input = stack[--top];

                    if (!ExpressionArithmetic.TryBitInsert(
                        field: field,
                        inserted: out var inserted,
                        offset: offset,
                        value: input,
                        width: width
                    )) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    stack[top++] = inserted;

                    continue;
                }
                if (token.Operation == ExpressionOp.BoardShift) {
                    stack[(top - 1)] = BoardQueries.ShiftMask(
                        ((BoardNeighbourQuery)token.Board!),
                        stack[(top - 1)]
                    );

                    continue;
                }
                if (token.Operation == ExpressionOp.BoardRay) {
                    stack[(top - 1)] = BoardQueries.RayMask(
                        ((BoardNeighbourQuery)token.Board!),
                        stack[(top - 1)]
                    );

                    continue;
                }
                if (token.Operation == ExpressionOp.BoardImage) {
                    var image = ((BoardNeighbourQuery)token.Board!);

                    stack[(top - 1)] = BoardQueries.ImageOfMask(
                        image.Topology,
                        image.Direction,
                        stack[(top - 1)]
                    );

                    continue;
                }
                if (ExpressionArithmetic.FunctionArity(operation: token.Operation) is > 0 and var functionArity) {
                    top -= functionArity;
                    if (
                        !ExpressionArithmetic.FunctionAdmits(
                        token.Operation,
                        (token.CarrierKind ?? kind)
                    ) ||
                        !ExpressionArithmetic.TryValidatedFunction(
                        arguments: stack.Slice(
                            length: functionArity,
                            start: top
                        ),
                        kind: (token.CarrierKind ?? kind),
                        operation: token.Operation,
                        value: out var applied
                    )
                    ) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    stack[top++] = applied;

                    continue;
                }
                if (ExpressionArithmetic.IsUnary(operation: token.Operation)) {
                    if (!ExpressionArithmetic.TryUnary(
                        token.Operation,
                        (token.CarrierKind ?? kind),
                        stack[(top - 1)],
                        out var unary
                    )) {
                        return Outcome.Faulted(why: ExpressionFault.Domain);
                    }

                    stack[(top - 1)] = unary;

                    continue;
                }

                var right = stack[--top];
                var left = stack[--top];

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

                stack[top++] = result;
            }
        } catch (ArithmeticException) {
            return Outcome.Faulted(why: ExpressionFault.Domain);
        }

        if (
            (top == 1) &&
            absent[0]
        ) {
            return Outcome.Absent;
        }

        return ((top == 1)
            ? Outcome.Finite(raw: stack[0])
            : Outcome.Faulted(why: ExpressionFault.Domain));
    }
    // Every cell the family's member row holds is visited once, in the arena's own member order, so the reduction
    // is deterministic. A member's own outcome that is not a number is the fold's.
    private static Outcome Fold(CompiledFold fold, IStateReader? reader, ReadOnlySpan<long> arguments) {
        if (
            (reader is null) ||
            (fold.RowOrdinal < 0)
        ) {
            return Outcome.Faulted(why: ExpressionFault.Domain);
        }

        var accumulator = ((fold.Operation == ExpressionOp.All)
            ? 1L
            : 0L
        );
        var arena = reader.Arena;
        var time = reader.Time;
        var cursor = 0;

        while (arena.TryNextCell(
            cursor: ref cursor,
            key: out var key,
            rowOrdinal: fold.RowOrdinal
        )) {
            _ = arena.TryReadLiveNumber(
                key: key,
                rowOrdinal: fold.RowOrdinal,
                time: in time,
                value: out var member
            );

            var memberOutcome = Run(
                arguments: arguments,
                kind: fold.MemberKind,
                member: member,
                program: fold.Body,
                reader: reader
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
}
