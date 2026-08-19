namespace Puck.ShaderVm;

/// <summary>What one program actually demands of an interpreter, measured over its instruction stream.</summary>
/// <param name="InstructionCount">The number of instructions.</param>
/// <param name="ConstantCount">The number of four-lane constants.</param>
/// <param name="StackDepth">The deepest the value stack ever grows.</param>
/// <param name="LocalCount">One past the highest local register the program addresses.</param>
/// <param name="LongestPath">The most instructions any single evaluation can retire.</param>
/// <param name="Branches">The number of jump instructions.</param>
public readonly record struct ShaderProgramStatistics(int InstructionCount, int ConstantCount, int StackDepth, int LocalCount, int LongestPath, int Branches) {
    /// <summary>Measures what a program demands.</summary>
    /// <param name="program">The packed program.</param>
    /// <returns>The measured demand.</returns>
    public static ShaderProgramStatistics Measure(ShaderProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var arrivals = new Dictionary<int, int>();
        var branches = 0;
        var depth = 0;
        var locals = 0;
        var longest = 0;
        var peak = 0;

        for (var index = 0; (index < program.InstructionCount); index++) {
            if (arrivals.Remove(key: index, value: out var arrival)) {
                depth = arrival;
            }

            var instruction = program.Instruction(index: index);

            switch (instruction.Op) {
                case ShaderOp.LoadLocal or ShaderOp.StoreLocal:
                    locals = Math.Max(val1: locals, val2: (checked((int)instruction.Operand) + 1));

                    break;
                case ShaderOp.Jump or ShaderOp.JumpIfZero:
                    branches++;
                    arrivals[checked((int)instruction.Operand)] = depth;

                    break;
                default:
                    break;
            }

            depth += Delta(op: instruction.Op);
            longest++;
            peak = Math.Max(val1: peak, val2: depth);
        }

        return new ShaderProgramStatistics(
            Branches: branches,
            ConstantCount: program.ConstantCount,
            InstructionCount: program.InstructionCount,
            LocalCount: locals,
            LongestPath: longest,
            StackDepth: peak
        );
    }
    private static int Delta(ShaderOp op) => op switch {
        ShaderOp.LoadInput or ShaderOp.LoadParameter or ShaderOp.LoadConstant or ShaderOp.LoadLocal or ShaderOp.Pick or ShaderOp.Duplicate => 1,
        ShaderOp.StoreLocal or ShaderOp.Drop or ShaderOp.JumpIfZero or ShaderOp.Halt => -1,
        >= ShaderOp.Add and <= ShaderOp.Greater => -1,
        >= ShaderOp.Lerp and <= ShaderOp.Select => -2,
        _ => 0,
    };
}
