namespace Puck.State.Rules;

/// <summary>A gate's answer folded over three values (false, true, unknown) from what is known of each of its
/// comparisons: the one walk that proves a gate closed whatever its other comparisons answer. Its short-circuit
/// instructions only spare work and never change the answer, so the fold skips them.</summary>
internal static class GateTruth {
    /// <summary>The comparison, or the gate, is false.</summary>
    public const byte False = 0;
    /// <summary>The comparison, or the gate, is true.</summary>
    public const byte True = 1;
    /// <summary>Nothing is known of the comparison, or the gate's answer depends on what is not known.</summary>
    public const byte Unknown = 2;

    /// <summary>Folds a postfix gate over what <paramref name="leaf"/> knows of each comparison.</summary>
    /// <param name="gate">The compiled gate.</param>
    /// <param name="leaf">What is known of the comparison at an index: <see cref="False"/>, <see cref="True"/> or
    /// <see cref="Unknown"/>.</param>
    /// <returns>The gate's answer, <see cref="Unknown"/> for an empty or malformed gate.</returns>
    public static byte Fold(GateToken[] gate, Func<int, byte> leaf) {
        ArgumentNullException.ThrowIfNull(argument: gate);
        ArgumentNullException.ThrowIfNull(argument: leaf);

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
                    stack[top++] = leaf(arg: index);
                    continue;
            }
        }

        return ((top == 1) ? stack[0] : Unknown);
    }
}
