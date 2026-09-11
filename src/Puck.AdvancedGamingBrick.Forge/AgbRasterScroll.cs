namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// Per-scanline scroll on the advanced machine, fed by a transfer that runs in each horizontal blank rather than by an
/// interrupt. The image direct-boots with no interrupt handler, so a transfer is the only way to change a scroll
/// register part way down the picture.
/// </summary>
/// <remarks>
/// <para>
/// Layout: one pair of halfwords per scanline — horizontal then vertical — because the two scroll registers are
/// adjacent, so a two-unit burst lands them both. The table holds <see cref="ScanlineCount"/> pairs and must sit in
/// internal memory, because the channel used here cannot read the cartridge image.
/// </para>
/// <para>
/// Phase: the burst for entry <c>i</c> runs in the horizontal blank of line <c>i</c>, so it governs line
/// <c>i + 1</c>. Line zero therefore comes from whatever the registers already hold, and a band starting at line
/// <c>L</c> is written from entry <c>L - 1</c>. <see cref="EmitFillFrom"/> applies that shift for the caller.
/// </para>
/// <para>
/// The channel advances its source across the picture while its destination reloads every burst. Repeat mode does not
/// rewind the source, so the caller re-arms once per frame.
/// </para>
/// </remarks>
public sealed class AgbRasterScroll {
    /// <summary>Scanlines the table covers; the picture is this tall.</summary>
    public const int ScanlineCount = 160;
    /// <summary>Bytes the table occupies: a horizontal and a vertical halfword per scanline.</summary>
    public const int TableByteCount = ScanlineCount * 4;

    private const uint ControlAddress = 0x040000BAu;
    private const uint DestinationAddress = 0x040000B4u;
    private const uint ScrollAddress = 0x04000010u;
    private const uint SourceAddress = 0x040000B0u;
    private const uint UnitCountAddress = 0x040000B8u;

    private readonly ThumbEmitter m_emitter;
    private readonly uint m_tableAddress;

    /// <summary>Creates the helper over the shared emitter.</summary>
    /// <param name="emitter">The routine emitter.</param>
    /// <param name="tableAddress">The scroll table's base in work memory.</param>
    public AgbRasterScroll(ThumbEmitter emitter, uint tableAddress) {
        ArgumentNullException.ThrowIfNull(argument: emitter);

        m_emitter = emitter;
        m_tableAddress = tableAddress;
    }

    /// <summary>Emits the per-frame re-arm, which rewinds the channel to the table's start.</summary>
    /// <remarks>Clearing the control register before setting it again is what makes the hardware latch the rewound
    /// source; writing the source alone while the channel is enabled changes nothing.</remarks>
    public void EmitRearm() {
        StoreHalf(address: ControlAddress, value: 0x0000u);
        StoreWord(address: SourceAddress, value: m_tableAddress);
        StoreWord(address: DestinationAddress, value: ScrollAddress);
        StoreHalf(address: UnitCountAddress, value: 2u);
        // Enable, horizontal-blank start, repeating, destination reloading each burst, source advancing through it.
        StoreHalf(address: ControlAddress, value: 0xA260u);
    }

    /// <summary>
    /// Emits a fill of the table from the scanline a band starts on through the picture's end, with one scroll pair.
    /// </summary>
    /// <param name="line">The first scanline the band governs.</param>
    /// <param name="scrollX">Emits a load of the horizontal scroll into the given register.</param>
    /// <param name="scrollY">Emits a load of the vertical scroll into the given register.</param>
    /// <remarks>Callers fill in ascending order: each band overwrites the tail the one before it left.</remarks>
    public void EmitFillFrom(int line, Action<LowRegister> scrollX, Action<LowRegister> scrollY) {
        ArgumentNullException.ThrowIfNull(argument: scrollX);
        ArgumentNullException.ThrowIfNull(argument: scrollY);

        var entry = Math.Max(val1: 0, val2: line - 1);
        if (entry >= ScanlineCount) {
            return;
        }

        var loop = m_emitter.NewLabel();
        scrollX(LowRegister.R5);
        scrollY(LowRegister.R6);
        m_emitter.LoadConstant(destination: LowRegister.R4, value: m_tableAddress + ((uint)entry * 4u));
        m_emitter.LoadConstant(destination: LowRegister.R3, value: (uint)(ScanlineCount - entry));
        m_emitter.MarkLabel(label: loop);
        m_emitter.StoreHalf(source: LowRegister.R5, baseRegister: LowRegister.R4, byteOffset: 0);
        m_emitter.StoreHalf(source: LowRegister.R6, baseRegister: LowRegister.R4, byteOffset: 2);
        m_emitter.AddImmediate(register: LowRegister.R4, value: 4);
        m_emitter.SubtractImmediate(register: LowRegister.R3, value: 1);
        m_emitter.Branch(condition: ThumbCondition.NotEqual, label: loop);
    }

    private void StoreHalf(uint address, uint value) {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: value);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreHalf(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
    }

    private void StoreWord(uint address, uint value) {
        m_emitter.LoadConstant(destination: LowRegister.R0, value: value);
        m_emitter.LoadConstant(destination: LowRegister.R2, value: address);
        m_emitter.StoreWord(source: LowRegister.R0, baseRegister: LowRegister.R2, byteOffset: 0);
    }
}
