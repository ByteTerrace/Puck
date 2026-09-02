namespace Puck.ShaderVm;

using System.Numerics;

/// <summary>An immutable packed Shader VM program.</summary>
public sealed class ShaderProgram {
    private readonly uint[] m_words;

    internal ShaderProgram(IReadOnlyList<ShaderInstruction> instructions, IReadOnlyList<Vector4> constants) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: instructions.Count, other: ShaderIsa.MaxInstructions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: constants.Count, other: ShaderIsa.MaxConstants);

        ValidateInstructions(instructions: instructions, constantCount: constants.Count);

        m_words = new uint[((ShaderIsa.HeaderWordCount + instructions.Count) + (constants.Count * 4))];
        m_words[0] = ShaderIsa.Magic;
        m_words[1] = ShaderIsa.Version;
        m_words[2] = ((uint)instructions.Count);
        m_words[3] = ((uint)constants.Count);

        for (var index = 0; (index < instructions.Count); index++) {
            m_words[(ShaderIsa.HeaderWordCount + index)] = instructions[index].Pack();
        }

        var constantBase = (ShaderIsa.HeaderWordCount + instructions.Count);

        for (var index = 0; (index < constants.Count); index++) {
            var value = constants[index];
            var wordBase = (constantBase + (index * 4));

            m_words[(wordBase + 0)] = BitConverter.SingleToUInt32Bits(value: value.X);
            m_words[(wordBase + 1)] = BitConverter.SingleToUInt32Bits(value: value.Y);
            m_words[(wordBase + 2)] = BitConverter.SingleToUInt32Bits(value: value.Z);
            m_words[(wordBase + 3)] = BitConverter.SingleToUInt32Bits(value: value.W);
        }
    }

    private ShaderProgram(uint[] words) => m_words = words;

    /// <summary>Gets the number of decoded instructions.</summary>
    public int InstructionCount => checked((int)m_words[2]);
    /// <summary>Gets the number of four-lane constants.</summary>
    public int ConstantCount => checked((int)m_words[3]);
    /// <summary>Gets the packed header, instruction, and constant-pool words.</summary>
    public ReadOnlyMemory<uint> Words => m_words;

    /// <summary>Reads one decoded instruction.</summary>
    /// <param name="index">The instruction index.</param>
    /// <returns>The decoded instruction.</returns>
    public ShaderInstruction Instruction(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: InstructionCount);

        return ShaderInstruction.Unpack(word: m_words[(ShaderIsa.HeaderWordCount + index)]);
    }
    /// <summary>Reads one four-lane constant.</summary>
    /// <param name="index">The constant index.</param>
    /// <returns>The constant value.</returns>
    public Vector4 Constant(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: ConstantCount);

        var wordBase = ((ShaderIsa.HeaderWordCount + InstructionCount) + (index * 4));

        return new Vector4(
            x: BitConverter.UInt32BitsToSingle(value: m_words[(wordBase + 0)]),
            y: BitConverter.UInt32BitsToSingle(value: m_words[(wordBase + 1)]),
            z: BitConverter.UInt32BitsToSingle(value: m_words[(wordBase + 2)]),
            w: BitConverter.UInt32BitsToSingle(value: m_words[(wordBase + 3)])
        );
    }
    /// <summary>Validates and copies a packed program received from another host boundary.</summary>
    /// <param name="words">The packed header, instructions, and constant-pool words.</param>
    /// <returns>An immutable validated program.</returns>
    /// <exception cref="ArgumentException">The header, length, instruction stream, or constant pool is invalid.</exception>
    public static ShaderProgram FromWords(ReadOnlySpan<uint> words) {
        if (words.Length < ShaderIsa.HeaderWordCount) {
            throw new ArgumentException(message: $"A Shader VM program needs at least {ShaderIsa.HeaderWordCount} header words.", paramName: nameof(words));
        }
        if (words[0] != ShaderIsa.Magic) {
            throw new ArgumentException(message: $"Shader VM magic 0x{words[0]:X8} does not match 0x{ShaderIsa.Magic:X8}.", paramName: nameof(words));
        }
        if (words[1] != ShaderIsa.Version) {
            throw new ArgumentException(message: $"Shader VM ISA version {words[1]} does not match host version {ShaderIsa.Version}.", paramName: nameof(words));
        }

        var instructionCount = checked((int)words[2]);
        var constantCount = checked((int)words[3]);

        if (instructionCount > ShaderIsa.MaxInstructions) {
            throw new ArgumentException(message: $"The packed program declares {instructionCount} instructions; the maximum is {ShaderIsa.MaxInstructions}.", paramName: nameof(words));
        }
        if (constantCount > ShaderIsa.MaxConstants) {
            throw new ArgumentException(message: $"The packed program declares {constantCount} constants; the maximum is {ShaderIsa.MaxConstants}.", paramName: nameof(words));
        }

        var expectedWordCount = ((ShaderIsa.HeaderWordCount + instructionCount) + (constantCount * 4));

        if (words.Length != expectedWordCount) {
            throw new ArgumentException(message: $"The packed program declares {instructionCount} instructions and {constantCount} constants, requiring {expectedWordCount} words; it carries {words.Length}.", paramName: nameof(words));
        }

        var instructions = new ShaderInstruction[instructionCount];

        for (var index = 0; (index < instructionCount); index++) {
            instructions[index] = ShaderInstruction.Unpack(word: words[(ShaderIsa.HeaderWordCount + index)]);
        }

        ValidateInstructions(constantCount: constantCount, instructions: instructions);

        return new ShaderProgram(words: words.ToArray());
    }

    // Jumps are forward-only, so one linear pass is a complete dataflow: an instruction's stack depth arrives either
    // by fallthrough or from the jumps targeting it, and the two must agree. No backward edge means no loop, which
    // bounds the GPU interpreter's work by the instruction count alone.
    private static void ValidateInstructions(IReadOnlyList<ShaderInstruction> instructions, int constantCount) {
        if (instructions.Count == 0) {
            throw new ArgumentException(message: "A Shader VM program must contain at least one instruction.", paramName: nameof(instructions));
        }

        var arrivals = new Dictionary<int, int>();
        var depth = 0;
        var halted = false;
        var reachable = true;

        for (var index = 0; (index < instructions.Count); index++) {
            if (arrivals.Remove(key: index, value: out var arrivalDepth)) {
                if (reachable && (depth != arrivalDepth)) {
                    throw new ArgumentException(message: $"Shader instruction {index} is reached with stack depth {depth} by fallthrough and {arrivalDepth} by jump.", paramName: nameof(instructions));
                }

                depth = arrivalDepth;
                reachable = true;
            }
            if (!reachable) {
                throw new ArgumentException(message: $"Shader instruction {index} is unreachable.", paramName: nameof(instructions));
            }

            var instruction = instructions[index];

            var (consumed, produced) = StackEffect(op: instruction.Op);

            _ = instruction.Pack();

            if ((instruction.Op == ShaderOp.LoadConstant) && (instruction.Operand >= constantCount)) {
                throw new ArgumentException(message: $"Shader instruction {index} loads constant {instruction.Operand}, but the pool contains {constantCount} values.", paramName: nameof(instructions));
            }
            if ((instruction.Op == ShaderOp.Pick) && (depth <= instruction.Operand)) {
                throw new ArgumentException(message: $"Shader instruction {index} picks {instruction.Operand} values back through a stack {depth} deep.", paramName: nameof(instructions));
            }
            if (depth < consumed) {
                throw new ArgumentException(message: $"Shader instruction {index} ({instruction.Op}) underflows the value stack.", paramName: nameof(instructions));
            }

            depth = ((depth - consumed) + produced);

            if (depth > ShaderIsa.MaxStackDepth) {
                throw new ArgumentException(message: $"Shader instruction {index} grows the value stack beyond {ShaderIsa.MaxStackDepth} values.", paramName: nameof(instructions));
            }

            switch (instruction.Op) {
                case ShaderOp.Jump or ShaderOp.JumpIfZero: {
                        var target = checked((int)instruction.Operand);

                        if (target <= index) {
                            throw new ArgumentException(message: $"Shader instruction {index} jumps backward to {target}; the Shader VM admits forward jumps only.", paramName: nameof(instructions));
                        }
                        if (target >= instructions.Count) {
                            throw new ArgumentException(message: $"Shader instruction {index} jumps to {target}, past the {instructions.Count}-instruction stream.", paramName: nameof(instructions));
                        }
                        if (arrivals.TryGetValue(key: target, out var existing) && (existing != depth)) {
                            throw new ArgumentException(message: $"Shader instruction {target} is reached with stack depth {existing} and {depth} by different jumps.", paramName: nameof(instructions));
                        }

                        arrivals[target] = depth;
                        reachable = (instruction.Op == ShaderOp.JumpIfZero);

                        break;
                    }
                case ShaderOp.Halt: {
                        if (depth != 0) {
                            throw new ArgumentException(message: $"Shader instruction {index} halts with {depth} values left beneath its result.", paramName: nameof(instructions));
                        }

                        halted = true;
                        reachable = false;

                        break;
                    }
                default:
                    break;
            }
        }

        if (reachable) {
            throw new ArgumentException(message: "A Shader VM program must end with Halt consuming its single result.", paramName: nameof(instructions));
        }
        if (!halted) {
            throw new ArgumentException(message: "A Shader VM program contains no reachable Halt.", paramName: nameof(instructions));
        }
        if (arrivals.Count != 0) {
            throw new ArgumentException(message: $"Shader jump target {arrivals.Keys.Min()} lies past the instruction stream.", paramName: nameof(instructions));
        }
    }
    private static (int Consumed, int Produced) StackEffect(ShaderOp op) => op switch {
        ShaderOp.LoadInput or ShaderOp.LoadParameter or ShaderOp.LoadConstant or ShaderOp.LoadLocal or ShaderOp.Pick => (0, 1),
        ShaderOp.StoreLocal or ShaderOp.Drop or ShaderOp.JumpIfZero or ShaderOp.Halt => (1, 0),
        ShaderOp.Duplicate => (1, 2),
        ShaderOp.Swap => (2, 2),
        ShaderOp.Jump => (0, 0),
        ShaderOp.LoadConstantDynamic or ShaderOp.Swizzle => (1, 1),
        >= ShaderOp.ValueNoise2 and <= ShaderOp.Fbm2 => (1, 1),
        >= ShaderOp.Absolute and <= ShaderOp.IntegerBits => (1, 1),
        >= ShaderOp.Add and <= ShaderOp.ArcTangent2 => (2, 1),
        >= ShaderOp.Lerp and <= ShaderOp.Select => (3, 1),
        _ => throw new ArgumentException(message: $"Shader operation {op} has no declared stack effect.", paramName: nameof(op)),
    };
}
