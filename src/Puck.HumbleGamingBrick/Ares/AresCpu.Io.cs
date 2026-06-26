namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// The CPU's memory-mapped registers and work/high RAM, ported from ares (<c>gb/cpu/io.cpp</c>). The CPU owns
/// 0xC000-0xFDFF (WRAM, with the CGB bank window), 0xFF80-0xFFFE (HRAM), JOYP, SB/SC, DIV/TIMA/TMA/TAC, IF, the CGB
/// KEY1/HDMA/SVBK registers, and IE — all latched at bus sub-cycle 2.
/// </summary>
public sealed partial class AresCpu {
    /// <summary>Sets the live joypad state. Each parameter is <see langword="true"/> when the button is pressed.</summary>
    public void SetJoypad(bool a, bool b, bool select, bool start, bool right, bool left, bool up, bool down) {
        var buttons = 0x0F;
        var dpad = 0x0F;

        if (a) { buttons &= ~0x01; }
        if (b) { buttons &= ~0x02; }
        if (select) { buttons &= ~0x04; }
        if (start) { buttons &= ~0x08; }
        if (right) { dpad &= ~0x01; }
        if (left) { dpad &= ~0x02; }
        if (up) { dpad &= ~0x04; }
        if (down) { dpad &= ~0x08; }

        m_joypButtons = (byte)buttons;
        m_joypDpad = (byte)dpad;
    }

    private ushort WramAddress(int address) {
        if (address < 0x1000) {
            return (ushort)address;
        }

        var bank = (m_wramBank + (m_wramBank == 0 ? 1 : 0));

        return (ushort)((bank << 12) | (address & 0x0FFF));
    }

    private void JoypPoll() {
        m_joyp = 0x0F;

        if (!m_p14) {
            m_joyp &= m_joypDpad;
        }

        if (!m_p15) {
            m_joyp &= m_joypButtons;
        }

        if (m_joyp != 0x0F) {
            Raise(interrupt: AresInterrupt.Joypad);
        }
    }

    /// <inheritdoc/>
    public byte ReadIo(int cycle, ushort address, byte data) {
        if (address <= 0xBFFF) {
            return data;
        }

        if ((address >= 0xC000) && (address <= 0xFDFF) && (cycle == 2)) {
            return m_wram[WramAddress(address: address & 0x1FFF)];
        }

        if ((address >= 0xFF80) && (address <= 0xFFFE) && (cycle == 2)) {
            return m_hram[address & 0x7F];
        }

        if (cycle != 2) {
            return data;
        }

        switch (address) {
            case 0xFF00: // JOYP
                JoypPoll();
                data = (byte)((data & 0xC0) | (m_joyp & 0x0F) | (m_p14 ? 0x10 : 0) | (m_p15 ? 0x20 : 0));

                return data;
            case 0xFF01: // SB
                return m_serialData;
            case 0xFF02: // SC
                data = (byte)((data & 0x7C)
                    | (m_serialClock ? 0x01 : 0)
                    | ((m_serialSpeed || !m_color) ? 0x02 : 0)
                    | (m_serialTransfer ? 0x80 : 0));

                return data;
            case 0xFF04: // DIV
                return (byte)(m_div >> 8);
            case 0xFF05: // TIMA
                return m_tima;
            case 0xFF06: // TMA
                return m_tma;
            case 0xFF07: // TAC
                data = (byte)((data & 0xF8) | (m_timerClock & 0x03) | (m_timerEnable ? 0x04 : 0));

                return data;
            case 0xFF0F: // IF
                return (byte)((data & 0xE0) | (m_interruptFlag & 0x1F));
            case 0xFF4D when m_color && m_cgbMode: // KEY1
                data = (byte)((data & 0x7E) | (m_speedSwitch ? 0x01 : 0) | (m_speedDouble != 0 ? 0x80 : 0));

                return data;
            case 0xFF55 when m_color && m_cgbMode: // HDMA5
                data = (byte)((m_dmaLength & 0x7F) | (m_hdmaActive ? 0 : 0x80));

                return data;
            case 0xFF70 when m_color && m_cgbMode: // SVBK
                return (byte)m_wramBank;
            case 0xFFFF: // IE
                return m_interruptEnable;
            default:
                return data;
        }
    }

    /// <inheritdoc/>
    public void WriteIo(int cycle, ushort address, byte data) {
        if (address <= 0xBFFF) {
            return;
        }

        if ((address >= 0xC000) && (address <= 0xFDFF) && (cycle == 2)) {
            m_wram[WramAddress(address: address & 0x1FFF)] = data;

            return;
        }

        if ((address >= 0xFF80) && (address <= 0xFFFE) && (cycle == 2)) {
            m_hram[address & 0x7F] = data;

            return;
        }

        if (cycle != 2) {
            return;
        }

        switch (address) {
            case 0xFF00: // JOYP
                m_p14 = ((data & 0x10) != 0);
                m_p15 = ((data & 0x20) != 0);

                return;
            case 0xFF01: // SB
                m_serialData = data;

                return;
            case 0xFF02: // SC
                m_serialClock = ((data & 0x01) != 0);
                m_serialSpeed = (((data & 0x02) != 0) && m_color);
                m_serialTransfer = ((data & 0x80) != 0);

                if (m_serialTransfer) {
                    m_serialBits = 8;
                }

                return;
            case 0xFF04: // DIV
                m_div = 0;

                return;
            case 0xFF05: // TIMA
                m_tima = data;

                return;
            case 0xFF06: // TMA
                m_tma = data;

                return;
            case 0xFF07: // TAC
                m_timerClock = (data & 0x03);
                m_timerEnable = ((data & 0x04) != 0);

                return;
            case 0xFF0F: // IF
                m_interruptFlag = (byte)(data & 0x1F);

                return;
            case 0xFF4D when m_color && m_cgbMode: // KEY1
                m_speedSwitch = ((data & 0x01) != 0);

                return;
            case 0xFF51 when m_color && m_cgbMode: // HDMA1
                m_dmaSource = (ushort)((m_dmaSource & 0x00FF) | (data << 8));

                return;
            case 0xFF52 when m_color && m_cgbMode: // HDMA2
                m_dmaSource = (ushort)((m_dmaSource & 0xFF00) | (data & 0xF0));

                return;
            case 0xFF53 when m_color && m_cgbMode: // HDMA3
                m_dmaTarget = (ushort)((m_dmaTarget & 0x00FF) | (data << 8));

                return;
            case 0xFF54 when m_color && m_cgbMode: // HDMA4
                m_dmaTarget = (ushort)((m_dmaTarget & 0xFF00) | (data & 0xF0));

                return;
            case 0xFF55 when m_color && m_cgbMode: // HDMA5
                WriteHdma5(data: data);

                return;
            case 0xFF70 when m_color && m_cgbMode: // SVBK
                m_wramBank = (data & 0x07);

                return;
            case 0xFFFF: // IE
                m_interruptEnable = data;

                return;
            default:
                return;
        }
    }

    private void WriteHdma5(byte data) {
        // A 1->0 transition stops an active HDMA (and does not trigger a GDMA).
        if (m_hdmaActive && ((data & 0x80) == 0)) {
            m_dmaLength = (data & 0x7F);
            m_hdmaActive = false;

            return;
        }

        m_dmaLength = (data & 0x7F);
        HdmaTrigger(hblank: m_hblank, active: true);
        m_hdmaActive = ((data & 0x80) != 0);

        if ((data & 0x80) == 0) {
            Step(clocks: 4);

            do {
                for (var loop = 0; loop < 16; loop += 1) {
                    WriteDma(address: m_dmaTarget++, data: ReadDma(address: m_dmaSource++, data: 0xFF));
                }

                Step(clocks: 2 << m_speedDouble);
            } while (m_dmaLength-- != 0);
        }
    }
}
