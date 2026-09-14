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
    private const int EntryStride = 8;
    private const uint GroupBaseAddress = 0x07000006u;
    private const int GroupStride = 32;

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

    private void StoreFrom(LowRegister register, uint address) {
        m_emitter.LoadConstant(
            destination: LowRegister.R2,
            value: address
        );
        m_emitter.StoreHalf(
            baseRegister: LowRegister.R2,
            byteOffset: 0,
            source: register
        );
    }

    /// <summary>Emits a parameter group's four halfwords for the given angle.</summary>
    /// <param name="group">The group index, 0 through 31.</param>
    /// <param name="angle">Loads the angle into the given register.</param>
    public void EmitParameters(int group, Action<LowRegister> angle) {
        ArgumentNullException.ThrowIfNull(argument: angle);

        // r6 = sine, r7 = cosine, both signed 8.8.
        angle(LowRegister.R0);
        AgbTurnLookup.Emit(
            destination: LowRegister.R6,
            emitter: m_emitter,
            quarterTurns: 0,
            tableAddress: m_tableAddress
        );
        angle(LowRegister.R0);
        AgbTurnLookup.Emit(
            destination: LowRegister.R7,
            emitter: m_emitter,
            quarterTurns: 64,
            tableAddress: m_tableAddress
        );

        var slot = (GroupBaseAddress + ((uint)(group * GroupStride)));

        StoreFrom(
            address: slot,
            register: LowRegister.R7
        );
        m_emitter.MoveImmediate(
            destination: LowRegister.R0,
            value: 0
        );
        m_emitter.SubtractRegister(
            destination: LowRegister.R0,
            operand: LowRegister.R6,
            source: LowRegister.R0
        );
        StoreFrom(
            address: (slot + EntryStride),
            register: LowRegister.R0
        );
        StoreFrom(
            address: (slot + (EntryStride * 2)),
            register: LowRegister.R6
        );
        StoreFrom(
            address: (slot + (EntryStride * 3)),
            register: LowRegister.R7
        );
    }
}
