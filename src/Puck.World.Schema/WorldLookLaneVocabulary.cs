namespace Puck.World;

/// <summary>The instructions a render lane expression may carry: constants, state reads, and the arithmetic, minimum,
/// maximum, clamp, sign and absolute operators the client's lane evaluator runs. The validator refuses any other
/// instruction by name, and the evaluator dispatches on this same set.</summary>
public static class WorldLookLaneVocabulary {
    /// <summary>Returns whether a lane expression may carry an instruction.</summary>
    /// <param name="instruction">The instruction.</param>
    /// <returns><see langword="true"/> for a constant, a state read, or a supported operator.</returns>
    public static bool Supports(Instruction instruction) {
        ArgumentNullException.ThrowIfNull(argument: instruction);

        return (instruction.Payload is InstructionPayload.Constant or InstructionPayload.State) ||
            (instruction.Operation is ExpressionOp.Negate or ExpressionOp.Absolute or ExpressionOp.Sign or ExpressionOp.Add or
                ExpressionOp.Subtract or ExpressionOp.Multiply or ExpressionOp.Divide or ExpressionOp.Minimum or
                ExpressionOp.Maximum or ExpressionOp.Clamp);
    }
}
