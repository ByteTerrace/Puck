namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The object parameter groups a turning sprite reads its matrix from. Only the advanced machine turns objects; the
/// humble machine can mirror one and nothing more.
/// </summary>
/// <remarks>
/// <para>
/// Layout: the thirty-two groups are interleaved with the object entries, one halfword every eight bytes starting at
/// 0x07000006, so group <c>n</c>'s four entries sit at 0x07000006 + n * 32 and every eighth byte after it. Writing a
/// group therefore skips over three object entries between each of its own halfwords.
/// </para>
/// <para>
/// Units: the angle is a whole turn in 256 steps and the matrix entries are signed 8.8, the same convention the
/// rotating background uses. The matrix is unscaled, so a turned object keeps its size.
/// </para>
/// </remarks>
public sealed class AgbSpriteTurn {
    private const uint GroupBaseAddress = 0x07000006u;
    private const int GroupStride = 32;
    private const int EntryStride = 8;

    private readonly ThumbEmitter m_emitter;
    private readonly uint m_tableAddress;

    /// <summary>Creates the helper over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="tableAddress">The baked turn table's address in the cartridge image.</param>
    public AgbSpriteTurn(ThumbEmitter emitter, uint tableAddress) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_tableAddress = tableAddress;
    }

    /// <summary>Emits a parameter group's four halfwords for the given angle.</summary>
    /// <param name="group">The group index, 0 through 31.</param>
    /// <param name="angle">Loads the angle into the given register.</param>
    public void EmitParameters(int group, Action<LowRegister> angle) {
        ArgumentNullException.ThrowIfNull(argument: angle);

        // r6 = sine, r7 = cosine, both signed 8.8.
        angle(LowRegister.R0);
        EmitTurnLookup(destination: LowRegister.R6, quarterTurns: 0);
        angle(LowRegister.R0);
        EmitTurnLookup(destination: LowRegister.R7, quarterTurns: 64);

        var slot = GroupBaseAddress + (uint)(group * GroupStride);
        StoreFrom(register: LowRegister.R7, address: slot);
        m_emitter.MoveImmediate(destination: LowRegister.R0, value: 0);
        m_emitter.SubtractRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R6);
        StoreFrom(register: LowRegister.R0, address: slot + EntryStride);
        StoreFrom(register: LowRegister.R6, address: slot + (EntryStride * 2));
        StoreFrom(register: LowRegister.R7, address: slot + (EntryStride * 3));
    }

    // Reads the turn table a quarter turn along to get cosine from the same sine values.
    private void EmitTurnLookup(LowRegister destination, int quarterTurns) {
        if (quarterTurns != 0) {
            m_emitter.AddImmediate(register: LowRegister.R0, value: (byte)quarterTurns);
        }

        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 255);
        m_emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R0, source: LowRegister.R1);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 1);
        m_emitter.LoadConstant(destination: LowRegister.R1, value: m_tableAddress);
        m_emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1);
        m_emitter.MoveImmediate(destination: LowRegister.R2, value: 0);
        m_emitter.LoadSignedHalfRegister(destination: destination, baseRegister: LowRegister.R0, offsetRegister: LowRegister.R2);
    }

    private void StoreFrom(LowRegister register, uint address) {
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreHalf(source: register, baseRegister: LowRegister.R2, byteOffset: 0);
    }
}
