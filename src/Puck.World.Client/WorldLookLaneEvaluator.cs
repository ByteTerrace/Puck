namespace Puck.World.Client;

/// <summary>Evaluates a render lane expression against a body's live state — the client-side,
/// presentation half of the shared <c>ExpressionSpelling</c>/<see cref="ExpressionProgram"/> grammar. Reads EASED
/// state (<see cref="WorldGaitDrivers.TryReadStateNumber(WorldDefinition, string, ulong, out float, int, ulong)"/>, the
/// same smoothing every other look/driver binding takes), not the raw authoritative tick a server rule reads — a
/// lane expression drives presentation without modifying simulation values.
/// Supports a restricted arithmetic subset of <see cref="Instruction"/> (constants, state reads, and the basic
/// arithmetic/comparison/min/max/clamp/sign operators); every other token, a malformed stack, or a failed read
/// (division by zero, an absent cell) evaluates to 0 — matching an unauthored lane, never a thrown exception on a
/// per-frame render path.</summary>
public static class WorldLookLaneEvaluator {
    private static bool TryBinary(Span<float> stack, ref int depth, Func<float, float, float> transform) {
        if (
            (depth < 2) ||
            !float.IsFinite(f: stack[(depth - 1)]) ||
            !float.IsFinite(f: stack[(depth - 2)])
        ) {
            return false;
        }

        stack[(depth - 2)] = transform(
            stack[(depth - 2)],
            stack[(depth - 1)]
        );
        depth--;

        return true;
    }
    private static bool TryUnary(Span<float> stack, ref int depth, Func<float, float> transform) {
        if (
            (depth < 1) ||
            !float.IsFinite(f: stack[(depth - 1)])
        ) {
            return false;
        }

        stack[(depth - 1)] = transform(stack[(depth - 1)]);

        return true;
    }

    /// <summary>Evaluates one lane expression.</summary>
    /// <param name="expression">The expression, or <see langword="null"/> for an unauthored lane (reads 0).</param>
    /// <param name="definition">The live definition.</param>
    /// <param name="tick">The tick to read eased state at.</param>
    /// <param name="bodyIndex">The body's index, substituted for <c>$body</c> in a state key.</param>
    /// <returns>The evaluated value, or 0 for a null expression, an unsupported token, or a failed evaluation.</returns>
    public static float Evaluate(ExpressionProgram? expression, WorldDefinition definition, ulong tick, int bodyIndex) {
        if (expression is not { Instructions.Count: > 0 } authored) {
            return 0f;
        }

        Span<float> stack = stackalloc float[authored.Instructions.Count];
        var depth = 0;

        foreach (var token in authored.Instructions) {
            switch (token) {
                case { Payload: InstructionPayload.Constant constant }: {
                        if (depth >= stack.Length) {
                            return 0f;
                        }

                        stack[depth++] = ((float)constant.Value);
                        break;
                    }
                case { Payload: InstructionPayload.State state }: {
                        if (depth >= stack.Length) {
                            return 0f;
                        }

                        var reference = ((state.Key is { } key)
                            ? $"state.{state.Name}.{key}"
                            : $"state.{state.Name}"
                        );

                        stack[depth++] = (WorldGaitDrivers.TryReadStateNumber(
                            bodyIndex: bodyIndex,
                            definition: definition,
                            reference: reference,
                            tick: tick,
                            value: out var value
                        )
                            ? value
                            : 0f
                        );
                        break;
                    }
                case { Operation: ExpressionOp.Negate }: {
                        if (!TryUnary(
                            depth: ref depth,
                            stack: stack,
                            transform: static v => -v
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Abs }: {
                        if (!TryUnary(
                            depth: ref depth,
                            stack: stack,
                            transform: MathF.Abs
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Sign }: {
                        if (!TryUnary(
                            stack: stack,
                            depth: ref depth,
                            transform: static v => MathF.Sign(x: v)
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Add }: {
                        if (!TryBinary(
                            depth: ref depth,
                            stack: stack,
                            transform: static (l, r) => (l + r)
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Subtract }: {
                        if (!TryBinary(
                            depth: ref depth,
                            stack: stack,
                            transform: static (l, r) => (l - r)
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Multiply }: {
                        if (!TryBinary(
                            depth: ref depth,
                            stack: stack,
                            transform: static (l, r) => (l * r)
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Divide }: {
                        if (
                            (depth < 2) ||
                            (stack[(depth - 1)] == 0f)
                        ) {
                            return 0f;
                        }

                        stack[(depth - 2)] = (stack[(depth - 2)] / stack[(depth - 1)]);
                        depth--;
                        break;
                    }
                case { Operation: ExpressionOp.Minimum }: {
                        if (!TryBinary(
                            depth: ref depth,
                            stack: stack,
                            transform: MathF.Min
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Maximum }: {
                        if (!TryBinary(
                            depth: ref depth,
                            stack: stack,
                            transform: MathF.Max
                        )) {
                            return 0f;
                        }

                        break;
                    }
                case { Operation: ExpressionOp.Clamp }: {
                        if (
                            (depth < 3) ||
                            !float.IsFinite(f: stack[(depth - 2)]) ||
                            !float.IsFinite(f: stack[(depth - 1)]) ||
                            (stack[(depth - 2)] > stack[(depth - 1)])
                        ) {
                            return 0f;
                        }

                        stack[(depth - 3)] = Math.Clamp(
                            value: stack[(depth - 3)],
                            min: stack[(depth - 2)],
                            max: stack[(depth - 1)]
                        );
                        depth -= 2;
                        break;
                    }
                default: {
                        // An unsupported token (state-only vocabulary — hex/board/morton/combinatorics/etc) — refuse
                        // by reading 0 rather than guessing.
                        return 0f;
                    }
            }
        }

        return ((depth == 1)
            ? (float.IsFinite(f: stack[0])
                ? stack[0]
                : 0f)
            : 0f
        );
    }
    /// <summary>Evaluates the four expressions in component order; absent entries are zero.</summary>
    public static System.Numerics.Vector4 EvaluateLanes(IReadOnlyList<ExpressionProgram?>? expressions, WorldDefinition definition, ulong tick, int bodyIndex) {
        var result = System.Numerics.Vector4.Zero;

        for (var index = 0; (index < Math.Min(
            val1: (expressions?.Count ?? 0),
            val2: 4
        )); index++) {
            result[index] = Evaluate(
                expressions![index],
                definition,
                tick,
                bodyIndex
            );
        }
        return result;
    }
}
