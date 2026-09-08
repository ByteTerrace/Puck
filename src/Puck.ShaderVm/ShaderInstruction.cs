namespace Puck.ShaderVm;

/// <summary>One stack-machine operation and its unsigned 24-bit operand.</summary>
/// <param name="Op">The generic value operation.</param>
/// <param name="Operand">The operation-specific operand.</param>
public readonly record struct ShaderInstruction(ShaderOp Op, uint Operand = 0u) {
    /// <summary>The largest operand representable by one packed instruction.</summary>
    public const uint MaxOperand = 0x00FF_FFFFu;

    /// <summary>Gets a value indicating whether the operation reads its operand at all.</summary>
    public static bool TakesOperand(ShaderOp op) => (op is
        ShaderOp.LoadInput or ShaderOp.LoadParameter or ShaderOp.LoadConstant or ShaderOp.LoadLocal or
        ShaderOp.StoreLocal or ShaderOp.Swizzle or ShaderOp.Pick or ShaderOp.Jump or ShaderOp.JumpIfZero or
        ShaderOp.Fbm2);
    /// <summary>Packs the operation into one word: opcode in bits 0–7 and operand in bits 8–31.</summary>
    /// <returns>The packed instruction word.</returns>
    public uint Pack() {
        ValidateEnum(value: Op, paramName: nameof(Op));
        if (Operand > MaxOperand) {
            throw new ArgumentOutOfRangeException(paramName: nameof(Operand), actualValue: Operand, message: $"A Shader VM operand may not exceed 0x{MaxOperand:X6}.");
        }
        ValidateOperand(op: Op, operand: Operand, paramName: nameof(Operand));

        return ((uint)Op) | (Operand << 8);
    }
    /// <summary>Decodes and validates one packed instruction word.</summary>
    /// <param name="word">The packed word.</param>
    /// <returns>The decoded instruction.</returns>
    /// <exception cref="ArgumentException">The word uses reserved bits or names an undeclared operation.</exception>
    public static ShaderInstruction Unpack(uint word) {
        var op = ((ShaderOp)(word & 0xFFu));
        var operand = (word >> 8);

        ValidateEnum(value: op, paramName: nameof(word));
        ValidateOperand(op: op, operand: operand, paramName: nameof(word));

        return new ShaderInstruction(Op: op, Operand: operand);
    }

    private static void ValidateOperand(ShaderOp op, uint operand, string paramName) {
        if (!TakesOperand(op: op)) {
            if (operand != 0u) {
                throw new ArgumentException(message: $"Shader operation {op} does not accept an operand.", paramName: paramName);
            }

            return;
        }
        switch (op) {
            case ShaderOp.LoadInput when !Enum.IsDefined(value: ((ShaderInput)operand)):
                throw new ArgumentException(message: $"Shader instruction names undeclared {nameof(ShaderInput)} value {operand}.", paramName: paramName);
            case ShaderOp.Swizzle when (operand > 0xFFu):
                throw new ArgumentException(message: $"Shader swizzle operand 0x{operand:X} uses bits outside the four two-bit lane selectors.", paramName: paramName);
            case ShaderOp.LoadLocal or ShaderOp.StoreLocal when (operand >= ShaderIsa.MaxLocals):
                throw new ArgumentException(message: $"Shader instruction addresses local {operand}; the register file holds {ShaderIsa.MaxLocals}.", paramName: paramName);
            case ShaderOp.Pick when (operand >= ShaderIsa.MaxStackDepth):
                throw new ArgumentException(message: $"Shader instruction picks {operand} values back; the stack holds {ShaderIsa.MaxStackDepth}.", paramName: paramName);
            case ShaderOp.Fbm2 when ((operand == 0u) || (operand > ShaderIsa.MaxOctaves)):
                throw new ArgumentException(message: $"Shader fbm asks for {operand} octaves; the interpreter evaluates one through {ShaderIsa.MaxOctaves}.", paramName: paramName);
            case ShaderOp.Jump or ShaderOp.JumpIfZero when (operand > ShaderIsa.MaxInstructions):
                throw new ArgumentException(message: $"Shader instruction jumps to {operand}; the stream holds at most {ShaderIsa.MaxInstructions}.", paramName: paramName);
            default:
                break;
        }
    }
    private static void ValidateEnum<T>(T value, string paramName) where T : struct, Enum {
        if (!Enum.IsDefined(value: value)) {
            throw new ArgumentException(message: $"Shader instruction names undeclared {typeof(T).Name} value {Convert.ToUInt64(value: value)}.", paramName: paramName);
        }
    }
}
