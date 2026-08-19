namespace Puck.ShaderVm;

using System.Numerics;

/// <summary>Builds a Shader VM program in evaluation order.</summary>
public sealed class ShaderProgramBuilder {
    private readonly List<Vector4> m_constants = [];
    private readonly List<ShaderInstruction> m_instructions = [];
    private readonly Dictionary<Vector4, uint> m_pool = [];

    /// <summary>Gets the index the next appended instruction takes.</summary>
    public int Count => m_instructions.Count;

    /// <summary>Appends one generic operation.</summary>
    /// <param name="op">The operation to evaluate.</param>
    /// <param name="operand">The operation-specific operand.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The program already contains the maximum instruction count.</exception>
    public ShaderProgramBuilder Append(ShaderOp op, uint operand = 0u) {
        if (m_instructions.Count >= ShaderIsa.MaxInstructions) {
            throw new InvalidOperationException(message: $"A Shader VM program may contain at most {ShaderIsa.MaxInstructions} instructions.");
        }

        var instruction = new ShaderInstruction(Op: op, Operand: operand);

        _ = instruction.Pack();
        m_instructions.Add(item: instruction);

        return this;
    }
    /// <summary>Appends an operation that loads one execution-context input.</summary>
    /// <param name="input">The input to load.</param>
    /// <returns>This builder.</returns>
    public ShaderProgramBuilder LoadInput(ShaderInput input) => Append(op: ShaderOp.LoadInput, operand: ((uint)input));
    /// <summary>Appends an operation that loads one caller-supplied parameter.</summary>
    /// <param name="index">The parameter index.</param>
    /// <returns>This builder.</returns>
    public ShaderProgramBuilder LoadParameter(int index) => Append(op: ShaderOp.LoadParameter, operand: checked((uint)index));
    /// <summary>Appends an operation that loads one local register.</summary>
    /// <param name="index">The register index.</param>
    /// <returns>This builder.</returns>
    public ShaderProgramBuilder LoadLocal(int index) => Append(op: ShaderOp.LoadLocal, operand: checked((uint)index));
    /// <summary>Appends an operation that pops the top value into one local register.</summary>
    /// <param name="index">The register index.</param>
    /// <returns>This builder.</returns>
    public ShaderProgramBuilder StoreLocal(int index) => Append(op: ShaderOp.StoreLocal, operand: checked((uint)index));
    /// <summary>Appends an operation that rearranges the top value's lanes.</summary>
    /// <param name="x">The source lane of the result's first lane.</param>
    /// <param name="y">The source lane of the result's second lane.</param>
    /// <param name="z">The source lane of the result's third lane.</param>
    /// <param name="w">The source lane of the result's fourth lane.</param>
    /// <returns>This builder.</returns>
    public ShaderProgramBuilder Swizzle(int x, int y, int z, int w) => Append(
        op: ShaderOp.Swizzle,
        operand: ShaderIsa.PackSwizzle(x: x, y: y, z: z, w: w)
    );
    /// <summary>Interns and loads one four-lane program constant.</summary>
    /// <param name="value">The constant value.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The program already contains the maximum constant count.</exception>
    public ShaderProgramBuilder LoadConstant(Vector4 value) => Append(op: ShaderOp.LoadConstant, operand: Intern(value: value));
    /// <summary>Interns and loads one constant replicated to every lane.</summary>
    /// <param name="value">The scalar value.</param>
    /// <returns>This builder.</returns>
    public ShaderProgramBuilder LoadConstant(float value) => LoadConstant(value: new Vector4(value: value));
    /// <summary>Interns one four-lane constant without loading it.</summary>
    /// <param name="value">The constant value.</param>
    /// <returns>The constant's pool index.</returns>
    /// <exception cref="InvalidOperationException">The program already contains the maximum constant count.</exception>
    public uint Intern(Vector4 value) {
        if (m_pool.TryGetValue(key: value, value: out var existing)) {
            return existing;
        }
        if (m_constants.Count >= ShaderIsa.MaxConstants) {
            throw new InvalidOperationException(message: $"A Shader VM program may contain at most {ShaderIsa.MaxConstants} constants.");
        }

        var index = ((uint)m_constants.Count);

        m_constants.Add(item: value);
        m_pool.Add(key: value, value: index);

        return index;
    }
    /// <summary>Appends a jump whose target is patched once the destination is known.</summary>
    /// <param name="op">The jump operation.</param>
    /// <returns>The index of the appended jump, for <see cref="PatchJump"/>.</returns>
    public int AppendJump(ShaderOp op) {
        var index = m_instructions.Count;

        _ = Append(op: op, operand: 1u);

        return index;
    }
    /// <summary>Points an appended jump at the instruction the builder will append next.</summary>
    /// <param name="jumpIndex">The index <see cref="AppendJump"/> returned.</param>
    public void PatchJump(int jumpIndex) => m_instructions[jumpIndex] = m_instructions[jumpIndex] with {
        Operand = checked((uint)m_instructions.Count),
    };
    /// <summary>Builds an immutable program, appending the terminating halt.</summary>
    /// <returns>The packed program.</returns>
    public ShaderProgram Build() {
        if ((m_instructions.Count == 0) || (m_instructions[^1].Op != ShaderOp.Halt)) {
            _ = Append(op: ShaderOp.Halt);
        }

        return new ShaderProgram(instructions: m_instructions, constants: m_constants);
    }
}
