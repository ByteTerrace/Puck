namespace Puck.State.Rules;

/// <summary>The one operator dispatch for a compiled postfix program: the walk a gate, a binding, an effect source,
/// and the compiler's own constant folder all evaluate through, so a folded constant and a live evaluation of the
/// same operation agree bit for bit.</summary>
/// <remarks>Evaluation allocates nothing once it has run: the value stack and the absence marks are leased from
/// the reader's <see cref="ArenaScratch"/> at the program's own length, a call's argument frame is a fixed few
/// words of machine stack, and a fold reads its family's cells straight off the arena.</remarks>
public static class RuleExpressions {
    /// <summary>The most arguments one shared function takes: a call's argument frame is this many eight-byte
    /// words of machine stack in every evaluator frame.</summary>
    public const int MaxArguments = 16;

    // The compiler folds constants with no evaluation in flight, so it has no host to borrow from.
    [ThreadStatic]
    private static ArenaScratch? FoldScratch;

    /// <summary>Evaluates a compiled postfix program.</summary>
    /// <param name="reader">The evaluation in flight, or <see langword="null"/> to fold constants — a live read
    /// refuses without one.</param>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <param name="value">The raw result.</param>
    /// <param name="fault">Why the program failed, or <see cref="ExpressionFault.None"/>.</param>
    /// <returns><see langword="true"/> when the program evaluated.</returns>
    public static bool TryEvaluate(IStateReader? reader, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind, out long value, out ExpressionFault fault) => Run(
        arguments: [],
        fault: out fault,
        kind: kind,
        member: null,
        program: program,
        reader: reader,
        value: out value
    );

    private static bool Run(IStateReader? reader, ReadOnlySpan<CompiledExpressionToken> program, CellKind kind, long? member, ReadOnlySpan<long> arguments, out long value, out ExpressionFault fault) {
        fault = ExpressionFault.Domain;

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
                        value = 0L;

                        return false;
                    }

                    absent[top] = false;
                    stack[top++] = arguments[((int)token.Constant)];

                    continue;
                }
                if (token.Operation == ExpressionOp.Member) {
                    if (member is not { } bound) {
                        value = 0L;

                        return false;
                    }

                    absent[top] = false;
                    stack[top++] = bound;

                    continue;
                }
                if (token.Operation == ExpressionOp.Operand) {
                    if (reader is null) {
                        value = 0L;

                        return false;
                    }

                    var fact = token.Operand!.Read(reader: reader);

                    if (fact.IsForever) {
                        fault = ExpressionFault.Forever;
                        value = 0L;

                        return false;
                    }

                    absent[top] = fact.IsAbsent;
                    stack[top++] = (fact.IsAbsent
                        ? 0L
                        : fact.ToRaw(kind: kind)
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
                    if (!TryFold(
                        arguments: arguments,
                        fault: out fault,
                        fold: fold,
                        reader: reader,
                        value: out var folded
                    )) {
                        value = 0L;

                        return false;
                    }

                    absent[top] = false;
                    stack[top++] = folded;

                    continue;
                }
                if (token.Call is { } call) {
                    if (call.Arity > MaxArguments) {
                        value = 0L;

                        return false;
                    }

                    top -= call.Arity;
                    for (var slot = 0; (slot < call.Arity); slot++) {
                        if (absent[(top + slot)]) {
                            fault = ExpressionFault.Absent;
                            value = 0L;

                            return false;
                        }

                        frame[slot] = stack[(top + slot)];
                    }
                    if (!Run(
                        arguments: frame[..call.Arity],
                        fault: out fault,
                        kind: kind,
                        member: member,
                        program: call.Body,
                        reader: reader,
                        value: out var called
                    )) {
                        value = 0L;

                        return false;
                    }

                    absent[top] = false;
                    stack[top++] = called;

                    continue;
                }

                var arity = ExpressionOperators.Arity(operation: token.Operation);

                for (var slot = (top - arity); (slot < top); slot++) {
                    if (absent[slot]) {
                        fault = ExpressionFault.Absent;
                        value = 0L;

                        return false;
                    }
                }

                absent[(top - arity)] = false;
                if (token.Operation == ExpressionOp.Clamp) {
                    var maximum = stack[--top];
                    var minimum = stack[--top];
                    var input = stack[--top];

                    if (minimum > maximum) {
                        value = 0L;

                        return false;
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
                        value = 0L;

                        return false;
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
                        value = 0L;

                        return false;
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
                if (token.Operation == ExpressionOp.BoardFill) {
                    stack[(top - 1)] = BoardQueries.FillMask(
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
                        kind
                    ) ||
                        !ExpressionArithmetic.TryValidatedFunction(
                        arguments: stack.Slice(
                            length: functionArity,
                            start: top
                        ),
                        kind: kind,
                        operation: token.Operation,
                        value: out var applied
                    )
                    ) {
                        value = 0L;

                        return false;
                    }

                    stack[top++] = applied;

                    continue;
                }
                if (ExpressionArithmetic.IsUnary(operation: token.Operation)) {
                    if (!ExpressionArithmetic.TryUnary(
                        token.Operation,
                        kind,
                        stack[(top - 1)],
                        out var unary
                    )) {
                        value = 0L;

                        return false;
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
                    kind,
                    left,
                    right,
                    out var result
                )) {
                    value = 0L;

                    return false;
                }

                stack[top++] = result;
            }
        } catch (ArithmeticException) {
            value = 0L;

            return false;
        }

        if (
            (top == 1) &&
            absent[0]
        ) {
            fault = ExpressionFault.Absent;
            value = 0L;

            return false;
        }

        value = ((top == 1)
            ? stack[0]
            : 0L
        );
        fault = ((top == 1)
            ? ExpressionFault.None
            : ExpressionFault.Domain
        );

        return (top == 1);
    }
    // Every cell the family's member row holds is visited once, in the arena's own member order, so the reduction
    // is deterministic.
    private static bool TryFold(CompiledFold fold, IStateReader? reader, ReadOnlySpan<long> arguments, out long value, out ExpressionFault fault) {
        fault = ExpressionFault.Domain;
        value = 0L;

        if (
            (reader is null) ||
            (fold.RowOrdinal < 0)
        ) {
            return false;
        }

        var accumulator = ((fold.Operation == ExpressionOp.All)
            ? 1L
            : 0L
        );
        var arena = reader.Arena;
        var count = arena.CellCount(rowOrdinal: fold.RowOrdinal);
        var time = reader.Time;

        for (var position = 0; (position < count); position++) {
            if (!arena.TryKeyAt(
                key: out var key,
                position: position,
                rowOrdinal: fold.RowOrdinal
            )) {
                continue;
            }

            _ = arena.TryReadLiveNumber(
                key: key,
                rowOrdinal: fold.RowOrdinal,
                time: in time,
                value: out var member
            );

            if (!Run(
                arguments: arguments,
                fault: out fault,
                kind: fold.MemberKind,
                member: member,
                program: fold.Body,
                reader: reader,
                value: out var applied
            )) {
                return false;
            }

            switch (fold.Operation) {
                case ExpressionOp.All:
                    if (applied == 0L) {
                        fault = ExpressionFault.None;
                        value = 0L;

                        return true;
                    }

                    break;
                case ExpressionOp.Any:
                    if (applied != 0L) {
                        fault = ExpressionFault.None;
                        value = 1L;

                        return true;
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
                        return false;
                    }

                    break;
            }
        }

        fault = ExpressionFault.None;
        value = accumulator;

        return true;
    }
}
