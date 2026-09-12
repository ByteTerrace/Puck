namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>Emits a lookup from the shared signed 8.8 sine table.</summary>
internal static class AgbTurnLookup {
    /// <summary>Emits the table lookup used for sine and quarter-turn cosine.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="tableAddress">The baked sine table's address in the cartridge image.</param>
    /// <param name="destination">The register receiving the signed table value.</param>
    /// <param name="quarterTurns">The byte-space turn offset to add before lookup.</param>
    public static void Emit(ThumbEmitter emitter, uint tableAddress, LowRegister destination, int quarterTurns) {
        if (quarterTurns != 0) {
            emitter.AddImmediate(register: LowRegister.R0, value: (byte)quarterTurns);
        }

        emitter.MoveImmediate(destination: LowRegister.R1, value: 255);
        emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R0, source: LowRegister.R1);
        emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 1);
        emitter.LoadConstant(destination: LowRegister.R1, value: tableAddress);
        emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1);
        emitter.MoveImmediate(destination: LowRegister.R2, value: 0);
        emitter.LoadSignedHalfRegister(destination: destination, baseRegister: LowRegister.R0, offsetRegister: LowRegister.R2);
    }
}
