namespace Puck.ShaderVm;

using System.Numerics;
using System.Runtime.CompilerServices;

/// <summary>Evaluates a packed Shader VM program on the host.</summary>
/// <remarks>
/// This is the reference semantics of every opcode. KEEP IN SYNC with
/// <c>Assets/Shaders/ShaderVm/shader-vm.hlsli</c>, whose interpreter must agree lane for lane.
/// </remarks>
public static class ShaderInterpreter {
    /// <summary>Evaluates a program against one execution context.</summary>
    /// <param name="program">The packed program.</param>
    /// <param name="context">The execution inputs.</param>
    /// <param name="parameters">The caller-supplied parameter table.</param>
    /// <returns>The program result.</returns>
    /// <exception cref="InvalidOperationException">The program addresses a parameter the table does not carry.</exception>
    public static Vector4 Evaluate(ShaderProgram program, in ShaderContext context, ReadOnlySpan<Vector4> parameters) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var words = program.Words.Span;
        var constantCount = program.ConstantCount;
        var instructionCount = program.InstructionCount;
        var constantBase = (ShaderIsa.HeaderWordCount + instructionCount);
        var locals = (stackalloc Vector4[ShaderIsa.MaxLocals]);
        var stack = (stackalloc Vector4[ShaderIsa.MaxStackDepth]);

        locals.Clear();

        var depth = 0;
        var pointer = 0;

        // The stream was decoded and validated when the program was built, so the hot loop reads raw words: no
        // per-instruction Enum.IsDefined, no bounds re-check.
        while (pointer < instructionCount) {
            var word = words[ShaderIsa.HeaderWordCount + pointer];
            var op = ((ShaderOp)(word & 0xFFu));
            var operand = (word >> 8);

            pointer++;

            switch (op) {
                case ShaderOp.LoadInput: {
                    stack[depth++] = (((ShaderInput)operand) switch {
                        ShaderInput.Coordinate => context.Coordinate,
                        ShaderInput.Time => new Vector4(value: context.Time),
                        _ => new Vector4(value: ((float)context.SampleIndex)),
                    });

                    break;
                }
                case ShaderOp.LoadParameter: {
                    if (operand >= parameters.Length) {
                        throw new InvalidOperationException(message: $"The program loads parameter {operand}; the caller supplied {parameters.Length}.");
                    }

                    stack[depth++] = parameters[checked((int)operand)];

                    break;
                }
                case ShaderOp.LoadConstant: {
                    stack[depth++] = Constant(index: ((int)operand), words: words, constantBase: constantBase);

                    break;
                }
                case ShaderOp.LoadConstantDynamic: {
                    stack[(depth - 1)] = Constant(
                        constantBase: constantBase,
                        index: Math.Clamp(value: ((int)stack[(depth - 1)].X), min: 0, max: (constantCount - 1)),
                        words: words
                    );

                    break;
                }
                case ShaderOp.LoadLocal: {
                    stack[depth++] = locals[checked((int)operand)];

                    break;
                }
                case ShaderOp.StoreLocal: {
                    locals[checked((int)operand)] = stack[--depth];

                    break;
                }
                case ShaderOp.Swizzle: {
                    var source = stack[(depth - 1)];

                    stack[(depth - 1)] = new Vector4(
                        x: Lane(value: source, lane: ShaderIsa.UnpackSwizzle(operand: operand, lane: 0)),
                        y: Lane(value: source, lane: ShaderIsa.UnpackSwizzle(operand: operand, lane: 1)),
                        z: Lane(value: source, lane: ShaderIsa.UnpackSwizzle(operand: operand, lane: 2)),
                        w: Lane(value: source, lane: ShaderIsa.UnpackSwizzle(operand: operand, lane: 3))
                    );

                    break;
                }
                case ShaderOp.Duplicate: {
                    stack[depth] = stack[(depth - 1)];
                    depth++;

                    break;
                }
                case ShaderOp.Swap: {
                    (stack[(depth - 1)], stack[(depth - 2)]) = (stack[(depth - 2)], stack[(depth - 1)]);

                    break;
                }
                case ShaderOp.Drop: {
                    depth--;

                    break;
                }
                case ShaderOp.Pick: {
                    stack[depth] = stack[((depth - 1) - ((int)operand))];
                    depth++;

                    break;
                }
                case ShaderOp.Jump: {
                    pointer = checked((int)operand);

                    break;
                }
                case ShaderOp.JumpIfZero: {
                    if (stack[--depth].X == 0f) {
                        pointer = checked((int)operand);
                    }

                    break;
                }
                case ShaderOp.Halt: {
                    return stack[--depth];
                }
                default: {
                    depth = Apply(
                        depth: depth,
                        op: op,
                        operand: operand,
                        stack: stack
                    );

                    break;
                }
            }
        }

        return stack[(depth - 1)];
    }
    private static Vector4 Constant(ReadOnlySpan<uint> words, int constantBase, int index) {
        var wordBase = (constantBase + (index * 4));

        return new Vector4(
            w: BitConverter.UInt32BitsToSingle(words[(wordBase + 3)]),
            x: BitConverter.UInt32BitsToSingle(words[(wordBase + 0)]),
            y: BitConverter.UInt32BitsToSingle(words[(wordBase + 1)]),
            z: BitConverter.UInt32BitsToSingle(words[(wordBase + 2)])
        );
    }
    private static int Apply(int depth, ShaderOp op, uint operand, Span<Vector4> stack) {
        if (op <= ShaderOp.IntegerBits) {
            stack[(depth - 1)] = Unary(op: op, value: stack[(depth - 1)]);

            return depth;
        }
        if (op <= ShaderOp.ArcTangent2) {
            stack[(depth - 2)] = Binary(left: stack[(depth - 2)], op: op, right: stack[(depth - 1)]);

            return (depth - 1);
        }
        if (op <= ShaderOp.Select) {
            stack[(depth - 3)] = Ternary(a: stack[(depth - 3)], b: stack[(depth - 2)], c: stack[(depth - 1)], op: op);

            return (depth - 2);
        }

        stack[(depth - 1)] = Field(op: op, octaves: ((int)operand), value: stack[(depth - 1)]);

        return depth;
    }
    private static Vector4 Unary(ShaderOp op, Vector4 value) => op switch {
        ShaderOp.Absolute => Vector4.Abs(value: value),
        ShaderOp.Negate => -value,
        ShaderOp.Floor => Map(value: value, map: MathF.Floor),
        ShaderOp.Ceiling => Map(value: value, map: MathF.Ceiling),
        ShaderOp.Fraction => Map(value: value, map: component => (component - MathF.Floor(x: component))),
        ShaderOp.Saturate => Vector4.Clamp(value1: value, min: Vector4.Zero, max: Vector4.One),
        ShaderOp.Truncate => Map(value: value, map: MathF.Truncate),
        ShaderOp.Round => Map(value: value, map: MathF.Round),
        ShaderOp.Sign => Map(value: value, map: component => ((float)MathF.Sign(x: component))),
        ShaderOp.Reciprocal => (Vector4.One / value),
        ShaderOp.SquareRoot => Vector4.SquareRoot(value: value),
        ShaderOp.InverseSquareRoot => (Vector4.One / Vector4.SquareRoot(value: value)),
        ShaderOp.Exponential => Map(value: value, map: MathF.Exp),
        ShaderOp.NaturalLogarithm => Map(value: value, map: MathF.Log),
        ShaderOp.Sine => Map(value: value, map: MathF.Sin),
        ShaderOp.Cosine => Map(value: value, map: MathF.Cos),
        ShaderOp.Normalize2 => Normalize(value: new Vector4(x: value.X, y: value.Y, z: 0f, w: 0f)),
        ShaderOp.Normalize3 => Normalize(value: new Vector4(x: value.X, y: value.Y, z: value.Z, w: 0f)),
        ShaderOp.Length2 => new Vector4(value: MathF.Sqrt(x: ((value.X * value.X) + (value.Y * value.Y)))),
        ShaderOp.Length3 => new Vector4(value: MathF.Sqrt(x: (((value.X * value.X) + (value.Y * value.Y)) + (value.Z * value.Z)))),
        ShaderOp.Hash3 => HashBits(value: value),
        ShaderOp.BitsToUnitFloat => Map(value: value, map: component => (((float)BitConverter.SingleToUInt32Bits(component)) * ShaderIsa.InverseTwoPow32)),
        ShaderOp.IntegerBits => Map(value: value, map: component => BitConverter.UInt32BitsToSingle(((uint)component))),
        _ => throw new ArgumentException(message: $"Shader operation {op} is not unary.", paramName: nameof(op)),
    };
    private static Vector4 Binary(Vector4 left, ShaderOp op, Vector4 right) => op switch {
        ShaderOp.Add => (left + right),
        ShaderOp.Subtract => (left - right),
        ShaderOp.Multiply => (left * right),
        ShaderOp.Divide => (left / right),
        ShaderOp.Minimum => Vector4.Min(value1: left, value2: right),
        ShaderOp.Maximum => Vector4.Max(value1: left, value2: right),
        ShaderOp.Power => Zip(left: left, right: right, zip: MathF.Pow),
        ShaderOp.Modulo => Zip(left: left, right: right, zip: (a, b) => (a % b)),
        ShaderOp.Step => Zip(left: left, right: right, zip: (edge, x) => ((x >= edge) ? 1f : 0f)),
        ShaderOp.Dot2 => new Vector4(value: ((left.X * right.X) + (left.Y * right.Y))),
        ShaderOp.Dot3 => new Vector4(value: (((left.X * right.X) + (left.Y * right.Y)) + (left.Z * right.Z))),
        ShaderOp.Cross3 => new Vector4(
            x: ((left.Y * right.Z) - (left.Z * right.Y)),
            y: ((left.Z * right.X) - (left.X * right.Z)),
            z: ((left.X * right.Y) - (left.Y * right.X)),
            w: 0f
        ),
        ShaderOp.Less => Zip(left: left, right: right, zip: (a, b) => ((a < b) ? 1f : 0f)),
        ShaderOp.Greater => Zip(left: left, right: right, zip: (a, b) => ((a > b) ? 1f : 0f)),
        ShaderOp.ArcTangent2 => Zip(left: left, right: right, zip: MathF.Atan2),
        _ => throw new ArgumentException(message: $"Shader operation {op} is not binary.", paramName: nameof(op)),
    };
    private static Vector4 Ternary(Vector4 a, Vector4 b, Vector4 c, ShaderOp op) => op switch {
        ShaderOp.Lerp => (a + ((b - a) * c)),
        ShaderOp.SmoothStep => new Vector4(
            x: SmoothStep(edge0: a.X, edge1: b.X, value: c.X),
            y: SmoothStep(edge0: a.Y, edge1: b.Y, value: c.Y),
            z: SmoothStep(edge0: a.Z, edge1: b.Z, value: c.Z),
            w: SmoothStep(edge0: a.W, edge1: b.W, value: c.W)
        ),
        ShaderOp.Clamp => Vector4.Clamp(value1: a, min: b, max: c),
        ShaderOp.Select => new Vector4(
            x: ((c.X != 0f) ? b.X : a.X),
            y: ((c.Y != 0f) ? b.Y : a.Y),
            z: ((c.Z != 0f) ? b.Z : a.Z),
            w: ((c.W != 0f) ? b.W : a.W)
        ),
        _ => throw new ArgumentException(message: $"Shader operation {op} is not ternary.", paramName: nameof(op)),
    };
    // The lattice fields read their seed from lane w as a raw bit pattern, so a caller composes a seed with plain
    // float arithmetic and lands it there through IntegerBits.
    private static Vector4 Field(ShaderOp op, int octaves, Vector4 value) {
        var seed = BitConverter.SingleToUInt32Bits(value.W);

        return op switch {
            ShaderOp.ValueNoise2 => new Vector4(value: LatticeNoise2(x: value.X, y: value.Y, seed: seed)),
            ShaderOp.ValueNoise3 => new Vector4(value: LatticeNoise3(x: value.X, y: value.Y, z: value.Z, seed: seed)),
            ShaderOp.Fbm2 => new Vector4(value: Fbm2(x: value.X, y: value.Y, seed: seed, octaves: octaves)),
            _ => throw new ArgumentException(message: $"Shader operation {op} is not a field sample.", paramName: nameof(op)),
        };
    }
    private static float Fbm2(float x, float y, uint seed, int octaves) {
        var amplitude = 0.5f;
        var normalizer = 0f;
        var value = 0f;

        for (var octave = 0; (octave < octaves); octave++) {
            value += (amplitude * LatticeNoise2(x: x, y: y, seed: (seed + ((uint)octave))));
            normalizer += amplitude;
            x = ((x * 2f) + 17f);
            y = ((y * 2f) + 17f);
            amplitude *= 0.5f;
        }

        return (value / normalizer);
    }
    private static float LatticeNoise2(float x, float y, uint seed) {
        var cellX = MathF.Floor(x: x);
        var cellY = MathF.Floor(x: y);
        var a = Corner(x: cellX, y: cellY, seed: seed);
        var b = Corner(x: (cellX + 1f), y: cellY, seed: seed);
        var c = Corner(x: cellX, y: (cellY + 1f), seed: seed);
        var d = Corner(x: (cellX + 1f), y: (cellY + 1f), seed: seed);
        var u = Quintic(value: (x - cellX));
        var v = Quintic(value: (y - cellY));

        return Mix(a: Mix(a: a, b: b, t: u), b: Mix(a: c, b: d, t: u), t: v);
    }
    private static float LatticeNoise3(float x, float y, float z, uint seed) {
        var cellZ = MathF.Floor(x: z);
        var w = Quintic(value: (z - cellZ));
        var lower = LatticeNoise2(x: x, y: y, seed: (seed ^ BitConverter.SingleToUInt32Bits(cellZ)));
        var upper = LatticeNoise2(x: x, y: y, seed: (seed ^ BitConverter.SingleToUInt32Bits((cellZ + 1f))));

        return Mix(a: lower, b: upper, t: w);
    }
    private static float Corner(float x, float y, uint seed) {
        var (hashed, _, _) = ShaderIsa.Pcg3d(
            x: BitConverter.SingleToUInt32Bits(x),
            y: BitConverter.SingleToUInt32Bits(y),
            z: seed
        );

        return (((float)hashed) * ShaderIsa.InverseTwoPow32);
    }
    private static Vector4 HashBits(Vector4 value) {
        var (x, y, z) = ShaderIsa.Pcg3d(
            x: BitConverter.SingleToUInt32Bits(value.X),
            y: BitConverter.SingleToUInt32Bits(value.Y),
            z: BitConverter.SingleToUInt32Bits(value.Z)
        );

        return new Vector4(
            x: BitConverter.UInt32BitsToSingle(x),
            y: BitConverter.UInt32BitsToSingle(y),
            z: BitConverter.UInt32BitsToSingle(z),
            w: 0f
        );
    }
    private static Vector4 Normalize(Vector4 value) {
        var length = value.Length();

        return ((length > 0f)
            ? (value / length)
            : Vector4.Zero
        );
    }
    private static Vector4 Map(Vector4 value, Func<float, float> map) => new(
        x: map(arg: value.X),
        y: map(arg: value.Y),
        z: map(arg: value.Z),
        w: map(arg: value.W)
    );
    private static Vector4 Zip(Vector4 left, Vector4 right, Func<float, float, float> zip) => new(
        x: zip(arg1: left.X, arg2: right.X),
        y: zip(arg1: left.Y, arg2: right.Y),
        z: zip(arg1: left.Z, arg2: right.Z),
        w: zip(arg1: left.W, arg2: right.W)
    );
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static float Lane(Vector4 value, int lane) => (lane switch {
        0 => value.X,
        1 => value.Y,
        2 => value.Z,
        _ => value.W,
    });
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static float Mix(float a, float b, float t) => (a + ((b - a) * t));
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static float Quintic(float value) => ((value * value * value) * ((value * ((value * 6f) - 15f)) + 10f));
    private static float SmoothStep(float edge0, float edge1, float value) {
        var t = Math.Clamp(value: ((value - edge0) / (edge1 - edge0)), min: 0f, max: 1f);

        return ((t * t) * (3f - (2f * t)));
    }
}
