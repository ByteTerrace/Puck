namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// The advanced machine's real-time clock, reached over the cartridge's four general-purpose pins rather than through
/// a memory-mapped register as the Color machine's is.
/// </summary>
/// <remarks>
/// <para>
/// Pins, in the data register's low four bits: bit 0 is the clock line, bit 1 the data line, bit 2 the chip select.
/// The direction register marks which of them the cartridge drives, so reading the reply means handing bit 1 back to
/// the device. The control register's low bit must be set or the data register reads as nothing.
/// </para>
/// <para>
/// Framing: a command byte goes out least significant bit first, each bit presented while the clock line is low and
/// taken on its rising edge. The reply comes back on falling edges. A command byte carries 0110 in its low nibble,
/// the command in bits 4 through 6, and the read flag in bit 7.
/// </para>
/// <para>
/// The device answers in binary-coded decimal — each nibble one decimal digit — while the authored document counts in
/// plain binary, as the Color machine's clock does. The conversion is part of the read for that reason.
/// </para>
/// </remarks>
public sealed class AgbRealTimeClock {
    /// <summary>The identifier the host scans the image for to decide the cartridge carries a clock.</summary>
    public const string SignatureText = "SIIRTC_V";
    /// <summary>Bytes the reply occupies: year, month, day, weekday, hour, minute, second.</summary>
    public const int ReplyByteCount = 7;

    private const uint ControlAddress = 0x080000C8u;
    private const uint DataAddress = 0x080000C4u;
    private const uint DirectionAddress = 0x080000C6u;
    // 0110 magic, command 2 (date and time) in bits 4-6, read in bit 7.
    private const uint ReadDateTimeCommand = 0xA6u;

    private readonly ThumbEmitter m_emitter;
    private readonly uint m_replyAddress;

    /// <summary>Creates the driver over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="replyAddress">Where the seven reply bytes are left, already converted to binary.</param>
    public AgbRealTimeClock(ThumbEmitter emitter, uint replyAddress) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_replyAddress = replyAddress;
    }

    /// <summary>Gets the reply byte holding the day of the month.</summary>
    public uint DayAddress => m_replyAddress + 2;
    /// <summary>Gets the reply byte holding the hour.</summary>
    public uint HourAddress => m_replyAddress + 4;
    /// <summary>Gets the reply byte holding the minute.</summary>
    public uint MinuteAddress => m_replyAddress + 5;
    /// <summary>Gets the reply byte holding the month.</summary>
    public uint MonthAddress => m_replyAddress + 1;
    /// <summary>Gets the reply byte holding the second.</summary>
    public uint SecondAddress => m_replyAddress + 6;
    /// <summary>Gets the reply byte holding the year within its century.</summary>
    public uint YearAddress => m_replyAddress;

    /// <summary>Emits the power-up: let the data register be read, and take the three driven pins.</summary>
    public void EmitBoot() {
        Write(address: ControlAddress, value: 1);
        Write(address: DirectionAddress, value: 0x7);
    }

    /// <summary>Emits one whole read, leaving the seven reply bytes in binary at the reply address.</summary>
    /// <remarks>
    /// The bit work is a runtime loop rather than unrolled, because fifty-six unrolled pin exchanges put their
    /// constants further from the loads than a program-counter-relative load can reach.
    /// </remarks>
    public void EmitRead() {
        var bits = m_emitter.NewLabel();
        var command = m_emitter.NewLabel();
        var reply = m_emitter.NewLabel();

        // Dropping the select line abandons any half-finished exchange, which is the only way back to a known state.
        Write(address: DirectionAddress, value: 0x7);
        m_emitter.LoadConstant(destination: LowRegister.R3, value: DataAddress);
        Pins(value: 0x0);
        Pins(value: 0x1);
        Pins(value: 0x5);

        // The command goes out least significant bit first, presented low and taken on the rising edge.
        m_emitter.LoadConstant(destination: LowRegister.R2, value: ReadDateTimeCommand);
        m_emitter.MoveImmediate(destination: LowRegister.R5, value: 8);
        m_emitter.MarkLabel(label: command);
        m_emitter.MoveRegister(destination: LowRegister.R0, source: LowRegister.R2);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 1);
        m_emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R0, source: LowRegister.R1);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 1);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 4);
        m_emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R1, source: LowRegister.R0);
        m_emitter.StoreHalf(source: LowRegister.R1, baseRegister: LowRegister.R3, byteOffset: 0);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 5);
        m_emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R1, source: LowRegister.R0);
        m_emitter.StoreHalf(source: LowRegister.R1, baseRegister: LowRegister.R3, byteOffset: 0);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalRight, destination: LowRegister.R2, source: LowRegister.R2, amount: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R5, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: command);

        // Hand the data line back so the device can drive its reply onto it.
        Write(address: DirectionAddress, value: 0x5);
        m_emitter.LoadConstant(destination: LowRegister.R3, value: DataAddress);
        m_emitter.LoadConstant(destination: LowRegister.R7, value: m_replyAddress);
        m_emitter.MoveImmediate(destination: LowRegister.R4, value: ReplyByteCount);

        m_emitter.MarkLabel(label: reply);
        m_emitter.MoveImmediate(destination: LowRegister.R6, value: 0);
        m_emitter.MoveImmediate(destination: LowRegister.R5, value: 8);
        m_emitter.MarkLabel(label: bits);
        Pins(value: 0x4);
        m_emitter.LoadHalf(destination: LowRegister.R0, baseRegister: LowRegister.R3, byteOffset: 0);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalRight, destination: LowRegister.R0, source: LowRegister.R0, amount: 1);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 1);
        m_emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R0, source: LowRegister.R1);
        // A shift register filled from the top: after eight turns the first bit received sits in the low place.
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 7);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalRight, destination: LowRegister.R6, source: LowRegister.R6, amount: 1);
        m_emitter.Alu(op: ThumbAlu.Or, destination: LowRegister.R6, source: LowRegister.R0);
        Pins(value: 0x5);
        m_emitter.SubtractImmediate(register: LowRegister.R5, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: bits);

        // Decimal-coded nibbles to binary: the high nibble is tens, taken as eight of it plus two more.
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalRight, destination: LowRegister.R0, source: LowRegister.R6, amount: 4);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R1, source: LowRegister.R0, amount: 3);
        m_emitter.ShiftImmediate(op: ThumbShift.LogicalLeft, destination: LowRegister.R0, source: LowRegister.R0, amount: 1);
        m_emitter.AddRegister(destination: LowRegister.R0, source: LowRegister.R0, operand: LowRegister.R1);
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: 15);
        m_emitter.Alu(op: ThumbAlu.And, destination: LowRegister.R6, source: LowRegister.R1);
        m_emitter.AddRegister(destination: LowRegister.R6, source: LowRegister.R6, operand: LowRegister.R0);
        m_emitter.StoreByte(source: LowRegister.R6, baseRegister: LowRegister.R7, byteOffset: 0);
        m_emitter.AddImmediate(register: LowRegister.R7, value: 1);
        m_emitter.SubtractImmediate(register: LowRegister.R4, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: reply);

        Pins(value: 0x0);
        Write(address: DirectionAddress, value: 0x7);
    }

    // Drives the pins, with the data register's address already in r3.
    private void Pins(byte value) {
        m_emitter.MoveImmediate(destination: LowRegister.R1, value: value);
        m_emitter.StoreHalf(source: LowRegister.R1, baseRegister: LowRegister.R3, byteOffset: 0);
    }

    private void Write(uint address, uint value) {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: value);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreHalf(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
    }
}
