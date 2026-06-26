// IO routing is a long address/cycle if-chain ported verbatim from ares; the analyzers' complexity heuristics do
// not apply to a register-decode table.
#pragma warning disable CA1502 // Avoid excessive complexity
#pragma warning disable CA1505 // Avoid unmaintainable code

namespace Puck.HumbleGamingBrick.Ares;

/// <summary>The PPU's memory-mapped registers, VRAM, and OAM, ported from ares (<c>gb/ppu/io.cpp</c>, DMG paths).</summary>
public sealed partial class AresPpu {
    private int VramAddress(int address) =>
        (address & 0x1FFF); // DMG: single 8 KiB bank.

    /// <inheritdoc/>
    public byte ReadIo(int cycle, ushort address, byte data) {
        if ((address >= 0x8000) && (address <= 0x9FFF) && (cycle == 2)) {
            return CanAccessVram() ? m_vram[VramAddress(address: address)] : data;
        }

        if ((address >= 0xFE00) && (address <= 0xFE9F) && (cycle == 2)) {
            return CanAccessOam() ? m_oam[address & 0xFF] : data;
        }

        if ((address < 0xFF40) || (address > 0xFF7F) || (cycle != 2)) {
            return data;
        }

        switch (address) {
            case 0xFF40: // LCDC
                return (byte)((m_bgEnable ? 0x01 : 0)
                    | (m_obEnable ? 0x02 : 0)
                    | (m_obSize ? 0x04 : 0)
                    | (m_bgTilemapSelect ? 0x08 : 0)
                    | (m_bgTiledataSelect ? 0x10 : 0)
                    | (m_windowDisplayEnable ? 0x20 : 0)
                    | (m_windowTilemapSelect ? 0x40 : 0)
                    | (m_displayEnable ? 0x80 : 0));
            case 0xFF41: // STAT
                return (byte)(((m_history >> 8) & 3)
                    | (CompareLyc() ? 0x04 : 0)
                    | (m_interruptHblank ? 0x08 : 0)
                    | (m_interruptVblank ? 0x10 : 0)
                    | (m_interruptOam ? 0x20 : 0)
                    | (m_interruptLyc ? 0x40 : 0)
                    | 0x80);
            case 0xFF42: // SCY
                return m_scy;
            case 0xFF43: // SCX
                return m_scx;
            case 0xFF44: // LY
                return GetLy();
            case 0xFF45: // LYC
                return m_lyc;
            case 0xFF46: // DMA
                return m_dmaBank;
            case 0xFF47: // BGP
                return (byte)(m_bgp[0] | (m_bgp[1] << 2) | (m_bgp[2] << 4) | (m_bgp[3] << 6));
            case 0xFF48: // OBP0
                return (byte)(m_obp[0] | (m_obp[1] << 2) | (m_obp[2] << 4) | (m_obp[3] << 6));
            case 0xFF49: // OBP1
                return (byte)(m_obp[4] | (m_obp[5] << 2) | (m_obp[6] << 4) | (m_obp[7] << 6));
            case 0xFF4A: // WY
                return m_wy;
            case 0xFF4B: // WX
                return m_wx;
            default:
                return data;
        }
    }

    /// <inheritdoc/>
    public void WriteIo(int cycle, ushort address, byte data) {
        if ((address >= 0x8000) && (address <= 0x9FFF) && (cycle == 2)) {
            VramWrites += 1;
            m_vram[VramAddress(address: address)] = data;

            return;
        }

        if ((address >= 0xFE00) && (address <= 0xFE9F) && (cycle == 2)) {
            if (m_dmaActive && (m_dmaClock >= 8)) {
                return;
            }

            m_oam[address & 0xFF] = data;

            return;
        }

        if ((address < 0xFF40) || (address > 0xFF7F)) {
            return;
        }

        if ((address == 0xFF40) && (cycle == 4)) { // LCDC
            WriteLcdc(data: data);

            return;
        }

        // DMG STAT-write hardware bug: a write force-enables all STAT sources for one T-cycle (cycle 2), which can
        // produce a spurious STAT interrupt; the real values land at cycle 4.
        if ((address == 0xFF41) && (cycle == 2)) { // STAT
            m_interruptHblank = true;
            m_interruptVblank = true;
            m_interruptOam = true;
            m_interruptLyc = true;

            return;
        }

        if ((address == 0xFF41) && (cycle == 4)) { // STAT
            m_interruptHblank = ((data & 0x08) != 0);
            m_interruptVblank = ((data & 0x10) != 0);
            m_interruptOam = ((data & 0x20) != 0);
            m_interruptLyc = ((data & 0x40) != 0);

            return;
        }

        if (cycle != 2) {
            return;
        }

        switch (address) {
            case 0xFF42: // SCY
                m_scy = data;

                return;
            case 0xFF43: // SCX
                m_scx = data;

                return;
            case 0xFF45: // LYC
                m_lyc = data;

                return;
            case 0xFF46: // DMA
                m_dmaBank = data;
                m_dmaActive = true;
                m_dmaClock = 0;

                return;
            case 0xFF47: // BGP
                m_bgp[0] = (byte)(data & 3);
                m_bgp[1] = (byte)((data >> 2) & 3);
                m_bgp[2] = (byte)((data >> 4) & 3);
                m_bgp[3] = (byte)((data >> 6) & 3);

                return;
            case 0xFF48: // OBP0
                m_obp[0] = (byte)(data & 3);
                m_obp[1] = (byte)((data >> 2) & 3);
                m_obp[2] = (byte)((data >> 4) & 3);
                m_obp[3] = (byte)((data >> 6) & 3);

                return;
            case 0xFF49: // OBP1
                m_obp[4] = (byte)(data & 3);
                m_obp[5] = (byte)((data >> 2) & 3);
                m_obp[6] = (byte)((data >> 4) & 3);
                m_obp[7] = (byte)((data >> 6) & 3);

                return;
            case 0xFF4A: // WY
                m_wy = data;

                return;
            case 0xFF4B: // WX
                m_wx = data;

                return;
            default:
                return;
        }
    }

    private void WriteLcdc(byte data) {
        var enable = ((data & 0x80) != 0);

        LcdcWriteTrace(data: data);

        if (m_displayEnable != enable) {
            m_mode = 0;
            m_ly = 0;
            m_lx = 0;

            if (enable) {
                m_latchDisplayEnable = true;
            }

            // Restart the PPU thread to begin a fresh frame (ares recreates the PPU cothread here); the in-progress
            // scanline is abandoned the next time the PPU runs.
            m_restartPending = true;
        }

        m_bgEnable = ((data & 0x01) != 0);
        m_obEnable = ((data & 0x02) != 0);
        m_obSize = ((data & 0x04) != 0);
        m_bgTilemapSelect = ((data & 0x08) != 0);
        m_bgTiledataSelect = ((data & 0x10) != 0);
        m_windowDisplayEnable = ((data & 0x20) != 0);
        m_windowTilemapSelect = ((data & 0x40) != 0);
        m_displayEnable = enable;
    }
}
