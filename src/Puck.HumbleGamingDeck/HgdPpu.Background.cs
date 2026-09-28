namespace Puck.HumbleGamingDeck;

// The background pipeline and the pixel multiplexer.
// https://www.nesdev.org/wiki/PPU_rendering and https://www.nesdev.org/wiki/PPU_scrolling
public sealed partial class HgdPpu {
    private ushort m_fetchAddress;
    private byte m_nametableLatch;
    private byte m_attributeLatch;
    private byte m_patternLowLatch;
    private byte m_patternHighLatch;
    private ushort m_patternLowShifter;
    private ushort m_patternHighShifter;
    private ushort m_attributeLowShifter;
    private ushort m_attributeHighShifter;

    // One rendering dot on a visible or pre-render line. Inside the dot the shifters move first, a reload then fills
    // their low bytes, and the pixel the caller emits afterwards reads the shifted state: pixel 0 on dot 1 shows the
    // first tile fetched on the previous line, and a reload on dot 9 lands after eight shifts.
    private void StepRenderingDot() {
        var dot = m_dot;

        if (((dot >= 2) && (dot <= 257)) || ((dot >= 322) && (dot <= 337))) {
            ShiftBackground();
        }
        if ((((dot & 7) == 1) && (dot >= 9) && (dot <= 257)) || (dot == 329) || (dot == 337)) {
            ReloadBackground();
        }
        if (((dot >= 1) && (dot <= 256)) || ((dot >= 321) && (dot <= 336))) {
            FetchBackground(phase: (dot - 1) & 7);
        } else if ((dot == 337) || (dot == 339)) {
            PutAddress(address: NametableAddress);
        } else if ((dot == 338) || (dot == 340)) {
            m_nametableLatch = LatchFetch();
        }
        if (dot == 256) {
            IncrementY();
        } else if (dot == 257) {
            // Horizontal position: coarse X and the horizontal nametable bit.
            m_v = ((ushort)((m_v & 0x7BE0) | (m_t & 0x041F)));
        } else if ((m_line == PreRenderLine) && (dot >= 280) && (dot <= 304)) {
            // Vertical position: fine Y, coarse Y and the vertical nametable bit.
            m_v = ((ushort)((m_v & 0x041F) | (m_t & 0x7BE0)));
        }
        StepSprites();
    }

    private ushort NametableAddress => ((ushort)(0x2000 | (m_v & 0x0FFF)));

    private void FetchBackground(int phase) {
        switch (phase) {
            case 0:
                PutAddress(address: NametableAddress);

                break;
            case 1:
                m_nametableLatch = LatchFetch();

                break;
            case 2:
                PutAddress(address: ((ushort)(0x23C0 | (m_v & 0x0C00) | ((m_v >> 4) & 0x38) | ((m_v >> 2) & 0x07))));

                break;
            case 3: {
                    var attribute = LatchFetch();

                    // Each attribute byte covers four 2x2-tile quadrants; coarse Y bit 1 and coarse X bit 1 pick one.
                    if ((m_v & 0x40) != 0) {
                        attribute >>= 4;
                    }
                    if ((m_v & 0x02) != 0) {
                        attribute >>= 2;
                    }
                    m_attributeLatch = ((byte)(attribute & 3));

                    break;
                }
            case 4:
                PutAddress(address: BackgroundPatternAddress);

                break;
            case 5:
                m_patternLowLatch = LatchFetch();

                break;
            case 6:
                PutAddress(address: ((ushort)(BackgroundPatternAddress + 8)));

                break;
            default:
                m_patternHighLatch = LatchFetch();
                IncrementCoarseX();

                break;
        }
    }

    private ushort BackgroundPatternAddress => ((ushort)(((m_control & 0x10) << 8) | (m_nametableLatch << 4) | ((m_v >> 12) & 7)));

    // A fetch drives its address on one dot and latches the data on the next; the board observes the address as it is
    // driven, which is when a board clocking on PPU A12 sees it change.
    private void PutAddress(ushort address) {
        m_fetchAddress = address;
        m_mapper.ObservePpuAddress(
            address: address,
            masterTick: m_masterTick
        );
    }
    private byte LatchFetch() =>
        m_mapper.PpuRead(
            address: m_fetchAddress,
            nametables: m_nametables
        );
    private void ShiftBackground() {
        m_patternLowShifter <<= 1;
        m_patternHighShifter <<= 1;
        m_attributeLowShifter <<= 1;
        m_attributeHighShifter <<= 1;
    }
    private void ReloadBackground() {
        m_patternLowShifter = ((ushort)((m_patternLowShifter & 0xFF00) | m_patternLowLatch));
        m_patternHighShifter = ((ushort)((m_patternHighShifter & 0xFF00) | m_patternHighLatch));
        m_attributeLowShifter = ((ushort)((m_attributeLowShifter & 0xFF00) | (((m_attributeLatch & 1) != 0) ? 0xFF : 0)));
        m_attributeHighShifter = ((ushort)((m_attributeHighShifter & 0xFF00) | (((m_attributeLatch & 2) != 0) ? 0xFF : 0)));
    }
    private void IncrementCoarseX() {
        if ((m_v & 0x001F) == 31) {
            m_v = ((ushort)((m_v & ~0x001F) ^ 0x0400));
        } else {
            ++m_v;
        }
    }
    private void IncrementY() {
        if ((m_v & 0x7000) != 0x7000) {
            m_v += 0x1000;

            return;
        }

        m_v &= 0x0FFF;

        var coarseY = (m_v >> 5) & 31;

        if (coarseY == 29) {
            coarseY = 0;
            m_v ^= 0x0800;
        } else if (coarseY == 31) {
            coarseY = 0;
        } else {
            ++coarseY;
        }
        m_v = ((ushort)((m_v & ~0x03E0) | (coarseY << 5)));
    }
    private void EmitPixel(int x) {
        int code;

        if (RenderingEnabled) {
            var background = BackgroundPixel(x: x);
            var sprite = SpritePixel(
                background: background,
                x: x
            );

            code = ((sprite != 0) ? sprite : (((background & 3) != 0) ? background : 0));
            code = ReadPalette(address: ((ushort)(0x3F00 | code)));
        } else {
            // With rendering off the picture is the backdrop, unless v points into palette RAM, which then shows the
            // colour it addresses.
            code = (((m_v & 0x3F00) == 0x3F00) ? ReadPalette(address: m_v) : m_palette[0]);
        }
        m_frame[((m_line * Width) + x)] = ((ushort)((code & PaletteMask) | ((m_mask & 0xE0) << 1)));
    }
    // The palette-relative index of the background pixel at x: two attribute bits above two pattern bits, zero when the
    // pattern bits are zero or the background is hidden.
    private int BackgroundPixel(int x) {
        if (((m_mask & 0x08) == 0) || ((x < 8) && ((m_mask & 0x02) == 0))) {
            return 0;
        }

        var bit = (15 - m_fineX);
        var pattern = ((m_patternLowShifter >> bit) & 1) | (((m_patternHighShifter >> bit) & 1) << 1);

        if (pattern == 0) {
            return 0;
        }

        return (((m_attributeLowShifter >> bit) & 1) << 2) | (((m_attributeHighShifter >> bit) & 1) << 3) | pattern;
    }
    private void TransferBackgroundState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.UInt16(value: ref m_fetchAddress);
        transfer.Byte(value: ref m_nametableLatch);
        transfer.Byte(value: ref m_attributeLatch);
        transfer.Byte(value: ref m_patternLowLatch);
        transfer.Byte(value: ref m_patternHighLatch);
        transfer.UInt16(value: ref m_patternLowShifter);
        transfer.UInt16(value: ref m_patternHighShifter);
        transfer.UInt16(value: ref m_attributeLowShifter);
        transfer.UInt16(value: ref m_attributeHighShifter);
    }
}
