namespace Puck.GameBoy;

/// <summary>
/// The audio processing unit: four sound channels (two pulse, one wave, one noise) plus the master control
/// registers (<c>NR50</c>-<c>NR52</c>) and the channel-3 wave-pattern RAM, occupying <c>0xFF10</c>-<c>0xFF3F</c>.
/// <para>
/// This stage models the register plane — the hardware read masks (each register exposes only some bits; the
/// rest read as one), the <c>NR52</c> power switch (clearing the APU zeroes every register and silences the
/// channels, and while powered down the registers are read-only at their masks), and wave RAM. The frame
/// sequencer and channel waveform generation build on top of it.
/// </para>
/// </summary>
public sealed class Apu : IClockedComponent {
    // The audio register block 0xFF10-0xFF26 (NR10..NR52), indexed from 0xFF10.
    private const int RegisterCount = 23;
    private const int WaveRamSize = 16;
    private const int MasterControlIndex = (MemoryMap.AudioMasterControl - MemoryMap.AudioBase);

    // Per-register read OR-masks: the bits that always read as one (write-only or unused bits). Indexed from
    // 0xFF10. NR52 (the last) is assembled separately from the power bit and the live channel-status bits.
    private static readonly byte[] s_readMasks = [
        0x80, 0x3F, 0x00, 0xFF, 0xBF, // NR10 NR11 NR12 NR13 NR14
        0xFF,                         // 0xFF15 (unused)
        0x3F, 0x00, 0xFF, 0xBF,       // NR21 NR22 NR23 NR24
        0x7F, 0xFF, 0x9F, 0xFF, 0xBF, // NR30 NR31 NR32 NR33 NR34
        0xFF,                         // 0xFF1F (unused)
        0xFF, 0x00, 0x00, 0xBF,       // NR41 NR42 NR43 NR44
        0x00, 0x00, 0x70,             // NR50 NR51 NR52
    ];

    private readonly byte[] m_registers = new byte[RegisterCount];
    private readonly byte[] m_waveRam = new byte[WaveRamSize];
    private readonly bool[] m_channelActive = new bool[4];

    private bool m_powered;

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Cpu;

    /// <summary>Reads an audio register or wave-RAM byte (<c>0xFF10</c>-<c>0xFF3F</c>).</summary>
    /// <param name="address">The address to read.</param>
    /// <returns>The value with hardware read-as-one bits applied.</returns>
    public byte Read(ushort address) {
        if (address >= MemoryMap.WaveRamBase) {
            return m_waveRam[address - MemoryMap.WaveRamBase];
        }

        var index = (address - MemoryMap.AudioBase);

        // 0xFF27-0xFF2F sit between the registers and wave RAM and are unmapped: open bus.
        if (index >= RegisterCount) {
            return 0xFF;
        }

        if (index == MasterControlIndex) {
            return ReadMasterControl();
        }

        return (byte)(m_registers[index] | s_readMasks[index]);
    }
    /// <summary>Writes an audio register or wave-RAM byte (<c>0xFF10</c>-<c>0xFF3F</c>).</summary>
    /// <param name="address">The address to write.</param>
    /// <param name="value">The value written.</param>
    public void Write(ushort address, byte value) {
        if (address >= MemoryMap.WaveRamBase) {
            // Wave RAM stays accessible regardless of the power state.
            m_waveRam[address - MemoryMap.WaveRamBase] = value;

            return;
        }

        var index = (address - MemoryMap.AudioBase);

        if (index >= RegisterCount) {
            return;
        }

        if (index == MasterControlIndex) {
            WriteMasterControl(value: value);

            return;
        }

        // While powered down every register but NR52 is inert and ignores writes.
        if (m_powered) {
            m_registers[index] = value;
        }
    }

    /// <summary>Seeds the documented DMG post-boot register state: the APU is powered on with the register values
    /// the boot ROM's startup chime leaves behind, and channel&#160;1 is still active. Used when starting a cartridge
    /// without running a boot ROM, so reads of the audio block match hardware.</summary>
    public void InitializePostBoot() {
        m_powered = true;

        // NR10..NR51 (indices 0-21) as left by the boot ROM. The 0xFF entries are the write-only / unused slots.
        byte[] postBoot = [
            0x80, 0xBF, 0xF3, 0xFF, 0xBF, // NR10 NR11 NR12 NR13 NR14
            0xFF,                         // 0xFF15
            0x3F, 0x00, 0xFF, 0xBF,       // NR21 NR22 NR23 NR24
            0x7F, 0xFF, 0x9F, 0xFF, 0xBF, // NR30 NR31 NR32 NR33 NR34
            0xFF,                         // 0xFF1F
            0xFF, 0x00, 0x00, 0xBF,       // NR41 NR42 NR43 NR44
            0x77, 0xF3,                   // NR50 NR51
        ];

        Array.Copy(sourceArray: postBoot, destinationArray: m_registers, length: postBoot.Length);

        // The boot chime leaves channel 1 sounding, so NR52 reports it active (bit 0).
        m_channelActive[0] = true;
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        // The frame sequencer and channel waveform generation are a later stage; the register plane is clocked
        // by nothing yet.
    }

    private byte ReadMasterControl() {
        var status = 0;

        for (var channel = 0; channel < m_channelActive.Length; channel += 1) {
            if (m_channelActive[channel]) {
                status |= (1 << channel);
            }
        }

        return (byte)(s_readMasks[MasterControlIndex] | (m_powered ? 0x80 : 0x00) | status);
    }
    private void WriteMasterControl(byte value) {
        var powerOn = ((value & 0x80) != 0);

        if (!powerOn && m_powered) {
            // Powering down zeroes every register (NR10-NR51) and silences the channels. NR52's own bit and wave
            // RAM are preserved.
            Array.Clear(array: m_registers);
            Array.Clear(array: m_channelActive);
        }

        m_powered = powerOn;
    }
}
