namespace Puck.HumbleGamingDeck;

// Object memory, per-dot sprite evaluation, and the sprite fetches.
// https://www.nesdev.org/wiki/PPU_sprite_evaluation and https://www.nesdev.org/wiki/PPU_OAM
public sealed partial class HgdPpu {
    private readonly byte[] m_spriteLow = new byte[8];
    private readonly byte[] m_spriteHigh = new byte[8];
    private readonly byte[] m_spriteAttributes = new byte[8];
    private readonly byte[] m_spriteX = new byte[8];
    private readonly byte[] m_nextSpriteLow = new byte[8];
    private readonly byte[] m_nextSpriteHigh = new byte[8];
    private readonly byte[] m_nextSpriteAttributes = new byte[8];
    private readonly byte[] m_nextSpriteX = new byte[8];

    private int m_spriteCount;
    private bool m_spriteZeroOnLine;
    private int m_nextSpriteCount;
    private bool m_nextSpriteZero;
    private byte m_oamBus;
    private int m_secondaryIndex;
    private int m_copyRemaining;
    private int m_spritesFound;
    private bool m_evaluationDone;
    private bool m_firstEvaluation;
    private byte m_fetchSpriteY;
    private byte m_fetchSpriteTile;

    private int SpriteHeight => (((m_control & 0x20) != 0) ? 16 : 8);

    // Reading $2004 while the PPU renders returns whatever is on its object-memory bus: $FF while secondary memory is
    // being cleared, then the bytes evaluation and the sprite fetches read.
    private byte ReadObjectMemory() {
        if (RenderingEnabled && RenderingLine) {
            return m_oamBus;
        }

        // The attribute byte has no bits 2-4.
        return (((m_oamAddress & 3) == 2) ? ((byte)(m_oam[m_oamAddress] & 0xE3)) : m_oam[m_oamAddress]);
    }
    // A $2004 write while rendering stores nothing and bumps only the sprite-index half of OAMADDR, as the evaluation
    // pointer does.
    private void WriteObjectMemory(byte value) {
        if (RenderingEnabled && RenderingLine) {
            m_oamAddress = ((byte)(m_oamAddress + 4));

            return;
        }

        m_oam[m_oamAddress] = value;
        ++m_oamAddress;
    }
    private void StepSprites() {
        var dot = m_dot;

        if (m_line < Height) {
            if ((dot >= 1) && (dot <= 64)) {
                m_oamBus = 0xFF;
                if ((dot & 1) == 0) {
                    m_secondaryOam[((dot >> 1) - 1)] = 0xFF;
                }
                if (dot == 64) {
                    m_secondaryIndex = 0;
                    m_copyRemaining = 0;
                    m_spritesFound = 0;
                    m_evaluationDone = false;
                    m_firstEvaluation = true;
                    m_nextSpriteZero = false;
                }
            } else if ((dot >= 65) && (dot <= 256)) {
                if ((dot & 1) != 0) {
                    m_oamBus = m_oam[m_oamAddress];
                } else {
                    EvaluateSprites();
                }
            }
        }
        if ((dot >= 257) && (dot <= 320)) {
            if (dot == 257) {
                m_nextSpriteCount = ((m_line < Height) ? m_spritesFound : 0);
            }
            m_oamAddress = 0;
            FetchSprites(dot: dot);
        }
    }
    private bool InRange(byte y) {
        var row = (m_line - y);

        return ((row >= 0) && (row < SpriteHeight));
    }
    // One write dot of evaluation. OAMADDR is the evaluation pointer: its top six bits index the sprite (n) and its low
    // two the byte (m), so a misaligned OAMADDR at dot 65 evaluates from the middle of an entry, as the hardware does.
    private void EvaluateSprites() {
        if (m_evaluationDone) {
            m_oamAddress = ((byte)(m_oamAddress + 4));

            return;
        }
        if (m_spritesFound < 8) {
            m_secondaryOam[m_secondaryIndex] = m_oamBus;
            if (m_copyRemaining > 0) {
                ++m_secondaryIndex;
                --m_copyRemaining;
                if (m_copyRemaining == 0) {
                    ++m_spritesFound;
                }
                AdvanceEvaluation(step: 1);
            } else if (InRange(y: m_oamBus)) {
                if (m_firstEvaluation) {
                    m_nextSpriteZero = true;
                }
                ++m_secondaryIndex;
                m_copyRemaining = 3;
                AdvanceEvaluation(step: 1);
            } else {
                AdvanceEvaluation(step: 4);
            }
            m_firstEvaluation = false;

            return;
        }
        if (InRange(y: m_oamBus)) {
            m_spriteOverflow = true;
            m_evaluationDone = true;

            return;
        }

        // Secondary memory is full: an out-of-range entry advances n and, through the hardware bug behind the
        // unreliable overflow flag, m as well, without a carry between them.
        var next = (m_oamAddress + 4);

        m_oamAddress = ((byte)((next & 0xFC) | ((m_oamAddress + 1) & 3)));
        if (next > 0xFF) {
            m_evaluationDone = true;
        }
    }
    private void AdvanceEvaluation(int step) {
        var next = (m_oamAddress + step);

        m_oamAddress = ((byte)next);
        if (next > 0xFF) {
            m_evaluationDone = true;
        }
    }
    // Dots 257-320 fetch the eight sprites of the next line, eight dots apiece: two nametable fetches the PPU discards,
    // then the pattern bytes. A slot evaluation left empty holds $FF, whose fetch the board still sees.
    private void FetchSprites(int dot) {
        var slot = ((dot - 257) >> 3);
        var phase = (dot - 257) & 7;
        var entry = (slot * 4);

        switch (phase) {
            case 0:
                m_oamBus = m_secondaryOam[entry];
                m_fetchSpriteY = m_oamBus;
                PutAddress(address: NametableAddress);

                break;
            case 1:
                m_oamBus = m_secondaryOam[(entry + 1)];
                m_fetchSpriteTile = m_oamBus;
                _ = LatchFetch();

                break;
            case 2:
                m_oamBus = m_secondaryOam[(entry + 2)];
                m_nextSpriteAttributes[slot] = m_oamBus;
                PutAddress(address: NametableAddress);

                break;
            case 3:
                m_oamBus = m_secondaryOam[(entry + 3)];
                m_nextSpriteX[slot] = m_oamBus;
                _ = LatchFetch();

                break;
            case 4:
                PutAddress(address: SpritePatternAddress(slot: slot));

                break;
            case 5:
                m_nextSpriteLow[slot] = SpritePattern(
                    slot: slot,
                    value: LatchFetch()
                );

                break;
            case 6:
                PutAddress(address: ((ushort)(SpritePatternAddress(slot: slot) + 8)));

                break;
            default:
                m_nextSpriteHigh[slot] = SpritePattern(
                    slot: slot,
                    value: LatchFetch()
                );

                break;
        }
    }
    private ushort SpritePatternAddress(int slot) {
        var attributes = m_nextSpriteAttributes[slot];
        var row = (m_line - m_fetchSpriteY) & 0xFF;

        if ((attributes & 0x80) != 0) {
            row = ((SpriteHeight - 1) - row);
        }
        if (SpriteHeight == 16) {
            var tile = (m_fetchSpriteTile & 0xFE) | ((row >> 3) & 1);

            return ((ushort)(((m_fetchSpriteTile & 1) << 12) | (tile << 4) | (row & 7)));
        }

        return ((ushort)(((m_control & 0x08) << 9) | (m_fetchSpriteTile << 4) | (row & 7)));
    }
    // An empty slot's fetch still happens, but it draws nothing; a horizontally flipped sprite's pattern is reversed here
    // so the multiplexer always reads bit 7 first.
    private byte SpritePattern(int slot, byte value) {
        if (slot >= m_nextSpriteCount) {
            return 0;
        }

        return (((m_nextSpriteAttributes[slot] & 0x40) != 0) ? ReverseBits(value: value) : value);
    }
    private static byte ReverseBits(byte value) {
        var reversed = 0;

        for (var bit = 0; (bit < 8); ++bit) {
            reversed |= (((value >> bit) & 1) << (7 - bit));
        }

        return ((byte)reversed);
    }
    // The sprites fetched during a line are the ones the next line shows.
    private void BeginLine() {
        m_nextSpriteLow.CopyTo(array: m_spriteLow, index: 0);
        m_nextSpriteHigh.CopyTo(array: m_spriteHigh, index: 0);
        m_nextSpriteAttributes.CopyTo(array: m_spriteAttributes, index: 0);
        m_nextSpriteX.CopyTo(array: m_spriteX, index: 0);
        m_spriteCount = m_nextSpriteCount;
        m_spriteZeroOnLine = m_nextSpriteZero;
        m_nextSpriteCount = 0;
        m_nextSpriteZero = false;
    }
    // The sprite-palette index of the first opaque sprite at x, or zero when there is none or the background wins its
    // priority; sprite 0 hit sets here, on an opaque sprite 0 pixel over an opaque background pixel anywhere but x 255.
    private int SpritePixel(int background, int x) {
        if (((m_mask & 0x10) == 0) || ((x < 8) && ((m_mask & 0x04) == 0))) {
            return 0;
        }
        for (var slot = 0; (slot < m_spriteCount); ++slot) {
            var offset = (x - m_spriteX[slot]);

            if ((offset < 0) || (offset > 7)) {
                continue;
            }

            var bit = (7 - offset);
            var pattern = ((m_spriteLow[slot] >> bit) & 1) | (((m_spriteHigh[slot] >> bit) & 1) << 1);

            if (pattern == 0) {
                continue;
            }

            var backgroundOpaque = ((background & 3) != 0);

            if ((slot == 0) && m_spriteZeroOnLine && backgroundOpaque && (x != 255)) {
                m_spriteZeroHit = true;
            }
            if (backgroundOpaque && ((m_spriteAttributes[slot] & 0x20) != 0)) {
                return 0;
            }

            return 0x10 | ((m_spriteAttributes[slot] & 3) << 2) | pattern;
        }

        return 0;
    }
    private void TransferSpriteState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Block(values: m_spriteLow.AsSpan());
        transfer.Block(values: m_spriteHigh.AsSpan());
        transfer.Block(values: m_spriteAttributes.AsSpan());
        transfer.Block(values: m_spriteX.AsSpan());
        transfer.Block(values: m_nextSpriteLow.AsSpan());
        transfer.Block(values: m_nextSpriteHigh.AsSpan());
        transfer.Block(values: m_nextSpriteAttributes.AsSpan());
        transfer.Block(values: m_nextSpriteX.AsSpan());
        transfer.Int32(value: ref m_spriteCount);
        transfer.Boolean(value: ref m_spriteZeroOnLine);
        transfer.Int32(value: ref m_nextSpriteCount);
        transfer.Boolean(value: ref m_nextSpriteZero);
        transfer.Byte(value: ref m_oamBus);
        transfer.Int32(value: ref m_secondaryIndex);
        transfer.Int32(value: ref m_copyRemaining);
        transfer.Int32(value: ref m_spritesFound);
        transfer.Boolean(value: ref m_evaluationDone);
        transfer.Boolean(value: ref m_firstEvaluation);
        transfer.Byte(value: ref m_fetchSpriteY);
        transfer.Byte(value: ref m_fetchSpriteTile);
    }
}
