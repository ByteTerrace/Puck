namespace Puck.State.Rules;

/// <summary>A gate's deadline conjunct: a comparison of the tick against a value read from state (<c>$tick &gt;= X</c>,
/// <c>$tick &gt; X</c>, or the same spelled with the tick on the right) whose falsehood alone closes the gate. While
/// the gate's other reads hold, its truth can change only when the tick crosses X, so a closed verdict carries the
/// tick it must be judged again at instead of making the rule volatile.</summary>
/// <param name="Token">The comparison's index in the gate.</param>
/// <param name="TickOnLeft">Whether the tick is the comparison's left side.</param>
/// <param name="Strict">Whether the tick must exceed X rather than reach it.</param>
/// <param name="Scale">The raw value one tick reads as on the comparison's side: 1 for an integer read, 1 &lt;&lt; 16 for a
/// Q48.16 read.</param>
public sealed record RuleDeadline(int Token, bool TickOnLeft, bool Strict, long Scale) {
    /// <summary>Finds a gate's deadline conjunct, when it has one: the first comparison of the tick alone against a
    /// side that reads no tick, ordered so the gate holds only at or after a tick, whose falsehood closes the gate
    /// whatever its other comparisons answer.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <returns>The deadline, or <see langword="null"/>.</returns>
    public static RuleDeadline? Find(GateToken[] gate) {
        ArgumentNullException.ThrowIfNull(argument: gate);

        for (var index = 0; (index < gate.Length); index++) {
            var token = gate[index];

            if ((token.Op != GateOp.Compare) || (token.ValueKind is not (CellKind.Int or CellKind.Fixed))) {
                continue;
            }

            var leftScale = TickScale(source: token.LeftSource, kind: token.ValueKind);
            var rightScale = TickScale(source: token.RightSource, kind: token.ValueKind);
            RuleDeadline? found = null;

            if ((leftScale > 0L) && !ReadsTick(source: token.RightSource) && (token.Comparison is (ExpressionOp.GreaterOrEqual or ExpressionOp.Greater))) {
                found = new RuleDeadline(Scale: leftScale, Strict: (token.Comparison == ExpressionOp.Greater), TickOnLeft: true, Token: index);
            } else if ((rightScale > 0L) && !ReadsTick(source: token.LeftSource) && (token.Comparison is (ExpressionOp.LessOrEqual or ExpressionOp.Less))) {
                found = new RuleDeadline(Scale: rightScale, Strict: (token.Comparison == ExpressionOp.Less), TickOnLeft: false, Token: index);
            }
            if ((found is not null) && Closes(gate: gate, token: index)) {
                return found;
            }
        }

        return null;
    }
    /// <summary>Returns the first tick at which the deadline comparison holds against X, or
    /// <see cref="ulong.MaxValue"/> when it never will: X absent, or forever.</summary>
    /// <param name="x">X as the comparison reads it, in the comparison's kind.</param>
    /// <returns>The tick.</returns>
    public ulong FirstHoldingTick(in Fact x) {
        if (!x.Outcome.TryFinite(raw: out var raw)) {
            return ulong.MaxValue;
        }

        var first = (Strict
            ? (Puck.Maths.BinaryIntegerFunctions.FloorDivide(value: raw, divisor: Scale) + 1L)
            : Puck.Maths.BinaryIntegerFunctions.CeilingDivide(value: raw, divisor: Scale));

        return ((first <= 0L) ? 0UL : (ulong)first);
    }

    // The raw value one tick reads as when a side is the tick alone: its operand read as an integer, or a one-token
    // program evaluated in an integer or Q48.16 carrier; 0 when the side is anything else.
    private static long TickScale(in CompiledValueSource source, CellKind kind) {
        if (source.Operand is TickOperand) {
            return 1L;
        }
        if (source.Expression is [{ Operand: TickOperand, Fold: null, Call: null }]) {
            return ((source.CarrierKind ?? kind) switch {
                CellKind.Int => 1L,
                CellKind.Fixed => (1L << 16),
                _ => 0L,
            });
        }

        return 0L;
    }
    private static bool ReadsTick(in CompiledValueSource source) {
        var needs = new RuleNeedsBuilder();

        RuleDataflow.CollectSourceFacts(into: needs, source: source);

        return needs.Build().ReadsTick;
    }
    // Whether the comparison at `token` reading false makes the whole gate read false, whatever every other comparison
    // answers: the postfix program folded over three values (false, true, unknown), skipping its short-circuit
    // instructions, which only spare work and never change the answer.
    private static bool Closes(GateToken[] gate, int token) {
        const byte False = 0, True = 1, Unknown = 2;
        var stack = new byte[gate.Length];
        var top = 0;

        for (var index = 0; (index < gate.Length); index++) {
            var op = gate[index];

            switch (op.Op) {
                case GateOp.ShortCircuit:
                    continue;
                case GateOp.Not:
                    stack[(top - 1)] = (stack[(top - 1)] switch { False => True, True => False, _ => Unknown });
                    continue;
                case GateOp.All:
                case GateOp.Any: {
                    var isAll = (op.Op == GateOp.All);
                    var decided = false;
                    var unknown = false;

                    for (var operand = (top - op.Arity); (operand < top); operand++) {
                        decided |= (stack[operand] == (isAll ? False : True));
                        unknown |= (stack[operand] == Unknown);
                    }
                    top -= op.Arity;
                    stack[top++] = (decided ? (isAll ? False : True) : (unknown ? Unknown : (isAll ? True : False)));
                    continue;
                }
                default:
                    stack[top++] = ((index == token) ? False : Unknown);
                    continue;
            }
        }

        return ((top == 1) && (stack[0] == False));
    }
}
