using Puck.Maths;

namespace Puck.State;

/// <summary>Why a compiled expression did not evaluate.</summary>
public enum ExpressionFault : byte {
    /// <summary>The expression evaluated.</summary>
    None,
    /// <summary>A table read named a key its table lacks; the read reported itself.</summary>
    TableKeyMissing,
    /// <summary>An operand read a fact with no number.</summary>
    Forever,
    /// <summary>Overflow, a divide by zero, a function argument outside its domain, or an invalid stack result.</summary>
    Domain,
}

/// <summary>The allocation-free read side every compiled operand and expression shares: key indirection, the
/// handle-addressed cell read, and the postfix expression evaluator. An evaluator hands these its
/// <see cref="IRuleReader"/>; nothing here holds state of its own.</summary>
public static class RuleEvaluation {
    /// <summary>Resolves an operand's cell key for the evaluation in flight: a literal key passes through; a
    /// <c>$cell:</c> indirection reads the cell's integer value as a key; a binding token reads the bound key; a
    /// document project's own key resolves through its <see cref="KeyFact"/>.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="key">The literal key, or <see langword="null"/> when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live indirection, or <see langword="null"/>.</param>
    public static string ResolveKey(IRuleReader reader, string? key, CompiledCellRef? keyFrom) {
        if (keyFrom is not { } indirection) {
            return key!;
        }
        if (indirection.Custom is { } custom) {
            return custom.Resolve(reader: reader);
        }
        if (indirection.Binding == BoundKey.Token) {
            return reader.BoundTokenKey ?? throw new InvalidOperationException("a $token key was read outside a pattern value expression");
        }
        if (indirection.Binding == BoundKey.Previous) {
            // A keyed row holds no slot cell, so the first token's "previous" reads as the absent cell.
            return reader.BoundPreviousKey ?? StateRow.SlotKey.Value;
        }
        if ((indirection.Binding == BoundKey.Each) && (reader.BoundEachKey is { } eachKey)) {
            return eachKey;
        }
        if (indirection.Binding != BoundKey.None) {
            return IndexKeyCache.Get(index: reader.BoundIndex(key: indirection.Binding));
        }

        // The row is fixed, but an indirection whose own inner key spelled a binding token reads whichever cell this
        // evaluation is at, never the compile-time key (empty here) — see CompiledCellRef.InnerKeyBinding.
        var innerKey = (((indirection.InnerKeyBinding == BoundKey.Each) && (reader.BoundEachKey is { } eachInnerKey))
            ? eachInnerKey
            : indirection.Key
        );

        return IndexKeyCache.Get(index: IntegerOf(value: ReadFixed(reader: reader, handle: indirection.Handle, key: innerKey)));
    }

    /// <summary>Returns the integer part of a Q48.16 value — the key or index a cell's value names.</summary>
    /// <param name="value">The fixed-point value.</param>
    public static long IntegerOf(FixedQ4816 value) => (value.Value >> FixedQ4816.FractionBitCount);

    /// <summary>Reads a declared cell as fixed point through its compiled handle — an advancing row's live value
    /// rather than its stored base. A cell the section no longer declares reads as zero.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="handle">The compiled row handle.</param>
    /// <param name="key">The cell key.</param>
    public static FixedQ4816 ReadFixed(IRuleReader reader, StateHandle handle, string key) {
        if (
            !StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: handle, key: key, tick: reader.Tick, row: out var declared, rawValue: out var rawValue, text: out _) ||
            (rawValue is not { } raw)
        ) {
            return FixedQ4816.Zero;
        }

        return ((declared.Kind == CellKind.Fixed)
            ? FixedQ4816.FromRawBits(value: raw)
            : StateReader.LiftSaturating(raw: raw)
        );
    }

    /// <summary>Reads a declared cell as a fact in the row's own encoding; an absent cell reads as integer zero.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="handle">The compiled row handle.</param>
    /// <param name="key">The cell key.</param>
    public static RuleFact ReadStateFact(IRuleReader reader, StateHandle handle, string key) {
        if (
            !StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: handle, key: key, tick: reader.Tick, row: out var declared, rawValue: out var rawValue, text: out _) ||
            (rawValue is not { } raw)
        ) {
            return RuleFact.Finite(value: 0L, kind: CellKind.Int);
        }

        return RuleFact.Finite(value: raw, kind: declared.Kind);
    }

    /// <summary>Evaluates a compiled postfix program in a cell kind. A forever fact, a missing table key, an
    /// overflow, a division by zero, or an invalid stack leaves the result unset and returns <see langword="false"/>.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="program">The compiled postfix program.</param>
    /// <param name="kind">The kind the program was compiled in.</param>
    /// <param name="value">The result, on success.</param>
    public static bool TryEvaluateExpression(IRuleReader reader, CompiledExpressionToken[] program, CellKind kind, out long value) =>
        TryEvaluateExpression(reader: reader, program: program, kind: kind, value: out value, fault: out _);

    /// <summary>Evaluates a compiled postfix expression, naming why it did not evaluate.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="program">The postfix program.</param>
    /// <param name="kind">The kind to evaluate in.</param>
    /// <param name="value">The raw result.</param>
    /// <param name="fault">Why the expression failed, or <see cref="ExpressionFault.None"/>.</param>
    /// <returns><see langword="true"/> when the expression evaluated.</returns>
    public static bool TryEvaluateExpression(IRuleReader reader, CompiledExpressionToken[] program, CellKind kind, out long value, out ExpressionFault fault) {
        fault = ExpressionFault.Domain;
        Span<long> stack = stackalloc long[RuleCapacity.MaxExpressionTokens];
        var top = 0;

        try {
            foreach (var token in program) {
                if (token.Operation == ExpressionOp.Constant) {
                    stack[top++] = token.Constant;
                    continue;
                }
                if (token.Operation == ExpressionOp.Operand) {
                    var fact = token.Operand!.Read(reader: reader);
                    if (reader.TableKeyMissing) {
                        reader.TableKeyMissing = false;
                        fault = ExpressionFault.TableKeyMissing;
                        value = 0L;
                        return false;
                    }
                    if (fact.IsForever) {
                        fault = ExpressionFault.Forever;
                        value = 0L;
                        return false;
                    }
                    stack[top++] = fact.ToRaw(kind: kind);
                    continue;
                }
                if (token.Operation == ExpressionOp.Clamp) {
                    var maximum = stack[--top];
                    var minimum = stack[--top];
                    var input = stack[--top];
                    if (minimum > maximum) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = Math.Clamp(value: input, min: minimum, max: maximum);
                    continue;
                }
                if (token.Operation == ExpressionOp.Select) {
                    var whenFalse = stack[--top];
                    var whenTrue = stack[--top];
                    var condition = stack[--top];
                    stack[top++] = ((condition != 0L) ? whenTrue : whenFalse);
                    continue;
                }
                if (token.Operation == ExpressionOp.BitField) {
                    var width = stack[--top];
                    var offset = stack[--top];
                    var input = stack[--top];
                    if (!ExpressionArithmetic.TryBitField(input, offset, width, out var field)) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = field;
                    continue;
                }
                if (token.Operation == ExpressionOp.BoardShift) {
                    stack[top - 1] = BoardQueries.ShiftMask((BoardNeighbourQuery)token.Board!, stack[top - 1]);
                    continue;
                }
                if (token.Operation == ExpressionOp.BoardImage) {
                    var image = (BoardNeighbourQuery)token.Board!;
                    stack[top - 1] = BoardQueries.ImageOfMask(image.Topology, image.Direction, stack[top - 1]);
                    continue;
                }
                if (token.Operation == ExpressionOp.BitInsert) {
                    var width = stack[--top];
                    var offset = stack[--top];
                    var field = stack[--top];
                    var input = stack[--top];
                    if (!ExpressionArithmetic.TryBitInsert(input, field, offset, width, out var inserted)) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = inserted;
                    continue;
                }
                if (ExpressionArithmetic.FunctionArity(operation: token.Operation) is > 0 and var arity) {
                    top -= arity;
                    if (!ExpressionArithmetic.TryFunction(operation: token.Operation, kind: kind, arguments: stack.Slice(start: top, length: arity), value: out var applied)) {
                        value = 0L;
                        return false;
                    }
                    stack[top++] = applied;
                    continue;
                }
                if (ExpressionArithmetic.IsUnary(token.Operation)) {
                    if (!ExpressionArithmetic.TryUnary(token.Operation, kind, stack[top - 1], out var unary)) {
                        value = 0L;
                        return false;
                    }
                    stack[top - 1] = unary;
                    continue;
                }

                var right = stack[--top];
                var left = stack[--top];
                // Data-dependent arithmetic refusal can occur thousands of times in a dense sample; the checked
                // semantics are preserved without allocating an exception per evaluation.
                if (!ExpressionArithmetic.TryBinary(token.Operation, kind, left, right, out var result)) {
                    value = 0L;
                    return false;
                }

                stack[top++] = result;
            }
        } catch (ArithmeticException) {
            value = 0L;
            return false;
        }

        value = ((top == 1) ? stack[0] : 0L);
        fault = ((top == 1) ? ExpressionFault.None : ExpressionFault.Domain);
        return (top == 1);
    }

    /// <summary>Evaluates a compiled postfix gate. Each comparison reads its operands through the reader; a
    /// <c>compareValue</c> conjunct whose expression faults is false and sets <paramref name="faulted"/>, so the
    /// caller can report it rather than let the gate silently stop holding. A gate with no tokens always holds.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="faulted">Whether a conjunct's expression overflowed, left a function's domain, or read a fact
    /// with no number; a missing table key is reported by the read itself and does not set this.</param>
    /// <param name="trace">An optional per-conjunct narration sink.</param>
    public static bool GateHolds(IRuleReader reader, GateToken[] gate, out bool faulted, List<string>? trace = null) {
        faulted = false;

        if (gate.Length == 0) {
            return true;
        }

        Span<bool> stack = stackalloc bool[RuleCapacity.MaxPredicateTokens];
        var top = 0;

        foreach (var predicate in gate) {
            if (predicate.Op == GateOp.Not) {
                stack[top - 1] = !stack[top - 1];
                trace?.Add(item: $"not -> {(stack[top - 1] ? "true" : "false")}");
                continue;
            }
            if (predicate.Op is GateOp.All or GateOp.Any) {
                var start = (top - predicate.Arity);
                var result = (predicate.Op == GateOp.All);

                for (var index = start; index < top; index++) {
                    result = ((predicate.Op == GateOp.All)
                        ? (result && stack[index])
                        : (result || stack[index]));
                }

                top = start;
                stack[top++] = result;
                trace?.Add(item: $"{((predicate.Op == GateOp.All) ? "all" : "any")} of {predicate.Arity} -> {(result ? "true" : "false")}");
                continue;
            }

            if (predicate.LeftExpression is { } leftExpression) {
                var rightValue = 0L;
                var rightFault = ExpressionFault.None;
                var leftOk = TryEvaluateExpression(reader: reader, program: leftExpression, kind: predicate.ValueKind, value: out var leftValue, fault: out var leftFault);
                var rightOk = leftOk && TryEvaluateExpression(reader: reader, program: predicate.RightExpression!, kind: predicate.ValueKind, value: out rightValue, fault: out rightFault);
                faulted |= ((leftFault is ExpressionFault.Domain or ExpressionFault.Forever) || (rightFault is ExpressionFault.Domain or ExpressionFault.Forever));
                var holds = rightOk && predicate.Comparison.Holds(value: FixedQ4816.FromRawBits(value: leftValue), valueIsForever: false, expected: FixedQ4816.FromRawBits(value: rightValue), expectedIsForever: false);
                stack[top++] = holds;
                if (trace is not null) {
                    var left = (leftOk ? DescribeFact(value: leftValue, kind: predicate.ValueKind, isForever: false) : "refused");
                    var right = (rightOk ? DescribeFact(value: rightValue, kind: predicate.ValueKind, isForever: false) : "refused");
                    trace.Add(item: $"{predicate.Describe}: {left} {DescribeComparison(comparison: predicate.Comparison)} {right} -> {(holds ? "true" : "false")}");
                }
                continue;
            }

            // Reached only for a Compare token with no LeftExpression, so Left is always set here.
            var value = predicate.Left!.Read(reader: reader);
            // The comparand is either the compile-time constant (Comparand null) or a second live operand read on the
            // same terms as the primary side. Both facts read this tick's live section, so a rule that just advanced
            // its own comparand row sees the post-advance value on the very next evaluation.
            var expected = ((predicate.Comparand is { } comparand)
                ? comparand.Read(reader: reader)
                : RuleFact.Finite(value: predicate.Value, kind: predicate.ValueKind)
            );
            var holdsHere = predicate.Comparison.Holds(
                value: FixedQ4816.FromRawBits(value: value.Value),
                valueIsForever: value.IsForever,
                expected: FixedQ4816.FromRawBits(value: expected.Value),
                expectedIsForever: expected.IsForever
            );
            stack[top++] = holdsHere;
            trace?.Add(item: $"{predicate.Describe}: {DescribeFact(value: value.Value, kind: value.Kind, isForever: value.IsForever)} {DescribeComparison(comparison: predicate.Comparison)} {DescribeFact(value: expected.Value, kind: expected.Kind, isForever: expected.IsForever)} -> {(holdsHere ? "true" : "false")}");
        }

        return ((top == 1) && stack[0]);
    }

    /// <summary>Formats a raw fact for a trace: <c>forever</c>, a fixed-point value, or an integer.</summary>
    /// <param name="value">The raw value.</param>
    /// <param name="kind">Its encoding.</param>
    /// <param name="isForever">Whether the fact is positive infinity.</param>
    public static string DescribeFact(long value, CellKind kind, bool isForever) =>
        (isForever
            ? "forever"
            : ((kind == CellKind.Fixed)
                ? FixedQ4816.FromRawBits(value: value).ToString()
                : value.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Formats a comparison as its operator spelling.</summary>
    /// <param name="comparison">The comparison.</param>
    public static string DescribeComparison(ActionStateComparison comparison) => comparison switch {
        ActionStateComparison.Equal => "==",
        ActionStateComparison.NotEqual => "!=",
        ActionStateComparison.Less => "<",
        ActionStateComparison.LessOrEqual => "<=",
        ActionStateComparison.Greater => ">",
        _ => ">=",
    };
}
