namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// Battery-backed state for the advanced machine, in the cartridge's static-RAM window. The stored block is a magic
/// pair, a version, a checksum and the payload; a block failing any of those checks restores the authored defaults
/// instead, so a fresh cartridge and a corrupted one behave alike.
/// </summary>
/// <remarks>
/// <para>
/// The save window is byte-wide: every access is a byte load or store, never a halfword or word. The wait-state
/// register is set for the slowest configuration at boot so a cartridge with slow silicon still reads correctly.
/// </para>
/// <para>
/// The image must also carry <see cref="SignatureText"/>, which is how a host recognizes the backup kind. The builder
/// places it; without it the window reads open-bus and nothing persists.
/// </para>
/// </remarks>
public sealed class AgbSaveModule {
    /// <summary>The identifier a host scans the image for to recognize the static-RAM backup.</summary>
    public const string SignatureText = "SRAM_V113";
    /// <summary>The first magic byte.</summary>
    private const byte MagicLow = 0x50;
    /// <summary>The second magic byte.</summary>
    private const byte MagicHigh = 0x46;
    /// <summary>Magic pair, version and checksum precede the payload.</summary>
    private const int HeaderByteCount = 4;
    private const uint SaveWindowAddress = 0x0E000000u;

    private readonly uint m_defaultsAddress;
    private readonly ThumbEmitter m_emitter;
    private readonly uint m_mirrorAddress;
    private readonly int m_payloadByteCount;
    private readonly byte m_version;

    /// <summary>Creates the module over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="mirrorAddress">The payload's mirror in work memory.</param>
    /// <param name="defaultsAddress">The authored defaults in the cartridge image.</param>
    /// <param name="payloadByteCount">The payload size in bytes.</param>
    /// <param name="version">The payload's layout version.</param>
    public AgbSaveModule(ThumbEmitter emitter, uint mirrorAddress, uint defaultsAddress, int payloadByteCount, byte version) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_defaultsAddress = defaultsAddress;
        m_emitter = emitter;
        m_mirrorAddress = mirrorAddress;
        m_payloadByteCount = payloadByteCount;
        m_version = version;
    }

    /// <summary>Emits a write of the mirror to the save window, with a fresh header and checksum.</summary>
    public void EmitStore() {
        var loop = m_emitter.NewLabel();
        m_emitter.LoadConstant(destination: LowRegister.R4, value: SaveWindowAddress);
        m_emitter.MoveImmediate(destination: LowRegister.R0, value: MagicLow);
        m_emitter.StoreByte(source: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.MoveImmediate(destination: LowRegister.R0, value: MagicHigh);
        m_emitter.StoreByte(source: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 1);
        m_emitter.MoveImmediate(destination: LowRegister.R0, value: m_version);
        m_emitter.StoreByte(source: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 2);

        // Copy the payload, accumulating the checksum as it goes.
        m_emitter.LoadConstant(destination: LowRegister.R5, value: m_mirrorAddress);
        m_emitter.LoadConstant(destination: LowRegister.R6, value: SaveWindowAddress + HeaderByteCount);
        m_emitter.MoveImmediate(destination: LowRegister.R7, value: 0);
        m_emitter.LoadConstant(destination: LowRegister.R3, value: (uint)m_payloadByteCount);
        m_emitter.MarkLabel(label: loop);
        m_emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R5, byteOffset: 0);
        m_emitter.StoreByte(source: LowRegister.R0, baseRegister: LowRegister.R6, byteOffset: 0);
        m_emitter.AddRegister(destination: LowRegister.R7, source: LowRegister.R7, operand: LowRegister.R0);
        m_emitter.AddImmediate(register: LowRegister.R5, value: 1);
        m_emitter.AddImmediate(register: LowRegister.R6, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R3, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: loop);

        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 255);
        m_emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R7, source: LowRegister.R1);
        m_emitter.StoreByte(source: LowRegister.R7, baseRegister: LowRegister.R4, byteOffset: 3);
    }

    /// <summary>Emits a read of the save window into the mirror, falling back to the authored defaults.</summary>
    public void EmitLoad() {
        var accept = m_emitter.NewLabel();
        var copy = m_emitter.NewLabel();
        var done = m_emitter.NewLabel();
        var restore = m_emitter.NewLabel();
        var sum = m_emitter.NewLabel();

        m_emitter.LoadConstant(destination: LowRegister.R4, value: SaveWindowAddress);
        m_emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.CompareImmediate(register: LowRegister.R0, value: MagicLow);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: restore);
        m_emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 1);
        m_emitter.CompareImmediate(register: LowRegister.R0, value: MagicHigh);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: restore);
        m_emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 2);
        m_emitter.CompareImmediate(register: LowRegister.R0, value: m_version);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: restore);

        // Sum the stored payload and compare against the recorded checksum.
        m_emitter.LoadConstant(destination: LowRegister.R5, value: SaveWindowAddress + HeaderByteCount);
        m_emitter.MoveImmediate(destination: LowRegister.R7, value: 0);
        m_emitter.LoadConstant(destination: LowRegister.R3, value: (uint)m_payloadByteCount);
        m_emitter.MarkLabel(label: sum);
        m_emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R5, byteOffset: 0);
        m_emitter.AddRegister(destination: LowRegister.R7, source: LowRegister.R7, operand: LowRegister.R0);
        m_emitter.AddImmediate(register: LowRegister.R5, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R3, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: sum);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 255);
        m_emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R7, source: LowRegister.R1);
        m_emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R4, byteOffset: 3);
        m_emitter.Alu(op: ThumbAlu.Compare, destination: LowRegister.R0, source: LowRegister.R7);
        m_emitter.Branch(condition: ThumbCondition.Equal, label: accept);

        m_emitter.MarkLabel(label: restore);
        m_emitter.LoadConstant(destination: LowRegister.R5, value: m_defaultsAddress);
        m_emitter.Branch(label: copy);
        m_emitter.MarkLabel(label: accept);
        m_emitter.LoadConstant(destination: LowRegister.R5, value: SaveWindowAddress + HeaderByteCount);

        m_emitter.MarkLabel(label: copy);
        m_emitter.LoadConstant(destination: LowRegister.R6, value: m_mirrorAddress);
        m_emitter.LoadConstant(destination: LowRegister.R3, value: (uint)m_payloadByteCount);
        var move = m_emitter.NewLabel();
        m_emitter.MarkLabel(label: move);
        m_emitter.LoadByte(destination: LowRegister.R0, baseRegister: LowRegister.R5, byteOffset: 0);
        m_emitter.StoreByte(source: LowRegister.R0, baseRegister: LowRegister.R6, byteOffset: 0);
        m_emitter.AddImmediate(register: LowRegister.R5, value: 1);
        m_emitter.AddImmediate(register: LowRegister.R6, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R3, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: move);
        m_emitter.MarkLabel(label: done);
    }
}
